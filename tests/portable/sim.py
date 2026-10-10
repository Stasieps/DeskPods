#!/usr/bin/env python3
"""Model of the popup logic (0.8.7, mirrored up to 0.8.47), replayed against the shape of a real capture.

The capture that produced this design (three minutes, 401 packets, one pair of
AirPods Pro 2) contained two transmitters sharing one identity:

    earbuds  lid byte 0x10, every 0.4-2.2s, bit 3 clear, no lid state
    case     lid byte 0x3X, bursts at about 8 Hz, bit 3 is the lid,
             bits 0-2 a counter incremented on every opening

Every scenario below asserts the two things that matter: the popup appears
the moment the lid opens, and it never comes back on its own once the lid is shut.
"""
import random

LOCKOUT = 0.25
MAX_LOCKOUT = 3.0
MIN_STREAK = 2
MAX_GAP = 1.5
# 0.8.7: the case bursts only when it has news, so silence is not a shut lid. Ten
# seconds of it take the popup down without burying the cycle.
TIMEOUT = 10.0
# 0.8.16: an empty case says its piece in one burst and then goes mute for good, and there
# is no closed word to react to - no packet in four days reports both buds out. A cycle that
# said everything inside BURST_WINDOW of the popup appearing gets the short QUIET_TAIL; a
# case still talking after that keeps the full TIMEOUT.
# 0.8.47: QUIET_TAIL 5 -> 6 s and BURST_WINDOW 2 -> 20 s (LidStateMachine.QuietTail and
# BurstWindow): a hand holding the lid open keeps the case talking, so the card stays;
# put down, it goes 6 s after the last word.
QUIET_TAIL = 6.0
BURST_WINDOW = 20.0
# 0.8.47: a cycle that left the screen without a close is SPENT - its lid is very probably
# still open, so its words may not pop the card back up (no pop-back). The hold follows the
# stream across an address rotation while it has not paused for longer than SPENT_WINDOW,
# and no shut word wakes anything for SPENT_WINDOW after the popup left. Replaces the
# 0.8.7 resume window of the same length.
SPENT_WINDOW = 120.0
ECHO_OVERRIDE = 5
ECHO_SPAN = 0.8
# 0.8.14: the case is off the air unless a hand moves it, so its first word after a long
# silence is an event and not a level. 0.8.47: 20 -> 10 s (LidStateMachine.WakeSilence),
# and before the case's first word since start or an OS reset the silence is UNKNOWN,
# which counts as long - the first opening after start, boot or unlock shows at once.
WAKE_SILENCE = 10.0
WAKE_FLOOR = 1.5
# The fixtures measure popup delay from the wake-up with their own threshold, so a
# fixture can be replayed with the rule removed and still measure from the same packet.
WAKE_MEASURE = 20.0
# 0.8.15: a popup raised by a wake-up rests on one stale word, so repeats of that same
# word cannot take it away. It ends when the case stops talking instead.
# 0.8.47: 2.5 -> 6 s (LidStateMachine.WakeQuiet): the real open word came 4.4-5.4 s
# after the wake-up in the recordings, and 2.5 s took the card away before it.
WAKE_QUIET = 6.0


