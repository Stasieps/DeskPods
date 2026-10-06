using PodsView;
internal static class Release33Regressions
{
    internal static void Run(Action<bool,string> check)
    {
        byte[] good = new byte[27];good[0]=7;good[1]=25;good[2]=1;good[3]=0x14;good[4]=0x20;
        check(AirPodsAdvertisementParser.TryParse(good,out _),"33 baseline parser setup");
        foreach (int length in Enumerable.Range(0,41))
        {
            var bytes=new byte[length];Array.Copy(good,bytes,Math.Min(length,good.Length));
            var before=bytes.ToArray();bool accepted=AirPodsAdvertisementParser.TryParse(bytes,out _);
            string header=AdvertisementDiagnostics.Header(bytes);string reason=AdvertisementDiagnostics.FailureReason(bytes);
            check(header.Contains("byteCount="+length),"33 framing length missing");
            check(bytes.SequenceEqual(before),"33 diagnostics mutated packet");
            check(AirPodsAdvertisementParser.TryParse(bytes,out _)==accepted,"33 diagnostics changed acceptance");
            if(length<27)check(reason=="short-payload","33 short parser failure");
        }
        for(int kind=0;kind<256;kind++)
        {
            var bytes=good.ToArray();bytes[0]=(byte)kind;
            string header=AdvertisementDiagnostics.Header(bytes);
            check(AirPodsAdvertisementParser.TryParse(bytes,out _)==(kind==7),"33 changed message-type acceptance");
            if(kind!=7)check(header.Contains("mode=-1") && AdvertisementDiagnostics.FailureReason(bytes)=="not-proximity","33 unknown-message privacy");
        }
        for(int mode=0;mode<256;mode++)
        {
            var bytes=good.ToArray();bytes[2]=(byte)mode;
            check(AirPodsAdvertisementParser.TryParse(bytes,out _)==(mode==1),"33 unsupported mode accepted");
            if(mode!=1)check(AdvertisementDiagnostics.FailureReason(bytes)=="unsupported-mode","33 mode rejection missing");
        }
        var prefixed=new byte[29];prefixed[0]=0x4C;good.CopyTo(prefixed,2);
        check(AirPodsAdvertisementParser.TryParse(prefixed,out _) && AdvertisementDiagnostics.Header(prefixed).Contains("companyPrefix=1"),"33 optional company prefix");
        var wrong=good.ToArray();wrong[1]=24;
        check(!AirPodsAdvertisementParser.TryParse(wrong,out _) && AdvertisementDiagnostics.FailureReason(wrong)=="wrong-length","33 declared length handling");
        var combined=new byte[31];combined[0]=0x10;combined[1]=2;combined[2]=1;combined[3]=0xAA;good.CopyTo(combined,4);
        check(!AirPodsAdvertisementParser.TryParse(combined,out _),"33 nested message unexpectedly changed parser behavior");
        check(AdvertisementDiagnostics.Header(combined).Contains("proximityOffset=4") && AdvertisementDiagnostics.Header(combined).Contains("framingComplete=1"),"33 framed nested proximity not visible in diagnostics");
        string stamp="2026-09-08 12:00:00.001 ";
        foreach(string eventName in new[]{"tick","heartbeat","watchdog","adv","parse","presentation","capture"})
            check(DiagnosticSanitizer.Sanitize(stamp+eventName+" seq=9 peer=2") is not null,"33 exported event missing "+eventName);
        string line=DiagnosticSanitizer.Sanitize(stamp+"parse seq=9 peer=2 parsed=0 byteCount=27 messageType=7 declaredLength=25 mode=0 companyPrefix=0 reason=unsupported-mode addr=ABCDEF123456 hex=SecretPayload alias=PrivateName")!;
        check(line.Contains("mode=0") && line.Contains("reason=unsupported-mode") && !line.Contains("ABCDEF") && !line.Contains("Secret") && !line.Contains("Private"),"33 framing export privacy");
        check(DiagnosticSanitizer.Sanitize(stamp+"presentation phase=hide-return op=7 superseded=0 popup=0 nativeVisible=0 opacityPct=0")!.Contains("nativeVisible=0"),"33 actual hidden state lost");
        check(DiagnosticSanitizer.Sanitize(stamp+"watchdog phase=end tickId=12 callbacks=30 apple=20 parsed=15 rejected=5 timeoutDue=1")!.Contains("tickId=12"),"33 watchdog export missing");
        check(DiagnosticSanitizer.Sanitize(stamp+"capture dropped=42")!.Contains("dropped=42"),"33 dropped log evidence lost");
        Console.WriteLine("DeskPods .33 diagnostic regressions completed (real C# helper; synthetic input)");
    }
}
