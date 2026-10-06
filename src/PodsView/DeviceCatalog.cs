namespace PodsView;

/// <summary>Identity comes from manufacturer bytes, never from the user-editable Bluetooth name.
/// Names identify models, NOT a unique physical pair or genuine Apple hardware.</summary>
internal static class DeviceCatalog
{
    internal static string DisplayName(ushort code, string fallback = "AirPods") => code switch
    {
        0x0220 => "AirPods 1", 0x0F20 => "AirPods 2", 0x1320 => "AirPods 3",
        0x0E20 => "AirPods Pro 1", 0x1420 => "AirPods Pro 2", 0x0A20 => "AirPods Max",
        0x0B20 => "Powerbeats Pro", 0x0520 => "Beats X", 0x1020 => "Beats Flex",
        0x1120 => "Beats Studio Buds", 0x0620 => "Beats Solo 3", 0x0920 => "Beats Studio 3",
        0x0320 => "Powerbeats 3", 0x0C20 => "Beats Solo Pro",
        // 0.8.40: newer models, battery only. HasLidProtocol below is deliberately unchanged.
        0x2420 => "AirPods Pro 2 USB-C", 0x1B20 => "AirPods 4 ANC", 0x1F20 => "AirPods Max USB-C",
        0x0D20 => "Powerbeats 4", 0x1220 => "Beats Fit Pro", 0x1620 => "Beats Studio Buds+",
        0x1720 => "Beats Studio Pro", 0x2520 => "Beats Solo 4", 0x2620 => "Beats Solo Buds",
        0 => fallback,
        _ => "Apple audio · " + code.ToString("X4")
    };
    internal static bool IsKnown(ushort code) => code is 0x0220 or 0x0F20 or 0x1320 or 0x0E20 or 0x1420 or 0x0A20
        or 0x0B20 or 0x0520 or 0x1020 or 0x1120 or 0x0620 or 0x0920 or 0x0320 or 0x0C20
        or 0x2420 or 0x1B20 or 0x1F20 or 0x0D20 or 0x1220 or 0x1620 or 0x1720 or 0x2520 or 0x2620; // 0.8.40
    // Existing earbud case protocol only. Max/unknown/Beats are battery-only here.
    // A recognised model is not a claim that its firmware has passed hardware QA.
    internal static bool HasLidProtocol(ushort code) => code is 0 or 0x0220 or 0x0F20 or 0x1320 or 0x0E20 or 0x1420;
    internal static string Key(ParsedAirPodsData data) =>
        (data.ModelCode == 0 || IsKnown(data.ModelCode) ? data.Model : "Apple-" + data.ModelCode.ToString("X4"))
        + "/" + data.ColorByte.ToString("X2");
    internal static string DisplayName(ParsedAirPodsData data) => DisplayName(data.ModelCode, data.Model);
}

/// <summary>A remembered model/colour is a hint, not authentication. Normal radio,
/// RSSI and lid classification checks remain mandatory.</summary>
internal static class StartupEligibility
{
    internal static bool CanProcess(RadioObservation observation, string rememberedKey, bool allowNearby = false) =>
        observation.RadioAgeMs is >= 0 and <= 2000
        && (rememberedKey.Length > 0 && DeviceCatalog.Key(observation.Data) == rememberedKey
                && observation.Rssi >= DeviceTracker.PopupFloor
            || PacketFilter.Allow(true, observation.Data.LeftBattery.HasValue || observation.Data.RightBattery.HasValue
                    || observation.Data.CaseBattery.HasValue, observation.Rssi, false, false, allowNearby));
}
