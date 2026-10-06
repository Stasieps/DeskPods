using PodsView;

// 0.8.35 - the first opening of a session.
//
// A static text check cannot prove a capacity bug, so these regressions drive the real
// signature table and the real classifier. The measurement they defend comes from the
// user's 2026-09-08 trace: one AirPods Pro 2 case announced six different signatures in
// a single afternoon (0x39, 0x3A, 0x3D, 0x3E, 0x49, 0x4D), because the low nibble of the
// shape is the charge nibble and it moves every time the lid moves. The table held four.
internal static class Release35Regressions
{
    internal static void Run(Action<bool, string> check)
    {
        var t = new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);

        ParsedAirPodsData Packet(int shape, byte word)
        {
            uint id = (uint)((((shape >> 4) | 0x40) << 16) | 0xFA00 | ((shape & 15) << 4) | 3);
            byte[] b = new byte[27]; b[0] = 7; b[1] = 25; b[2] = 1; b[3] = 0x14; b[4] = 0x20;
            b[5] = (byte)(id >> 16); b[6] = (byte)(id >> 8); b[7] = (byte)id; b[8] = word;
            check(AirPodsAdvertisementParser.TryParse(b, out var d), "35: synthetic fixture parser");
            check(LidSignal.Shape(id) == shape, "35: fixture did not preserve the shape");
            return d!;
        }

        bool Classify(LidSignal signal, ParsedAirPodsData d, ulong peer, DateTimeOffset at,
            bool trusted = true, bool bound = true, bool fresh = true, short rssi = -60) =>
            CaseSignalClassifier.Believe(signal, d, peer, at, trusted, bound, fresh, true, rssi, out _);

        // ---- 1. One case needs more than four signature slots -------------------
        int[] family = { 0x39, 0x3A, 0x3D, 0x3E, 0x49, 0x4D };
        var seeded = new LidSignal();
        seeded.Seed("39,3A,3D,3E,49,4D", t);
        check(seeded.LearnedShapes().Split(',').Length == family.Length,
            "35: the signature table evicted the case's own family while loading it");
        ulong peer = 100;
        foreach (int shape in family)
        {
            var d = Packet(shape, 0x51);
            check(Classify(seeded, d, peer++, t.AddSeconds(1)),
                "35: a learned signature was not believed on a rotated address");
        }

        // Still bounded. Twenty distinct signatures may not grow without a limit.
        var many = new LidSignal();
        var parts = new List<string>();
        for (int s = 0; s < 20; s++) parts.Add(s.ToString("X2"));
        many.Seed(string.Join(",", parts), t);
        check(many.LearnedShapes().Split(',').Length == 12, "35: the signature table is unbounded");

        // ---- 2. The cold-bootstrap silence gate did NOT move --------------------
        // 20 s was tried in 0.8.35 and rejected: replay fixture 06-earbuds-only is
        // recorded air with the case shut, and it announces an open lid 32.1 s in.
        check(CaseSignalClassifier.ColdQuiet == TimeSpan.FromSeconds(120),
            "35: the cold-bootstrap silence gate moved; fixture 06 forbids a shorter one");

        // 0.8.37: the cold profile is the two validated signatures, nothing more.
        // Widening it to the charging variants was tried in 0.8.35 and rejected by
        // Release31, which walks all 256 shapes. Charging equivalence lives in
        // ShapeIsFresh and only after the sibling signature has been learned.
        foreach (int shape in new[] { 0x39, 0x49 })
            check(CaseSignalClassifier.MatchesPro2CaseProfile(Packet(shape, 0x51)),
                "35: the case profile lost a validated signature");
        foreach (int shape in new[] { 0x08, 0x18, 0x28, 0x38, 0x3A, 0x3D, 0x3E, 0x4D, 0x98 })
            check(!CaseSignalClassifier.MatchesPro2CaseProfile(Packet(shape, 0x51)),
                "35: the case profile accepted an unvalidated signature");

        // ---- 3. 0.8.41: inside the case, charge variants of a learned signature speak ------
        for (int seed = 0; seed < 256; seed++)
        {
            var signal = new LidSignal();
            signal.Seed(seed.ToString("X2"), t);
            var d = Packet(seed ^ 4, 0x51);
            check(Classify(signal, d, 1, t.AddMilliseconds(10)),
                "35: a charge variant of a learned case signature was not believed");
        }

        // ---- 4. A change seen across an address rotation is still a change -----
        var rotate = new LidSignal();
        check(!Classify(rotate, Packet(0x62, 0x51), 10, t),
            "35: an unlearned signature was believed on sight");
        check(Classify(rotate, Packet(0x62, 0x59), 11, t.AddSeconds(1)),
            "35: a changed word across an address rotation was discarded");

        // A frozen copy repeating one byte for ever can never produce a change.
        var frozen = new LidSignal();
        check(!Classify(frozen, Packet(0x63, 0x51), 20, t),
            "35: an unlearned signature was believed on sight");
        check(!Classify(frozen, Packet(0x63, 0x51), 21, t.AddSeconds(1)),
            "35: a repeated word across a rotation became a lid movement");

        // A word slept on for longer than Forget is a level, not an event.
        var slept = new LidSignal();
        check(!Classify(slept, Packet(0x64, 0x51), 30, t),
            "35: an unlearned signature was believed on sight");
        check(!Classify(slept, Packet(0x64, 0x59), 31, t.AddMinutes(6)),
            "35: a word slept on for six minutes counted as a lid movement");

        Console.WriteLine("DeskPods 0.8.37: signature-table capacity and first-open regressions passed");
    }
}