class Lid:
    """Mirror of LidStateMachine (0.8.47)."""

    def __init__(self, lockout=LOCKOUT):
        self.lockout = min(lockout, MAX_LOCKOUT)
        self.open = False
        self.streak = 0
        self.streak_cycle = -1
        self.last_open = None
        self.closed_at = None
        self.closed_cycle = -1
        self.closed_addr = None
        self.live_cycle = -1
        self.live_addr = None
        self.echo = 0
        self.echo_start = None
        self.echo_override = False
        self.spent_cycle = -1
        self.spent_addr = None
        self.spent_at = None
        self.spent_by_hand = False
        self.last_case = None
        self.last_silence = None  # LastSilenceMs; None = unknown (-1 in C#)
        self.wake_open = None
        self.wake_cycle = -1
        self.wake_unconfirmed = False
        self.cycle_shown = None
        self.raw_opens = {}
        self.closing_edge = False

    def clear_spent(self):
        self.spent_cycle, self.spent_addr, self.spent_at, self.spent_by_hand = -1, None, None, False

    def mark_spent(self, cycle, addr, now, by_hand):
        self.spent_cycle, self.spent_addr, self.spent_at, self.spent_by_hand = cycle, addr, now, by_hand

    def handle(self, carries, is_open, cycle, addr, now, trusted=True, bound=True, proven_fresh=False,
               explicit_lid=False):
        self.closing_edge = False
        if not bound or not trusted:
            return 'none'
        # RecentLidEdges: bounded negative evidence, kept even before classification.
        observed_closing_edge = False
        if proven_fresh and addr and 0 <= cycle <= 7:
            self.raw_opens = {k:v for k,v in self.raw_opens.items() if 0 <= now-v[1] <= MAX_GAP}
            if is_open:
                self.raw_opens[addr] = (cycle,now)
                if len(self.raw_opens) > 32:
                    del self.raw_opens[min(self.raw_opens,key=lambda k:self.raw_opens[k][1])]
            else:
                observed_closing_edge = addr in self.raw_opens and self.raw_opens[addr][0] == cycle
                self.raw_opens.pop(addr,None)
        self.closing_edge = observed_closing_edge
        if not carries:
            return 'none'
        # 0.8.47: silence since the case's own last word. Before its first word since start
        # or an OS reset it is unknown, and unknown counts as long.
        heard_before = self.last_case is not None
        since_case = now - self.last_case if heard_before else float('inf')
        self.last_silence = since_case if heard_before else None
        if not heard_before or now > self.last_case:
            self.last_case = now
        # A cycle that left the screen without a close is spent: its counter from the address
        # it was hidden on, or from a rotated address while its stream has not paused long.
        spent = (self.spent_cycle >= 0 and cycle == self.spent_cycle
                 and (addr == self.spent_addr or since_case <= SPENT_WINDOW))
        if not is_open:
            if (self.open and self.wake_unconfirmed and cycle == self.wake_cycle
                    and self.wake_open is not None and now - self.wake_open < WAKE_FLOOR):
                return 'update'
            # The stale snapshot that raised the wake-up, said again: not news until the
            # counter moves.
            if self.open and self.wake_unconfirmed and cycle == self.wake_cycle:
                return 'update'
            # 0.8.47: a late copy - "shut" under the counter before the one on screen, or
            # stamped before the last open word. BLE does not deliver in order.
            if self.open and ((self.last_open is not None and now < self.last_open)
                              or (self.live_cycle >= 0 and cycle == (self.live_cycle + 7) & 7)):
                return 'update'
            # The wake-up door: the case broke a silence, so a hand moved it. Not after a
            # spent cycle - its own counter never wakes, and no counter does for SPENT_WINDOW.
            spent_hold = spent or (self.spent_cycle >= 0 and self.spent_at is not None
                                   and 0 <= now - self.spent_at <= SPENT_WINDOW)
            if (not self.open and proven_fresh and since_case >= WAKE_SILENCE
                    and not observed_closing_edge and not spent_hold):
                self.open = True
                self.cycle_shown = now
                self.streak = MIN_STREAK
                self.streak_cycle = cycle
                self.echo = 0
                self.clear_spent()
                self.live_cycle, self.live_addr = cycle, addr
                self.last_open = now
                self.wake_open = now
                self.wake_cycle = cycle
                self.wake_unconfirmed = True
                self.closed_at = None
                self.closed_cycle = -1
                return 'open'
            # A real close: the counter is buried, and its stale open copies are echoes.
            self.streak = 0
            self.closed_cycle = cycle
            self.closed_addr = addr
            self.echo_override = True
            if self.closed_at is None or now > self.closed_at:
                self.closed_at = now
            self.clear_spent()
            self.wake_open = None
            self.wake_unconfirmed = False
            self.wake_cycle = -1
            if not self.open:
                return 'none'
            self.open = False
            return 'close'
        if self.last_open is not None and now < self.last_open:
            return 'update' if self.open else 'none'
        if self.open:
            self.last_open = now
            self.live_cycle, self.live_addr = cycle, addr
            self.wake_unconfirmed = False
            self.wake_open = None
            self.wake_cycle = -1
            return 'update'
        # 0.8.47: the lid of a spent cycle is still open; its words are not a new opening.
        if spent:
            self.spent_addr = addr
            self.streak = 0
            self.last_open = now
            return 'none'
        # An echo is the buried cycle coming back from the address that buried it. The
        # counter only runs 0-7 and Apple rotates the case address, so the same number from
        # a new address is a real opening (0.8.9).
        echo_of_closed = cycle == self.closed_cycle and addr == self.closed_addr
        # 0.8.9 instant path: decided before the lockout. A three-bit counter can repeat
        # after a long quiet, but only after a RADIO close (echo_override).
        idle_reopen = echo_of_closed and self.echo_override and since_case > SPENT_WINDOW
        if proven_fresh and (not echo_of_closed or idle_reopen):
            self.open = True
            self.cycle_shown = now
            self.streak = MIN_STREAK
            self.streak_cycle = cycle
            self.echo = 0
            self.clear_spent()
            self.live_cycle, self.live_addr = cycle, addr
            self.last_open = now
            self.wake_unconfirmed = False
            self.closed_at = None
            self.closed_cycle = -1
            return 'open'
        if echo_of_closed:
            if self.echo == 0 or self.last_open is None or now - self.last_open > MAX_GAP:
                self.echo, self.echo_start = 1, now
            else:
                self.echo += 1
            if not self.echo_override or self.echo < ECHO_OVERRIDE or now - self.echo_start < ECHO_SPAN:
                self.streak = 0
                self.last_open = now
                return 'none'
            self.closed_cycle = -1
            self.closed_at = None
            self.echo = 0
        # 0.8.47: the lockout covers the counter that was just closed, nothing else.
        if self.closed_at is not None and cycle == self.closed_cycle and now - self.closed_at < self.lockout:
            self.streak = 0
            self.last_open = now
            return 'none'
        if self.streak == 0 or self.streak_cycle != cycle or now - self.last_open > MAX_GAP:
            self.streak = 1
            self.streak_cycle = cycle
            self.last_open = now
            return 'none'
        self.streak += 1
        self.last_open = now
        if self.streak < MIN_STREAK:
            return 'none'
        self.open = True
        self.cycle_shown = now
        self.echo = 0
        self.clear_spent()
        self.live_cycle, self.live_addr = cycle, addr
        self.wake_unconfirmed = False
        self.closed_at = None
        self.closed_cycle = -1
        return 'open'

    def said_its_piece(self):
        """Mirror of LidStateMachine.SaidItsPiece: the whole burst landed at the start."""
        return (self.cycle_shown is not None and self.last_case is not None
                and self.last_case - self.cycle_shown <= BURST_WINDOW)

    def tail(self):
        """How much silence this popup is allowed: short for a mute case, full for a talker."""
        return QUIET_TAIL if self.said_its_piece() else TIMEOUT

    def timed_out(self, now):
        if not self.open or self.last_open is None:
            return False
        # A wake-up the case never confirmed may not outstay the burst that carried it.
        if self.wake_unconfirmed and self.last_case is not None and now - self.last_case > WAKE_QUIET:
            return True
        return now - self.last_open > self.tail()

    def end_unconfirmed_wake(self, now):
        """A wake-up nobody confirmed: nothing is spent, so the next one can fire."""
        self.open = False
        self.streak = 0
        self.streak_cycle = -1
        self.echo = 0
        self.wake_open = None
        self.wake_unconfirmed = False
        self.wake_cycle = -1
        self.last_open = now

    def pending_due(self):
        """When the watchdog next has something to do, or None."""
        if not self.open:
            return None
        if self.wake_unconfirmed and self.last_case is not None:
            return self.last_case + WAKE_QUIET
        if self.last_open is not None:
            return self.last_open + self.tail()
        return None

    def tick(self, now):
        """App's 250 ms watchdog: ends an unconfirmed wake-up, or a cycle gone quiet."""
        done = []
        while True:
            due = self.pending_due()
            if due is None or due > now:
                return done
            if self.wake_unconfirmed:
                self.end_unconfirmed_wake(due)
                done.append(('wake-quiet', due))
            else:
                self.suspend_for_silence(due)
                done.append(('silence', due))

    def suspend_for_silence(self, now):
        """Silence hides the popup without burying the cycle; 0.8.47 marks it spent."""
        if not self.open:
            return
        self.open = False
        self.streak = 0
        self.streak_cycle = -1
        self.echo = 0
        self.wake_open = None
        self.wake_unconfirmed = False
        self.wake_cycle = -1
        if self.live_cycle >= 0:
            self.mark_spent(self.live_cycle, self.live_addr, now, by_hand=False)

    def reset_for_system(self, now):
        self.__init__(self.lockout)

    def force_closed(self, now):
        """The close button: buries the cycle on screen and (0.8.47) marks it spent."""
        self.echo_override = False
        if self.live_cycle >= 0:
            self.closed_cycle = self.live_cycle
            self.closed_addr = self.live_addr
            self.mark_spent(self.live_cycle, self.live_addr, now, by_hand=True)
        self.open = False
        self.streak = 0
        self.echo = 0
        self.streak_cycle = -1
        self.last_open = now
        self.closed_at = now
        self.wake_open = None
        self.wake_unconfirmed = False
        self.wake_cycle = -1


EARBUD = 0x49DAA4EFA596
CASE_A = 0x7EE46A4935D5
CASE_B = 0x6FDAFCD3BA2D
checks = 0
fails = []


def check(cond, what):
    global checks
    checks += 1
    if not cond:
        fails.append(what)


def earbud_stream(t0, t1, rng):
    """The all-day trickle from the pods themselves."""
    out, t = [], t0
    while t < t1:
        out.append((t, False, True, 0, EARBUD))
        t += rng.uniform(0.37, 2.24)
    return out


def case_burst(t0, is_open, cycle, addr, n, rng):
    out, t = [], t0
    for _ in range(n):
        out.append((t, True, is_open, cycle, addr))
        t += rng.uniform(0.08, 0.72)
    return out, t


