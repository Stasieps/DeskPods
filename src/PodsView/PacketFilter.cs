namespace PodsView;

/// <summary>
/// The single place that decides whether an advertisement is worth looking at.
///
/// It is deliberately free of Windows types. Every rule here is replayed against
/// recorded air by the smoke tests in tests/replay, and that harness has to compile
/// without WinRT.
///
/// The rule that earned this file its own file is the one about an unknown reading.
/// Windows hands over -127 dBm when the radio attached no measurement to the packet at
/// all. Twelve such packets appear in the captures of 2026-08-29, and every single one
/// of them repeated a lid byte and a cycle counter that had already been announced
/// seconds or minutes earlier - never once a new event. The clearest is 21:41:07.949,
/// a "lid closed, cycle 1" with no reading, while the case went on advertising "lid
/// open, cycle 1" 750 ms later and only really closed at 21:41:16. A packet with no
/// reading is a duplicate the radio could not place, so it is not evidence of anything
/// and never reaches the state machine. It is still written to the log as
/// "drop reason=filter", so nothing becomes invisible.
///
/// Distance, on the other hand, is not a reason to drop a packet from the case: the
/// case is the only transmitter that reports the lid at all. Deciding whose case it is
/// belongs to DeviceTracker, which follows model plus colour and therefore survives the
/// address rotation - and which says the same thing in its own words: once the device
/// is known, no signal floor may silently drop its packets.
/// </summary>
internal static class PacketFilter
{
	/// <summary>What Windows reports when the radio gave no reading at all.</summary>
	public const short UnknownRssi = -127;

	/// <summary>A known pair may fade a long way before its packets stop counting.</summary>
	public const short PairedFloor = -95;

	/// <summary>Before pairing is known, only a case on this desk gets through.</summary>
	public const short StrangerFloor = -55;

	/// <summary>The same window, a little wider, when "allow nearby" is on.</summary>
	public const short NearbyFloor = -70;

	/// <summary>
	/// Answers whether a parsed advertisement may reach the rest of the app.
	/// </summary>
	/// <param name="carriesLidState">True only for packets from the case (lid byte bit 5).</param>
	/// <param name="hasBatteryData">True when at least one battery nibble decoded.</param>
	/// <param name="rssi">Signal reading, or <see cref="UnknownRssi"/> when there is none.</param>
	/// <param name="familyIsPaired">Windows lists this AirPods family among paired devices.</param>
	/// <param name="isConnected">The followed pair is connected right now.</param>
	/// <param name="allowNearby">The "allow nearby" setting.</param>
	public static bool Allow(bool carriesLidState, bool hasBatteryData, short rssi, bool familyIsPaired, bool isConnected, bool allowNearby)
	{
		// No measurement means no evidence. See the note above: every packet like this in
		// the captures was a duplicate of something already announced.
		if (rssi <= UnknownRssi)
			return false;

		// The case is the only transmitter that reports the lid, and reporting the lid is the
		// entire point of this app. Once the family is known, such a packet is never dropped
		// for being faint; DeviceTracker decides whose it is.
		if (carriesLidState && (familyIsPaired || isConnected))
			return true;

		if (familyIsPaired)
			return isConnected || rssi >= PairedFloor;

		// While Windows is still enumerating paired devices, accept only a close AirPods case
		// carrying real battery data. This removes startup delay without reacting to a
		// neighbour's pair.
		short nearbyFloor = allowNearby ? NearbyFloor : StrangerFloor;
		return hasBatteryData && rssi >= nearbyFloor;
	}
}
