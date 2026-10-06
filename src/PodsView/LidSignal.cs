namespace PodsView;

/// <summary>
/// Decides whether the lid byte of an advertisement may be believed.
///
/// Until 0.8.9 the app believed the lid only when bit 5 of that byte was set, that is in
/// the range 0x30-0x3F. The capture of 2026-08-29 22:26 proves that rule wrong. One and
/// the same case, address 508E6E99E5BD, announced
///
///     22:27:20.521  0x11   lid open, cycle 1     bit 5 clear
///     22:27:28.417  0x19   lid shut, cycle 1     bit 5 clear
///     22:27:29.370  0x12   lid open, cycle 2     bit 5 clear
///     22:28:01.492  0x3A   lid shut, cycle 2     bit 5 set
///     22:28:03.160  0x33   lid open, cycle 3     bit 5 set
///
/// The low nibble runs on without a break across that boundary, so both ranges carry the
/// same lid information - only the high bits differ, and they follow the case's address
/// rotation. The 0x50-0x5F range appears the same way in the 20:52 and 21:03 captures.
///
/// The high bits cannot simply be ignored, though. A second address, 7C6934E7440F, sent
/// 0x11 one hundred and seventy times in a row without ever changing it - a frozen copy of
/// an old state. Believing that as a level would pin the popup open forever. So 0.8.10
/// separated a level from an event:
///
///   * in the explicit range the byte is believed as a level, exactly as before;
///   * outside it, the first byte from an address is only a baseline and decides nothing;
///   * a <b>change</b> of the meaningful nibble is an event, and it also confirms that this
///     address really does report the lid;
///   * once an address is confirmed, its repeats are believed as levels too.
///
/// That last rule matters as much as the others. The state machine is fed by a stream and
/// hides the popup when the case goes quiet, so if only the transitions reached it, a lid
/// held open would look like silence and the popup would vanish by itself after ten
/// seconds - which is precisely the 20:52:36 complaint. A frozen stream never earns
/// confirmation, so it still says nothing at all.
///
/// <para>
/// What 0.8.10 could not do, and 0.8.11 can. Confirmation belonged to an address, and the
/// case rotates its address every minute or two. Every rotation therefore threw away the
/// case's very first announcement - which is exactly the moment the lid is opened. The
/// capture of 23:00 measures the cost three times over:
/// </para>
///
///     23:01:01.197  4B45E954BDBB  0x11  baseline, then 34 repeats, all discarded
///     23:01:07.447  the popup finally appears, after a shut and a second open
///     23:02:16.466  453A6FB0A387  0x11  baseline   ->  believed 2.69 s later
///     23:03:16.139  67E7A7D03420  0x11  baseline   ->  believed 3.30 s later
///
/// The old comment here claimed this could not be fixed by trusting the device instead of
/// the address, because the frozen copy shares the same model and colour. That was true of
/// model and colour, and false of the payload. Bytes 5 to 7 - the status nibble, the two
/// earbud batteries and the case battery with its charge flags - are a fingerprint, and in
/// all four recorded sessions the living case and the frozen copy never once shared it:
///
///     living case   24 FA 93   73 7A 93     (04 FA 94 / 53 2A 94 in the 20:45 capture)
///     frozen copy   02 F7 8F   13 A7 A3     (22 F2 8F / 33 A2 A4 in the same capture)
///
/// The copy even rotated its own address at 23:07:24 and carried the identical 02 F7 8F 11
/// across, which is what makes a fingerprint worth trusting where an address is not.
///
/// So a fingerprint may earn trust, and only an explicit packet may grant it: that is the
/// case stating a lid transition outright, and the frozen copy has never sent one in 4605
/// recorded packets. Once a fingerprint is trusted, the first byte from a <b>new</b>
/// address carrying it is believed as a level - source <c>fresh</c> - so a rotation costs
/// nothing. Replayed over the four sessions this fires eight times, every one of them the
/// real case, up to 12.69 s earlier than before, and never once on the copy.
///
/// Trust is deliberately narrow. It is granted by nothing but an explicit packet, it is
/// refreshed only while it keeps being used, it expires after <see cref="TrustTtl"/>, at
/// most four fingerprints are held at a time, and a radio restart clears it. Anything not
/// covered by it falls back to the 0.8.10 behaviour untouched.
///
/// <para>
/// What 0.8.11 still could not do, and 0.8.13 can. A whole fingerprint is too brittle to
/// carry trust far: bytes 6 and 7 hold the two earbud batteries and the case battery, so it
/// changes when the case discharges by ten percent and again when an earbud is taken out of
/// the case. Trust was therefore almost never in place at the moment it was needed, and the
/// capture of 2026-08-30 17:15-17:23 shows what that costs - all three of the user's
/// complaints are one and the same event, the first packet of a new address being discarded:
/// </para>
///
///     17:19:34.589  540697  lid 0x51 open  baseline  ignored -> popup only at 17:19:39.201
///                   (the app had just been started: the very first opening showed nothing)
///     17:21:45.995  C5DF32  lid 0x51 open  baseline  ignored -> popup only at 17:21:52.283
///                   (the case had been off the air for 67 s: an open, a shut and an open)
///     17:20:25.038  24F696  lid 0x51 open  baseline  ignored -> no popup at all, ever
///                   (an earbud was out, so the case sent two packets and fell silent)
///
/// What survives all of that is the shape of the payload: the status nibble of byte 5 and
/// the charge nibble of byte 7. Counted over every recorded session, the living case sends
/// 39 and 49 - it never sends anything else - and its frozen copies send 08, 18, 1A, 28, 38,
/// 3A and 98. A shape is learned only from a transmitter that proved itself, by an explicit
/// packet or by changing its lid byte at one address, and it is then believed for the first
/// word of any new address - source <c>known</c>. Replayed over all six recordings, roughly
/// ten thousand packets, that rule fires 51 times, every one of them the real case, up to
/// 41 s earlier than the popup actually appeared, and not once on a copy.
///
/// The shape is saved between runs (<see cref="LearnedShapes"/>), because otherwise the
/// first opening after every start of the app would still be thrown away. It expires after
/// <see cref="ShapeTtl"/>, at most four are held, and a radio restart keeps them: they
/// describe the hardware, not the radio session.
/// </summary>
internal sealed class LidSignal
{
	/// <summary>Bit 5 of the lid byte, set only in the 0x30-0x3F range.</summary>
	public const byte ExplicitLidBit = 0b0010_0000;

