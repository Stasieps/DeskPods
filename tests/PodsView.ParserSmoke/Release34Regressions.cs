using PodsView;
using System.Text.Json;

internal static class Release34Regressions
{
    internal static void Run(Action<bool,string> check)
    {
        var t = new DateTimeOffset(2026,9,8,0,0,0,TimeSpan.Zero);
        ParsedAirPodsData Packet(int shape, byte word)
        {
            uint id = (uint)((((shape >> 4) | 0x40) << 16) | 0xFA00 | ((shape & 15) << 4) | 3);
            byte[] b = new byte[27]; b[0]=7; b[1]=25; b[2]=1; b[3]=0x14; b[4]=0x20;
            b[5]=(byte)(id>>16); b[6]=(byte)(id>>8); b[7]=(byte)id; b[8]=word;
            check(AirPodsAdvertisementParser.TryParse(b,out var d),"34: synthetic fixture parser");
            check(LidSignal.Shape(id)==shape,"34: fixture did not preserve safe-export shape");
            return d!;
        }
        bool Classify(LidSignal signal, ParsedAirPodsData d, ulong peer, DateTimeOffset at,
            bool trusted=true, bool bound=true, bool fresh=true, short rssi=-60) =>
            CaseSignalClassifier.Believe(signal,d,peer,at,trusted,bound,fresh,true,rssi,out _);
        for (int seed=0; seed<256; seed++)
        {
            var signal=new LidSignal(); signal.Seed(seed.ToString("X2"),t);
            var d=Packet(seed^4,0x51);
            // 0.8.41: synthetic packets carry bit 6 like real case packets, and inside the case every
            // charge variant of a learned signature is that case. The earbud outside the case is
            // refused by its status byte instead (ParserSmoke Program.cs, 0.8.41 block).
            check(Classify(signal,d,1,t.AddMilliseconds(10)),
                "34: a charge variant of a learned case signature was not believed");
        }
        foreach (int seed in new[]{0x39,0x3D,0x49,0x4D})
        {
            var d=Packet(seed^4,0x51);
            var stale=new LidSignal(); stale.Seed(seed.ToString("X2"),t);
            check(!Classify(stale,d,1,t.AddDays(8)),"34: expired charging counterpart revived");
            var future=new LidSignal(); future.Seed(seed.ToString("X2"),t.AddSeconds(1));
            check(!Classify(future,d,1,t),"34: future charging counterpart revived");
            foreach (var flags in new[]{(false,true),(true,false)})
            {
                var signal=new LidSignal();signal.Seed(seed.ToString("X2"),t);
                check(!Classify(signal,d,1,t,flags.Item1,flags.Item2),"34: equivalence bypassed trusted/bound");
            }
            var unknownRssi=new LidSignal();unknownRssi.Seed(seed.ToString("X2"),t);
            check(!Classify(unknownRssi,d,1,t,rssi:-127),"34: equivalence bypassed RSSI guard");
        }
        check(!Packet(0x49,0x51).CaseCharging && Packet(0x4D,0x51).CaseCharging,
            "34: 0x04 is not the parser's case-charging bit");
        foreach (int cycle in Enumerable.Range(0,8))
        {
            var lid=new LidStateMachine();lid.BeginListening(t,t);
            check(lid.Handle(false,true,cycle,4,t.AddSeconds(40),provenFresh:true)==LidAction.None,
                "34: unknown open was promoted");
            lid.Handle(false,true,0,1,t.AddSeconds(40.2),provenFresh:true); // interleaved static peer
            check(lid.Handle(true,false,cycle,4,t.AddSeconds(40.578),provenFresh:true)==LidAction.None,
                "34: observed close became a false wake");
            check(lid.LastObservedClosingEdge && !lid.IsOpen,"34: missing negative closing-edge evidence");
        }
        // TTL boundary, separate peers/cycles, no stale, backwards-clock or unbound evidence.
        foreach (double gap in new[]{1.5,1.501})
        {
            var lid=new LidStateMachine();lid.BeginListening(t,t);
            lid.Handle(false,true,1,4,t.AddSeconds(40),provenFresh:true);
            var action=lid.Handle(true,false,1,4,t.AddSeconds(40+gap),provenFresh:true);
            check((action==LidAction.None)==(gap<=1.5),"34: negative evidence TTL boundary changed");
        }
        foreach (var pair in new[]{(1,5UL),(2,4UL)})
        {
            var lid=new LidStateMachine();lid.BeginListening(t,t);
            lid.Handle(false,true,1,4,t.AddSeconds(40),provenFresh:true);
            check(lid.Handle(true,false,pair.Item1,pair.Item2,t.AddSeconds(40.5),provenFresh:true)==LidAction.Open,
                "34: one peer/cycle incorrectly vetoed another");
        }
        var reset=new LidStateMachine(); reset.BeginListening(t,t);
        reset.Handle(false,true,1,4,t.AddSeconds(40),provenFresh:true);
        reset.ResetForSystem(t.AddSeconds(40.1));reset.BeginListening(t,t.AddSeconds(40.1));
        check(reset.Handle(true,false,1,4,t.AddSeconds(40.5),provenFresh:true)==LidAction.Open,"34: negative evidence survived system reset");
        var edges=new RecentLidEdges();
        for(ulong peer=1;peer<=1000;peer++)
        {
            edges.Observe(true,1,peer,t.AddMilliseconds(peer),true,TimeSpan.FromMilliseconds(1500));
            check(edges.Count<=32,"34: negative-evidence memory unbounded");
        }
        edges.Reset();edges.Observe(true,1,4,t,false,TimeSpan.FromMilliseconds(1500));
        check(!edges.Observe(false,1,4,t.AddMilliseconds(10),true,TimeSpan.FromMilliseconds(1500)),"34: stale open counted");
        edges.Observe(true,1,4,t.AddSeconds(1),true,TimeSpan.FromMilliseconds(1500));
        check(!edges.Observe(false,1,4,t,true,TimeSpan.FromMilliseconds(1500)),"34: future open counted");

        // Actual exported sequence/timing/shape/lid words, not recovered private bytes.
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"replay-safe","22-charging-open-close.json")));
        foreach (bool learned in new[]{false,true})
        {
            var signal=new LidSignal();if(learned)signal.Seed("39,49",t);
            var lid=new LidStateMachine();lid.BeginListening(t,t);
            var actions=new Dictionary<int,LidAction>();int total=0;
            foreach(var r in doc.RootElement.GetProperty("rows").EnumerateArray())
            {
                var d=Packet(r.GetProperty("shape").GetInt32(),r.GetProperty("lidByte").GetByte());
                var at=t.AddMilliseconds(r.GetProperty("ms").GetDouble());var peer=r.GetProperty("peer").GetUInt64();
                bool believed=Classify(signal,d,peer,at);
                var action=lid.Handle(believed,d.IsCaseOpen,d.LidOpenCounter,peer,at,provenFresh:true,explicitLid:d.CarriesLidState);
                actions[r.GetProperty("seq").GetInt32()]=action;total++;
            }
            check(total==258,"34: safe replay lost rows");
            check(actions[36]==(learned?LidAction.Open:LidAction.None),"34: first raw-open outcome");
            check(actions[42]==(learned?LidAction.Close:LidAction.None),"34: close created false wake");
            if(!learned)check(actions[45]==LidAction.Open,"34: genuine next-cycle opening suppressed");
        }
        check(DiagnosticSanitizer.Sanitize("2026-09-08 12:00:00.001 case closeEdge=1 addr=PRIVATE")
            =="2026-09-08 12:00:00.001 case closeEdge=1","34: close-edge diagnostic missing/leaking");
        Console.WriteLine("DeskPods 0.8.34: charging and negative-edge core regressions passed");
    }
}
