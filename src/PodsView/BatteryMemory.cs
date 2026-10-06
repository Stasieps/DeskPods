namespace PodsView;

/// <summary>Cache each earbud independently. Reusing a value never renews its timestamp.</summary>
internal sealed class EarbudCache
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private sealed class Memory
    {
        public int? Left, Right;
        public DateTimeOffset LeftAt, RightAt;
    }
    private readonly Dictionary<string, Memory> _items = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    internal ParsedAirPodsData Merge(string key, ParsedAirPodsData data, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(key, out Memory? m)) _items[key] = m = new();
            if (data.LeftBattery is int left && !data.LeftCached) { m.Left = left; m.LeftAt = now; }
            if (data.RightBattery is int right && !data.RightCached) { m.Right = right; m.RightAt = now; }
            bool leftValid = m.LeftAt != default && now >= m.LeftAt && now - m.LeftAt <= Lifetime;
            bool rightValid = m.RightAt != default && now >= m.RightAt && now - m.RightAt <= Lifetime;
            bool leftCached = data.LeftBattery is null && leftValid;
            bool rightCached = data.RightBattery is null && rightValid;
            // A cached percentage does not prove a current charging state.
            return data with
            {
                LeftBattery = data.LeftBattery ?? (leftValid ? m.Left : null),
                RightBattery = data.RightBattery ?? (rightValid ? m.Right : null),
                LeftCharging = data.LeftBattery is not null && !data.LeftCached && data.LeftCharging,
                RightCharging = data.RightBattery is not null && !data.RightCached && data.RightCharging,
                LeftCached = data.LeftCached || leftCached,
                RightCached = data.RightCached || rightCached
            };
        }
    }
    internal void Clear() { lock (_gate) _items.Clear(); }
}

/// <summary>
/// UI-independent last accepted state. Keeps a case reading, never turns it into a live one,
/// and never accepts unbound/queued packets. Dispatcher-owned in the application.
/// </summary>
internal sealed class BatteryMemory
{
    internal ParsedAirPodsData? Data { get; private set; }
    internal AirPodsPacket? Packet { get; private set; }
    internal string DeviceKey { get; private set; } = string.Empty;
    internal int? LastCase { get; private set; }
    internal DateTimeOffset LastCaseAt { get; private set; }

    internal int? LastLeft { get; private set; }
    internal int? LastRight { get; private set; }

    internal void Seed(int? value, string? key, int? left = null, int? right = null, string? earbudsKey = null)
    {
        LastCase = AppSettings.ValidBattery(value);
        DeviceKey = key ?? string.Empty;
        // 0.8.46: the earbuds are only restored for the same pair the case belongs to.
        if (string.IsNullOrEmpty(earbudsKey) || DeviceKey.Length == 0 || earbudsKey == DeviceKey)
        {
            LastLeft = AppSettings.ValidBattery(left);
            LastRight = AppSettings.ValidBattery(right);
            if (DeviceKey.Length == 0 && !string.IsNullOrEmpty(earbudsKey)) DeviceKey = earbudsKey;
        }
        if (LastCase is not null || LastLeft is not null || LastRight is not null)
            Data = new ParsedAirPodsData("AirPods", LastLeft, LastRight, LastCase, false, false, false,
                0, false, LeftCached: true, RightCached: true, CaseCached: true);
    }

    internal bool Apply(AirPodsPacket packet)
    {
        if (!packet.Bound || !packet.Trusted) return false;
        if (Packet is not null && packet.ReceivedAt < Packet.ReceivedAt) return false;
        if (DeviceKey.Length > 0 && packet.DeviceKey.Length > 0 && DeviceKey != packet.DeviceKey)
        {
            LastCase = null;
            LastCaseAt = default;
            LastLeft = LastRight = null;
            Data = null;
        }
        if (packet.DeviceKey.Length > 0) DeviceKey = packet.DeviceKey;
        ParsedAirPodsData data = packet.Data;
        if (data.CaseBattery is int current && !data.CaseCached)
        {
            LastCase = current;
            LastCaseAt = packet.ReceivedAt;
        }
        // 0.8.46: an earbud in a shut case is silent, and the one in the ear reports it as
        // "no value". Its last live charge stays on screen in grey instead of a dash.
        if (data.LeftBattery is int left && !data.LeftCached) LastLeft = left;
        if (data.RightBattery is int right && !data.RightCached) LastRight = right;
        Data = data with
        {
            LeftBattery = data.LeftBattery ?? LastLeft,
            RightBattery = data.RightBattery ?? LastRight,
            LeftCached = data.LeftCached || data.LeftBattery is null,
            RightCached = data.RightCached || data.RightBattery is null,
            LeftCharging = data.LeftBattery is not null && !data.LeftCached && data.LeftCharging,
            RightCharging = data.RightBattery is not null && !data.RightCached && data.RightCharging,
            CaseBattery = data.CaseBattery ?? LastCase,
            CaseCached = data.CaseCached || data.CaseBattery is null,
            CaseCharging = data.CaseBattery is not null && !data.CaseCached && data.CaseCharging
        };
        Packet = packet with { Data = Data };
        return true;
    }
}
