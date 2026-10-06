namespace PodsView;

/// <summary>
/// Decodes Apple Continuity proximity-pairing manufacturer data (company 0x004C).
/// Battery values are transmitted as nibbles in 10% steps. Values 0xB–0xF mean
/// that a component is currently unavailable.
/// </summary>
internal static class AirPodsAdvertisementParser
{
    private const byte ProximityPairingMessage = 0x07;
    private const byte PairedPayloadLength = 0x19;
    private const byte PairedMode = 0x01;

    public static bool TryParse(ReadOnlySpan<byte> manufacturerPayload, out ParsedAirPodsData? result)
    {
        result = null;
        if (manufacturerPayload.Length >= 29 && manufacturerPayload[0] == 0x4C && manufacturerPayload[1] == 0x00)
            manufacturerPayload = manufacturerPayload[2..];

        if (manufacturerPayload.Length < 27 ||
            manufacturerPayload[0] != ProximityPairingMessage ||
            manufacturerPayload[1] != PairedPayloadLength ||
            manufacturerPayload[2] != PairedMode)
            return false;

        ushort modelCode = (ushort)((manufacturerPayload[3] << 8) | manufacturerPayload[4]);
        string model = modelCode switch
        {
            0x0220 => "AirPods (1-ше покоління)",
            0x0F20 => "AirPods (2-ге покоління)",
            0x1320 => "AirPods (3-тє покоління)",
            0x0E20 => "AirPods Pro",
            0x1420 => "AirPods Pro (2-ге покоління)",
            0x0A20 => "AirPods Max",
            0x0B20 => "Powerbeats Pro",
            0x0520 => "Beats X",
            0x1020 => "Beats Flex",
            0x1120 => "Beats Studio Buds",
            0x0620 => "Beats Solo 3",
            0x0920 => "Beats Studio 3",
            0x0320 => "Powerbeats 3",
            0x0C20 => "Beats Solo Pro",
            // 0.8.40: models missing from the table. Without a name their family resolved to
            // "Unknown", and while the pair was connected the packet filter dropped every packet.
            // Names only: none of these gets the lid protocol, no capture from that hardware exists.
            0x2420 => "AirPods Pro (2-ге покоління, USB-C)",
            0x1B20 => "AirPods 4 (ANC)",
            0x1F20 => "AirPods Max (USB-C)",
            0x0D20 => "Powerbeats (4-те покоління)",
            0x1220 => "Beats Fit Pro",
            0x1620 => "Beats Studio Buds+",
            0x1720 => "Beats Studio Pro",
            0x2520 => "Beats Solo 4",
            0x2620 => "Beats Solo Buds",
            _ => "Apple audio " + modelCode.ToString("X4")
        };

        int orientationNibble = manufacturerPayload[5] >> 4;
        bool flipped = (orientationNibble & 0x02) == 0;
        int highBatteryNibble = manufacturerPayload[6] >> 4;
        int lowBatteryNibble = manufacturerPayload[6] & 0x0F;
        int chargeNibble = manufacturerPayload[7] >> 4;
        int caseBatteryNibble = manufacturerPayload[7] & 0x0F;
        byte lidIndication = manufacturerPayload[8];
        byte lidOpenCounter = (byte)(lidIndication & 0b0000_0111);
        // Settled by a capture from this very hardware: the case sends 0x31 0x32 0x33
        // while the lid is open and 0x39 0x3A 0x3B once it is shut, so bit 3 means closed.
        bool isCaseOpen = (lidIndication & 0b0000_1000) == 0;
        // Bit 5 means the lid state is stated outright, and until 0.8.9 that was treated as
        // the only way to learn it. The capture of 22:26 shows the very same case reporting
        // the very same lid in the 0x10-0x1F range after an address rotation, and the 20:52
        // capture uses 0x50-0x5F, so the flag marks a range and not the presence of
        // information. LidSignal decides now; a byte without the flag is still passed on.
        bool carriesLidState = (lidIndication & LidSignal.ExplicitLidBit) != 0;
        // Bytes 5 to 7 as one number. Across the four recorded sessions of 2026-08-29 the
        // living case and the frozen copy of it never once shared this triple - 24 FA 93
        // and 73 7A 93 against 02 F7 8F and 13 A7 A3 - and the copy kept its own triple even
        // when it rotated its address. LidSignal trusts it for exactly that reason.
        uint identity = (uint)((manufacturerPayload[5] << 16) | (manufacturerPayload[6] << 8) | manufacturerPayload[7]);

        int? leftBattery = DecodeBattery(flipped ? highBatteryNibble : lowBatteryNibble);
        int? rightBattery = DecodeBattery(flipped ? lowBatteryNibble : highBatteryNibble);
        int? caseBattery = DecodeBattery(caseBatteryNibble);
        int leftChargeBit = flipped ? 0b0010 : 0b0001;
        int rightChargeBit = flipped ? 0b0001 : 0b0010;

        result = new ParsedAirPodsData(
            model,
            leftBattery,
            rightBattery,
            caseBattery,
            (chargeNibble & leftChargeBit) != 0,
            (chargeNibble & rightChargeBit) != 0,
            (chargeNibble & 0b0100) != 0,
            lidOpenCounter,
            isCaseOpen,
            lidIndication,
            manufacturerPayload[9],
            carriesLidState,
            identity, ModelCode: modelCode);
        return true;
    }

    internal static int? DecodeBattery(int nibble) => nibble is >= 0 and <= 10 ? nibble * 10 : null;

    internal static string FamilyForModel(string model)
    {
        if (model.Contains("Beats", StringComparison.OrdinalIgnoreCase)) return "Beats";
        if (model.Contains("Max", StringComparison.OrdinalIgnoreCase)) return "AirPods Max";
        if (model.Contains("Pro", StringComparison.OrdinalIgnoreCase)) return "AirPods Pro";
        if (model.Contains("AirPods", StringComparison.OrdinalIgnoreCase)) return "AirPods";
        if (model.Contains("Beats", StringComparison.OrdinalIgnoreCase) || model.Contains("Powerbeats", StringComparison.OrdinalIgnoreCase)) return "Beats";
        return "Unknown";
    }
}