	/// <summary>Cycle counter plus the shut bit: the part of the byte that carries meaning.</summary>
	public const byte LidStateMask = 0b0000_1111;

	/// <summary>An address unheard for this long is forgotten, so the table cannot grow.</summary>
	public static readonly TimeSpan Forget = TimeSpan.FromMinutes(5);

	/// <summary>
	/// How long a fingerprint stays trusted after it was last used. The batteries inside the
	/// fingerprint drift as the case discharges, so an old one describes a device that no
	/// longer exists and is better forgotten than believed.
	/// </summary>
	public static readonly TimeSpan TrustTtl = TimeSpan.FromMinutes(30);

	/// <summary>At most this many fingerprints are held; the oldest is dropped first.</summary>
	private const int MaxTrusted = 4;

	/// <summary>At most this many signatures are held; the oldest learned is dropped first.</summary>
	// 0.8.35: this was 4, and 4 was too small to hold one case.
	// The 2026-09-08 trace shows the SAME AirPods Pro 2 case announcing six different
	// signatures in a single afternoon - 0x39, 0x3A, 0x3D, 0x3E, 0x49 and 0x4D - because
	// the shape carries byte 7's charge nibble, and that nibble moves every time an earbud
	// starts or stops charging, which is exactly what opening the lid does. With room for
	// only four, every lid movement evicted a signature the case was still using, so the
	// table never converged and the first word of the next address kept landing on
	// "baseline". Twelve holds the whole family with room to spare and is still bounded.
	private const int MaxShapes = 12;

	/// <summary>At most this many per-signature last words are held; the oldest is dropped first.</summary>
	private const int MaxShapeWords = 8;

	/// <summary>
	/// How long a learned signature is believed after it was last used. Unlike a fingerprint
	/// it carries no battery reading, so it does not rot as the case discharges, and it is
	/// re-stamped every time it is used - a case in daily use therefore never loses it. A week
	/// covers a weekend away from the desk; past that the app would rather wait for a second
	/// opening than speak for hardware that may be long gone.
	/// </summary>
	public static readonly TimeSpan ShapeTtl = TimeSpan.FromDays(7);

	private readonly object _sync = new();
	private readonly Dictionary<ulong, Entry> _seen = new();
	private readonly Dictionary<uint, DateTimeOffset> _trusted = new();

	/// <summary>
	/// 0.8.35: the last meaningful nibble seen for a signature, independent of the address.
	/// The change rule below is keyed by address, and the case rotates its address about once
	/// per burst - the 2026-09-08 11:59 trace walks peers 1 to 7 inside ninety seconds - so a
	/// lid movement that straddles a rotation was invisible and taught the app nothing.
	/// </summary>
	private readonly Dictionary<byte, ShapeWord> _shapeWords = new();

