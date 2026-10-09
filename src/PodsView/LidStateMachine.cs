namespace PodsView;

public enum LidAction
{
    None,
    Open,
    Update,
    Close
}

/// <summary>
/// Decides when the case popup is on screen.
///
/// The 0.8.6 capture settled what the radio actually carries: two transmitters share
/// one identity.
///
///   earbuds  lid byte 0x10, every 0.4-2.2s, all day long, no lid information at all
///   case     lid byte 0x3X, in bursts about eight times a second while it has news
///
/// Bit 3 of the case byte is the lid: 0x31 0x32 0x33 while open, 0x39 0x3A 0x3B once
/// shut. Bits 0-2 are a cycle counter the case increments every time the lid is opened.
/// Earbud packets are ignored entirely by this class.
///
/// 0.8.7 fixes the two things 0.8.6 got wrong, both of which came from one place -
/// treating silence as a close:
///
///   1. The case does not stream while the lid sits open; it bursts when it has news
///      and then goes quiet. A four second silence therefore hid a popup whose lid was
///      still open. The silence limit is now ten seconds and, far more importantly,
///      silence no longer buries the cycle: it suspends it. The very next burst from
///      the same cycle puts the popup straight back (<see cref="SuspendForSilence"/>).
///   2. The close lockout could be set to a minute from the settings window, which
///      turned into a minute of waiting before the popup was allowed to appear. It is
///      now capped at three seconds no matter what is asked for.
///
/// A burial - <see cref="ForceClosed"/> - is still absolute, but only the close button,
/// standby and unlock use it, because there a human or the OS really did end the cycle.
/// </summary>
/// 0.8.8 removes the last delay a human could feel. The two-packet proof existed for one
/// reason: a lone open packet might belong to the backlog Windows replays after standby.
/// When the radio timestamp proves a packet is seconds new that cannot be true, so one such
/// packet of a cycle other than the one just closed opens the popup on the spot. Anything
/// that cannot prove its age still has to arrive as a burst.
public sealed class LidStateMachine
{
    /// <summary>Case packets needed to show the popup. Its bursts run at about 8 Hz.</summary>
    // Show on the first believable case packet. Waiting for a second packet made
    // the visible latency depend on the case burst and failed for empty cases.
    private const int MinStreak = 1;

    /// <summary>Two case packets further apart than this are not one burst.</summary>
    private static readonly TimeSpan MaxStreamGap = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Silence from the case while the popup is up. A shut lid announces itself with a
    /// closed packet, so this is only a backstop for the case that stops talking
    /// altogether - and being wrong about it is now cheap, because the popup can resume.
    /// </summary>
    public static readonly TimeSpan StreamTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The shorter tail for a case that said its whole piece in one burst and went mute.
    /// An empty case is exactly that: it speaks while the lid moves and then says nothing at
    /// all - there is no closed word to react to, because no packet in four days of traces
    /// reports both earbuds out. Measured over all 232 recorded popups: 174 cycles said
    /// everything within <see cref="BurstWindow"/> of the popup going up and not one of them
    /// ever spoke again after a gap longer than five seconds, so this costs nothing.
    /// </summary>
    public static readonly TimeSpan QuietTail = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long after the popup appears the case may still be talking and count as one burst.
    /// Past this it is a talkative cycle: those own every long gap in the recordings (worst
    /// 9.99 s, all seven ended with a real closed word), so they keep <see cref="StreamTimeout"/>.
    /// </summary>
    public static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(2);

    /// <summary>How long a suspended cycle may still be resumed by its own case.</summary>
    private static readonly TimeSpan ResumeWindow = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Packets of an already closed cycle needed before the guard steps aside. Echoes come
    /// one at a time; only a case with something to say bursts like this.
    /// </summary>
    // Same-cycle echoes are not a new opening. Keep the old sustained-burst
    // fail-open guard so a repeated stale packet cannot resurrect the card after
    // a close, while genuinely fresh/new-cycle packets still use the instant path.
    private const int EchoOverrideStreak = 5;

    /// <summary>How long that burst must last to count as a real opening.</summary>
    private static readonly TimeSpan EchoOverrideSpan = TimeSpan.FromMilliseconds(800);

