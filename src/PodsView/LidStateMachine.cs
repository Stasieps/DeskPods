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
///
/// 0.8.47 rewrites when the popup leaves and when it may come back. The report was blunt:
/// open the case and nothing happens, or the popup only appears on the close; hold it open
/// and it vanishes and pops back by itself; open it after a reboot and it takes ages. What
/// the recordings show, and what changed:
///
///   * a popup hidden by silence no longer resumes. A case lying open speaks every few dozen
///     seconds - fixture 13 is one cycle talking for thirty minutes with gaps of up to 96 s -
///     and every one of those words put the card back for another ten seconds. A cycle that
///     left the screen without a close is now spent: its own words cannot raise it again and
///     its shut word is the lid closing, never a wake-up. A new opening has a new counter.
///   * the close button marks the cycle spent in the same way, so an address rotation of the
///     same open lid cannot bring a dismissed card back either.
///   * silence before the first case word of a session is unknown, not zero. Measured from
///     the start of listening it kept the first opening after a start, a reboot or an unlock
///     from waking the popup for twenty seconds.
///   * the wake-up threshold is 10 s (was 20 s) and an unconfirmed wake-up survives 6 s of
///     quiet (was 2.5 s): in the traces the real open words follow the stale shut word after
///     4.4-5.4 s, so the old floor dropped the card just before the case confirmed it.
///   * the quiet tail is 6 s for the first 20 s of a popup and 10 s after that (was 5 s, and
///     only within 2 s): fixture 08 pauses 5.4 s early on, fixture 04 6.9 s later on.
///   * the 250 ms lockout guards only the counter that was just closed.
///   * a shut word older than the last open word, or carrying the counter of the cycle before
///     the one on screen, is a late copy and cannot take the popup away.
public sealed class LidStateMachine
{
    /// <summary>Case packets needed to show the popup. Its bursts run at about 8 Hz.</summary>
    // Show on the first believable case packet. Waiting for a second packet made
    // the visible latency depend on the case burst and failed for empty cases.
    // A packet that cannot prove its age still arms first and opens on the next one.
    private const int MinStreak = 1;

    /// <summary>Two case packets further apart than this are not one burst.</summary>
    private static readonly TimeSpan MaxStreamGap = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Silence from the case while the popup is up, once the case has been talking for longer
    /// than <see cref="BurstWindow"/>. A shut lid announces itself with a closed packet, so
    /// this is only a backstop for the case that stops talking altogether.
    /// </summary>
    public static readonly TimeSpan StreamTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The shorter tail for a popup whose case has not been talking for long. An empty case
    /// speaks while the lid moves and then says nothing at all, and there is no closed word
    /// to react to. 0.8.47: six seconds, because fixture 08 pauses 5.4 s between the first
    /// and the second open word of one cycle and five seconds dropped the card in between.
    /// </summary>
    public static readonly TimeSpan QuietTail = TimeSpan.FromSeconds(6);

    /// <summary>
    /// How long after the popup appears the short tail applies. Past this the case has proven
    /// it keeps talking while the lid is held open, and it keeps <see cref="StreamTimeout"/>:
    /// fixture 04 pauses 6.9 s more than a minute into one open lid.
    /// </summary>
    public static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long a spent cycle keeps its hold. Its stream may pause this long and still be the
    /// same stream, and for this long after the popup left nothing may wake it. Also the
    /// silence after which the counter that was closed is no longer an echo (idle reopen).
    /// </summary>
    private static readonly TimeSpan SpentWindow = TimeSpan.FromSeconds(120);

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
    /// quiet spell means a hand is on it, and its first word is often the state it had before
    /// it fell asleep: at 20:57:08 on 2026-08-30 it answered with 0x58 (shut, counter 0) and
    /// the true 0x52 only came 4.7 s later. 0.8.47 lowers the threshold from 20 s to 10 s:
    /// inside a burst the case speaks at about 8 Hz (4852 of 4900 measured gaps are under
    /// 2 s), and a case that was shut a few seconds ago and opened again must not wait.
    /// Before its first word of a session the silence counts as long: the app cannot know
    /// how long the case was quiet, and the first opening after a start, a reboot or an
    /// unlock is exactly the one the user is waiting for.
    /// </summary>
    private static readonly TimeSpan WakeSilence = TimeSpan.FromSeconds(10);

