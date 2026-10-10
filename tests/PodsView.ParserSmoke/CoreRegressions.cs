using PodsView;

internal static class CoreRegressions
{
    internal static void Run(Action<bool, string> check)
    {
        var origin = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var data = new ParsedAirPodsData("AirPods Pro", 80, 70, 40, false, false, true, 1, true);
        var cache = new EarbudCache();
        _ = cache.Merge("pro/00", data, origin);
        for (int minute = 1; minute <= 60; minute++)
        {
            ParsedAirPodsData merged = cache.Merge("pro/00", data with { LeftBattery = null, RightBattery = 50 }, origin.AddMinutes(minute));
            check(minute <= 10 ? merged.LeftBattery == 80 : merged.LeftBattery is null, $"Other channel renewed left TTL at minute {minute}");
            check(merged.RightBattery == 50 && !merged.RightCached, "Current right value must be retained");
        }
        var leftOnly = new EarbudCache();
        _ = leftOnly.Merge("a", data with { LeftBattery = null, RightCharging = true }, origin);
        ParsedAirPodsData retained = leftOnly.Merge("a", data with { RightBattery = null }, origin.AddSeconds(1));
        check(retained.RightBattery == 70 && retained.RightCached && !retained.RightCharging, "Cached charging is not live charging");
        check(leftOnly.Merge("b", data with { RightBattery = null }, origin.AddSeconds(2)).RightBattery is null, "Different device must not inherit cache");

        var state = new BatteryMemory();
        state.Seed(40, "pro/00");
        check(state.Data?.CaseBattery == 40 && state.Data.CaseCached && !state.Data.CaseCharging, "Startup/manual case restore");
        var packet = new AirPodsPacket(data, origin, -55, 1, "", DeviceKey: "pro/00");
        check(!state.Apply(packet with { Bound = false }), "Unbound packet accepted");
        check(!state.Apply(packet with { Trusted = false }), "Queued packet accepted");
        check(state.LastCase == 40 && state.Packet is null, "Rejected packet mutated state");
        check(state.Apply(packet) && state.Data?.CaseCharging == true, "Live case should be charging");
        check(state.Apply(packet with { Data = data with { CaseBattery = null }, ReceivedAt = origin.AddSeconds(1) }), "Valid null-case packet rejected");
        check(state.Data?.CaseBattery == 40 && state.Data.CaseCached && !state.Data.CaseCharging, "Missing case must become remembered, not charging");
        check(state.Packet is not null, "First burst must survive without a window");
        check(!state.Apply(packet), "Out-of-order packet replaced newer state");
        state.Apply(packet with { Data = data with { CaseBattery = null }, DeviceKey = "different/00", ReceivedAt = origin.AddSeconds(2) });
        check(state.LastCase is null && state.Data?.CaseBattery is null, "Switching devices retained the previous case charge");

        // 0.8.46: one earbud in the ear, the other in the shut case. The silent one keeps
        // its last live charge, marked as last known and never as charging.
        var pods = new BatteryMemory();
        var live = new AirPodsPacket(data with { RightCharging = true }, origin, -55, 1, "", DeviceKey: "pro/00");
        check(pods.Apply(live) && pods.LastRight == 70, "Live right charge not remembered");
        check(pods.Apply(live with { Data = data with { RightBattery = null }, ReceivedAt = origin.AddHours(3) }), "Right-silent packet rejected");
        check(pods.Data?.RightBattery == 70 && pods.Data.RightCached && !pods.Data.RightCharging, "Silent earbud lost its last charge or looks live");
        check(pods.Data?.LeftBattery == 80 && !pods.Data.LeftCached, "Live earbud marked as remembered");
        pods.Apply(live with { Data = data with { RightBattery = null }, DeviceKey = "other/00", ReceivedAt = origin.AddHours(4) });
        check(pods.Data?.RightBattery is null, "Another pair inherited the earbud charge");
        var restoredPods = new BatteryMemory();
        restoredPods.Seed(40, "pro/00", 60, 30, "pro/00");
        check(restoredPods.Data is { LeftBattery: 60, RightBattery: 30, LeftCached: true, RightCached: true }, "Earbud charges not restored at start");
        var foreign = new BatteryMemory();
        foreign.Seed(40, "pro/00", 60, 30, "other/00");
        check(foreign.LastLeft is null && foreign.LastRight is null, "Earbud charges of another pair restored");

        // 0.8.46: the updater only offers a newer, final release and picks the versioned setup.
        static System.Text.Json.JsonElement Release(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
        const string assets = "\"assets\":[{\"name\":\"DeskPods_Setup.exe\",\"browser_download_url\":\"https://github.com/Stasieps/DeskPods/releases/download/v0.8.46/DeskPods_Setup.exe\"},{\"name\":\"DeskPods_Setup_v0.8.46.exe\",\"browser_download_url\":\"https://github.com/Stasieps/DeskPods/releases/download/v0.8.46/DeskPods_Setup_v0.8.46.exe\"}]";
        UpdateInfo? newer = UpdateRelease.Pick(Release("{\"tag_name\":\"v0.8.46\"," + assets + "}"), "0.8.45");
        check(newer?.Display == "0.8.46" && newer.InstallerUrl!.EndsWith("DeskPods_Setup_v0.8.46.exe"), "Newer release or its versioned setup missed");
        check(UpdateRelease.Pick(Release("{\"tag_name\":\"v0.8.46\"," + assets + "}"), "0.8.46") is null, "Current release offered as an update");
        check(UpdateRelease.Pick(Release("{\"tag_name\":\"v0.8.40\"," + assets + "}"), "0.8.46") is null, "Older release offered as an update");
        check(UpdateRelease.Pick(Release("{\"tag_name\":\"v0.9.0\",\"prerelease\":true}"), "0.8.46") is null, "Pre-release offered as an update");
        check(UpdateRelease.Pick(Release("{\"tag_name\":\"v0.9.0\",\"assets\":[{\"name\":\"DeskPods_Setup_v0.9.0.exe\",\"browser_download_url\":\"http://evil.example/x.exe\"}]}"), "0.8.46")?.InstallerUrl is null, "Untrusted download accepted");
        check(UpdateRelease.TryParseVersion("v0.8.10", out Version ten) && UpdateRelease.IsNewer(ten, new Version(0, 8, 9)), "0.8.10 must be newer than 0.8.9");

        // 0.8.47: the setup has to match the SHA-256 GitHub publishes with the asset.
        check(newer?.InstallerSha256 is null, "A release without digests invented a SHA-256");
        byte[] setupBytes = System.Text.Encoding.ASCII.GetBytes("MZ DeskPods setup");
        byte[] setupHash = System.Security.Cryptography.SHA256.HashData(setupBytes);
        string setupHex = Convert.ToHexString(setupHash).ToLowerInvariant();
        string otherHex = new string('7', 64);
        string Asset(string name, string hex) => "{\"name\":\"" + name + "\",\"browser_download_url\":\"https://github.com/Stasieps/DeskPods/releases/download/v0.8.47/" + name + "\",\"digest\":\"sha256:" + hex + "\"}";
        UpdateInfo? hashed = UpdateRelease.Pick(Release("{\"tag_name\":\"v0.8.47\",\"assets\":[" + Asset("DeskPods_Setup.exe", otherHex) + "," + Asset("DeskPods_Setup_v0.8.47.exe", setupHex.ToUpperInvariant()) + "]}"), "0.8.46");
        check(hashed?.InstallerUrl?.EndsWith("DeskPods_Setup_v0.8.47.exe") == true && hashed.InstallerSha256 == setupHex, "The versioned setup did not bring its own SHA-256");
        UpdateInfo? fallback = UpdateRelease.Pick(Release("{\"tag_name\":\"v0.8.47\",\"assets\":[" + Asset("DeskPods_Setup.exe", otherHex) + "]}"), "0.8.46");
        check(fallback?.InstallerUrl?.EndsWith("/DeskPods_Setup.exe") == true && fallback.InstallerSha256 == otherHex, "The fallback setup did not bring its own SHA-256");
        check(UpdateRelease.Sha256Hex(null) is null && UpdateRelease.Sha256Hex("") is null && UpdateRelease.Sha256Hex("sha512:" + setupHex) is null
              && UpdateRelease.Sha256Hex("sha256:" + setupHex[..63]) is null && UpdateRelease.Sha256Hex("sha256:" + setupHex[..63] + "g") is null, "A malformed digest was accepted");
        check(UpdateRelease.Sha256Hex("SHA256:" + setupHex.ToUpperInvariant()) == setupHex, "A well-formed digest was refused");
        check(UpdateRelease.DigestAccepts(setupHex, setupHash) && UpdateRelease.DigestAccepts(setupHex.ToUpperInvariant(), setupHash), "A setup matching its SHA-256 was refused");
        check(!UpdateRelease.DigestAccepts(otherHex, setupHash), "A setup that does not match its SHA-256 was accepted");
        check(UpdateRelease.DigestAccepts(null, setupHash), "A release without a digest can no longer update");

        var settings = new AppSettings { LastCaseBattery = 40, CaseShapes = "49", TrayHintShown = false };
        AppSettings dialog = settings.Clone();
        settings.RememberCase(50, "pro/00");
        settings.RememberShapes("pro/00", "49,39");
        settings.TrayHintShown = true;
        dialog.Language = "en";
        settings.ApplyPreferences(dialog);
        check(settings.LastCaseBattery == 50 && settings.CaseShapes == "49,39" && settings.TrayHintShown, "Preferences rolled back telemetry");
        check(settings.Language == "en", "User preference not applied");
        check(settings.ReadCaseShapes("another/00") == "", "Learned signatures leaked to another selected model/color");
        check(AppSettings.ValidBattery(-1) is null && AppSettings.ValidBattery(110) is null && AppSettings.ValidBattery(35) is null, "Corrupt remembered reading accepted");
        string folder = Path.Combine(Path.GetTempPath(), "PodsView-tests-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "settings.json");
        try
        {
            Task<bool>[] saves = Enumerable.Range(0, 24).Select(_ => Task.Run(() => settings.Save(path))).ToArray();
            Task.WaitAll(saves);
            check(saves.All(s => s.Result), "Concurrent settings save failed");
            AppSettings saved = AppSettings.Load(path);
            check(saved.LastCaseBattery == 50 && saved.CaseShapes == "49,39" && saved.Language == "en", "Saved snapshot is corrupt or stale");
            check(Directory.GetFiles(folder, "*.tmp").Length == 0, "Temporary settings files leaked");
            File.WriteAllText(path, "{\"SettingsRevision\":8,\"Theme\":\"mono\",\"LastCaseBattery\":40}");
            AppSettings migrated = AppSettings.Load(path);
            check(migrated.Theme == "refined" && migrated.PreviousTheme == "mono" && migrated.LastCaseBattery == 40, "Design migration changed telemetry or lost Classic");
            migrated.Theme = "mono";
            check(migrated.Save(path) && AppSettings.Load(path).Theme == "mono", "Classic choice did not survive restart");
            check(AppSettings.ThemeIds.Contains("refined") && AppSettings.ThemeIds.Contains("mono"), "Refined or Classic missing");
            // 0.8.47: TERMINAL, CARBON and NEON are gone. A saved choice of one of them lands
            // on Refined instead of a missing palette; E-INK, the paper theme, stays.
            foreach (string removed in new[] { "terminal", "carbon", "neon" })
            {
                check(!AppSettings.ThemeIds.Contains(removed), $"Removed theme {removed} is still offered");
                File.WriteAllText(path, "{\"SettingsRevision\":9,\"Theme\":\"" + removed + "\",\"PreviousTheme\":\"" + removed + "\"}");
                AppSettings landed = AppSettings.Load(path);
                check(landed.Theme == "refined" && landed.PreviousTheme == "refined", $"A saved {removed} theme did not fall back to Refined");
            }
            File.WriteAllText(path, "{\"SettingsRevision\":9,\"Theme\":\"eink\"}");
            check(AppSettings.ThemeIds.Contains("eink") && AppSettings.Load(path).Theme == "eink", "The E-INK theme did not survive");
            check(AppSettings.ThemeIds.Length == 5, "0.8.47 offers exactly five themes");
            File.WriteAllText(path, "{ broken json");
            check(AppSettings.Load(path).Theme == "refined", "Corrupt JSON must safely fall back");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }

        foreach (string name in new[] { "Powerbeats Pro", "Beats Solo Pro", "Beats Studio Buds" })
            check(AirPodsAdvertisementParser.FamilyForModel(name) == "Beats", $"Wrong family for {name}");
        check(AirPodsAdvertisementParser.FamilyForModel("AirPods Pro (2nd generation)") == "AirPods Pro", "AirPods Pro family regressed");
        for (int raw = 0; raw <= 100; raw += 10)
        {
            check(Math.Abs(BatteryFormat.Bar(raw) * 100 - BatteryFormat.Value(raw)) < 1e-9, "Bar and number disagree");
            for (int threshold = 10; threshold <= 50; threshold += 5)
            {
                check(BatteryFormat.ShouldAlert(raw, false, false, threshold) == (BatteryFormat.Value(raw) <= threshold), "Alert differs from visible value");
                check(!BatteryFormat.ShouldAlert(raw, true, false, threshold), "Charging value caused low alert");
                check(!BatteryFormat.ShouldAlert(raw, false, true, threshold), "Cached value caused low alert");
            }
        }
        check(!BatteryFormat.ShouldAlert(null, false, false, 20), "Unknown value caused low alert");

        var signal = new LidSignal();
        signal.Believe(1, 0x24FA93, 0x31, true, origin, out _);
        string exported = signal.ExportState();
        var restored = new LidSignal();
        restored.Seed(exported, origin.AddDays(6));
        check(restored.Believe(2, 0x24FA93, 0x51, false, origin.AddDays(6), out string why) && why == "known", "Valid dated shape lost after restart");
        var expired = new LidSignal();
        expired.Seed(exported, origin.AddDays(8));
        check(!expired.Believe(3, 0x24FA93, 0x51, false, origin.AddDays(8), out _), "Restart renewed an expired shape");
        var unknown = new LidSignal();
        unknown.Believe(7, 0x02F58F, 0x11, false, origin, out _);
        check(!unknown.Believe(7, 0x02F58F, 0x19, false, origin.AddMinutes(6), out string oldWhy) && oldWhy == "baseline", "Per-address TTL must apply even to one dictionary entry");
        signal.Reset();
        check(signal.Believe(9, 0x24FA93, 0x51, false, origin.AddSeconds(1), out _), "Radio reset forgot the learned shape");

        check(ProductMotion.FrameAt(0) == 30 && ProductMotion.FrameAt(4.5) == 60 && ProductMotion.FrameAt(13.5) == 0, "Motion extrema/center do not match atlas");
        int previous = ProductMotion.FrameAt(0);
        for (int frame = 1; frame <= 1080; frame++)
        {
            double seconds = frame / 60.0;
            int current = ProductMotion.FrameAt(seconds);
            check(current >= 0 && current < ProductMotion.FrameCount && Math.Abs(current - previous) <= 1, "Animation jumps or indexes past the atlas");
            previous = current;
        }
        check(ProductMotion.FrameAt(18) == ProductMotion.FrameAt(0), "Animation loop seam");
        check(ProductMotion.FrameAt(double.NaN) == 30, "Invalid time must keep a safe still frame");
    }
}

namespace PodsView
{
    // Only the logging sink is stubbed: the tested settings/cache/parser/lid code is linked from src.
    internal static class Logger
    {
        internal static void Error(string message, Exception error) { }
    }
}