	/// <summary>Signatures of transmitters that proved they report the lid.</summary>
	private readonly Dictionary<byte, ShapeMemory> _shapes = new();

	/// <summary>
	/// What is remembered about a learned signature. The two times are separate on purpose.
	/// <see cref="LearnedAt"/> fixes the place of a signature in the saved list, so moving it
	/// would reorder that list and rewrite the settings file on every packet.
	/// <see cref="UsedAt"/> is what freshness is measured from, and until 0.8.20 it did not
	/// exist: a signature kept the time of its first lesson for ever, so a case that had been
	/// using it all day still lost the first word of its next address twelve hours in. That is
	/// the report "it will not pop on the first try, only when I open the case a second time":
	/// the discarded word is the lid being opened, and the second opening only works because by
	/// then the address is in the table and its changed byte counts as an event.
	/// </summary>
	private struct ShapeMemory
	{
		public DateTimeOffset LearnedAt;
		public DateTimeOffset UsedAt;
	}

	private struct Entry
	{
		public byte State;
		public bool Confirmed;
		public DateTimeOffset At;
	}

	private struct ShapeWord
	{
		public byte State;
		public DateTimeOffset At;
	}

	/// <summary>
	/// Records what an address just said and answers whether that packet may be treated as
	/// lid information.
	/// </summary>
	/// <param name="identity">
	/// Payload bytes 5 to 7 packed into one number: the fingerprint of the device that sent
	/// this packet. Zero means unknown and is never trusted.
	/// </param>
	/// <param name="source">
	/// Why the answer came out that way, for the log: explicit, fresh, change, hold, baseline
	/// or static.
	/// </param>
	/// <param name="caseSide">
	/// 0.8.41: the packet was sent from inside the case (CaseSignalClassifier.OutsideCase is
	/// false), so every charge variant of a learned signature is that same case.
	/// </param>
	public bool Believe(ulong address, uint identity, byte lidByte, bool explicitLidState, DateTimeOffset now, out string source, bool validatedCaseProfile = false, bool caseSide = false)
	{
		byte state = (byte)(lidByte & LidStateMask);
		lock (_sync)
		{
				if (_seen.Count > 8) Prune(now);
				bool known = _seen.TryGetValue(address, out Entry previous)
                    && now >= previous.At && now - previous.At <= Forget;

			bool confirmed = known && previous.Confirmed;
			bool trustedIdentity = identity != 0
				&& _trusted.TryGetValue(identity, out DateTimeOffset trustedAt)
				&& now - trustedAt <= TrustTtl;
			byte shape = Shape(identity);
				bool knownShape = identity != 0 && (ShapeIsFresh(shape, now) || caseSide && FamilyIsFresh(shape, now));
			// A change of the same signature across an address rotation is still a change.
			// A frozen copy repeating one byte for ever can never produce one.
			bool rotatedChange = identity != 0 && !known
				&& _shapeWords.TryGetValue(shape, out ShapeWord lastWord)
				&& now >= lastWord.At && now - lastWord.At <= Forget
				&& lastWord.State != state;
			bool believe;
			if (explicitLidState)
			{
				// The case states the transition outright. This is the only evidence strong
				// enough to let a fingerprint speak for a future address.
				source = "explicit";
				believe = true;
				confirmed = true;
				Trust(identity, now);
				Learn(identity, now);
			}
			                else if (validatedCaseProfile)
                {
                    // This gate is supplied only by CaseSignalClassifier after exact model,
                    // profile, pairing/binding and fresh-radio checks. Do not label it explicit.
                    source = "model-profile";
                    believe = true;
                    confirmed = true;
                    // 0.8.38: believe, but do NOT learn. This door is opened by the
                    // CarriesLidState fast path, which proves freshness and pairing - never
                    // identity. Learning here wrote a signature into the table without a
                    // single explicit word or byte change, and every later address sharing
                    // that shape then walked straight in as "known". A frozen copy shares
                    // model and colour with the real case, so it could inherit the entry and
                    // raise a popup on nothing. Teaching stays with explicit, change and
                    // rotate-change, exactly as the 0.8.37 handoff asked.
                }
				else if (!known && trustedIdentity)
			{
				// A rotation of an address we already know the device behind. Its first word
				// counts, which is the whole point: that word is the lid being opened.
				source = "fresh";
				believe = true;
				confirmed = true;
				Trust(identity, now);
			}
			else if (!known && knownShape)
			{
				// A new address whose shape the case has already proved. Its first word counts,
				// and that word is the lid being opened - after a restart of the app, after a
				// pause, or from a case holding no earbuds that will only ever send two packets.
				source = "known";
				believe = true;
				confirmed = true;
				Learn(identity, now);
			}
			else if (!known && rotatedChange)
			{
				// A brand-new address, but this signature just said something different at the
				// address it left behind. That is a lid movement seen across a rotation, and it
				// is the only evidence an unlearned case can offer inside its first cycle.
				source = "rotate-change";
				believe = true;
				confirmed = true;
				Learn(identity, now);
			}
			else if (!known)
			{
				source = "baseline";
				believe = false;
			}
			else if (previous.State != state)
			{
				source = "change";
				believe = true;
				confirmed = true;
				// A change is proof of life, but not proof of identity: it keeps an existing
				// trust warm and never creates one.
				if (trustedIdentity) Trust(identity, now);
				// It is proof of shape, though: whatever address this is, it reports the lid.
				Learn(identity, now);
			}
				else if (confirmed)
				{
					source = "hold";
					believe = true;
				}
				else if (knownShape)
				{
					// An address already in the table, unconfirmed, repeating the byte it slept
					// on - and a signature this case has since proven at another address. Until
					// 0.8.20 this was read as "static" and thrown away, which is exactly the
					// packet the user was waiting for. The frozen copy cannot reach this door:
					// its signatures are never learned, because it has never sent an explicit
					// word nor changed a byte at one address.
					source = "known";
					believe = true;
					confirmed = true;
					Learn(identity, now);
				}
			else
			{
				source = "static";
				believe = false;
			}

                if (believe && knownShape) Remember(shape, now);
				RememberWord(shape, state, now, identity);
				_seen[address] = new Entry { State = state, Confirmed = confirmed, At = now };
			return believe;
		}
	}