    /// <summary>The hard ceiling on the lockout. Nothing may delay a real opening longer.</summary>
    // 0.8.37: back to the 3 s ceiling. Zero clamped every configured lockout to nothing,
    // including the 3 s the C# suites set, so close -> immediate reopen could flicker.
    private static readonly TimeSpan MaxCloseLockout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Silence from the case after which its first word is read as a wake-up rather than as
    /// the state of the lid.
    ///
    /// The case is off the air unless something happens to it, so a packet arriving after a
    /// long quiet means a hand is on it. Counted over the thirteen bursts of the 0.8.13
    /// session on 2026-08-30: in eight of them the first word said "open" and the popup was
    /// instant; in the other five it said "closed" and the popup had to wait for the next
    /// real opening - 0.5 s, 0.9 s, 1.3 s, and 4.7 s after the case had been silent for 108
    /// minutes. That last one is the case where opening shows nothing until the lid is shut
    /// and opened once more: at 20:57:08 the case answered its wake-up with the state
    /// it had before falling asleep - lid byte 0x58, counter 0 - and the true 0x52 only
    /// arrived 4.7 s later, after a second lid movement by hand.
    ///
    /// Replayed over all ten recorded sessions this fires six times. Five were followed by a
    /// real opening within seconds, so they remove waits of 0.5 s, 0.7 s, 1.4 s, 2.2 s and
    /// 4.7 s. One had nothing behind it, and it costs a correct battery reading on screen
    /// until the ten-second silence rule takes it away - never a stuck window. The threshold
    /// sits above every gap measured inside a burst and below every silence between bursts:
    /// of 4900 measured gaps between believed case packets, 4852 are under 2 s and every one
    /// over 20 s is a real pause between bursts.
    /// </summary>
    private static readonly TimeSpan WakeSilence = TimeSpan.FromSeconds(20);

    /// <summary>The wake-up threshold in seconds, for the session header in the log.</summary>
    public static double WakeSilenceSeconds => WakeSilence.TotalSeconds;

    /// <summary>
    /// How long a popup raised by a wake-up may not be taken away by the case's own word.
    /// Without it the same stale "closed", repeated a fifth of a second later, would flick
    /// the popup off the screen before it could be read. Silence still removes it through
    /// <see cref="StreamTimeout"/>, so a wake-up with nothing behind it costs at most ten
    /// seconds of a battery reading on screen and never a stuck window.
    /// </summary>
    private static readonly TimeSpan WakeFloor = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// How long a popup raised by a wake-up survives the case going quiet again when the
    /// case never confirmed the lid is open.
    ///
    /// The four bursts of the 0.8.14 traces that never popped lasted 0.0 s, 1.7 s, 2.5 s and
    /// 3.5 s and consisted of nothing but repeats of one stale closed word. The popup has to
    /// outlive those repeats to be readable, but it may not sit there afterwards, so it goes
    /// two and a half seconds after the case stops talking instead of waiting out the full
    /// ten second silence rule.
    /// </summary>
    public static readonly TimeSpan WakeQuiet = TimeSpan.FromMilliseconds(2500);

    // 0.8.37: a short default backstop. 800 ms delayed a real reopen, 0 ms let a close be
    // undone by the next packet of the same movement; 250 ms is below human reopen speed.
    private TimeSpan _closeLockout = TimeSpan.FromMilliseconds(250);

    /// <summary>Backstop against a reopen within a moment of a close. Always <= 3s.</summary>
    public TimeSpan CloseLockout
    {
        get => _closeLockout;
        set => _closeLockout = value < TimeSpan.Zero
            ? TimeSpan.Zero
            : value > MaxCloseLockout ? MaxCloseLockout : value;
    }

    /// <summary>Why the last case packet did not reach the screen. For the log only.</summary>
    public string LastBlockReason { get; private set; } = string.Empty;

    /// <summary>Milliseconds the case was silent before the packet just handled, or -1.</summary>
    public int LastSilenceMs { get; private set; } = -1;

    private readonly RecentLidEdges _recentEdges = new();
    internal bool LastObservedClosingEdge { get; private set; }