# 1. Real cycles: open, look, close, wait. Nothing may pop up during the waiting.
for seed in range(60):
    rng = random.Random(seed)
    lid = Lid()
    addr = CASE_A if seed % 2 else CASE_B
    now = 0.0
    cycle = rng.randrange(8)
    for _ in range(6):
        events = earbud_stream(now, now + 4, rng)
        opened_at = now + 4
        burst, after = case_burst(opened_at, True, cycle, addr, rng.choice([3, 9, 24, 57]), rng)
        events += burst
        shut_at = after + rng.uniform(0.2, 1.0)
        shut, after2 = case_burst(shut_at, False, cycle, addr, rng.choice([3, 5, 20]), rng)
        events += shut
        # stale open packets of the cycle just closed, plus the earbud trickle
        events += [(after2 + 2.0 + i * rng.uniform(2.0, 8.0), True, True, cycle, addr) for i in range(rng.randrange(0, 6))]
        events += earbud_stream(after2, after2 + 40, rng)
        events.sort(key=lambda e: e[0])
        shown_at = None
        for t, carries, is_open, c, a in events:
            lid.tick(t)
            act = lid.handle(carries, is_open, c, a, t, proven_fresh=True)
            if act == 'open':
                if shown_at is None:
                    shown_at = t
                check(t >= opened_at, 'popup opened before the lid did')
                check(t <= shut_at, 'popup opened after the lid was shut')
            if act == 'close':
                check(t >= shut_at, 'popup closed before the lid did')
            if lid.timed_out(t):
                lid.suspend_for_silence(t)
        check(shown_at is not None, 'a real opening did not show the popup')
        if shown_at is not None:
            check(shown_at - opened_at < 0.3, 'popup took longer than 0.3s (%.2f)' % (shown_at - opened_at))
        check(not lid.open, 'popup still up while the case is shut')
        now = events[-1][0] + rng.uniform(1, 30)
        cycle = (cycle + 1) % 8

# 2. The address rotates and the counter repeats: a real opening must survive it.
for delay in (3.5, 5.0, 8.0, 15.0, 19.0, 21.0, 60.0):
    lid = Lid()
    lid.handle(True, True, 2, CASE_A, 0.0)
    check(lid.handle(True, True, 2, CASE_A, 0.15) == 'open', 'setup')
    lid.handle(True, False, 2, CASE_A, 1.0)
    check(not lid.open, 'a closed word must still end the cycle')
    lid.handle(True, True, 2, CASE_B, 1.0 + delay)
    check(lid.handle(True, True, 2, CASE_B, 1.15 + delay) == 'open', 'a rotated address must be able to open the popup')

# 3. Same address, same counter: an echo, for as long as the guard lasts.
for delay in (0.1, 1.0, 3.5, 10.0, 19.5, 45.0, 300.0):
    lid = Lid()
    lid.handle(True, True, 4, CASE_A, 0.0)
    lid.handle(True, True, 4, CASE_A, 0.15)
    lid.handle(True, False, 4, CASE_A, 2.0)
    for i in range(30):
        act = lid.handle(True, True, 4, CASE_A, 2.0 + delay + i * 2.5)
        check(act != 'open', 'a lone echo of the closed cycle reopened the popup after %.1fs' % delay)

# 3b. Fail-open: a sustained burst of the closed cycle is not an echo any more.
lid = Lid()
lid.handle(True, True, 4, CASE_A, 0.0)
lid.handle(True, True, 4, CASE_A, 0.15)
lid.handle(True, False, 4, CASE_A, 2.0)
acts = []
for i in range(14):
    at = 6 + i * 0.15
    lid.tick(at)
    acts.append(lid.handle(True, True, 4, CASE_A, at))
check('open' in acts, 'a sustained burst of the closed cycle must not be lost for ever')

# 4. Nothing but earbuds, for an hour.
rng = random.Random(99)
lid = Lid()
for t, carries, is_open, c, a in earbud_stream(0, 3600, rng):
    check(lid.handle(carries, is_open, c, a, t) != 'open', 'the earbud trickle opened the popup')

# 5. Wake-up backlog and a neighbour.
lid = Lid()
for i in range(400):
    check(lid.handle(True, True, 1, CASE_A, i * 0.02, trusted=False) != 'open', 'a queued packet opened the popup')
for i in range(400):
    check(lid.handle(True, True, 1, 0xAAAA, 100 + i * 0.2, bound=False) != 'open', 'a neighbour opened the popup')

# 6. Dismissed by the close button: gone until the lid physically moves again.
lid = Lid()
lid.handle(True, True, 6, CASE_A, 0.0)
lid.handle(True, True, 6, CASE_A, 0.15)
lid.force_closed(0.5)
for i in range(200):
    check(lid.handle(True, True, 6, CASE_A, 0.6 + i * 0.1) != 'open', 'a dismissed cycle came back')
lid.handle(True, True, 7, CASE_A, 30.0)
check(lid.handle(True, True, 7, CASE_A, 30.15) == 'open', 'the next opening after a dismissal must work')

# 7. Counter wrap 7 -> 0.
lid = Lid()
lid.handle(True, True, 7, CASE_A, 0.0)
lid.handle(True, True, 7, CASE_A, 0.15)
lid.handle(True, False, 7, CASE_A, 1.0)
lid.tick(4.5)
lid.handle(True, True, 0, CASE_A, 4.5)
check(lid.handle(True, True, 0, CASE_A, 4.65) == 'open', 'the counter wrapping to zero must still be a new cycle')

# 8. Cold start with the lid already open.
lid = Lid()
lid.handle(True, True, 3, CASE_A, 0.0)
check(lid.handle(True, True, 3, CASE_A, 0.2) == 'open', 'the popup must appear if the app starts with the lid open')

# 0.8.14. Measured on 2026-08-30 at 20:57:08: after 108 minutes of silence the case answered
# its wake-up with the state it had before sleeping - lid 0x58, counter 0 - and the true open
# word only came 4.7 s later, after a second lid movement by hand.
lid = Lid()
# 0.8.47: the first word a session hears breaks an unknown silence, and unknown counts as
# long - after start, boot or unlock the first movement of the case shows at once.
check(lid.handle(True, False, 4, CASE_A, 0.0, proven_fresh=True) == 'open',
      'the first word a session hears must wake the popup')
check(lid.tick(0.0 + WAKE_QUIET + 0.1) and not lid.open,
      'an unconfirmed first-word wake ends when the case stops talking')
check(lid.handle(True, False, 0, CASE_A, 100.0, proven_fresh=True) == 'open',
      'the first word of a case that has been quiet must raise the popup')
check(lid.handle(True, False, 0, CASE_A, 100.4, proven_fresh=True) == 'update',
      'the same stale word must not flick the popup off the screen')
# 0.8.15 corrects what 0.8.14 expected next. It believed the case once the 1.5 s floor had
# passed, but on 2026-09-01 at 18:46:02 the case answered a 2395 s silence with 27 copies of
# one closed word over 3.5 s and never said 'open' at all: believing the copies is exactly
# what took the popup off the screen. The word the wake-up was raised on is not evidence,
# however often it is repeated - only a different counter, or silence, ends the popup.
check(lid.handle(True, False, 0, CASE_A, 102.0, proven_fresh=True) == 'update',
      'a repeat of the stale word the wake-up was raised on is not a close')
check(lid.handle(True, False, 0, CASE_A, 103.0, proven_fresh=True) == 'update',
      'and it is still not a close on the twentieth repeat')
check(lid.tick(103.0 + WAKE_QUIET + 0.1) and not lid.open,
      'a wake-up the case never confirms ends when the case stops talking')

# A different counter is news, and an explicit word carries it at once.
lid = Lid()
lid.handle(True, False, 0, CASE_A, 0.0, proven_fresh=True)
lid.tick(10.0)  # 0.8.47: that first word woke the popup; let the watchdog end it
check(lid.handle(True, False, 0, CASE_A, 100.0, proven_fresh=True) == 'open', 'setup')
check(lid.handle(True, False, 1, CASE_A, 102.0, proven_fresh=True, explicit_lid=True) == 'close',
      'a shut lid reported under a new counter must take the popup away')

