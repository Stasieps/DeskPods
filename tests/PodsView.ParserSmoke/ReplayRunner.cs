using System.Globalization;
using PodsView;

internal static class ReplayRunner
{
    private sealed record RadioRow(char Kind, int Ms, ulong Address, short Rssi, int AgeMs, bool Trusted, bool Bound, string Payload);
    internal static void Run(Action<bool, string> expect)
    {
        string? folder = null;
        foreach (string seed in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            DirectoryInfo? dir = new(seed);
            for (int depth = 0; dir is not null && depth < 10; depth++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "tests", "replay");
                if (Directory.Exists(candidate)) { folder = candidate; break; }
            }
            if (folder is not null) break;
        }
        expect(folder is not null, "Replay directory missing");
        string[] paths = Directory.GetFiles(folder!, "*.txt").Order(StringComparer.Ordinal).ToArray();
        expect(paths.Length >= 19, "All 19 regression recordings must be present");
        foreach (string path in paths) RunFixture(path, expect);
    }
    private static void RunFixture(string path, Action<bool, string> expect)
    {
        string name = Path.GetFileNameWithoutExtension(path), shapes = "";
        bool paired = true, connected = false, nearby = true;
        int? dismissMs = null;
        var rows = new List<RadioRow>();
        var expectations = new List<string[]>();
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('!'))
            {
                string[] head = line[1..].Split(' ', 2, StringSplitOptions.TrimEntries);
                string rest = head.Length > 1 ? head[1] : "";
                switch (head[0])
                {
                    case "name": name = rest; break;
                    case "paired": paired = rest == "1"; break;
                    case "connected": connected = rest == "1"; break;
                    case "nearby": nearby = rest == "1"; break;
                    case "shapes": shapes = rest.Replace(' ', ','); break;
                    case "dismiss": dismissMs = int.Parse(rest, CultureInfo.InvariantCulture); break;
                    case "window": break; // Recording annotation, not an execution instruction.
                    case "expect": expectations.Add(rest.Split(' ', StringSplitOptions.RemoveEmptyEntries)); break;
                    default: expect(false, $"{name}: unsupported header {head[0]}"); break;
                }
                continue;
            }
            string[] f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            expect(f.Length == 8, $"{name}: expected eight fields");
            rows.Add(new(f[0][0], int.Parse(f[1], CultureInfo.InvariantCulture), ulong.Parse(f[2], NumberStyles.HexNumber),
                short.Parse(f[3], CultureInfo.InvariantCulture), int.Parse(f[4], CultureInfo.InvariantCulture), f[5] == "1", f[6] == "1", f[7]));
        }
        expect(rows.Count > 0, $"{name}: empty fixture");
        expect(rows.Zip(rows.Skip(1)).All(pair => pair.First.Ms <= pair.Second.Ms), $"{name}: timestamps out of order");
        var origin = new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
        var machine = new LidStateMachine();
        var signal = new LidSignal();
        var traffic = new CaseProfileHistory();
        var annotationSignal = new LidSignal();
        signal.Seed(shapes, origin);
        annotationSignal.Seed(shapes, origin);
        var shows = new List<double>();
        var lives = new List<double>();
        int hides = 0, physicalCycle = -1;
        string physical = "none";
        double? pendingShow = null, pendingHide = null, visibleSince = null, previousCase = null, wakeAt = null;
        double worstShow = 0, worstHide = 0, longestGap = 0;
        int nextTick = 250;
        bool dismissed = dismissMs is null;
        void Leaves(double at)
        {
            if (visibleSince is double since) { lives.Add(at - since); visibleSince = null; }
            if (pendingHide is double hide) { worstHide = Math.Max(worstHide, at - hide); pendingHide = null; }
        }
        void Advance(int until)
        {
            // Exercise the real watchdog methods on a 250-ms timer, including between bursts.
            while (nextTick <= until)
            {
                DateTimeOffset at = origin.AddMilliseconds(nextTick);
                if (machine.ShouldTimeout(at))
                {
                    if (machine.WakeUnconfirmed) machine.EndUnconfirmedWake(at);
                    else machine.SuspendForSilence(at);
                    hides++;
                    Leaves(nextTick);
                }
                nextTick += 250;
            }
        }
        foreach (RadioRow row in rows)
        {
            if (!dismissed && row.Ms >= dismissMs!.Value)
            {
                Advance(dismissMs.Value);
                // 0.8.47: App calls ForceClosed only while the lid machine owns the popup.
                if (machine.IsOpen) machine.ForceClosed(origin.AddMilliseconds(dismissMs.Value));
                Leaves(dismissMs.Value);
                dismissed = true;
            }
            Advance(row.Ms);
            if (!AirPodsAdvertisementParser.TryParse(Convert.FromHexString(row.Payload), out ParsedAirPodsData? data) || data is null) continue;
            DateTimeOffset now = origin.AddMilliseconds(row.Ms);
            // Legacy classification-dependent metric, NOT physical lid latency.
            // Release31Regressions prescribes first input edges independently.
            if (row.Kind == 'p' && annotationSignal.Believe(row.Address, data.Identity, data.LidByte, data.CarriesLidState, now, out _))
            {
                if (data.IsCaseOpen && (physical != "open" || data.LidOpenCounter != physicalCycle))
                { pendingShow = row.Ms; physical = "open"; physicalCycle = data.LidOpenCounter; }
                else if (!data.IsCaseOpen && physical == "open") { pendingHide = row.Ms; physical = "closed"; }
            }
            if (row.Rssi <= PacketFilter.UnknownRssi || !row.Trusted || !row.Bound) continue;
            bool believe = CaseSignalClassifier.Believe(signal,data,row.Address,now,row.Trusted,row.Bound,
                row.AgeMs>=0 && row.AgeMs<=2000,paired,row.Rssi,out _,traffic.Observe(data.Identity,now,origin));
            bool hasBattery = data.LeftBattery is not null || data.RightBattery is not null || data.CaseBattery is not null;
            if (!PacketFilter.Allow(believe, hasBattery, row.Rssi, paired, connected, nearby)) continue;
            if (believe)
            {
                if (previousCase is double prev && row.Ms - prev > Math.Max(longestGap, 20000))
                { longestGap = row.Ms - prev; wakeAt = row.Ms; }
                previousCase = row.Ms;
            }
            LidAction action = machine.Handle(believe, data.IsCaseOpen, data.LidOpenCounter, row.Address, now,
                trusted: row.Trusted, bound: row.Bound, provenFresh: row.AgeMs >= 0 && row.AgeMs <= 2000, explicitLid: data.CarriesLidState);
            if (action == LidAction.Open)
            {
                shows.Add(row.Ms);
                visibleSince ??= row.Ms;
            }
            if ((action == LidAction.Open || action == LidAction.Update) && visibleSince is not null && pendingShow is double pending)
            { worstShow = Math.Max(worstShow, row.Ms - pending); pendingShow = null; }
            if (action == LidAction.Close) { hides++; Leaves(row.Ms); }
        }
        // 0.8.47: StreamTimeout (10 s) is the longest tail, so let every timer run out.
        Advance(rows[^1].Ms + (int)LidStateMachine.StreamTimeout.TotalMilliseconds + 1000);
        if (visibleSince is not null) lives.Add(double.PositiveInfinity);
        if (pendingShow is not null) worstShow = double.PositiveInfinity;
        if (pendingHide is not null) worstHide = double.PositiveInfinity;
        double? wakeDelay = wakeAt is double woke ? shows.Where(s => s >= woke).Select(s => (double?)(s - woke)).FirstOrDefault() : null;
        foreach (string[] e in expectations)
        {
            int limit = e.Length > 1 ? int.Parse(e[1], CultureInfo.InvariantCulture) : 0;
            bool pass = e[0] switch
            {
                "shows" => shows.Count == limit,
                "hides" => hides == limit,
                "never-show" => shows.Count == 0,
                "show-within-ms" => worstShow <= limit,
                "hide-within-ms" => worstHide <= limit,
                "show-after-wake-within-ms" => wakeDelay is double d && d <= limit,
                "show-after-wake-after-ms" => wakeDelay is null || wakeDelay.Value >= limit,
                "popup-lives-at-least-ms" => lives.Count > 0 && lives.Min() >= limit,
                "popup-lives-at-most-ms" => lives.Count > 0 && lives.Max() <= limit,
                _ => throw new InvalidOperationException($"{name}: unsupported expectation {e[0]}")
            };
            expect(pass, $"{name}: {string.Join(' ', e)} failed (shows={shows.Count}, hides={hides}, show={worstShow}, hide={worstHide}, wake={wakeDelay})");
        }
        Console.WriteLine($"   replay {name}: {shows.Count} shows, {hides} hides, worst show {worstShow:F0} ms, worst hide {worstHide:F0} ms");
    }
}
