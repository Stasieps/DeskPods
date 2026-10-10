namespace PodsView;

// Shapes observed in the supplied Pro2 captures, NOT a universal Apple protocol.
// A matching startup/continuous stream must not bootstrap: old fixtures 06/11
// demonstrate why. Require a remembered pair, fresh packet and observed raw-shape
// silence longer than ColdQuiet in THIS scanner epoch. Physical pair identity is
// not guaranteed.
//
// 0.8.35 and 0.8.37 close the measured hole that kept the first opening off the
// screen, and 0.8.37 removes the part of 0.8.35 that the real .NET gate rejected.
// A recorded 2026-09-08 11:58:33 session is the evidence:
//
//   11:59:14.249  peer 4  lidByte 0x51  shape 0x4D  open, cycle 1  -> baseline, DISCARDED
//   11:59:14.348  peer 4  lidByte 0x51  shape 0x4D  open, cycle 1  -> static,   DISCARDED
//   11:59:14.458  peer 4  lidByte 0x51  shape 0x4D  open, cycle 1  -> static,   DISCARDED
//   11:59:14.827  peer 4  lidByte 0x59  shape 0x4D  SHUT, cycle 1  -> change,   BELIEVED
//
// The popup therefore appeared on the closing word, not on the opening. Two findings:
//  * WIDENING THIS PROFILE to the charging variants 0x3D/0x4D was TRIED IN 0.8.35 AND
//    REVERTED IN 0.8.37. Release31Regressions walks all 256 shapes and requires that a
//    cold, never-learned device bootstrap for exactly 0x39 and 0x49, because fixtures
//    06/11 contain phantom copies that would otherwise raise a popup on nothing. The
//    widening also could not have rescued the trace above: that case had been quiet for
//    40.9 s, far below ColdQuiet, so the profile branch was never reachable there.
//    Charging equivalence stays where it is safe - ShapeIsFresh keeps the narrow ^0x04
//    alias, but only once the sibling signature has actually been learned.
//  * lowering the 120 s silence gate was TRIED IN 0.8.35 AND REJECTED. The gap from the
//    watcher start to that first case word was 40.9 s, so 20 s would have covered it -
//    but replay fixture 06-earbuds-only is recorded air in which the case was shut the
//    whole minute and still announced lid 0x11 from a fresh address 32.1 s in. At 20 s
//    that fixture raises a popup that never physically happened. 32.1 s against 40.9 s
//    leaves no honest threshold, so the gate stays at 120 s and the first opening is
//    rescued by the signature table instead.
// 0.8.47 replaces the cold-profile door (0x39/0x49 after ColdQuiet) by the in-case door in
// Believe below: see the comment there for the evidence and for what it costs.
internal static class CaseSignalClassifier
{
    /// <summary>Observed raw-shape silence that separates a moved case from a continuous stream.</summary>
    internal static readonly TimeSpan ColdQuiet = TimeSpan.FromSeconds(120);

    // 0.8.41: payload byte 5 says where the transmitting earbud is. Across all 21 recorded
    // replay sessions the 977 packets with bit 6 (this earbud in the case) or bit 2 (both
    // earbuds in the case) set carry every real lid transition, while all 736 packets with
    // neither bit - statuses 0x02, 0x03, 0x11 and 0x13, and 0x0B for both earbuds in the
    // ears - repeat an open-looking lid word and never once a shut one. An earbud outside
    // the case does not know the lid. With one earbud worn and one in the case the worn one
    // kept the card open, and its signatures collided with the case's own in LidSignal
    // (0x3A is in both of its lists), so it could also cost the case its first opening.
    internal static bool OutsideCase(ParsedAirPodsData data) =>
        data.Identity != 0 && ((data.Identity >> 16) & 0x44) == 0;

    internal static bool MatchesPro2CaseProfile(ParsedAirPodsData data) =>
        data.ModelCode == 0x1420 && data.Identity != 0
        && LidSignal.Shape(data.Identity) is 0x39 or 0x49;
    internal static bool Believe(LidSignal memory, ParsedAirPodsData data, ulong address,
        DateTimeOffset now, bool trusted, bool bound, bool provenFresh, bool rememberedPair,
        short rssi, out string source, TimeSpan observedQuiet = default)
    {
        if (!trusted || !bound || rssi <= PacketFilter.UnknownRssi) { source = "rejected"; return false; }
        if (!DeviceCatalog.HasLidProtocol(data.ModelCode)) { source = "battery-only-model"; return false; }
        // 0.8.41: an earbud outside the case does not know the lid.
        if (OutsideCase(data)) { source = "out-of-case"; return false; }
        // LidSignal keeps learning exactly as before (explicit, change, rotate-change, known).
        if (memory.Believe(address,data.Identity,data.LidByte,data.CarriesLidState,now,out source,caseSide:true)) return true;
        // 0.8.47: the in-case door. A fresh packet from the remembered, bound AirPods Pro 2,
        // sent from inside the case (bit 6 or bit 2 of byte 5), is the lid speaking. Waiting
        // for LidSignal to learn its shape is what kept the first opening off the screen: in
        // the safe export replay-safe/22 the open words of a charging case were discarded as
        // baseline/static and the popup only appeared on the close word. The guards that made
        // the old profile door safe stay: trusted, bound, a usable RSSI, a radio timestamp
        // proven seconds new, the remembered pair and exactly model 0x1420. An earbud outside
        // the case was refused above (0.8.41). What this costs, measured on the 21 recorded
        // sessions: fixture 06 shows its lone 0x11 word for the quiet tail (a lone word cannot
        // be told from a real one - fixtures 01, 08, 14, 15 and 18 open with exactly that),
        // and fixture 13's sparse stream shows once and is then spent (LidStateMachine).
        // Other models keep the learned path until a recording of theirs proves the same.
        // observedQuiet and the cold profile below are kept for the callers and the tests.
        if (provenFresh && rememberedPair && data.ModelCode == 0x1420 && data.Identity != 0)
        {
            source = "in-case";
            return true;
        }
        return false;
    }
}
internal sealed class CaseProfileHistory
{
    private readonly Dictionary<byte, DateTimeOffset> _last = new();
    private DateTimeOffset _epoch;
    internal void Reset() { _last.Clear(); _epoch = default; }
    internal TimeSpan Observe(uint identity, DateTimeOffset now, DateTimeOffset listeningSince)
    {
        if (identity == 0 || listeningSince == default || listeningSince > now) return TimeSpan.Zero;
        if (_epoch != listeningSince) { _last.Clear(); _epoch = listeningSince; }
        byte shape = LidSignal.Shape(identity);
        DateTimeOffset before = _last.TryGetValue(shape,out var seen) ? seen : listeningSince;
        if (now < before) return TimeSpan.Zero;
        _last[shape] = now;
        return now-before;
    }
}