    private bool _open;
    private int _openStreak;
    private int _streakCycle = -1;
    private DateTimeOffset _lastOpenAt;
    private DateTimeOffset _cycleShownAt;
    private DateTimeOffset _lastCaseAt;
    private DateTimeOffset _wakeOpenedAt;
    private int _wakeCycle = -1;
    private bool _wakeUnconfirmed;
    private DateTimeOffset _firstSeenAt;
    private DateTimeOffset _closedAt;
    private int _closedCycle = -1;
    private ulong _closedAddress;
    private int _echoStreak;
    private DateTimeOffset _echoStartedAt;
    private bool _echoOverrideAllowed;
    private bool _manuallyDismissed;
    private int _liveCycle = -1;
    private ulong _liveAddress;
    private int _suspendedCycle = -1;
    private ulong _suspendedAddress;
    private DateTimeOffset _suspendedAt;

    public bool IsOpen => _open;

    /// <summary>Continuous radio listening for the previously identified device only.
    /// No persisted wall-clock silence: restarting the watcher starts a new epoch.</summary>
    public void BeginListening(DateTimeOffset since, DateTimeOffset now)
    {
        if (_firstSeenAt == default && since != default && since <= now) _firstSeenAt = since;
    }

    /// <summary>True while a cycle is hidden only because the case went quiet.</summary>
    public bool IsSuspended => _suspendedCycle >= 0;

    /// <summary>
    /// True while a popup raised by a wake-up is inside its floor. The belt-and-braces hide
    /// in App has to ask this, or the stale closed word would undo the wake-up immediately.
    /// </summary>
    public bool WakeFloorHolds(DateTimeOffset now) => _open && _wakeOpenedAt != default && now - _wakeOpenedAt < WakeFloor;

    /// <summary>True while a wake-up popup is up that the case has not confirmed with an open word.</summary>
    public bool WakeUnconfirmed => _open && _wakeUnconfirmed;

    /// <summary>
    /// True while the popup is being held up on purpose although the word in hand says the
    /// lid is shut. The belt-and-braces hide in App has to ask this, or it would undo the
    /// wake-up and the close confirmation from the outside.
    /// </summary>
    public bool HoldsClosedWord(DateTimeOffset now) =>
        WakeFloorHolds(now) || (_open && _wakeUnconfirmed);

