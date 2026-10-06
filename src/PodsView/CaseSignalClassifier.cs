namespace PodsView;

// Shapes observed in the supplied Pro2 captures, NOT a universal Apple protocol.
// A matching startup/continuous stream must not bootstrap: old fixtures 06/11
// demonstrate why. Require a remembered pair, fresh packet and observed raw-shape
// silence longer than ColdQuiet in THIS scanner epoch. Physical pair identity is
// not guaranteed.
//
// 0.8.35 and 0.8.37 close the measured hole that kept the first opening off the
// screen, and 0.8.37 removes the part of 0.8.35 that the real .NET gate rejected.
// The user's own 2026-09-08 11:58:33 session is the evidence:
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
        // A fresh packet from the remembered, bound case is already the event the user
        // is waiting for. Do not make the first opening wait for 120 seconds of profile
        // history or for a second advertisement. The old profile gate is retained only
        // for non-explicit packets; the fast path is limited to packets that carry the
        // case/lid field and have a usable radio timestamp.
        bool profile = provenFresh && rememberedPair && data.CarriesLidState
            || provenFresh && rememberedPair && observedQuiet > ColdQuiet
                && MatchesPro2CaseProfile(data);
        return memory.Believe(address,data.Identity,data.LidByte,data.CarriesLidState,now,out source,validatedCaseProfile:profile,caseSide:true);
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