    /// <summary>The wake-up threshold in seconds, for the session header in the log.</summary>
    public static double WakeSilenceSeconds => WakeSilence.TotalSeconds;

    /// <summary>
    /// How long a popup raised by a wake-up may not be taken away by the case's own word.
    /// Without it the same stale "closed", repeated a fifth of a second later, would flick
    /// the popup off the screen before it could be read.
    /// </summary>
    private static readonly TimeSpan WakeFloor = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// How long a popup raised by a wake-up survives the case going quiet again when the
    /// case never confirmed the lid is open. 0.8.47: six seconds (was 2.5 s). The real open
    /// words arrive 4.4-5.4 s after the stale shut word in the recordings, and a card that
    /// left 2.5 s after the wake-up was gone exactly when the case confirmed the opening.
    /// </summary>
    public static readonly TimeSpan WakeQuiet = TimeSpan.FromSeconds(6);

    // 0.8.37: a short default backstop. 800 ms delayed a real reopen, 0 ms let a close be
    // undone by the next packet of the same movement; 250 ms is below human reopen speed.
    // 0.8.47: it guards only the counter that was just closed - a new counter is a new lid.
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

    /// <summary>Milliseconds the case was silent before the packet just handled, or -1 when
    /// this was its first word since listening began.</summary>
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
    private bool _holdingShutWord;
    private DateTimeOffset _closedAt;
    private int _closedCycle = -1;
    private ulong _closedAddress;
    private int _echoStreak;
    private DateTimeOffset _echoStartedAt;
    private bool _echoOverrideAllowed;
    private int _liveCycle = -1;
    private ulong _liveAddress;
    private int _spentCycle = -1;
    private ulong _spentAddress;
    private DateTimeOffset _spentAt;
    private bool _spentByHand;

    public bool IsOpen => _open;

    /// <summary>
    /// Kept for its callers. 0.8.47: silence is no longer measured from the start of
    /// listening. Before the case's first word it is unknown, and unknown counts as long -
    /// see <see cref="WakeSilence"/>.
    /// </summary>
    public void BeginListening(DateTimeOffset since, DateTimeOffset now)
    {
    }

    /// <summary>
    /// True while a cycle left the screen without a close - silence or the close button -
    /// and its own words may not bring it back. The next opening has a new counter.
    /// </summary>
    public bool IsSuspended => _spentCycle >= 0;

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
    /// wake-up and the late-copy rule from the outside.
    /// </summary>
    public bool HoldsClosedWord(DateTimeOffset now) =>
        WakeFloorHolds(now) || (_open && _wakeUnconfirmed) || (_open && _holdingShutWord);