    /// <param name="carriesLidState">True only for case packets (bit 5 of the lid byte).</param>
    /// <param name="isCaseOpen">The lid flag: bit 3 clear.</param>
    /// <param name="cycle">The lid cycle counter, bits 0-2.</param>
    /// <param name="address">The radio address the packet came from.</param>
    /// <param name="trusted">False for packets Windows queued instead of receiving now.</param>
    /// <param name="bound">False for every device except the one this app follows.</param>
    /// <param name="provenFresh">
    /// True only when the radio timestamp proves the packet is seconds new. Such a packet
    /// cannot come from the backlog Windows replays after standby, which is minutes old, so
    /// one of them is enough to raise the popup the moment the lid counter moves.
    /// </param>
    /// <param name="explicitLid">
    /// True when the lid byte carried Apple's explicit lid bit. Retained for diagnostics;
    /// the existing silence-based wake heuristic can also act on an explicit closed word.
    /// </param>
    public LidAction Handle(bool carriesLidState, bool isCaseOpen, int cycle, ulong address, DateTimeOffset now, bool trusted = true, bool bound = true, bool provenFresh = false, bool explicitLid = false)
    {
        LastObservedClosingEdge = false;
        if (!bound) { LastBlockReason = "other device"; return LidAction.None; }
        if (!trusted) { LastBlockReason = "queued by windows"; return LidAction.None; }

        // Rejected packets must not manufacture silence for this device.
        if (_firstSeenAt == default) _firstSeenAt = now;

        // The earbuds' own advertisement. It says nothing about the lid, so it may not
        // open the popup, close it, or touch a single timer.
        // Retain only bounded negative evidence, even before lid classification.
        // This does not promote an unclassified word or change a display timer.
        LastObservedClosingEdge = _recentEdges.Observe(isCaseOpen, cycle, address, now, provenFresh, MaxStreamGap);
        if (!carriesLidState) { LastBlockReason = "earbud packet, no lid state"; return LidAction.None; }

        // Silence measured from the last word of the case, or from the start of the session
        // if it has not spoken yet. At the very first packet that difference is zero, so a
        // session can never open with a wake-up it has not earned.
        DateTimeOffset heardLast = _lastCaseAt > _firstSeenAt ? _lastCaseAt : _firstSeenAt;
        TimeSpan sinceCase = now - heardLast;
        LastSilenceMs = (int)Math.Clamp(sinceCase.TotalMilliseconds, 0, int.MaxValue);
        _lastCaseAt = now;

        // The case has just broken a long silence, and it never does that on its own: a hand
        // moved it. Its first word after sleeping can be the state it had before - see
        // WakeSilence - so it is taken as an event, not as a level, and the popup goes up at
        // once.
        //
        // 0.8.15 removes the exemption 0.8.14 gave to explicit words. It was written to keep
        // a deliberate report of a shut lid intact, but this case reports every word
        // explicitly: on 2026-09-01 at 18:46:02 it answered a 2395 s silence with 27 explicit
        // "closed" words in 3.5 s and never said "open" once, and the same happened after
        // silences of 73 s, 514 s and 6865 s. Four openings, no popup, and the trace shows
        // wakeMs=-1 on every line of the session - the rule never even armed. What a stale
        // word says cannot matter; that the case spoke at all is the event.
        //
        // A suspended cycle is the one case where the burst may really be a close: there the
        // lid was open, the case went quiet, and the popup left the screen on the silence
        // rule, so the hand on the case may well be shutting it. Nothing is woken then.
        bool freshSuspension = IsSuspended && now - _suspendedAt <= ResumeWindow;
        if (!isCaseOpen && provenFresh && !_open && !_manuallyDismissed && !freshSuspension && sinceCase >= WakeSilence && !LastObservedClosingEdge)
        {
            _open = true;
            _cycleShownAt = now;
            _openStreak = MinStreak;
            _streakCycle = cycle;
            _echoStreak = 0;
            _suspendedCycle = -1;
            _liveCycle = cycle;
            _liveAddress = address;
            _lastOpenAt = now;
            _wakeOpenedAt = now;
            _wakeCycle = cycle;
            // This door only opens for a word that says the lid is shut, so the popup it
            // raises always rests on a claim the case never made: unproven by definition.
            // An open word after a long silence needs none of this - it takes the ordinary
            // path below and shows the popup at once.
            _wakeUnconfirmed = true;
            _closedAt = default;
            _closedCycle = -1;
            _manuallyDismissed = false;
            LastBlockReason = string.Empty;
            return LidAction.Open;
        }

        if (!isCaseOpen)
        {
            // A wake-up popup may not be flicked off the screen by the very word that raised
            // it, repeated a moment later. After the floor the case is believed again.
            if (_open && _wakeUnconfirmed && cycle == _wakeCycle && _wakeOpenedAt != default && now - _wakeOpenedAt < WakeFloor)
            {
                LastBlockReason = "wake floor " + (int)(now - _wakeOpenedAt).TotalMilliseconds + "/" + (int)WakeFloor.TotalMilliseconds + "ms";
                return LidAction.Update;
            }

            // The stale snapshot that raised the wake-up, said again. The case repeats it
            // eight times a second - 27 copies in 3.5 s on 2026-09-01 at 18:46:02 - so
            // reading the second copy as a close is what took the popup off the screen while
            // the lid was still in the user's hand. Only a different cycle counter is news.
            if (_open && _wakeUnconfirmed && cycle == _wakeCycle)
            {
                LastBlockReason = "stale wake word, cycle " + cycle;
                return LidAction.Update;
            }

            _openStreak = 0;
            _suspendedCycle = -1;
            _closedCycle = cycle;
            _closedAddress = address;
            _echoOverrideAllowed = true;
            _manuallyDismissed = false;
            if (_closedAt == default || now > _closedAt) _closedAt = now;
            LastBlockReason = "lid closed";
            _wakeOpenedAt = default;
            _wakeUnconfirmed = false;
            if (!_open) return LidAction.None;
            _open = false;
            return LidAction.Close;
        }

        if (_lastOpenAt != default && now < _lastOpenAt) return _open ? LidAction.Update : LidAction.None;

        if (_open)
        {
            // The case says the lid is open, so nothing is pending any more: the wake-up is
            // confirmed and the provisional wake floor must end. Otherwise an actual
            // close arriving during that floor would be ignored.
            _lastOpenAt = now;
            _liveCycle = cycle;
            _liveAddress = address;
            _wakeUnconfirmed = false;
            _wakeOpenedAt = default;
            _wakeCycle = -1;
            LastBlockReason = string.Empty;
            return LidAction.Update;
        }

        // A cycle that was only suspended by silence was never closed: the lid is very
        // probably still open and the case simply had nothing to say. Its own next burst
        // is allowed straight back past the echo and lockout guards. A proven-fresh
        // observation resumes immediately; without a usable timestamp the two-packet
        // proof remains mandatory.
        bool resuming = _suspendedCycle >= 0
            && cycle == _suspendedCycle
            && address == _suspendedAddress
            && now - _suspendedAt <= ResumeWindow;

        // An echo is the cycle that was buried, coming from the address that buried it.
        // Apple rotates the case address every few minutes, and the counter only runs 0-7,
        // so the same number from a new address is a real opening rather than an echo.
        // Reading it as an echo cost 124 ms on 2026-08-29 at 21:40:21.
        bool echoOfClosedCycle = cycle == _closedCycle && address == _closedAddress;

        // A hand just opened the lid: the counter is not the one that was buried, the radio
        // timestamp proves the packet is seconds new, and it comes from the case this app
        // follows. Nothing is left to confirm.
        //
        // This has to be decided before the lockout below, and that order is the whole fix
        // of 0.8.9. Measured on 2026-08-29: 11 of 13 openings were held back by that lockout
        // for 104-833 ms, and all 192 packets it refused carried a counter the lid had never
        // closed on - every one of them a real opening. Repeated flips stacked it up to the
        // 6260 ms in the log at 21:43:40. A packet that cannot prove its age still goes the
        // long way round: echo gate, lockout, then the two-packet burst.
        // A three-bit cycle counter can repeat after hours off-air. A fresh, classified
        // case event after a full quiet resume window is not a short-tail echo.
        // This matches the existing wake heuristic, but only after a RADIO close;
        // a dismissed card, a stale packet or an earbud copy cannot use this door.
        bool idleReopen = echoOfClosedCycle && _echoOverrideAllowed && !_manuallyDismissed
            && sinceCase > ResumeWindow;
        if (provenFresh && (resuming || !echoOfClosedCycle || idleReopen))
        {
            _open = true;
            _cycleShownAt = now;
            _openStreak = MinStreak;
            _streakCycle = cycle;
            _echoStreak = 0;
            _suspendedCycle = -1;
            _liveCycle = cycle;
            _liveAddress = address;
            _lastOpenAt = now;
            _wakeUnconfirmed = false;
            _closedAt = default;
            _closedCycle = -1;
            _manuallyDismissed = false;
            LastBlockReason = string.Empty;
            return LidAction.Open;
        }

        if (!resuming)
        {
            // The case bumps its counter every time the lid is opened, so an open packet
            // still carrying the cycle that was just closed is an echo of the past, not a
            // new opening. It stays refused until the counter moves on or the case starts
            // advertising from a new address.
            if (echoOfClosedCycle)
            {
                if (_echoStreak == 0 || _lastOpenAt == default || now - _lastOpenAt > MaxStreamGap)
                {
                    _echoStreak = 1;
                    _echoStartedAt = now;
                }
                else
                {
                    _echoStreak++;
                }

                // Fail-open, so this rule can never be the reason the popup stops appearing:
                // if a sustained burst of this cycle really is on the air, the counter simply
                // did not move, and the popup is allowed through a fraction of a second later.
                if (!_echoOverrideAllowed || _echoStreak < EchoOverrideStreak || now - _echoStartedAt < EchoOverrideSpan)
                {
                    _openStreak = 0;
                    _lastOpenAt = now;
                    LastBlockReason = "echo of closed cycle " + cycle + " (" + _echoStreak + ")";
                    return LidAction.None;
                }

                _closedCycle = -1;
                _closedAt = default;
                _echoStreak = 0;
            }

            if (_closedAt != default && now - _closedAt < _closeLockout)
            {
                _openStreak = 0;
                _lastOpenAt = now;
                LastBlockReason = "lockout " + (int)(now - _closedAt).TotalMilliseconds + "/" + (int)_closeLockout.TotalMilliseconds + "ms";
                return LidAction.None;
            }
        }

        if (_openStreak == 0 || _streakCycle != cycle || now - _lastOpenAt > MaxStreamGap)
        {
            _openStreak = 1;
            _streakCycle = cycle;
            _lastOpenAt = now;
            LastBlockReason = (resuming ? "resume " : "") + "proof 1/" + MinStreak;
            return LidAction.None;
        }

        _openStreak++;
        _lastOpenAt = now;
        if (_openStreak < MinStreak)
        {
            LastBlockReason = (resuming ? "resume " : "") + "proof " + _openStreak + "/" + MinStreak;
            return LidAction.None;
        }

        _open = true;
        _cycleShownAt = now;
        _echoStreak = 0;
        _suspendedCycle = -1;
        _liveCycle = cycle;
        _liveAddress = address;
        _wakeUnconfirmed = false;
        _closedAt = default;
        _closedCycle = -1;
        _manuallyDismissed = false;
        LastBlockReason = string.Empty;
        return LidAction.Open;
    }

