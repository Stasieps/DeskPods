using PodsView;

// 0.8.48 - what the recordings of 2026-10-10 showed.
//
// 1. After it wakes up the earbud counts the lid openings from zero again (23:53:58: counter
//    5 on the old address, counter 0 on the new one). A first word "shut" with counter 1 or
//    more is a lid that was already opened and shut, and the card it raised came 3.7-6.1 s
//    after the case was closed (19:49:54, 23:53:40, 00:07:05).
// 2. An earbud that is only half awake in the case (status 0x04 / 0x24) says "open" two or
//    three times and falls silent without ever saying "shut" (23:39:36, 00:26:03, 00:49:47),
//    so its card waited the full QuietTail. Fixture 07 has 2.93 s of silence with the lid
//    open between half-awake and full words, which is what bounds HalfAwakeTail from below.
internal static class Release48Regressions
{
    internal static void Run(Action<bool, string> check)
    {
        var t = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);

        // ---- 1. The wake door opens on counter 0 only ---------------------------
        foreach (int cycle in Enumerable.Range(0, 8))
        {
            var lid = new LidStateMachine(); lid.BeginListening(t, t);
            var action = lid.Handle(true, false, cycle, 4, t.AddSeconds(40), provenFresh: true);
            check(action == (cycle == 0 ? LidAction.Open : LidAction.None),
                "48: a shut word with counter " + cycle + " after a long silence gave " + action);
            check(lid.IsOpen == (cycle == 0), "48: wrong lid state after a shut word with counter " + cycle);
            if (cycle == 0) continue;

            // The next opening moves the counter on and still shows at once, on the same
            // address or after a rotation.
            foreach (ulong address in new ulong[] { 4, 5 })
            {
                var next = new LidStateMachine(); next.BeginListening(t, t);
                next.Handle(true, false, cycle, 4, t.AddSeconds(40), provenFresh: true);
                var opened = next.Handle(true, true, (cycle + 1) & 7, address, t.AddSeconds(41.5), provenFresh: true);
                check(opened == LidAction.Open,
                    "48: the opening after a shut word with counter " + cycle + " did not show (" + opened + ", " + next.LastBlockReason + ")");
            }
        }

        // ---- 2. Half-awake words get the short tail -----------------------------
        check(LidStateMachine.HalfAwakeTail == TimeSpan.FromMilliseconds(3500), "48: HalfAwakeTail moved");
        check(LidStateMachine.HalfAwakeTail < LidStateMachine.QuietTail, "48: the half tail is not shorter than the quiet tail");
        check(LidStateMachine.HalfAwakeTail > TimeSpan.FromMilliseconds(2930),
            "48: the half tail would end a card the lid kept open for 2.93 s (fixture 07)");
        {
            var lid = new LidStateMachine(); lid.BeginListening(t, t);
            check(lid.Handle(true, true, 1, 4, t.AddSeconds(40), provenFresh: true, halfAwake: true) == LidAction.Open,
                "48: a half-awake open word did not show the card");
            lid.Handle(true, true, 1, 4, t.AddSeconds(40.2), provenFresh: true, halfAwake: true);
            lid.Handle(true, true, 1, 4, t.AddSeconds(40.4), provenFresh: true, halfAwake: true);
            check(lid.HalfAwakeOnly && lid.ActiveTail == LidStateMachine.HalfAwakeTail,
                "48: a card raised by half-awake words lacks the half tail");
            check(!lid.ShouldTimeout(t.AddSeconds(43.9)), "48: the half tail fired early");
            check(lid.ShouldTimeout(t.AddSeconds(43.901)), "48: the half tail did not fire");
        }
        {
            // Half-awake first, 2.93 s of silence, then the earbud wakes properly.
            var lid = new LidStateMachine(); lid.BeginListening(t, t);
            lid.Handle(true, true, 1, 4, t.AddSeconds(40), provenFresh: true, halfAwake: true);
            check(!lid.ShouldTimeout(t.AddSeconds(42.93)), "48: the half tail ended the card during fixture 07's gap");
            check(lid.Handle(true, true, 1, 4, t.AddSeconds(42.93), provenFresh: true) == LidAction.Update,
                "48: a full word did not keep the card");
            check(!lid.HalfAwakeOnly && lid.ActiveTail == LidStateMachine.QuietTail,
                "48: a full word did not end the half tail");
            lid.Handle(true, true, 1, 4, t.AddSeconds(43.1), provenFresh: true, halfAwake: true);
            check(!lid.HalfAwakeOnly, "48: a half word after a full word brought the half tail back");
            check(!lid.ShouldTimeout(t.AddSeconds(43.1 + 3.6)), "48: a card with a full word used the half tail");
        }
        {
            // A wake-up the case has not confirmed keeps its own rule, even when half awake;
            // once a half-awake open word confirms it, the half tail applies.
            var lid = new LidStateMachine(); lid.BeginListening(t, t);
            check(lid.Handle(true, false, 0, 4, t.AddSeconds(40), provenFresh: true, halfAwake: true) == LidAction.Open,
                "48: a counter-0 wake did not show");
            check(lid.WakeUnconfirmed && !lid.HalfAwakeOnly, "48: an unconfirmed wake took the half tail");
            lid.Handle(true, true, 1, 4, t.AddSeconds(40.5), provenFresh: true, halfAwake: true);
            check(!lid.WakeUnconfirmed && lid.HalfAwakeOnly, "48: a confirmed half-awake wake lacks the half tail");
        }
        {
            // Every new opening starts the count again with its own word.
            var lid = new LidStateMachine(); lid.BeginListening(t, t);
            lid.Handle(true, true, 1, 4, t.AddSeconds(40), provenFresh: true);
            lid.Handle(true, false, 1, 4, t.AddSeconds(41), provenFresh: true);
            check(lid.Handle(true, true, 2, 4, t.AddSeconds(42), provenFresh: true, halfAwake: true) == LidAction.Open,
                "48: the next opening did not show");
            check(lid.HalfAwakeOnly, "48: a full word of the last cycle leaked into the next one");
        }

        // ---- 3. The classifier: bit 2 without bit 6 of status byte 5 ------------
        ParsedAirPodsData Packet(byte status, byte pods)
        {
            byte[] b = new byte[27]; b[0] = 7; b[1] = 25; b[2] = 1; b[3] = 0x14; b[4] = 0x20;
            b[5] = status; b[6] = pods; b[7] = 0x53; b[8] = 0x51;
            check(AirPodsAdvertisementParser.TryParse(b, out var d) && d is not null, "48: synthetic fixture parser");
            return d!;
        }
        foreach (byte status in new byte[] { 0x04, 0x24 })
            check(CaseSignalClassifier.HalfAwake(Packet(status, 0xF8)), "48: half-awake status 0x" + status.ToString("X2") + " not recognised");
        foreach (byte status in new byte[] { 0x53, 0x55, 0x71, 0x73, 0x44, 0x00, 0x20 })
            check(!CaseSignalClassifier.HalfAwake(Packet(status, 0x88)), "48: status 0x" + status.ToString("X2") + " read as half awake");
        check(!CaseSignalClassifier.HalfAwake(new ParsedAirPodsData("AirPods", null, null, null, false, false, false, 0, true)),
            "48: a packet without identity read as half awake");
    }
}