    /// <param name="carriesLidState">True only for case packets the classifier believed.</param>
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
    /// the silence-based wake heuristic acts on an explicit closed word as well.
    /// </param>
    public LidAction Handle(bool carriesLidState, bool isCaseOpen, int cycle, ulong address, DateTimeOffset now, bool trusted = true, bool bound = true, bool provenFresh = false, bool explicitLid = false)
    {
        LastObservedClosingEdge = false;
        _holdingShutWord = false;
        if (!bound) { LastBlockReason = "other device"; return LidAction.None; }
        if (!trusted) { LastBlockReason = "queued by windows"; return LidAction.None; }

        // Retain only bounded negative evidence, even before lid classification.
        // This does not promote an unclassified word or change a display timer.
        LastObservedClosingEdge = _recentEdges.Observe(isCaseOpen, cycle, address, now, provenFresh, MaxStreamGap);
        // The earbuds' own advertisement. It says nothing about the lid, so it may not
        // open the popup, close it, or touch a single timer.
        if (!carriesLidState) { LastBlockReason = "earbud packet, no lid state"; return LidAction.None; }

        // 0.8.47: silence since the case's own last word. Before its first word of this
        // session (or since the OS reset) it is unknown, and unknown counts as long.
        bool heardBefore = _lastCaseAt != default;
        TimeSpan sinceCase = heardBefore ? now - _lastCaseAt : TimeSpan.MaxValue;
        LastSilenceMs = heardBefore ? (int)Math.Clamp(sinceCase.TotalMilliseconds, 0, int.MaxValue) : -1;
        if (!heardBefore || now > _lastCaseAt) _lastCaseAt = now;

        // A cycle that left the screen without a close is spent. Its counter is the lid that
        // is still open, from the address it was hidden on, or from a rotated address while
        // its stream has not paused for longer than SpentWindow.
        bool spent = _spentCycle >= 0 && cycle == _spentCycle
            && (address == _spentAddress || sinceCase <= SpentWindow);

        if (!isCaseOpen)
        {
            // A wake-up popup may not be flicked off the screen by the very word that raised
            // it, repeated a moment later. After the floor the case is believed again.
            if (_open && _wakeUnconfirmed && cycle == _wakeCycle && _wakeOpenedAt != default && now - _wakeOpenedAt < WakeFloor)
            {
                _holdingShutWord = true;
                LastBlockReason = "wake floor " + (int)(now - _wakeOpenedAt).TotalMilliseconds + "/" + (int)WakeFloor.TotalMilliseconds + "ms";
                return LidAction.Update;
            }

            // The stale snapshot that raised the wake-up, said again. The case repeats it
            // eight times a second - 27 copies in 3.5 s on 2026-09-01 at 18:46:02 - so
            // reading the second copy as a close is what took the popup off the screen while
            // the lid was still in the user's hand. Only a different cycle counter is news.
            if (_open && _wakeUnconfirmed && cycle == _wakeCycle)
            {
                _holdingShutWord = true;
                LastBlockReason = "stale wake word, cycle " + cycle;
                return LidAction.Update;
            }

            // 0.8.47: a late copy. The counter moves when the lid opens, so "shut" with the
            // counter of the cycle before the one on screen is the past, and so is a shut word
            // stamped earlier than the last open word. BLE does not deliver in order, and
            // reading such a copy as a close made a rapid flip flicker off and back on.
            if (_open && ((_lastOpenAt != default && now < _lastOpenAt)
                || (_liveCycle >= 0 && cycle == ((_liveCycle + 7) & 7))))
            {
                _holdingShutWord = true;
                LastBlockReason = "late shut word, cycle " + cycle;
                return LidAction.Update;
            }

            // The case has just broken a silence, and it never does that on its own: a hand
            // moved it. Its first word after sleeping can be the state it had before - see
            // WakeSilence - so it is taken as an event, not as a level, and the popup goes up
            // at once. What a stale word says cannot matter; that the case spoke at all is
            // the event (0.8.15: this case marks every word explicit).
            //
            // Not after a spent cycle, though: there the lid was left open, and a hand on the
            // case is far more likely to be shutting it. Its own counter never wakes anything,
            // and no counter does for SpentWindow after the popup left.
            bool spentHold = spent || (_spentCycle >= 0 && now >= _spentAt && now - _spentAt <= SpentWindow);
            if (!_open && provenFresh && sinceCase >= WakeSilence && !LastObservedClosingEdge && !spentHold)
            {
                _open = true;
                _cycleShownAt = now;
                _openStreak = MinStreak;
                _streakCycle = cycle;
                _echoStreak = 0;
                ClearSpent();
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
                LastBlockReason = string.Empty;
                return LidAction.Open;
            }

            // A real close: the counter is buried, and its stale open copies are echoes.
            _openStreak = 0;
            _closedCycle = cycle;
            _closedAddress = address;
            _echoOverrideAllowed = true;
            if (_closedAt == default || now > _closedAt) _closedAt = now;
            ClearSpent();
            LastBlockReason = "lid closed";
            _wakeOpenedAt = default;
            _wakeUnconfirmed = false;
            _wakeCycle = -1;
            if (!_open) return LidAction.None;
            _open = false;
            return LidAction.Close;
        }

        if (_lastOpenAt != default && now < _lastOpenAt)
        {
            LastBlockReason = "late open word, cycle " + cycle;
            return _open ? LidAction.Update : LidAction.None;
        }

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

        // 0.8.47: the lid of a spent cycle is still open; its words are not a new opening.
        // The address follows the stream, so a rotation does not lift the hold either.
        if (spent)
        {
            _spentAddress = address;
            _openStreak = 0;
            _lastOpenAt = now;
            LastBlockReason = (_spentByHand ? "spent, dismissed cycle " : "spent, hidden cycle ") + cycle;
            return LidAction.None;
        }

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
        // closed on - every one of them a real opening. A packet that cannot prove its age
        // still goes the long way round: echo gate, lockout, then the two-packet burst.
        // A three-bit cycle counter can repeat after hours off-air. A fresh, classified
        // case event after a full quiet window is not a short-tail echo - but only after a
        // RADIO close; a dismissed card, a stale packet or an earbud copy cannot use this door.
        bool idleReopen = echoOfClosedCycle && _echoOverrideAllowed && sinceCase > SpentWindow;
        if (provenFresh && (!echoOfClosedCycle || idleReopen))
        {
            _open = true;
            _cycleShownAt = now;
            _openStreak = MinStreak;
            _streakCycle = cycle;
            _echoStreak = 0;
            ClearSpent();
            _liveCycle = cycle;
            _liveAddress = address;
            _lastOpenAt = now;
            _wakeUnconfirmed = false;
            _closedAt = default;
            _closedCycle = -1;
            LastBlockReason = string.Empty;
            return LidAction.Open;
        }

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

        // 0.8.47: only the counter that was just closed. A different counter is a new lid.
        if (_closedAt != default && cycle == _closedCycle && now - _closedAt < _closeLockout)
        {
            _openStreak = 0;
            _lastOpenAt = now;
            LastBlockReason = "lockout " + (int)(now - _closedAt).TotalMilliseconds + "/" + (int)_closeLockout.TotalMilliseconds + "ms";
            return LidAction.None;
        }

        if (_openStreak == 0 || _streakCycle != cycle || now - _lastOpenAt > MaxStreamGap)
        {
            _openStreak = 1;
            _streakCycle = cycle;
            _lastOpenAt = now;
            LastBlockReason = "proof 1/" + MinStreak;
            return LidAction.None;
        }

        _openStreak++;
        _lastOpenAt = now;
        if (_openStreak < MinStreak)
        {
            LastBlockReason = "proof " + _openStreak + "/" + MinStreak;
            return LidAction.None;
        }

        _open = true;
        _cycleShownAt = now;
        _echoStreak = 0;
        ClearSpent();
        _liveCycle = cycle;
        _liveAddress = address;
        _wakeUnconfirmed = false;
        _closedAt = default;
        _closedCycle = -1;
        LastBlockReason = string.Empty;
        return LidAction.Open;
    }

