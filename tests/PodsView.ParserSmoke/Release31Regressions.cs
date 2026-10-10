using PodsView;

internal static class Release31Regressions
{
    internal static void Run(Action<bool, string> check)
    {
        var t = new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero);
        ParsedAirPodsData Packet(uint identity, byte word, ushort model = 0x1420)
        {
            byte[] bytes = new byte[27];
            bytes[0] = 0x07; bytes[1] = 0x19; bytes[2] = 1;
            bytes[3] = (byte)(model >> 8); bytes[4] = (byte)model;
            bytes[5] = (byte)(identity >> 16); bytes[6] = (byte)(identity >> 8); bytes[7] = (byte)identity;
            bytes[8] = word;
            check(AirPodsAdvertisementParser.TryParse(bytes, out var parsed), "31: raw fixture parser failed");
            return parsed!;
        }
        var traffic = new Dictionary<LidSignal, CaseProfileHistory>();
        bool Classify(LidSignal signal, ParsedAirPodsData data, ulong address, DateTimeOffset at,
            bool fresh = true, bool paired = true, bool bound = true, bool trusted = true, short rssi = -65)
        {
            if (!traffic.TryGetValue(signal,out var shapeHistory)) traffic[signal] = shapeHistory = new CaseProfileHistory();
            return CaseSignalClassifier.Believe(signal,data,address,at,trusted,bound,fresh,paired,rssi,out _,shapeHistory.Observe(data.Identity,at,t));
        }

        // Expected edge is defined from the test INPUT, never by Believe(). This test
        // fails on .30's baseline gate even though the old latency metric could say 0ms.
        foreach (int hours in new[] { 3, 6, 24 })
        foreach (uint identity in new uint[] { 0x24FA93, 0x735697 })
        {
            var signal = new LidSignal(); var lid = new LidStateMachine();
            lid.BeginListening(t, t);
            var first = Packet(identity, 0x51); var at = t.AddHours(hours);
            bool believed = Classify(signal, first, 10, at);
            check(lid.Handle(believed, first.IsCaseOpen, first.LidOpenCounter, 10, at, provenFresh: true) == LidAction.Open,
                "31: raw first Pro2 case packet after idle did not immediately open");
            var close = Packet(identity, 0x59);
            check(lid.Handle(Classify(signal, close, 10, at.AddMilliseconds(20)), close.IsCaseOpen,
                close.LidOpenCounter, 10, at.AddMilliseconds(20), provenFresh: true) == LidAction.Close,
                "31: raw classified close was delayed");
            // Cold-profile fallback is not a timer bypass; an address rotation is covered too.
            var reopened = Packet(identity, 0x52);
            check(lid.Handle(Classify(signal, reopened, 11, at.AddMilliseconds(40)), true, 2, 11,
                at.AddMilliseconds(40), provenFresh: true) == LidAction.Open, "31: rotated first case packet lost");
        }
        // Exhaust the 8-bit profile space. 0.8.47: the cold profile (only shapes 0x39/0x49
        // after 120 s of quiet) is replaced by the in-case door - a fresh word of the
        // remembered AirPods Pro 2 sent from inside the case is believed whatever its shape,
        // because waiting to learn the shape is what lost the first opening. Merely having a
        // battery or being nearby still bootstraps nothing: the same word from an earbud
        // OUTSIDE the case (byte 5 without bit 6 and bit 2) is never believed.
        for (int shape = 0; shape <= 255; shape++)
        {
            uint identity = (uint)((((shape >> 4) | 0x40) << 16) | 0xA000 | ((shape & 15) << 4) | 3);
            var data = Packet(identity, 0x11);
            check(Classify(new LidSignal(), data, 1, t.AddHours(3)), "31: the in-case door refused a fresh in-case word");
            if (((shape >> 4) & 0x04) != 0) continue;
            uint outside = (uint)(((shape >> 4) << 16) | 0xA000 | ((shape & 15) << 4) | 3);
            check(!Classify(new LidSignal(), Packet(outside, 0x11), 1, t.AddHours(3)), "31: an earbud outside the case was believed as the lid");
        }
        foreach (uint identity in new uint[] { 0x02F78F, 0x13A7A3, 0x22F28F, 0x33A2A4 })
        {
            var signal = new LidSignal(); var data = Packet(identity, 0x11);
            for (int i = 0; i < 200; i++)
                check(!Classify(signal, data, (ulong)(i / 20 + 1), t.AddSeconds(i)), "31: static copy opened a case");
        }
        var profile = Packet(0x24FA93, 0x51);
        foreach (var flags in new[] { (false,true,true,true), (true,false,true,true), (true,true,false,true), (true,true,true,false) })
            check(!Classify(new LidSignal(), profile, 1, t.AddHours(3), flags.Item1, flags.Item2, flags.Item3, flags.Item4),
                "31: profile bypassed fresh/pair/bound/trusted guard");
        check(!Classify(new LidSignal(), profile, 1, t.AddHours(3), rssi:-127), "31: unknown RSSI bootstrapped profile");
        foreach (ushort model in new ushort[] { 0x0E20, 0x0A20, 0xFE20 })
            check(!Classify(new LidSignal(), Packet(0x24FA93, 0x51, model), 1, t.AddHours(3)), "31: Pro2 profile leaked to another model");
        // 0.8.47: a continuous in-case stream is believed word by word (LidStateMachine shows
        // its cycle once and then holds it as spent); the out-of-case copies never are.
        foreach (uint id in new uint[] { 0x24FA93, 0x735697 })
        {
            var repeatSignal = new LidSignal(); var repeated = Packet(id,0x11);
            for (int i=0;i<1000;i++) check(Classify(repeatSignal,repeated,1,t.AddSeconds(i)),"31: the in-case door dropped a repeated in-case word");
        }
        foreach (uint id in new uint[] { 0x20FA93, 0x335697 })
        {
            var repeatSignal = new LidSignal(); var repeated = Packet(id,0x11);
            for (int i=0;i<1000;i++) check(!Classify(repeatSignal,repeated,1,t.AddSeconds(i)),"31: continuous matching-shape stream promoted");
        }
        var testHistory = new CaseProfileHistory();
        check(testHistory.Observe(0x24FA93,t.AddHours(3),default)==TimeSpan.Zero,"31: invented listening epoch");
        check(testHistory.Observe(0x24FA93,t.AddHours(3),t).TotalHours==3,"31: lost measured quiet");
        check(testHistory.Observe(0x24FA93,t.AddHours(3).AddSeconds(1),t).TotalSeconds==1,"31: raw repeat failed to reset quiet");
        check(testHistory.Observe(0x24FA93,t.AddHours(4),t.AddHours(4))==TimeSpan.Zero,"31: scanner restart inherited quiet");
        var manual = new LidStateMachine();
        manual.Handle(true,true,1,1,t,provenFresh:true); manual.ForceClosed(t.AddSeconds(1));
        check(manual.Handle(true,true,1,1,t.AddHours(3),provenFresh:true)==LidAction.None,
            "31: profile/reset changes reopened deliberately dismissed same cycle");
        // OS reset is NOT manual dismissal; establish the actual new listening epoch.
        var system = new LidStateMachine();
        system.Handle(true,true,1,1,t,provenFresh:true); system.ForceClosed(t.AddSeconds(1));
        system.ResetForSystem(t.AddSeconds(2)); system.BeginListening(t.AddSeconds(2),t.AddSeconds(2));
        check(system.Handle(true,false,1,1,t.AddHours(3),provenFresh:true)==LidAction.Open,
            "31: OS reset falsely retained manual dismissal");
        system.Handle(true,true,2,1,t.AddHours(3).AddMilliseconds(10),provenFresh:true);
        check(system.Handle(true,false,2,1,t.AddHours(3).AddMilliseconds(20),provenFresh:true)==LidAction.Close,
            "31: close after reset waited for wake floor");
        // 0.8.47: after an OS reset (boot, unlock) the silence is unknown, and unknown counts
        // as long, so the case's first word wakes the popup at once.
        var cold = new LidStateMachine(); cold.ResetForSystem(t);
        check(cold.Handle(true,false,1,1,t.AddHours(3),provenFresh:true)==LidAction.Open && cold.LastSilenceMs < 0,
            "31: the first word after an OS reset did not wake the popup");

