using PodsView;

internal static class Release29Regressions
{
    internal static void Run(Action<bool,string> check)
    {
        var t = new DateTimeOffset(2026,9,7,12,0,0,TimeSpan.Zero);
        LidStateMachine ClosedPair()
        {
            var m = new LidStateMachine();
            check(m.Handle(true,true,1,1,t,provenFresh:true) == LidAction.Open,"Setup did not open");
            check(m.Handle(true,false,1,1,t.AddSeconds(1),provenFresh:true) == LidAction.Close,"Setup did not close");
            return m;
        }
        foreach (int hours in new[] { 3, 6, 24 })
        {
            var m = ClosedPair();
            check(m.Handle(true,true,1,1,t.AddHours(hours),provenFresh:true) == LidAction.Open,
                "First same-counter packet after hours waited for a second packet");
            check(m.Handle(true,false,1,1,t.AddHours(hours).AddMilliseconds(20),provenFresh:true) == LidAction.Close,
                "Confirmed idle reopening delayed a real close");
        }
        var firstAfterIdle = new LidStateMachine();
        firstAfterIdle.BeginListening(t,t);
        check(firstAfterIdle.Handle(true,false,1,1,t.AddHours(3),provenFresh:true) == LidAction.Open,
            "Familiar silent case failed to wake on the first packet after 3 hours of listening");
        var cold = new LidStateMachine();
        check(cold.Handle(true,false,1,1,t.AddHours(3),provenFresh:true) == LidAction.None,
            "Unknown first observation invented earlier silence");
        var future = new LidStateMachine(); future.BeginListening(t.AddHours(4),t);
        check(future.Handle(true,false,1,1,t.AddHours(3),provenFresh:true) == LidAction.None,"Future listen epoch was trusted");
        var shortEcho = ClosedPair();
        check(shortEcho.Handle(true,true,1,1,t.AddSeconds(30),provenFresh:true) == LidAction.None,"Short echo bypassed its guard");
        var boundary = ClosedPair();
        check(boundary.Handle(true,true,1,1,t.AddSeconds(121),provenFresh:true) == LidAction.None,"Exactly 120 seconds bypassed the idle boundary");
        var stale = ClosedPair();
        check(stale.Handle(true,true,1,1,t.AddHours(3),provenFresh:false) == LidAction.None,"Unknown timestamp used idle fast path");
        var rejected = ClosedPair();
        check(rejected.Handle(true,true,1,1,t.AddHours(3),trusted:false,provenFresh:true) == LidAction.None,"Backlog used idle path");
        check(rejected.Handle(true,true,1,1,t.AddHours(3),bound:false,provenFresh:true) == LidAction.None,"Neighbour used idle path");
        check(rejected.Handle(false,true,1,1,t.AddHours(3),provenFresh:true) == LidAction.None,"Earbud copy used idle path");
        var dismissed = new LidStateMachine();
        dismissed.Handle(true,true,1,1,t,provenFresh:true); dismissed.ForceClosed(t.AddSeconds(1));
        check(dismissed.Handle(true,true,1,1,t.AddHours(3),provenFresh:true) == LidAction.None,"Idle reopened a manually dismissed cycle");
        var dismissedWake = new LidStateMachine();
        dismissedWake.Handle(true,true,1,1,t,provenFresh:true); dismissedWake.ForceClosed(t.AddSeconds(1));
        check(dismissedWake.Handle(true,false,1,1,t.AddHours(3),provenFresh:true) == LidAction.None,"Closed-word wake bypassed manual dismissal");

        byte[] bytes = Convert.FromHexString("071901142070A93631000045121212".PadRight(54,'0'));
        check(AirPodsAdvertisementParser.TryParse(bytes,out var parsed) && parsed!.ModelCode == 0x1420,"Raw model code was discarded");
        check(DeviceCatalog.DisplayName(parsed!) == "AirPods Pro 2","Model generation was erased");
        check(DeviceCatalog.Key(parsed!) == "AirPods Pro (2-ге покоління)/00","Existing signal-memory key changed");
        var row = new RadioObservation(parsed!,1,-65,"",t,1);
        string key = DeviceCatalog.Key(parsed!);
        check(StartupEligibility.CanProcess(row,key),"Remembered nearby pair waited for discovery");
        check(!StartupEligibility.CanProcess(row,""),"Unidentified weak pair bypassed discovery");
        check(StartupEligibility.CanProcess(row with { Rssi = -50 },""),"Already eligible near case blocked by discovery");
        check(!StartupEligibility.CanProcess(row with { RadioAgeMs = 2001 },key),"Stale startup fast path");
        check(!StartupEligibility.CanProcess(row with { RadioAgeMs = -1 },key),"Unknown age startup fast path");
        check(!StartupEligibility.CanProcess(row with { Rssi = -127 },key),"No RSSI startup fast path");
        var buffer = new StartupObservationBuffer(); buffer.Add(row); buffer.Remove(row.Address);
        check(buffer.Drain(t.AddMilliseconds(5)).Length == 0,"Fast processed observation replayed at discovery completion");
        bytes[3] = 0xFE; bytes[4] = 0x20;
        check(AirPodsAdvertisementParser.TryParse(bytes,out var unknown),"Unknown model packet should retain diagnostics");
        check(DeviceCatalog.DisplayName(unknown!).Contains("FE20"),"Unknown model disguised as a known generation");
        check(!DeviceCatalog.HasLidProtocol(0xFE20) && !DeviceCatalog.HasLidProtocol(0x0A20),"Unknown/Max drives case popup");
        foreach (ushort code in new ushort[]{0x0220,0x0F20,0x1320,0x0E20,0x1420,0x0A20,0x0B20,0x0520,0x1020,0x1120,0x0620,0x0920,0x0320,0x0C20})
            check(DeviceCatalog.IsKnown(code) && DeviceCatalog.DisplayName(code).Length > 0,"Missing existing model");

        check(SupportLink.Normalize("https://ko-fi.com/example") == "https://ko-fi.com/example","Valid Ko-fi URL rejected");
        foreach (string url in new[]{"", "http://ko-fi.com/example","https://ko-fi.com.evil.test/example","file:///C:/test","https://other:secret@ko-fi.com/example","https://ko-fi.com/example?redirect=bad","https://ko-fi.com/"})
            check(SupportLink.Normalize(url) is null,"Unsafe support URL accepted");
        string? clean = DiagnosticSanitizer.Sanitize("2026-09-07 12:00:00.001 air addr=ABCDEF123456 key=MyPods id=24FA93 hex=07190114 src=known ageMs=2 rssi=-55");
        check(clean is not null && clean.Contains("src=known") && clean.Contains("ageMs=2"),"Safe timing trace lost useful fields");
        check(!clean!.Contains("ABCDEF") && !clean.Contains("24FA93") && !clean.Contains("MyPods") && !clean.Contains("07190114"),"Diagnostics exposed identifiers");
        check(DiagnosticSanitizer.Sanitize("2026-09-07 12:00:00.001 [ERROR] C:\\Users\\PrivateName") is null,"Export contains free-form private errors");
        check(DiagnosticSanitizer.Sanitize("2026-09-07 12:00:00.001 show reason=private_name") == "2026-09-07 12:00:00.001 show","Unknown reason leaked");
        var preferences = new AppSettings(); preferences.RememberModel(0x1420,key); preferences.ApplyPreferences(new AppSettings());
        check(preferences.LastModelCode == 0x1420 && preferences.LastModelDeviceKey == key,"Dialog reset live identity metadata");
    }
}