# 0.8.15. The exemption 0.8.14 gave to explicit words is measured wrong on this hardware:
# the case marks every word explicit, so the wake-up never fired once in the whole session
# (wakeMs=-1 on every trace line). Four openings after silences of 73 s, 514 s, 2395 s and
# 6865 s produced no popup at all, and two of those bursts contained no open word at all -
# 19 and 27 closed words in a row - so there was nothing else left to wait for.
lid = Lid()
lid.handle(True, False, 3, CASE_A, 0.0, proven_fresh=True, explicit_lid=True)
lid.tick(10.0)  # 0.8.47: that first word woke the popup; let the watchdog end it
check(lid.handle(True, False, 3, CASE_A, 100.0, proven_fresh=True, explicit_lid=True) == 'open',
      'an explicit word after a long silence is the case waking up, not reporting a shut lid')

# The one case that is left alone: the popup was lost to silence with the lid open, so the
# hand on the case may be shutting it. 0.8.47: that cycle is spent - its own shut word never
# wakes, and no shut word does for SPENT_WINDOW after the popup left.
lid = Lid()
lid.handle(True, True, 2, CASE_A, 0.0, proven_fresh=True)
lid.suspend_for_silence(30.0)
check(lid.handle(True, False, 2, CASE_A, 60.0, proven_fresh=True, explicit_lid=True) == 'none',
      'a case whose open cycle was just suspended may be being shut, so nothing is woken')
lid = Lid()
lid.handle(True, True, 2, CASE_A, 0.0, proven_fresh=True)
lid.suspend_for_silence(30.0)
check(lid.handle(True, False, 2, CASE_A, 30.0 + SPENT_WINDOW + 10.0, proven_fresh=True,
                 explicit_lid=True) == 'none',
      'the shut word of a spent cycle may never wake the popup - its lid was left open')
lid = Lid()
lid.handle(True, True, 2, CASE_A, 0.0, proven_fresh=True)
lid.suspend_for_silence(30.0)
check(lid.handle(True, False, 3, CASE_A, 60.0, proven_fresh=True) == 'none',
      'no shut word may wake anything within SPENT_WINDOW of a spent popup')
lid = Lid()
lid.handle(True, True, 2, CASE_A, 0.0, proven_fresh=True)
lid.suspend_for_silence(30.0)
check(lid.handle(True, False, 3, CASE_A, 30.0 + SPENT_WINDOW + 10.0, proven_fresh=True) == 'open',
      'past SPENT_WINDOW another counter breaking a silence is a wake-up again')

# Inside a burst the packets are a fraction of a second apart, so nothing there may wake.
# An explicit closed word still hides the popup on the spot - closing the lid by hand has
# to stay instant. An inferred one waits out CloseConfirm on the watchdog instead.
lid = Lid()
lid.handle(True, True, 2, CASE_A, 0.0, proven_fresh=True)
check(lid.handle(True, False, 2, CASE_A, 0.3, proven_fresh=True, explicit_lid=True) == 'close',
      'an explicit closed word inside a burst still closes the popup at once')
lid = Lid()
lid.handle(True, True, 2, CASE_A, 0.0, proven_fresh=True)
# Every closed word still hides the popup at once. The four flickers in the field looked
# like noise until the cycle counter was read: 0x52#2 -> 0x5A#2 -> 0x53#3 is a hand
# clicking the lid, and delaying those closes would only make shutting the case feel slow.
lid = Lid()
lid.handle(True, True, 2, CASE_A, 0.0, proven_fresh=True, explicit_lid=True)
check(lid.handle(True, False, 2, CASE_A, 0.3, proven_fresh=True) == 'close',
      'a closed word inside a burst hides the popup at once')
check(lid.handle(True, True, 2, CASE_A, 0.6, proven_fresh=True) != 'open',
      'a shut lid stays shut inside a burst')

# A packet Windows queued cannot prove it is new, so it may not wake anything.
lid = Lid()
lid.handle(True, False, 1, CASE_A, 0.0, proven_fresh=True)
lid.tick(10.0)  # 0.8.47: that first word woke the popup; let the watchdog end it
check(lid.handle(True, False, 1, CASE_A, 100.0, proven_fresh=False) == 'none',
      'a packet that cannot prove its age must not wake the popup')

# A session that starts while the case is off the air: the earbuds chatter, the case says
# nothing, and when it finally speaks a hand is on it. Both such moments in the recordings
# were followed by a real opening within 2.2 s.
lid = Lid()
lid.handle(False, False, 0, EARBUD, 0.0)
check(lid.handle(True, False, 5, CASE_A, 25.0, proven_fresh=True) == 'open',
      'a case silent through the first 25 s of a session is waking, not settling')

# 0.8.47: earbud packets carry no lid and measure no silence, so the case's first word still
# wakes; five seconds of quiet AFTER the case has spoken is normal and may not.
lid = Lid()
lid.handle(False, False, 0, EARBUD, 0.0)
check(lid.handle(True, False, 5, CASE_A, 5.0, proven_fresh=True) == 'open' and lid.last_silence is None,
      'earbud chatter measured a silence for the case')
lid = Lid()
lid.handle(True, True, 5, CASE_A, 0.0, proven_fresh=True)
lid.handle(True, False, 5, CASE_A, 1.0, proven_fresh=True)
check(lid.handle(True, False, 5, CASE_A, 6.0, proven_fresh=True) == 'none',
      'five seconds of quiet is normal between bursts and may not wake anything')

# 9. The lid stays open but the case goes quiet for half a minute. The popup goes away,
# and 0.8.47 keeps it away: the cycle is spent, so a case lying open may not pop the card
# back up on every burst (no pop-back). The next opening carries a new counter and shows.
for quiet in (11.0, 20.0, 45.0, 110.0):
    lid = Lid()
    lid.handle(True, True, 2, CASE_A, 0.0)
    check(lid.handle(True, True, 2, CASE_A, 0.15) == 'open', 'setup')
    t = 0.15
    while not lid.timed_out(t):
        t += 0.25
    check(5.5 < t - 0.15 < 7.0, 'a case that said its piece in one burst gets the short tail')
    lid.suspend_for_silence(t)
    check(not lid.open, 'silence must take the popup down')
    back = t + quiet
    lid.handle(True, True, 2, CASE_A, back)
    check(lid.handle(True, True, 2, CASE_A, back + 0.14) != 'open',
          'a case lying open popped the card back up after %.0fs of quiet' % quiet)
    check(lid.handle(True, True, 2, CASE_A, back + 0.3, proven_fresh=True) != 'open',
          'a fresh word of a spent cycle popped the card back up after %.0fs of quiet' % quiet)
    lid.handle(True, False, 2, CASE_A, back + 2.0)
    check(lid.handle(True, True, 3, CASE_A, back + 3.0, proven_fresh=True) == 'open',
          'the next real opening after a spent cycle must show at once')

# 9a. 0.8.16: the short tail belongs only to a case that said everything at once. A case
# still talking after the burst window keeps the full ten seconds, because every long gap in
# the recordings - up to 9.99 s, all of them followed by a real closed word - is a cycle
# like this one.
lid = Lid()
lid.handle(True, True, 2, CASE_A, 0.0)
check(lid.handle(True, True, 2, CASE_A, 0.15) == 'open', 'setup')
t = 0.15
# 0.8.47: the burst window is 20 s, so this case keeps talking past it.
while t < BURST_WINDOW + 3.0:
    t += 0.5
    lid.handle(True, True, 2, CASE_A, t)