    /// <summary>
    /// True when everything the case had to say landed inside the first <see cref="BurstWindow"/>
    /// of this popup. A mute case looks like this; a lid held open does not, because the case
    /// keeps talking. It can only ever turn false as more words arrive, so the tail can only
    /// grow - which is why the recordings show no session cut short by it.
    /// </summary>
    public bool SaidItsPiece => _cycleShownAt != default && _lastCaseAt != default
        && _lastCaseAt - _cycleShownAt <= BurstWindow;

    /// <summary>How much silence this popup is allowed before it goes.</summary>
    public TimeSpan ActiveTail => SaidItsPiece ? QuietTail : StreamTimeout;

    /// <summary>True when the case went quiet while the popup was up.</summary>
    public bool ShouldTimeout(DateTimeOffset now)
    {
        if (!_open || _lastOpenAt == default) return false;

        // A wake-up the case never confirmed rests on one stale word, so it may not outstay
        // the burst that carried it: two and a half seconds after the last word it goes.
        if (_wakeUnconfirmed && _lastCaseAt != default && now - _lastCaseAt > WakeQuiet) return true;

        return now - _lastOpenAt > ActiveTail;
    }

    /// <summary>
    /// Ends a popup that a wake-up raised and the case never confirmed. Nothing is suspended
    /// and nothing is buried: the cycle was never proven, so the next wake-up has to be free
    /// to fire. Suspending it here would block exactly the openings this release is fixing.
    /// </summary>
    public void EndUnconfirmedWake(DateTimeOffset now)
    {
        _open = false;
        _openStreak = 0;
        _streakCycle = -1;
        _echoStreak = 0;
        _suspendedCycle = -1;
        _wakeOpenedAt = default;
        _wakeUnconfirmed = false;
        _lastOpenAt = now;
    }

