using PodsView;

internal static class PopupLatencyRegressions
{
    internal static void Run(Action<bool,string> check)
    {
        var t = new DateTimeOffset(2026,9,6,0,0,0,TimeSpan.Zero);
        var data = new ParsedAirPodsData("AirPods Pro",80,80,50,false,false,false,1,true,
            LidByte:0x31,CarriesLidState:true,Identity:0x24FA95);
        RadioObservation Row(ulong address, double ms, bool open = true, int age = 1) =>
            new(data with { IsCaseOpen = open, LidByte = (byte)(open ? 0x31 : 0x39) },address,-65,"",t.AddMilliseconds(ms),age);
        var buffer = new StartupObservationBuffer();
        buffer.Add(Row(1,10));
        var ready = buffer.Drain(t.AddMilliseconds(200));
        check(ready.Length == 1 && ready[0].Data.IsCaseOpen, "First startup opening was lost during device discovery");
        check(buffer.Count == 0, "Startup observations replayed twice");
        var signal = new LidSignal();
        var machine = new LidStateMachine();
        var first = ready[0];
        bool eligible = PacketFilter.Allow(true,true,first.Rssi,true,false,false);
        bool believable = signal.Believe(first.Address,first.Data.Identity,first.Data.LidByte,first.Data.CarriesLidState,first.ReceivedAt,out _);
        check(eligible && machine.Handle(believable,true,1,first.Address,first.ReceivedAt,provenFresh:true) == LidAction.Open,
            "Buffered paired case did not open on its first eligible fresh packet");
        buffer.Add(Row(1,10));buffer.Add(Row(1,40,false));
        ready = buffer.Drain(t.AddMilliseconds(200));
        check(ready.Length == 1 && !ready[0].Data.IsCaseOpen, "A superseded startup opening may flash after close");
        buffer.Add(Row(1,10));
        check(buffer.Drain(t.AddMilliseconds(2200)).Length == 0, "Stale startup packet replayed");
        buffer.Add(Row(1,100,age:1900));
        check(buffer.Drain(t.AddMilliseconds(300)).Length == 0, "Radio age ignored during startup buffering");
        buffer.Add(Row(1,300));
        check(buffer.Drain(t.AddMilliseconds(100)).Length == 0, "Future startup observation accepted");
        for(ulong i=0;i<100;i++) buffer.Add(Row(i,100+i));
        check(buffer.Count == StartupObservationBuffer.Capacity, "Startup buffer is unbounded");
        buffer.Clear();check(buffer.Count == 0, "Stop/restart retained startup observations");
        check(!PacketFilter.Allow(true,true,-65,false,false,false), "Startup buffer bypasses pairing/RSSI filtering");
        var unknown = new LidSignal();
        check(!unknown.Believe(2,0x02F58F,0x11,false,t,out _), "Unknown baseline was incorrectly trusted for speed");

        // Buffering must retain classification proof, but emit only the latest state.
        var nonexplicit = data with { CarriesLidState = false, LidByte = 0x19, IsCaseOpen = false };
        buffer.Add(new(nonexplicit,7,-65,"",t.AddMilliseconds(10),1));
        buffer.Add(new(nonexplicit with { LidByte = 0x11, IsCaseOpen = true },7,-65,"",t.AddMilliseconds(50),1));
        var latest = buffer.Drain(t.AddMilliseconds(100)).Single();
        check(latest.Previous is not null, "Startup coalescing erased a nonexplicit lid transition");
        var transitionSignal = new LidSignal();
        var prior = latest.Previous!;
        transitionSignal.Believe(prior.Address,prior.Data.Identity,prior.Data.LidByte,false,prior.ReceivedAt,out _);
        check(transitionSignal.Believe(latest.Address,latest.Data.Identity,latest.Data.LidByte,false,latest.ReceivedAt,out _),
            "First nonexplicit transition was lost during startup discovery");
        var legacySignal = new LidSignal();
        legacySignal.Seed("49",latest.ReceivedAt);
        check(legacySignal.Believe(latest.Address,latest.Data.Identity,latest.Data.LidByte,false,latest.ReceivedAt,out _),
            "Legacy signal memory appears newer than the first observation");
        buffer.Add(new(nonexplicit,7,-65,"",t,1));
        buffer.Add(new(nonexplicit with { LidByte = 0x11, IsCaseOpen = true },7,-65,"",t.AddMilliseconds(1900),1));
        check(buffer.Drain(t.AddMilliseconds(2100)).Single().Previous is null,"Expired startup proof was retained");

        var resume = new LidStateMachine();
        check(resume.Handle(true,true,1,1,t,provenFresh:true) == LidAction.Open,"Resume fixture did not open");
        resume.SuspendForSilence(t.AddSeconds(6));
        // 0.8.47: no pop-back. A cycle that silence took off the screen is spent; its lid is
        // very probably still open, so even a fresh word of it may not raise the card again.
        check(resume.Handle(true,true,1,1,t.AddSeconds(30),provenFresh:true) == LidAction.None,
            "A silence-hidden cycle re-popped (0.8.47: no pop-back)");
        resume.ForceClosed(t.AddSeconds(31));
        check(resume.Handle(true,true,1,1,t.AddSeconds(32),provenFresh:true) == LidAction.None,
            "Fresh resume change bypassed manual dismissal");
        var uncertain = new LidStateMachine();
        uncertain.Handle(true,true,1,1,t,provenFresh:true);uncertain.SuspendForSilence(t.AddSeconds(6));
        check(uncertain.Handle(true,true,1,1,t.AddSeconds(30)) == LidAction.None,"Unproven resume bypassed burst proof");

        var wake = new LidStateMachine();
        // 0.8.47: the case's first word since start breaks an unknown silence, so it is a wake.
        check(wake.Handle(true,false,1,1,t,provenFresh:true)==LidAction.Open,"First word since start did not wake (wake)");
        check(!wake.ShouldTimeout(t.AddSeconds(6)) && wake.ShouldTimeout(t.AddSeconds(6.1)),"Unconfirmed wake ignored the 6 s quiet (wake)");
        wake.EndUnconfirmedWake(t.AddSeconds(6.1));
        check(wake.Handle(true,false,1,1,t.AddSeconds(30),provenFresh:true)==LidAction.Open,"Wake fixture did not open");
        wake.Handle(true,true,2,1,t.AddSeconds(30.1),provenFresh:true);
        check(!wake.HoldsClosedWord(t.AddSeconds(30.2)),"Confirmed opening retained the wake floor");
        check(wake.Handle(true,false,2,1,t.AddSeconds(30.2),provenFresh:true)==LidAction.Close,
            "Confirmed real close waited for the 1500 ms wake floor");
        var changedClose = new LidStateMachine();
        // 0.8.47: the case's first word since start breaks an unknown silence, so it is a wake.
        check(changedClose.Handle(true,false,1,1,t,provenFresh:true)==LidAction.Open,"First word since start did not wake (changedClose)");
        check(!changedClose.ShouldTimeout(t.AddSeconds(6)) && changedClose.ShouldTimeout(t.AddSeconds(6.1)),"Unconfirmed wake ignored the 6 s quiet (changedClose)");
        changedClose.EndUnconfirmedWake(t.AddSeconds(6.1));
        changedClose.Handle(true,false,1,1,t.AddSeconds(30),provenFresh:true);
        check(changedClose.Handle(true,false,2,1,t.AddSeconds(30.2),provenFresh:true)==LidAction.Close,
            "Different-cycle close was hidden by the provisional wake floor");
        var repeats = new LidStateMachine();
        // 0.8.47: the case's first word since start breaks an unknown silence, so it is a wake.
        check(repeats.Handle(true,false,1,1,t,provenFresh:true)==LidAction.Open,"First word since start did not wake (repeats)");
        check(!repeats.ShouldTimeout(t.AddSeconds(6)) && repeats.ShouldTimeout(t.AddSeconds(6.1)),"Unconfirmed wake ignored the 6 s quiet (repeats)");
        repeats.EndUnconfirmedWake(t.AddSeconds(6.1));
        repeats.Handle(true,false,1,1,t.AddSeconds(30),provenFresh:true);
        check(repeats.Handle(true,false,1,1,t.AddSeconds(30.2),provenFresh:true)==LidAction.Update,
            "Repeated stale wake snapshot prematurely hid the popup");
        // 0.8.47: the case's own first word wakes by design (its silence before that is
        // unknown). A neighbour may neither raise the popup nor touch that silence clock.
        var neighbour = new LidStateMachine();
        check(neighbour.Handle(true,false,1,2,t,bound:false,provenFresh:true)==LidAction.None && !neighbour.IsOpen,
            "A neighbour manufactured startup silence and a false wake");
        check(neighbour.Handle(true,false,1,1,t.AddSeconds(30),provenFresh:true)==LidAction.Open && neighbour.LastSilenceMs < 0,
            "The case's own first word did not wake, or a neighbour started its silence clock");
        neighbour.EndUnconfirmedWake(t.AddSeconds(36.1));
        for (int i = 1; i <= 20; i++)
            check(neighbour.Handle(true,i % 2 == 0,3,2,t.AddSeconds(36 + i * 0.5),bound:false,provenFresh:true)==LidAction.None && !neighbour.IsOpen,
                $"Neighbour word {i} touched the popup");
        check(neighbour.Handle(true,false,1,1,t.AddSeconds(46),provenFresh:true)==LidAction.Open && neighbour.LastSilenceMs == 16000,
            "A neighbour's chatter shortened the case's own silence");
    }
}