check(not lid.said_its_piece(), 'a case still talking past the burst window has not said its piece')
last = t
while not lid.timed_out(t):
    t += 0.25
check(t - last > 9.0, 'a talkative cycle must still be given the full ten seconds')

# 9b. After a suspension a lone stale packet still may not resurrect anything.
lid = Lid()
lid.handle(True, True, 3, CASE_A, 0.0)
lid.handle(True, True, 3, CASE_A, 0.15)
lid.suspend_for_silence(11.0)
for i in range(40):
    check(lid.handle(True, True, 3, CASE_A, 11.0 + i * 3.0) != 'open',
          'a lone stale packet resurrected a suspended popup')

# 9c. A closed packet during the silence ends the cycle: the stale open packets that
# trail after it are echoes again, not a resume. (A sustained 8 Hz burst of them is
# still a real opening - that is the deliberate fail-open of scenario 3b.)
lid = Lid()
lid.handle(True, True, 4, CASE_A, 0.0)
lid.handle(True, True, 4, CASE_A, 0.15)
lid.suspend_for_silence(11.0)
lid.handle(True, False, 4, CASE_A, 12.0)
check(lid.spent_cycle == -1, 'a closed packet must clear the spent mark')
for i in range(40):
    check(lid.handle(True, True, 4, CASE_A, 12.5 + i * 2.5) != 'open',
          'the lid was shut during the silence, so a stale packet may not resume')

# 10. 0.8.8: a packet whose age the radio can prove opens the popup on the spot, and every
# guard that mattered in 0.8.7 still holds when every packet claims to be fresh.
lid = Lid()
check(lid.handle(True, True, 1, CASE_A, 0.0, proven_fresh=True) == 'open', 'a proven-fresh new cycle must open at once')
check(lid.handle(True, True, 1, CASE_A, 0.13, proven_fresh=True) == 'update', 'the rest of the burst only refreshes')
check(lid.handle(True, False, 1, CASE_A, 2.0, proven_fresh=True) == 'close', 'a closed packet still hides it')
for i in range(60):
    check(lid.handle(True, True, 1, CASE_A, 2.5 + i * 2.5, proven_fresh=True) != 'open', 'a fresh echo of the closed cycle reopened the popup')
check(lid.handle(True, True, 2, CASE_A, 200.0, proven_fresh=True) == 'open', 'the next real opening must be instant again')

# 10b. Freshness on its own is no licence: not bound, not trusted, no lid state - no popup.
lid = Lid()
for i in range(400):
    check(lid.handle(True, True, 3, 0xAAAABBBBCCCC, i * 0.2, bound=False, proven_fresh=True) != 'open', 'a neighbour opened the popup instantly')
for i in range(400):
    check(lid.handle(True, True, 3, CASE_A, 100 + i * 0.03, trusted=False, proven_fresh=True) != 'open', 'a queued packet opened the popup instantly')
for i in range(400):
    check(lid.handle(False, True, 3, EARBUD, 200 + i * 0.4, proven_fresh=True) != 'open', 'an earbud packet opened the popup instantly')

# 10c. The close button buries the cycle even when everything that follows is fresh.
lid = Lid()
check(lid.handle(True, True, 4, CASE_A, 0.0, proven_fresh=True) == 'open', 'burial fixture opens')
lid.force_closed(1.0)
for i in range(200):
    check(lid.handle(True, True, 4, CASE_A, 1 + i * 0.13, proven_fresh=True) != 'open', 'a dismissed cycle came back instantly')
check(lid.handle(True, True, 5, CASE_A, 60.0, proven_fresh=True) == 'open', 'a new cycle after a dismissal must be instant')

# 10d. On a machine whose radio timestamps are unusable nothing is ever proven fresh, and
# there the two-packet proof of 0.8.7 must still work exactly as before.
lid = Lid()
check(lid.handle(True, True, 6, CASE_A, 0.0) != 'open', 'without proof a single packet may not open the popup')
check(lid.handle(True, True, 6, CASE_A, 0.15) == 'open', 'two packets still open the popup')

# ---------------------------------------------------------------------------
# Replay of recorded air.
#
# Everything above this line is traffic this file made up. This part is not: the
# fixtures in tests/replay are cut straight out of podsview-trace.log, with the real
# payloads, the real gaps and the real signal readings. The C# smoke test runs the
# very same files through the real parser, the real filter and the real state machine.
# A change that would be felt on the desk fails here first.
# ---------------------------------------------------------------------------
import os
import glob

UNKNOWN_RSSI = -127
PAIRED_FLOOR = -95
STRANGER_FLOOR = -55
NEARBY_FLOOR = -70
INSTANT_AGE_MS = 2000


# How long a fingerprint stays trusted after it was last used, and how many are kept.
TRUST_TTL = 1800.0
SHAPE_TTL = 7 * 24 * 3600.0  # LidSignal.ShapeTtl, measured from the last use
MAX_TRUSTED = 4
# 0.8.35: LidSignal._shapeWords / CaseSignalClassifier.ColdQuiet.
MAX_SHAPE_WORDS = 8
# 0.8.35: the shape table used to share MAX_TRUSTED's 4 slots. One case needs more than
# that: the 2026-09-08 trace has 0x39, 0x3A, 0x3D, 0x3E, 0x49 and 0x4D from one case.
MAX_SHAPES = 12
COLD_QUIET = 120.0  # CaseSignalClassifier.ColdQuiet; its cold profile is unused since 0.8.47