    /// <summary>
    /// Takes the popup off the screen because the case stopped talking, and remembers the
    /// cycle so its own next burst can put it straight back. This is deliberately not a
    /// close: no lockout is armed, no cycle is buried, and the echo guard is untouched.
    /// Silence is missing evidence, not evidence of a shut lid.
    /// </summary>
    public void SuspendForSilence(DateTimeOffset now)
    {
        if (!_open) return;
        _open = false;
        _openStreak = 0;
        _streakCycle = -1;
        _echoStreak = 0;
        _wakeUnconfirmed = false;
        _wakeOpenedAt = default;
        if (_liveCycle < 0) return;
        _suspendedCycle = _liveCycle;
        _suspendedAddress = _liveAddress;
        _suspendedAt = now;
    }

    /// <summary>
    /// Hides the popup and buries the cycle now on screen, so nothing brings it back until
    /// the lid is physically opened again and the counter moves on. Used by the close
    /// button, standby and unlock - places where a human or the OS really did end the
    /// cycle. Unlike a suspension, a burial has no way out.
    /// </summary>
    /// <summary>OS lifecycle reset, not a user's dismissal. Forget radio-cycle ownership
    /// and its timers. A new listening epoch must establish its own silence.</summary>
    public void ResetForSystem(DateTimeOffset now)
    {
        _recentEdges.Reset(); LastObservedClosingEdge = false;
        _open = false; _openStreak = 0; _streakCycle = -1;
        _lastOpenAt = default; _cycleShownAt = default; _lastCaseAt = default;
        _wakeOpenedAt = default; _wakeCycle = -1; _wakeUnconfirmed = false;
        _firstSeenAt = default; _closedAt = default; _closedCycle = -1; _closedAddress = 0;
        _echoStreak = 0; _echoStartedAt = default; _echoOverrideAllowed = false;
        _manuallyDismissed = false; _liveCycle = -1; _liveAddress = 0;
        _suspendedCycle = -1; _suspendedAddress = 0; _suspendedAt = default;
        LastSilenceMs = -1; LastBlockReason = "system-reset";
    }

