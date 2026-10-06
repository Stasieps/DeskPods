namespace PodsView;

internal sealed record RadioObservation(ParsedAirPodsData Data, ulong Address, short Rssi,
    string Hex, DateTimeOffset ReceivedAt, int RadioAgeMs)
{
    // Classification evidence only; never replayed as a popup event.
    internal RadioObservation? Previous { get; init; }
    internal long Sequence { get; init; }
    internal int TracePeer { get; init; }
}

/// <summary>Owned by BluetoothMonitor's lock. No trust, binding or lid decision is made here.</summary>
internal sealed class StartupObservationBuffer
{
    internal const int Capacity = 24;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(2);
    private readonly Dictionary<ulong, RadioObservation> _latest = new();
    internal int Count => _latest.Count;
    internal void Add(RadioObservation observation)
    {
        if (_latest.TryGetValue(observation.Address, out var previous))
        {
            if (observation.ReceivedAt < previous.ReceivedAt) return;
            if (previous.Data.Model == observation.Data.Model && previous.Data.ColorByte == observation.Data.ColorByte)
            {
                // Preserve one distinct transition/explicit state so startup coalescing does
                // not erase the evidence needed to classify a previously unknown case.
                var evidence = previous.Data.CarriesLidState || ((previous.Data.LidByte ^ observation.Data.LidByte) & 0x0F) != 0
                    ? previous with { Previous = null } : previous.Previous;
                observation = observation with { Previous = evidence };
            }
        }
        _latest[observation.Address] = observation;
        if (_latest.Count > Capacity)
            _latest.Remove(_latest.MinBy(pair => pair.Value.ReceivedAt).Key);
    }
    internal RadioObservation[] Drain(DateTimeOffset now)
    {
        bool Fresh(RadioObservation item) => now >= item.ReceivedAt && now-item.ReceivedAt <= Lifetime
            && (item.RadioAgeMs < 0 || item.RadioAgeMs + (now-item.ReceivedAt).TotalMilliseconds <= Lifetime.TotalMilliseconds);
        var result = _latest.Values.Where(Fresh)
            .Select(item => item with { Previous = item.Previous is { } previous && Fresh(previous) ? previous : null })
            .OrderBy(item => item.ReceivedAt).ToArray();
        _latest.Clear();
        return result;
    }
    internal void Remove(ulong address) => _latest.Remove(address);
    internal void Clear() => _latest.Clear();
}