class LidBytes:
    """Mirror of LidSignal: which lid bytes may be read as lid state.

    The explicit range 0x30-0x3F is a level, as it always was. Outside it the first byte
    from an address is only a baseline, a change of the low nibble is an event and also
    confirms that the address reports the lid, and from then on its repeats are believed
    as levels so the stream never looks silent. A frozen copy is never confirmed.
    """

    def __init__(self):
        self._seen = {}
        self._trusted = {}
        self._shapes = {}
        # 0.8.35: last meaningful nibble per signature, so a change that straddles an
        # address rotation is still a change. A frozen copy repeats one byte for ever
        # and can never produce one.
        self._shape_words = {}

    @staticmethod
    def shape(identity):
        """Mirror of LidSignal.Shape: status nibble of byte 5, charge nibble of byte 7."""
        return (((identity >> 16) & 0x0F) << 4) | ((identity >> 4) & 0x0F)

    def seed(self, shapes, now=0.0):
        """Mirror of LidSignal.Seed: what an earlier run of the app had learned."""
        for value in shapes:
            self._remember(value, now)

    def believe(self, addr, identity, lid, now=0.0, validated_case_profile=False):
        state = lid & 0x0F
        prev = self._seen.get(addr)
        if prev is not None and not 0 <= now - prev[2] <= 300.0:
            prev = None
        confirmed = prev[1] if prev is not None else False
        trusted = (identity != 0 and identity in self._trusted
                   and now - self._trusted[identity] <= TRUST_TTL)
        known_shape = identity != 0 and self._fresh(self.shape(identity), now)
        word = self._shape_words.get(self.shape(identity)) if identity != 0 else None
        rotated = (prev is None and word is not None
                   and 0 <= now - word[1] <= 300.0 and word[0] != state)
        if lid & 0x20:
            src, believe, confirmed = 'explicit', True, True
            self._trust(identity, now)
            self._learn(identity, now)
        elif validated_case_profile:
            src, believe, confirmed = 'model-profile', True, True
            self._learn(identity, now)
        elif prev is None and trusted:
            src, believe, confirmed = 'fresh', True, True
            self._trust(identity, now)
        elif prev is None and known_shape:
            src, believe, confirmed = 'known', True, True
            self._learn(identity, now)
        elif prev is None and rotated:
            # A brand-new address, but this signature just said something different at the
            # address it left behind: a lid movement seen across a rotation. It is the only
            # evidence an unlearned case can offer inside its very first cycle.
            src, believe, confirmed = 'rotate-change', True, True
            self._learn(identity, now)
        elif prev is None:
            src, believe = 'baseline', False
        elif prev[0] != state:
            src, believe, confirmed = 'change', True, True
            if trusted:
                self._trust(identity, now)
            self._learn(identity, now)
        elif confirmed:
            src, believe = 'hold', True
        elif known_shape:
            # An address already in the table, unconfirmed, repeating the byte it slept on,
            # whose signature this case has since proven elsewhere. The frozen copy cannot
            # reach this door: its signatures are never learned.
            src, believe, confirmed = 'known', True, True
            self._learn(identity, now)
        else:
            src, believe = 'static', False
        if believe and known_shape:
            self._remember(self.shape(identity), now)
        if identity != 0:
            self._shape_words[self.shape(identity)] = (state, now)
            if len(self._shape_words) > MAX_SHAPE_WORDS:
                del self._shape_words[min(self._shape_words,
                                          key=lambda key: self._shape_words[key][1])]
        self._seen[addr] = (state, confirmed, now)
        return believe, src

    def _trust(self, identity, now):
        if identity == 0:
            return
        self._trusted[identity] = now
        if len(self._trusted) > MAX_TRUSTED:
            del self._trusted[min(self._trusted, key=lambda key: self._trusted[key])]

    def _learn(self, identity, now):
        if identity == 0:
            return
        self._remember(self.shape(identity), now)

    def _fresh(self, value, now):
        values = (value,value ^ 4) if value in (0x39,0x3D,0x49,0x4D) else (value,)
        return any(v in self._shapes and 0 <= now-self._shapes[v][1] <= SHAPE_TTL for v in values)

    def _remember(self, value, now):
        # (learned, used). The learned time fixes the order of the saved list, so it must not
        # move; the used time is what freshness is measured from, and it must.
        entry = self._shapes.get(value)
        if entry is not None:
            self._shapes[value] = (entry[0], now)
            return
        self._shapes[value] = (now, now)
        if len(self._shapes) > MAX_SHAPES:
            del self._shapes[min(self._shapes, key=lambda key: self._shapes[key][1])]

    def reset(self):
        # Signatures survive a radio restart on purpose; see LidSignal.Reset.
        self._seen.clear()
        self._trusted.clear()


# ---- the lid byte: a level in the explicit range, an event everywhere else ----
# 7C6934E7440F sent 0x11 one hundred and seventy times on 2026-08-29 without ever
# changing it. Reading that as "lid open" would pin the popup on the screen forever.
frozen = LidBytes()
why = None
for _ in range(170):
    believed, why = frozen.believe(0x7C6934E7440F, 0x02F88F, 0x11)
    check(not believed, 'the frozen 0x11 stream must never be believed')
check(why == 'static', 'a repeat from an unconfirmed address is static')

# 508E6E99E5BD is the case. It announced the lid without bit 5 for 41 seconds and then
# switched to the explicit range mid-cycle, which is what 0.8.9 could not follow.
rotating = LidBytes()
check(rotating.believe(0x508E6E99E5BD, 0x24FA93, 0x11) == (False, 'baseline'),
      'the first byte from an address decides nothing')
check(rotating.believe(0x508E6E99E5BD, 0x24FA93, 0x19) == (True, 'change'),
      'a changed nibble outside the explicit range is a real lid event')
check(rotating.believe(0x508E6E99E5BD, 0x24FA93, 0x19) == (True, 'hold'),
      'a confirmed address keeps the stream alive with its repeats')
check(rotating.believe(0x508E6E99E5BD, 0x24FA93, 0x3A) == (True, 'explicit'),
      'the explicit range is believed whatever came before it')
rotating.reset()
# A reset clears the addresses and the fingerprints, but not the signatures: they describe
# the hardware, and forgetting them at every watcher restart is what used to cost the user
# the first opening after the app started.
check(rotating.believe(0x508E6E99E5BD, 0x24FA93, 0x19) == (True, 'known'),
      'a reset keeps what the case taught the app about its own signature')
check(rotating.believe(0x7C6934E7440F, 0x02F58F, 0x11) == (False, 'baseline'),
      'a reset still leaves the frozen copy with nothing to stand on')


def decode_battery(nibble):
    return nibble * 10 if 0 <= nibble <= 10 else None


# ---- a rotated address: the case may lend its fingerprint, the frozen copy may not ----
# The case renames itself every minute or two, and 0.8.10 threw away the first thing every
# new address said - which is exactly the lid being opened. Payload bytes 5 to 7 tell the
# living case (24 FA 93 / 73 7A 93) from the copy (02 F7 8F / 13 A7 A3) in all four
# recorded sessions, and only an explicit packet may lend that fingerprint onward.
rotation = LidBytes()
check(rotation.believe(0x4B45E954BDBB, 0x24FA93, 0x11, 0.0) == (False, 'baseline'),
      'an untrusted fingerprint may not raise the popup on its own')
check(rotation.believe(0x455A43108D8F, 0x24FA93, 0x38, 30.0)[0],
      'an explicit packet is believed and lends its fingerprint')
check(rotation.believe(0x453A6FB0A387, 0x24FA93, 0x11, 66.0) == (True, 'fresh'),
      "after a rotation the case's first word is the lid opening")
check(rotation.believe(0x6F0A5E515EDF, 0x02F78F, 0x11, 90.0) == (False, 'baseline'),
      'a new address alone proves nothing: the frozen copy rotates too')
check(rotation.believe(0x67E7A7D03420, 0x24FA93, 0x11, 2700.0) == (True, 'known'),
      'after the fingerprint expires the shape still speaks for the case (0.8.13)')


# ---- the shape: what is left of a fingerprint once the batteries are taken out of it ----
# 24 FA 93 and 73 56 97 are the same case an evening apart, with a different charge and an
# earbud missing; 02 F5 8F and 13 A7 A3 are its frozen copies. Byte 5's status nibble and
# byte 7's charge nibble tell them apart in every one of the six recorded sessions.
check(LidBytes.shape(0x24FA93) == 0x49 and LidBytes.shape(0x735697) == 0x39,
      'the living case shapes are 49 and 39')
check(LidBytes.shape(0x02F58F) == 0x28 and LidBytes.shape(0x13A7A3) == 0x3A,
      'the frozen copies keep their own shapes')

# An empty case sends one or two packets from a fresh address and then falls silent, so it
# never lives long enough to change its lid byte. Without a remembered shape that opening is
# lost for good - this is the 2026-08-30 17:20:25 case.
empty = LidBytes()
check(empty.believe(0x536662B3B4B2, 0x24F696, 0x51) == (False, 'baseline'),
      'with nothing learned the empty case is still only a baseline')
empty.seed([0x49])
check(empty.believe(0xC5DF3218C4A1, 0x24F696, 0x51) == (True, 'known'),
      'a remembered shape makes the first word of a new address count')
check(empty.believe(0x7C6934E7440F, 0x02F58F, 0x11) == (False, 'baseline'),
      'a copy is not helped by the case having been learned')

