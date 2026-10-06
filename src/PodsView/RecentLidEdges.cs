namespace PodsView;

/// <summary>Negative-only evidence: a recent same-peer, same-cycle open/close
/// edge is a close, not a wake after silence. This never grants lid trust.</summary>
internal sealed class RecentLidEdges
{
    private const int Limit = 32;
    private readonly Dictionary<ulong, (int Cycle, DateTimeOffset At)> _opens = new();
    internal int Count => _opens.Count;
    internal void Reset() => _opens.Clear();

    internal bool Observe(bool isOpen, int cycle, ulong address, DateTimeOffset now,
        bool provenFresh, TimeSpan maxGap)
    {
        if (!provenFresh || address == 0 || cycle is < 0 or > 7) return false;
        foreach (ulong key in _opens.Where(p => now < p.Value.At || now-p.Value.At > maxGap).Select(p => p.Key).ToArray())
            _opens.Remove(key);
        if (!isOpen)
        {
            bool edge = _opens.TryGetValue(address, out var last) && last.Cycle == cycle;
            _opens.Remove(address);
            return edge;
        }
        _opens[address] = (cycle, now);
        if (_opens.Count > Limit)
            _opens.Remove(_opens.OrderBy(p => p.Value.At).First().Key);
        return false;
    }
}