    /// <summary>
    /// True while everything the case said landed inside the first <see cref="BurstWindow"/>
    /// of this popup. It can only ever turn false as more words arrive, so the tail can only
    /// grow.
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
        // the burst that carried it: WakeQuiet after the last word it goes.
        if (_wakeUnconfirmed && _lastCaseAt != default && now - _lastCaseAt > WakeQuiet) return true;

        return now - _lastOpenAt > ActiveTail;
    }

    /// <summary>
    /// Ends a popup that a wake-up raised and the case never confirmed. Nothing is spent and
    /// nothing is buried: the cycle was never proven, so the next wake-up has to be free to
    /// fire.
    /// </summary>
    public void EndUnconfirmedWake(DateTimeOffset now)
    {
        _open = false;
        _openStreak = 0;
        _streakCycle = -1;
        _echoStreak = 0;
        _wakeOpenedAt = default;
        _wakeUnconfirmed = false;
        _wakeCycle = -1;
        _lastOpenAt = now;
    }

    /// <summary>
    /// Takes the popup off the screen because the case stopped talking. This is not a close:
    /// no lockout is armed and no cycle is buried, because silence is missing evidence, not
    /// evidence of a shut lid. 0.8.47: the cycle is spent, though - its lid is very probably
    /// still open, and it may not pop the card back up every time it says something.
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
        _wakeCycle = -1;
        if (_liveCycle < 0) return;
        MarkSpent(_liveCycle, _liveAddress, now, byHand: false);
    }

    /// <summary>OS lifecycle reset, not a user's dismissal. Forget radio-cycle ownership
    /// and its timers. A new listening epoch must establish its own silence.</summary>
    public void ResetForSystem(DateTimeOffset now)
    {
        _recentEdges.Reset(); LastObservedClosingEdge = false;
        _open = false; _openStreak = 0; _streakCycle = -1;
        _lastOpenAt = default; _cycleShownAt = default; _lastCaseAt = default;
        _wakeOpenedAt = default; _wakeCycle = -1; _wakeUnconfirmed = false; _holdingShutWord = false;
        _closedAt = default; _closedCycle = -1; _closedAddress = 0;
        _echoStreak = 0; _echoStartedAt = default; _echoOverrideAllowed = false;
        _liveCycle = -1; _liveAddress = 0;
        ClearSpent();
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
        _ when LastBlockReason.StartsWith("wake floor", StringComparison.Ordinal) => "wake-floor",
        _ when LastBlockReason.StartsWith("stale wake", StringComparison.Ordinal) => "wake-repeat",
        _ when LastBlockReason.StartsWith("spent", StringComparison.Ordinal) => "spent",
        _ when LastBlockReason.StartsWith("late", StringComparison.Ordinal) => "late",
        _ => "unclassified"
    };

    /// <summary>
    /// Hides the popup and buries the cycle now on screen: its counter is an echo from the
    /// address it was dismissed on, and 0.8.47 also marks it spent, so neither its own words,
    /// an address rotation nor its shut word bring the card back. Used by the close button
    /// only - App calls it while the lid machine owns the popup, never for a card shown by
    /// hand. The next opening carries a new counter and shows at once.
    /// </summary>
    public void ForceClosed(DateTimeOffset now)
    {
        _echoOverrideAllowed = false;
        if (_liveCycle >= 0)
        {
            _closedCycle = _liveCycle;
            _closedAddress = _liveAddress;
            MarkSpent(_liveCycle, _liveAddress, now, byHand: true);
        }
        _open = false;
        _openStreak = 0;
        _echoStreak = 0;
        _streakCycle = -1;
        _lastOpenAt = now;
        _closedAt = now;
        _wakeOpenedAt = default;
        _wakeUnconfirmed = false;
        _wakeCycle = -1;
    }

    private void MarkSpent(int cycle, ulong address, DateTimeOffset now, bool byHand)
    {
        _spentCycle = cycle;
        _spentAddress = address;
        _spentAt = now;
        _spentByHand = byHand;
    }

    private void ClearSpent()
    {
        _spentCycle = -1;
        _spentAddress = 0;
        _spentAt = default;
        _spentByHand = false;
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
            + " suspended=" + (_spentCycle >= 0 ? 1 : 0)
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
