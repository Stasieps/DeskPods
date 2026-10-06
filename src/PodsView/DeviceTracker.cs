namespace PodsView;

/// <summary>
/// Picks the one pair of AirPods this app follows and rejects every other Apple
/// device on the air.
///
/// This is the fix for the bug that survived every timing change: the advertisement
/// filter only checked the model family and a -95 dBm floor, which covers a whole
/// building. A neighbour's case could raise the popup, and their closed packets
/// re-armed the anti-echo timers that then held back the real one.
///
/// Apple rotates the BLE address roughly every 15 minutes, so the address cannot be
/// the identity. Model plus case colour survives that rotation, and the signal floor
/// keeps a far-away pair with the same fingerprint out.
/// </summary>
public sealed class DeviceTracker
{
    /// <summary>Below this the device is not on this desk, so it may not drive the popup.</summary>
    public const short PopupFloor = -75;

    /// <summary>A challenger must beat the bound device by this much to take over.</summary>
    private const double SwitchMargin = 6.0;

    /// <summary>Consecutive winning packets a challenger needs.</summary>
    private const int SwitchStreak = 3;

    private static readonly TimeSpan BoundSilence = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Forget = TimeSpan.FromMinutes(2);

    private sealed class Track
    {
        public ulong Address;
        public double Rssi;
        public DateTimeOffset LastSeen;
        public int Count;
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, Track> _tracks = new(StringComparer.Ordinal);
    private string _boundKey = string.Empty;
    private string _challenger = string.Empty;
    private int _challengeStreak;

    public string BoundKey { get { lock (_sync) return _boundKey; } }

    /// <summary>
    /// Where this class reports its decisions. The app points it at the log file. It is a
    /// hook rather than a direct call so the class stays free of Windows-only code and can
    /// be compiled straight into the smoke tests.
    /// </summary>
    public static Action<string>? Trace { get; set; }

    /// <summary>
    /// Records one advertisement and answers whether it came from the followed device.
    /// </summary>
    public bool Observe(string key, ulong address, short rssi, DateTimeOffset now, out string reason)
    {
        lock (_sync)
        {
            foreach (string stale in _tracks.Where(pair => now - pair.Value.LastSeen > Forget).Select(pair => pair.Key).ToList())
                if (!string.Equals(stale, _boundKey, StringComparison.Ordinal)) _tracks.Remove(stale);

            if (!_tracks.TryGetValue(key, out Track? track))
            {
                track = new Track { Rssi = rssi };
                _tracks[key] = track;
            }

            track.Rssi = track.Count == 0 ? rssi : (track.Rssi * 0.7) + (rssi * 0.3);
            track.LastSeen = now;
            track.Address = address;
            track.Count++;

            if (_boundKey.Length == 0)
            {
                if (rssi < PopupFloor) { reason = "too far to bind"; return false; }
                _boundKey = key;
                Trace?.Invoke($"Following {key} (rssi {rssi})");
                reason = "bound";
                return true;
            }

            if (string.Equals(_boundKey, key, StringComparison.Ordinal))
            {
                _challengeStreak = 0;
                // No signal floor once the device is known: a dip behind a laptop lid or a
                // -127 reading from the radio must not silently drop its packets.
                reason = "bound";
                return true;
            }

            _tracks.TryGetValue(_boundKey, out Track? current);
            Track bound = current ?? new Track { LastSeen = now, Rssi = 0 };
            bool boundSilent = now - bound.LastSeen > BoundSilence;
            bool clearlyCloser = track.Rssi > bound.Rssi + SwitchMargin;

            if (rssi >= PopupFloor && (boundSilent || clearlyCloser))
            {
                if (!string.Equals(_challenger, key, StringComparison.Ordinal)) { _challenger = key; _challengeStreak = 0; }
                _challengeStreak++;
                if (_challengeStreak >= SwitchStreak)
                {
                    Trace?.Invoke($"Switching from {_boundKey} to {key} ({(boundSilent ? "the old one went quiet" : "much stronger signal")})");
                    _boundKey = key;
                    _challenger = string.Empty;
                    _challengeStreak = 0;
                    reason = "bound";
                    return true;
                }
                reason = "challenger " + _challengeStreak + "/" + SwitchStreak;
                return false;
            }

            if (string.Equals(_challenger, key, StringComparison.Ordinal)) _challengeStreak = 0;
            reason = "other device";
            return false;
        }
    }
}

/// <summary>
/// Works out what bit 3 of the lid byte actually means on this hardware.
///
/// The published Continuity notes say 1 means open, the packets this app was built
/// against behaved the other way round, and a wrong guess produces exactly the two
/// complaints that would not go away: a popup that appears late and one that appears
/// on its own. Rather than guess, this listens: only an open case streams for many
/// seconds without a break, so whichever value dominates long streams is "open".
/// </summary>
public static class LidPolarity
{
    private static readonly TimeSpan StreamGap = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LongStream = TimeSpan.FromSeconds(6);
    private static readonly object Sync = new();
    private static DateTimeOffset _streamStart;
    private static DateTimeOffset _lastSeen;
    private static int _setInStream;
    private static int _clearInStream;
    private static int _setVotes;
    private static int _clearVotes;

    /// <summary>Returns true once the air has answered the question.</summary>
    public static bool Observe(bool bitSet, DateTimeOffset now, out bool inverted)
    {
        inverted = true;
        lock (Sync)
        {
            if (_lastSeen == default || now - _lastSeen > StreamGap || now < _lastSeen)
            {
                CountFinishedStream();
                _streamStart = now;
                _setInStream = 0;
                _clearInStream = 0;
            }

            _lastSeen = now;
            if (bitSet) _setInStream++; else _clearInStream++;

            if (_setVotes + _clearVotes < 3) return false;
            if (_setVotes >= _clearVotes * 3) { inverted = false; return true; }
            if (_clearVotes >= _setVotes * 3) { inverted = true; return true; }
            return false;
        }
    }

    private static void CountFinishedStream()
    {
        if (_streamStart == default || _lastSeen - _streamStart < LongStream) return;
        if (_setInStream > _clearInStream) _setVotes++;
        else if (_clearInStream > _setInStream) _clearVotes++;
    }
}
