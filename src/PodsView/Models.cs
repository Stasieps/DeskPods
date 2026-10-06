namespace PodsView;

internal enum MonitorMode
{
    Starting,
    Scanning,
    Connected,
    Disconnected,
    BluetoothUnavailable,
    Error
}

internal sealed record ParsedAirPodsData(
    string Model,
    int? LeftBattery,
    int? RightBattery,
    int? CaseBattery,
    bool LeftCharging,
    bool RightCharging,
    bool CaseCharging,
    byte LidOpenCounter,
    bool IsCaseOpen,
    byte LidByte = 0,
    byte ColorByte = 0,
    bool CarriesLidState = false,
    // Payload bytes 5 to 7 packed into one number: the status nibble, both earbud
    // batteries, and the case battery with its charge flags. It is the only field that
    // separates the living case from the frozen copy that shares its model and colour.
    uint Identity = 0,
    bool LeftCached = false,
    bool RightCached = false,
    bool CaseCached = false,
    ushort ModelCode = 0);

internal sealed record AirPodsPacket(
    ParsedAirPodsData Data,
    DateTimeOffset ReceivedAt,
    short Rssi,
    ulong BluetoothAddress,
    string RawHex,
    bool Trusted = true,
    bool Bound = true,
    string DeviceKey = "",
    bool ProvenFresh = false,
    bool LidBelievable = false,
    DateTimeOffset? ListeningSince = null,
    long Sequence = 0,
    int TracePeer = 0);

internal sealed record DeviceStatus(
    MonitorMode Mode,
    string DeviceName,
    string? Family,
    bool IsPaired,
    bool IsConnected,
    bool WatcherRunning,
    short? LastRssi,
    DateTimeOffset? LastPacketAt,
    string Detail,
    ushort IdentifiedModelCode = 0,
    bool MultiplePairedCandidates = false)
{
    public static DeviceStatus Initial { get; } = new(
        MonitorMode.Starting,
        "AirPods",
        null,
        false,
        false,
        false,
        null,
        null,
        "Підготовка Bluetooth…");
}