	/// <summary>
	/// Forgets every address and every fingerprint. Used when the radio is restarted.
	/// Learned signatures stay: they describe the hardware and not the radio session, and
	/// dropping them would mean waiting for a second opening after every watcher restart.
	/// </summary>
	public void Reset()
	{
		lock (_sync)
		{
			_seen.Clear();
			_trusted.Clear();
			_shapeWords.Clear();
		}
	}

	/// <summary>
	/// Records the last meaningful nibble of a signature so a change can be recognised after
	/// the case rotates its address. Bounded, and the entry expires with <see cref="Forget"/>
	/// at lookup, so a word slept on for hours never counts as a movement.
	/// </summary>
	private void RememberWord(byte shape, byte state, DateTimeOffset now, uint identity)
	{
		if (identity == 0) return;
		_shapeWords[shape] = new ShapeWord { State = state, At = now };
		if (_shapeWords.Count <= MaxShapeWords) return;
		byte oldest = 0;
		DateTimeOffset oldestAt = DateTimeOffset.MaxValue;
		foreach (KeyValuePair<byte, ShapeWord> pair in _shapeWords)
		{
			if (pair.Value.At > oldestAt) continue;
			oldest = pair.Key;
			oldestAt = pair.Value.At;
		}
		_shapeWords.Remove(oldest);
	}

	private void Trust(uint identity, DateTimeOffset now)
	{
		if (identity == 0) return;
		_trusted[identity] = now;
		if (_trusted.Count <= MaxTrusted) return;
		uint oldest = 0;
		DateTimeOffset oldestAt = DateTimeOffset.MaxValue;
		foreach (KeyValuePair<uint, DateTimeOffset> pair in _trusted)
		{
			if (pair.Value > oldestAt) continue;
			oldest = pair.Key;
			oldestAt = pair.Value;
		}
		_trusted.Remove(oldest);
	}

	/// <summary>
	/// The part of a fingerprint that survives: the status nibble of payload byte 5 and the
	/// charge nibble of byte 7. The two earbud batteries and the case battery are left out
	/// on purpose - they change every ten percent and every time an earbud leaves the case.
	/// </summary>
	public static byte Shape(uint identity) => (byte)((((identity >> 16) & 0x0F) << 4) | ((identity >> 4) & 0x0F));

	/// <summary>The learned signatures as hex, oldest first. This is what gets saved.</summary>
	public string LearnedShapes()
	{
		lock (_sync)
		{
			if (_shapes.Count == 0) return string.Empty;
			List<KeyValuePair<byte, ShapeMemory>> ordered = new(_shapes);
			ordered.Sort((left, right) => left.Value.LearnedAt.CompareTo(right.Value.LearnedAt));
			string[] parts = new string[ordered.Count];
			for (int index = 0; index < ordered.Count; index++) parts[index] = ordered[index].Key.ToString("X2");
			return string.Join(",", parts);
		}
	}