# A shape is learned by a change as well, because a change is proof this transmitter reports
# the lid, and it is dropped once it is a day old.
learn = LidBytes()
check(learn.believe(0x1111, 0x735697, 0x51, 0.0) == (False, 'baseline'), 'first byte is a baseline')
check(learn.believe(0x1111, 0x735697, 0x52, 1.0) == (True, 'change'), 'a change is an event')
check(learn.believe(0x2222, 0x735796, 0x51, 2.0) == (True, 'known'), 'the shape learned by that change speaks')
check(learn.believe(0x3333, 0x735796, 0x51, 2.0 + SHAPE_TTL + 1) == (False, 'baseline'),
      'a signature older than SHAPE_TTL is not believed')

# ---- 2026-09-03: the first opening after a pause does nothing ----
# It would not pop on the first try, only when the case was opened a second time.
#
# A signature is learned once and keeps the time of that first lesson for ever, because
# refreshing it would reorder the saved list. So an app that has been running since the
# morning holds a signature that has gone cold at noon - even though the case has been
# using it all day - and the next address rotation lands on a baseline again. The whole
# wake-up burst is then discarded, and the popup waits for a second lid movement, which
# arrives from an address already in the table and therefore counts as a change.
day = LidBytes()
check(day.believe(0x1111, 0x24FA93, 0x51, 0.0) == (False, 'baseline'), 'the first byte is a baseline')
check(day.believe(0x1111, 0x24FA93, 0x52, 1.0) == (True, 'change'), 'a change teaches the signature')
# The case renames itself every minute or two, and every rotation leans on that signature.
moment = 90.0
while moment < 13 * 3600.0:
    believed, why = day.believe(0x200000 + int(moment), 0x24FA93, 0x51, moment)
    check(believed and why == 'known',
          f'a signature the case keeps using must not go cold ({moment / 3600.0:.1f} h in, got {why})')
    moment += 900.0
check(day.believe(0x999999, 0x24FA93, 0x51, 13 * 3600.0 + 400.0) == (True, 'known'),
      'the first word after a pause must count on a signature that was in use all day')

# An address already in the table, unconfirmed, repeating the byte it slept on. Its
# signature has since been proven by another address, so this is the living case too -
# and reading it as "static" threw away exactly the packet the user was waiting for.
mixed = LidBytes()
check(mixed.believe(0xAAAA, 0x24FA93, 0x51, 0.0) == (False, 'baseline'), 'nothing is known yet')
# 0.8.35 changes this one line on purpose. AAAA said 0x51 and BBBB says 0x52 one second
# later: the same signature, a different lid word, across an address rotation. That is the
# case being opened again, and discarding it is the missed first opening this release is
# about. The frozen-copy guard below still holds, because a copy never changes its byte.
check(mixed.believe(0xBBBB, 0x24FA93, 0x52, 1.0) == (True, 'rotate-change'),
      'a lid word that changed across a rotation must be believed (0.8.35)')
check(mixed.believe(0xBBBB, 0x24FA93, 0x51, 2.0) == (True, 'change'), 'a change teaches the signature')
check(mixed.believe(0xAAAA, 0x24FA93, 0x51, 3.0) == (True, 'known'),
      'an address whose signature has since been proven is no longer a dead end')

# What may not change: a signature nobody has used for longer than the TTL is forgotten,
# and none of this hands the frozen copy anything to stand on.
cold = LidBytes()
cold.seed([LidBytes.shape(0x24FA93)], 0.0)
check(cold.believe(0x1111, 0x24FA93, 0x51, SHAPE_TTL + 1.0) == (False, 'baseline'),
      'a signature unused for longer than the TTL is not believed')
check(cold.believe(0x7C6934E7440F, 0x02F58F, 0x11, SHAPE_TTL + 2.0) == (False, 'baseline'),
      'the frozen copy still has nothing to stand on')
for _ in range(40):
    believed, why = cold.believe(0x7C6934E7440F, 0x02F78F, 0x11, SHAPE_TTL + 3.0)
    check(not believed and why == 'static', 'the frozen stream stays refused, whatever is learned')


def parse_payload(text):
    """Mirror of AirPodsAdvertisementParser.TryParse - the lid fields it decides on."""
    raw = bytes.fromhex(text)
    if len(raw) >= 29 and raw[0] == 0x4C and raw[1] == 0x00:
        raw = raw[2:]
    if len(raw) < 27 or raw[0] != 0x07 or raw[1] != 0x19 or raw[2] != 0x01:
        return None
    lid = raw[8]
    batteries = (decode_battery(raw[6] >> 4), decode_battery(raw[6] & 0x0F), decode_battery(raw[7] & 0x0F))
    return {
        'lid': lid,
        'model': (raw[3] << 8) | raw[4],
        'identity': (raw[5] << 16) | (raw[6] << 8) | raw[7],
        'counter': lid & 0b0000_0111,
        'is_open': (lid & 0b0000_1000) == 0,
        'carries': (lid & 0b0010_0000) != 0,
        'has_battery': any(b is not None for b in batteries),
    }


def packet_allowed(carries, has_battery, rssi, paired, connected, nearby):
    """Mirror of PacketFilter.Allow."""
    if rssi <= UNKNOWN_RSSI:
        return False
    if carries and (paired or connected):
        return True
    if paired:
        return connected or rssi >= PAIRED_FLOOR
    return has_battery and rssi >= (NEARBY_FLOOR if nearby else STRANGER_FLOOR)


def read_fixture(path):
    header = {'paired': True, 'connected': False, 'nearby': True}
    fixture = {'name': os.path.basename(path), 'expects': [], 'dismiss': None, 'packets': [],
               'shapes': []}
    for line in open(path, encoding='utf-8'):
        line = line.strip()
        if not line or line.startswith('#'):
            continue
        if line.startswith('!'):
            tag, _, rest = line[1:].partition(' ')
            if tag == 'name':
                fixture['name'] = rest.strip()
            elif tag in ('paired', 'connected', 'nearby'):
                header[tag] = rest.strip() == '1'
            elif tag == 'expect':
                fixture['expects'].append(rest.split())
            elif tag == 'dismiss':
                fixture['dismiss'] = int(rest)
            elif tag == 'shapes':
                # What the app had learned before this recording started, as saved settings.
                fixture['shapes'] = [int(part, 16) for part in rest.replace(',', ' ').split()]
            continue
        kind, ms, addr, rssi, age, trusted, bound, payload = line.split()
        fixture['packets'].append((kind, int(ms), int(addr, 16), int(rssi), int(age),
                                   trusted == '1', bound == '1', payload))
    fixture['header'] = header
    return fixture