    internal string BlockCode => LastBlockReason switch
    {
        "" => "accepted",
        "other device" => "other-device",
        "queued by windows" => "queued",
        "earbud packet, no lid state" => "unclassified",
        "lid closed" => "closed",
        _ when LastBlockReason.StartsWith("echo", StringComparison.Ordinal) => "echo",
        _ when LastBlockReason.StartsWith("lockout", StringComparison.Ordinal) => "lockout",
        _ when LastBlockReason.StartsWith("proof", StringComparison.Ordinal) => "proof",
        _ when LastBlockReason.StartsWith("resume", StringComparison.Ordinal) => "proof",
        _ when LastBlockReason.StartsWith("wake floor", StringComparison.Ordinal) => "wake-floor",
        _ when LastBlockReason.StartsWith("stale wake", StringComparison.Ordinal) => "wake-repeat",
        _ => "unclassified"
    };

    public void ForceClosed(DateTimeOffset now)
    {
        _echoOverrideAllowed = false;
        _manuallyDismissed = true;
        if (_liveCycle >= 0)
        {
            _closedCycle = _liveCycle;
            _closedAddress = _liveAddress;
        }
        _open = false;
        _openStreak = 0;
        _echoStreak = 0;
        _streakCycle = -1;
        _suspendedCycle = -1;
        _lastOpenAt = now;
        _closedAt = now;
        _wakeOpenedAt = default;
        _wakeUnconfirmed = false;
    }

    /// <summary>
    /// One line of machine state for the trace file. Everything here is an integer in
    /// milliseconds on purpose: no decimal separators, no locale surprises, greppable.
    /// </summary>
    public string Describe(DateTimeOffset now)
    {
        int sinceCase = _lastCaseAt == default ? -1 : (int)(now - _lastCaseAt).TotalMilliseconds;
        int sinceOpen = _lastOpenAt == default ? -1 : (int)(now - _lastOpenAt).TotalMilliseconds;
        int lockoutLeft = _closedAt == default ? 0 : (int)(_closeLockout - (now - _closedAt)).TotalMilliseconds;
        if (lockoutLeft < 0) lockoutLeft = 0;
        int toTimeout = _open && _lastOpenAt != default ? (int)(ActiveTail - (now - _lastOpenAt)).TotalMilliseconds : -1;
        int sinceWake = _wakeOpenedAt == default ? -1 : (int)(now - _wakeOpenedAt).TotalMilliseconds;
        return "lidOpen=" + (_open ? 1 : 0)
            + " suspended=" + (_suspendedCycle >= 0 ? 1 : 0)
            + " streak=" + _openStreak + "/" + MinStreak
            + " cycle=" + _liveCycle
            + " closedCycle=" + _closedCycle
            + " sinceCaseMs=" + sinceCase
            + " sinceOpenMs=" + sinceOpen
            + " lockoutLeftMs=" + lockoutLeft
            + " toTimeoutMs=" + toTimeout
            + " tail=" + (SaidItsPiece ? "quiet" : "stream")
            + " silenceMs=" + LastSilenceMs
            + " wakeMs=" + sinceWake
            + " wakeHold=" + (_wakeUnconfirmed ? 1 : 0);
    }
}