	/// <summary>Puts back what an earlier run learned, so the first opening already counts.</summary>
        public void Seed(string saved, DateTimeOffset? referenceTime = null)
        {
            if (string.IsNullOrWhiteSpace(saved)) return;
            DateTimeOffset now = referenceTime ?? DateTimeOffset.UtcNow;
            bool dated = saved.StartsWith("v2|", StringComparison.Ordinal);
            string body = dated ? saved[3..] : saved;
            lock (_sync)
            {
                foreach (string part in body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string[] pieces = part.Split('@');
                    if (!byte.TryParse(pieces[0], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out byte shape)) continue;
                    DateTimeOffset used = now; // One-time migration of undated legacy settings.
                    if (dated)
                    {
                        if (pieces.Length != 2 || !long.TryParse(pieces[1], out long unix)) continue;
                        try { used = DateTimeOffset.FromUnixTimeSeconds(unix); } catch (ArgumentOutOfRangeException) { continue; }
                        if (used > now.AddMinutes(5) || now - used > ShapeTtl) continue;
                    }
                    Remember(shape, used);
                }
            }
        }

        /// <summary>Preserve the actual last-use timestamps across application restarts.</summary>
        public string ExportState()
        {
            lock (_sync)
            {
                return "v2|" + string.Join(",", _shapes.OrderBy(p => p.Value.LearnedAt)
                    .Select(p => p.Key.ToString("X2") + "@" + p.Value.UsedAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

	private void Learn(uint identity, DateTimeOffset now)
	{
		if (identity == 0) return;
		Remember(Shape(identity), now);
	}

	/// <summary>True while a learned signature is fresh enough to speak for another address.</summary>
    private bool ShapeIsFresh(byte shape, DateTimeOffset now)
    {
        if (ExactShapeIsFresh(shape, now)) return true;
        // Only the two observed case signatures and their charging variants.
        // The low 0x04 bit is CaseCharging in the parser's charge nibble.
        // No learning from a model match, no generic mask over arbitrary shapes.
        return shape is 0x39 or 0x3D or 0x49 or 0x4D
            && ExactShapeIsFresh((byte)(shape ^ 0x04), now);
    }

    /// <summary>
    /// 0.8.41: inside the case the charge nibble only says which earbud is charging, so every
    /// charge variant of a learned signature is the same case: 0x38 (the earbud in the case is
    /// full) is 0x39 (it is charging). This could not be allowed while the worn earbud reached
    /// LidSignal, because it sent the same signatures; OutsideCase keeps it out now.
    /// </summary>
    private bool FamilyIsFresh(byte shape, DateTimeOffset now)
    {
        foreach (KeyValuePair<byte, ShapeMemory> pair in _shapes)
            if ((pair.Key & 0xF8) == (shape & 0xF8) && now >= pair.Value.UsedAt && now - pair.Value.UsedAt <= ShapeTtl)
                return true;
        return false;
    }

    private bool ExactShapeIsFresh(byte shape, DateTimeOffset now) =>
        _shapes.TryGetValue(shape, out ShapeMemory memory)
        && now >= memory.UsedAt && now - memory.UsedAt <= ShapeTtl;

	/// <summary>
	/// A signature keeps the time of the lesson that taught it, so the saved list keeps its
	/// order and the settings file is not rewritten hundreds of times a minute. Using it again
	/// moves only its freshness. When the table is full the least recently used one goes -
	/// dropping the first one learned would throw away the signature in daily use.
	/// </summary>
	private void Remember(byte shape, DateTimeOffset now)
	{
		if (_shapes.TryGetValue(shape, out ShapeMemory memory))
		{
			_shapes[shape] = new ShapeMemory { LearnedAt = memory.LearnedAt, UsedAt = now };
			return;
		}
		_shapes[shape] = new ShapeMemory { LearnedAt = now, UsedAt = now };
		if (_shapes.Count <= MaxShapes) return;
		byte oldest = 0;
		DateTimeOffset oldestAt = DateTimeOffset.MaxValue;
		foreach (KeyValuePair<byte, ShapeMemory> pair in _shapes)
		{
			if (pair.Value.UsedAt > oldestAt) continue;
			oldest = pair.Key;
			oldestAt = pair.Value.UsedAt;
		}
		_shapes.Remove(oldest);
	}

	private void Prune(DateTimeOffset now)
	{
		List<ulong>? stale = null;
		foreach (KeyValuePair<ulong, Entry> pair in _seen)
		{
			if (now - pair.Value.At <= Forget) continue;
			stale ??= new List<ulong>();
			stale.Add(pair.Key);
		}
		if (stale is null) return;
		foreach (ulong address in stale) _seen.Remove(address);
	}
}