        // Missing-cycle sequence in the supplied SAFE diagnostic export: keep it missing.
        // Without an open packet we must never fabricate an opening on a quick closed word.
        var recorded = new LidStateMachine();
        recorded.Handle(true,true,2,1,t,provenFresh:true);
        check(recorded.Handle(true,false,2,1,t.AddMilliseconds(400),provenFresh:true)==LidAction.Close,"31: log close2");
        check(recorded.Handle(true,false,3,1,t.AddMilliseconds(5200),provenFresh:true)==LidAction.None,
            "31: missing log opening was fabricated at close3");
        check(recorded.Handle(true,true,4,1,t.AddMilliseconds(5900),provenFresh:true)==LidAction.Open,"31: log open4 delayed");

        string raw = "2026-09-07 23:04:15.013 rx seq=17 peer=2 modelCode=5152 lidByte=81 shape=73 explicit=0 open=1 cycle=1 rssi=-65 ageMs=2 callbackMs=0 addr=ABCDEF123456 hex=071901Secret";
        string clean = DiagnosticSanitizer.Sanitize(raw)!;
        check(clean.Contains("peer=2") && clean.Contains("shape=73") && clean.Contains("lidByte=81"), "31: receive evidence removed from safe export");
        check(!clean.Contains("ABCDEF") && !clean.Contains("071901Secret"), "31: receive evidence leaked identifiers");
        check(DiagnosticSanitizer.Sanitize("2026-09-07 23:04:15.014 case seq=17 block=proof believable=0 action=None")!.Contains("block=proof"), "31: decision cause absent from export");
        Console.WriteLine("DeskPods .31 receive/reset/privacy regressions completed (overall runner determines PASS; synthetic radio; real C# sources)");
    }
}