def run_fixture(fixture):
    head = fixture['header']
    dismiss = fixture['dismiss']
    lid = Lid()
    signal = LidBytes()
    truth = LidBytes()
    shape_traffic = {}
    signal.seed(fixture['shapes'])
    truth.seed(fixture['shapes'])
    shows = []
    hides = []
    pending_show = None
    pending_hide = None
    worst_show = 0.0
    worst_hide = 0.0
    wake_at = None
    wake_gap = 0.0
    prev_case_at = None
    phys_state = None
    phys_cycle = None
    dismissed = dismiss is None
    lives = []
    state = {'visible_since': None, 'worst_hide': 0.0, 'pending_hide': None}

    def leaves(at):
        """The popup leaving the screen, whichever rule took it away."""
        if state['visible_since'] is not None:
            lives.append(at - state['visible_since'])
            state['visible_since'] = None
        if state['pending_hide'] is not None:
            state['worst_hide'] = max(state['worst_hide'], at - state['pending_hide'])
            state['pending_hide'] = None

    def watchdog(until):
        """App's 250 ms tick, driven by the model itself so both agree."""
        for _kind, at in lid.tick(until):
            hides.append(at)
            leaves(at)

    for kind, ms, addr, rssi, age, trusted, bound, payload in fixture['packets']:
        now = ms / 1000.0
        watchdog(now)
        if not dismissed and ms >= dismiss:
            # 0.8.47: App calls ForceClosed only while the lid machine owns the popup.
            if lid.open:
                lid.force_closed(dismiss / 1000.0)
            dismissed = True
        data = parse_payload(payload)
        if data is None:
            continue

        # LEGACY classification-dependent replay metric, NOT physical latency. A suspect
        # line never counts: the radio could not place it, so it is not an event. The air is
        # read through its own tracker, so nothing the app drops can bend the truth.
        if kind == 'p' and truth.believe(addr, data['identity'], data['lid'], now)[0]:
            if data['is_open'] and (phys_state != 'open' or data['counter'] != phys_cycle):
                pending_show = now
                phys_state, phys_cycle = 'open', data['counter']
            elif not data['is_open'] and phys_state == 'open':
                state['pending_hide'] = now
                phys_state = 'closed'

        # A packet with no reading is dropped before the lid tracker, exactly as in
        # BluetoothMonitor, so a byte the radio could not place cannot fake a change.
        if rssi <= UNKNOWN_RSSI or not trusted or not bound:
            continue
        # Mirror of CaseSignalClassifier.Believe. 0.8.41: an earbud outside the case (byte 5
        # without bit 6 and bit 2) does not know the lid and never reaches the signature table.
        # 0.8.47: what the table does not believe, the in-case door does - a fresh word of the
        # remembered AirPods Pro 2 from inside the case. The 0.8.37 cold profile is gone.
        identity = data['identity']
        if identity != 0 and ((identity >> 16) & 0x44) == 0:
            believe = False
        else:
            believe = signal.believe(addr, identity, data['lid'], now)[0]
            if (not believe and head['paired'] and 0 <= age <= INSTANT_AGE_MS
                    and data['model'] == 0x1420 and identity != 0):
                believe = True

        if not packet_allowed(believe, data['has_battery'], rssi,
                              head['paired'], head['connected'], head['nearby']):
            continue

        if believe:
            # The wake-up of this window: the believed case packet that broke the longest
            # silence. A case is off the air unless a hand moves it, so this is the moment
            # the user opened the lid and the moment popup delay is measured from.
            if prev_case_at is not None and now - prev_case_at > max(wake_gap, WAKE_MEASURE):
                wake_gap = now - prev_case_at
                wake_at = now
            prev_case_at = now

        action = lid.handle(believe, data['is_open'], data['counter'], addr, now,
                            trusted=trusted, bound=bound,
                            proven_fresh=0 <= age <= INSTANT_AGE_MS,
                            explicit_lid=data['carries'])
        if action == 'open':
            shows.append(now)
            if state['visible_since'] is None:
                state['visible_since'] = now
            if pending_show is not None:
                worst_show = max(worst_show, now - pending_show)
                pending_show = None
        elif action == 'update' and state['visible_since'] is not None:
            # The popup is on the screen and has just been refreshed with this packet, so the
            # user is already looking at what the case said. That is the state being shown.
            if pending_show is not None:
                worst_show = max(worst_show, now - pending_show)
                pending_show = None
        elif action == 'close':
            hides.append(now)
            leaves(now)

    # The recording ends but the app keeps ticking, so a held-back close still lands.
    if fixture['packets']:
        watchdog(fixture['packets'][-1][1] / 1000.0 + TIMEOUT + 1.0)  # 0.8.47: the longest tail
    if state['visible_since'] is not None:
        lives.append(float('inf'))
    worst_hide = max(worst_hide, state['worst_hide'])
    pending_hide = state['pending_hide']

    if pending_show is not None:
        worst_show = float('inf')
    if pending_hide is not None:
        worst_hide = float('inf')

    name = fixture['name']
    for exp in fixture['expects']:
        if exp[0] == 'shows':
            check(len(shows) == int(exp[1]), '%s: the popup appeared %d times, not %s' % (name, len(shows), exp[1]))
        elif exp[0] == 'hides':
            check(len(hides) == int(exp[1]), '%s: the popup was hidden %d times, not %s' % (name, len(hides), exp[1]))
        elif exp[0] == 'never-show':
            check(not shows, '%s: the popup appeared %d times and should never have' % (name, len(shows)))
        elif exp[0] == 'show-within-ms':
            check(worst_show * 1000 <= int(exp[1]),
                  '%s: the popup was %s late, limit %s ms' % (name, fmt_delay(worst_show), exp[1]))
        elif exp[0] == 'hide-within-ms':
            check(worst_hide * 1000 <= int(exp[1]),
                  '%s: the popup stayed up %s after the lid shut, limit %s ms' % (name, fmt_delay(worst_hide), exp[1]))
        elif exp[0] in ('show-after-wake-within-ms', 'show-after-wake-after-ms'):
            # Delay from the wake-up: the case broke a long silence, which only happens when a
            # hand moves it, so this is the wait the user actually feels.
            later = [] if wake_at is None else [s for s in shows if s >= wake_at]
            gap = None if not later else (later[0] - wake_at) * 1000
            if exp[0] == 'show-after-wake-within-ms':
                check(gap is not None and gap <= int(exp[1]),
                      '%s: the popup took %s after the case woke, limit %s ms'
                      % (name, 'forever' if gap is None else '%.0f ms' % gap, exp[1]))
            else:
                check(gap is None or gap >= int(exp[1]),
                      '%s: the popup came %s after the case woke and may not come sooner than %s ms'
                      % (name, 'never' if gap is None else '%.0f ms' % gap, exp[1]))
        elif exp[0] == 'popup-lives-at-least-ms':
            worst = min(lives) if lives else 0.0
            check(bool(lives) and worst * 1000 >= int(exp[1]),
                  '%s: the popup was only readable for %s, needs %s ms'
                  % (name, 'no time at all' if not lives else fmt_delay(worst), exp[1]))
        elif exp[0] == 'popup-lives-at-most-ms':
            worst = max(lives) if lives else 0.0
            check(worst * 1000 <= int(exp[1]),
                  '%s: the popup stayed up %s, limit %s ms' % (name, fmt_delay(worst), exp[1]))
        else:
            check(False, '%s: unknown expectation %s' % (name, exp[0]))
    return name, len(shows), len(hides), worst_show, worst_hide


def fmt_delay(value):
    return 'never' if value == float('inf') else '%.0f ms' % (value * 1000)


replay_dir = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'replay')
replay_files = sorted(glob.glob(os.path.join(replay_dir, '*.txt')))
check(len(replay_files) >= 6, 'the recorded fixtures are missing from tests/replay')
print('replay of recorded air (%d fixtures):' % len(replay_files))
for replay_path in replay_files:
    replay_name, n_shows, n_hides, late_show, late_hide = run_fixture(read_fixture(replay_path))
    print('   %-30s %d shows, %d hides, worst show %-8s worst hide %s'
          % (replay_name, n_shows, n_hides, fmt_delay(late_show), fmt_delay(late_hide)))

print('popup model: %s (%d checks, %d failures)' % ('PASS' if not fails else 'FAIL', checks, len(fails)))
for f in fails:
    print('   ', f)
raise SystemExit(1 if fails else 0)
