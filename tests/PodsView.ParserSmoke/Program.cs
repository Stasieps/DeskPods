using System.Globalization;
using PodsView;

int checks = 0;
Console.WriteLine("PodsView parser tests: 0.8.41 / in-case-r1");

void Expect(bool condition, string message)
{
    checks++;
    if (!condition)
        throw new InvalidOperationException(message);
}

static byte[] Bytes(string hex) => Convert.FromHexString(hex.PadRight(54, '0'));
static DateTimeOffset At(double seconds) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(checked((long)Math.Round(seconds * TimeSpan.TicksPerSecond)));

// ===================================================================
// Advertisement parser
// ===================================================================
Expect(AirPodsAdvertisementParser.DecodeBattery(10) == 100, "10 must decode to 100%");
Expect(AirPodsAdvertisementParser.DecodeBattery(5) == 50, "5 must decode to 50%");
Expect(AirPodsAdvertisementParser.DecodeBattery(11) is null, "11 must mean unavailable");
Expect(AirPodsAdvertisementParser.DecodeBattery(15) is null, "15 must mean unavailable");

// Synthetic AirPods Pro packet: R=100%, L=90%, case=60%, both pods charging.
byte[] sample = Bytes("0719010e2070a93601000045121212");
Expect(AirPodsAdvertisementParser.TryParse(sample, out ParsedAirPodsData? first), "Packet was rejected");
Expect(first is not null, "Parser returned null");
Expect(first!.Model == "AirPods Pro", "Model detection failed");
Expect(first.LeftBattery == 90 && first.RightBattery == 100 && first.CaseBattery == 60, "Batteries were decoded incorrectly");
Expect(first.LeftCharging && first.RightCharging && !first.CaseCharging, "Charging bits were decoded incorrectly");
Expect(first.LidByte == sample[8], "The raw lid byte must be carried through for diagnostics");
Expect(first.ColorByte == sample[9], "The colour byte is part of the device fingerprint");

// The capture from real hardware, byte by byte:
//   case    0x31 0x32 0x33 open, 0x39 0x3A 0x3B shut, bits 0-2 count the openings
//   earbuds 0x10 forever, no lid state at all
byte[] caseOpen = (byte[])sample.Clone();
caseOpen[8] = 0x31;
byte[] caseShut = (byte[])sample.Clone();
caseShut[8] = 0x39;
byte[] caseOpen2 = (byte[])sample.Clone();
caseOpen2[8] = 0x32;
byte[] earbuds = (byte[])sample.Clone();
earbuds[8] = 0x10;

Expect(AirPodsAdvertisementParser.TryParse(caseOpen, out ParsedAirPodsData? co) && co is not null, "Case packet was rejected");
Expect(co!.CarriesLidState, "Bit 5 marks a packet from the case");
Expect(co.IsCaseOpen, "0x31 is an open lid");
Expect(co.LidOpenCounter == 1, "Bits 0-2 are the lid cycle counter");
Expect(AirPodsAdvertisementParser.TryParse(caseShut, out ParsedAirPodsData? cs) && cs is not null, "Closed case packet was rejected");
Expect(cs!.CarriesLidState && !cs.IsCaseOpen && cs.LidOpenCounter == 1, "0x39 is the same cycle, lid shut");
Expect(AirPodsAdvertisementParser.TryParse(caseOpen2, out ParsedAirPodsData? co2) && co2 is not null && co2.IsCaseOpen && co2.LidOpenCounter == 2, "0x32 is the next opening");
Expect(AirPodsAdvertisementParser.TryParse(earbuds, out ParsedAirPodsData? eb) && eb is not null, "Earbud packet was rejected");
Expect(!eb!.CarriesLidState, "0x10 comes from the earbuds and carries no lid state");

// Real AirPods Pro 2 packet captured from hardware.
byte[] realPro2 = Convert.FromHexString("07190114200b538f10000848273aff815e50000000261e7bfab684");
Expect(AirPodsAdvertisementParser.TryParse(realPro2, out ParsedAirPodsData? second), "Real AirPods Pro 2 packet was rejected");
Expect(second is not null, "Real packet parsed as null");
Expect(second!.Model == "AirPods Pro (2-ге покоління)", "AirPods Pro 2 model detection failed");
Expect(second.LeftBattery == 50 && second.RightBattery == 30 && second.CaseBattery is null, "Batteries of the real packet were decoded incorrectly");
Expect(!second.CarriesLidState, "The real 0x10 packet must not be treated as lid state");

byte[] prefixed = new byte[realPro2.Length + 2];
prefixed[0] = 0x4C;
prefixed[1] = 0x00;
realPro2.CopyTo(prefixed, 2);
Expect(AirPodsAdvertisementParser.TryParse(prefixed, out _), "Company-id-prefixed packet was rejected");
Expect(!AirPodsAdvertisementParser.TryParse([0x07, 0x19], out _), "Short packet was accepted");
Expect(!AirPodsAdvertisementParser.TryParse(new byte[27], out _), "Non-AirPods packet was accepted");

// ===================================================================
// Device identity
// ===================================================================
var devices = new DeviceTracker();
Expect(!devices.Observe("AirPods Pro/00", 0x11, -88, At(0), out _), "A far away pair must not become the followed device");
Expect(devices.Observe("AirPods Pro/00", 0x11, -45, At(1), out _), "The first close pair becomes the followed device");
Expect(devices.BoundKey == "AirPods Pro/00", "The fingerprint of the followed device must be remembered");
Expect(devices.Observe("AirPods Pro/00", 0x22, -47, At(2), out _), "A new address with the same fingerprint is the same case");
Expect(devices.Observe("AirPods Pro/00", 0x22, -127, At(2.5), out _), "A dead signal reading must not drop the followed device");
Expect(devices.Observe("AirPods Pro/00", 0x22, -90, At(3), out _), "A weak packet from the followed device is still its packet");
for (int i = 0; i < 5; i++)
    Expect(!devices.Observe("AirPods Pro/11", 0x99, -80, At(4 + i * 0.5), out _), $"A neighbour pair must be ignored (packet {i})");
Expect(!devices.Observe("AirPods Pro/11", 0x99, -60, At(30), out _), "One packet is not enough to take over");
Expect(!devices.Observe("AirPods Pro/11", 0x99, -60, At(31), out _), "Two packets are not enough to take over");
Expect(devices.Observe("AirPods Pro/11", 0x99, -60, At(32), out _), "After the followed pair went quiet a strong one may take over");
Expect(devices.BoundKey == "AirPods Pro/11", "The hand-over must be recorded");

// ===================================================================
// Lid state machine - the 0.8.6 fix
// ===================================================================
const ulong caseAddr = 0x7EE46A4935D5;
const ulong caseAddr2 = 0x6FDAFCD3BA2D;

// The earbud trickle: 0x10 every second or so, all day long, and always with bit 3
// clear. This is what used to raise the popup with the case shut.
var lid = new LidStateMachine { CloseLockout = TimeSpan.FromSeconds(3) };
for (int i = 0; i < 600; i++)
    Expect(lid.Handle(false, true, 0, 0x49DAA4EFA596, At(i * 0.9)) == LidAction.None, $"An earbud packet ({i}) must never touch the popup");

// A real opening: two case packets of one burst, so about a sixth of a second.
Expect(lid.Handle(true, true, 1, caseAddr, At(600)) == LidAction.None, "The first case packet arms the stream");
Expect(lid.Handle(true, true, 1, caseAddr, At(600.13)) == LidAction.Open, "Two case packets must show the popup at once");
Expect(lid.Handle(true, true, 1, caseAddr, At(600.26)) == LidAction.Update, "Further packets refresh in place");
Expect(lid.Handle(false, true, 0, 0x49DAA4EFA596, At(600.4)) == LidAction.None, "An earbud packet must not even count as a refresh");
Expect(lid.Handle(true, false, 1, caseAddr, At(601)) == LidAction.Close, "One closed packet must hide the popup");

// Everything that arrives afterwards still carries cycle 1: it is the past.
for (int i = 1; i <= 60; i++)
    Expect(lid.Handle(true, true, 1, caseAddr, At(601 + i * 3.0)) == LidAction.None, $"Echo packet {i} of the closed cycle reopened the popup");
for (int i = 1; i <= 200; i++)
    Expect(lid.Handle(false, true, 0, 0x49DAA4EFA596, At(601 + i * 0.7)) == LidAction.None, $"Earbud packet {i} after the close reopened the popup");

// Fail-open: if a whole burst of the closed cycle is really on the air, the counter
// did not move and the popup must still appear rather than be lost for ever.
var stubborn = new LidStateMachine { CloseLockout = TimeSpan.FromSeconds(3) };
Expect(stubborn.Handle(true, true, 1, caseAddr, At(0)) == LidAction.None, "Stubborn fixture arms");
Expect(stubborn.Handle(true, true, 1, caseAddr, At(0.15)) == LidAction.Open, "Stubborn fixture opens");
Expect(stubborn.Handle(true, false, 1, caseAddr, At(2)) == LidAction.Close, "Stubborn fixture closes");
LidAction reopened = LidAction.None;
for (int i = 1; i <= 14 && reopened != LidAction.Open; i++)
    reopened = stubborn.Handle(true, true, 1, caseAddr, At(6 + i * 0.15));
Expect(reopened == LidAction.Open, "A sustained burst of the closed cycle must still be able to show the popup");

// Opening the lid again bumps the counter, and that must be instant.
Expect(lid.Handle(true, true, 2, caseAddr, At(800)) == LidAction.None, "A new cycle arms the stream");
Expect(lid.Handle(true, true, 2, caseAddr, At(800.12)) == LidAction.Open, "A new cycle must open the popup immediately");
Expect(lid.Handle(true, false, 2, caseAddr, At(801)) == LidAction.Close, "And close again");

// The case rotates its radio address; the capture shows the counter repeating after
// that, so a real opening must never be lost to the echo rule.
Expect(lid.Handle(true, true, 2, caseAddr2, At(806)) == LidAction.None, "A new address arms the stream");
Expect(lid.Handle(true, true, 2, caseAddr2, At(806.2)) == LidAction.Open, "The same counter from a new address is a real opening");
Expect(lid.Handle(true, false, 2, caseAddr2, At(807)) == LidAction.Close, "Closing after the rotation");

// A repeated same-address/same-cycle word still needs a sustained burst, not two echoes.
Expect(lid.Handle(true, true, 2, caseAddr2, At(840)) == LidAction.None, "Echo burst arms");
Expect(lid.Handle(true, true, 2, caseAddr2, At(840.2)) == LidAction.None, "Two echoes are not a reopening");
Expect(lid.Handle(true, true, 2, caseAddr2, At(840.4)) == LidAction.None, "Three echoes are not a reopening");
Expect(lid.Handle(true, true, 2, caseAddr2, At(840.6)) == LidAction.None, "Four echoes are not a reopening");
// Clearing the echo guard is not the same as opening: the normal two-packet
// proof still runs. The fifth echo becomes proof 1/2; the sixth completes it.
LidAction fifthEcho = lid.Handle(true, true, 2, caseAddr2, At(840.8));
Expect(fifthEcho == LidAction.None && !lid.IsOpen,
    $"The fifth echo should arm proof, not open: {fifthEcho}; {lid.Describe(At(840.8))}; {lid.LastBlockReason}");
LidAction sixthEcho = lid.Handle(true, true, 2, caseAddr2, At(840.9));
Expect(sixthEcho == LidAction.Open && lid.IsOpen,
    $"The next packet should complete proof: {sixthEcho}; {lid.Describe(At(840.9))}; {lid.LastBlockReason}");
Expect(lid.Handle(true, false, 2, caseAddr2, At(841)) == LidAction.Close, "Closing once more");

// Separate count from duration: even five repeats spanning only 799 ms cannot
// clear the echo guard. At exactly 800 ms proof starts; another packet is needed.
var echoBoundary = new LidStateMachine();
Expect(echoBoundary.Handle(true, true, 1, caseAddr, At(850), provenFresh: true) == LidAction.Open, "Boundary fixture opens");
Expect(echoBoundary.Handle(true, false, 1, caseAddr, At(851)) == LidAction.Close, "Boundary fixture closes");
foreach (int milliseconds in new[] { 0, 200, 400, 600, 799 })
    Expect(echoBoundary.Handle(true, true, 1, caseAddr, At(852).AddMilliseconds(milliseconds)) == LidAction.None,
        $"Echo guard cleared before 800 ms at {milliseconds} ms");
Expect(echoBoundary.Handle(true, true, 1, caseAddr, At(852).AddMilliseconds(800)) == LidAction.None,
    "Reaching 800 ms arms proof but must not bypass the second confirmation");
Expect(echoBoundary.Handle(true, true, 1, caseAddr, At(852).AddMilliseconds(900)) == LidAction.Open,
    "A follow-up packet after the echo guard must complete proof");

// The backlog Windows replays on wake-up says nothing about the lid.
for (int i = 1; i <= 60; i++)
    Expect(lid.Handle(true, true, 5, caseAddr, At(900 + i * 0.03), trusted: false) == LidAction.None, $"Queued packet {i} reopened the popup");
// Nor does a pair in the next room.
for (int i = 1; i <= 60; i++)
    Expect(lid.Handle(true, true, 6, 0xAAAA, At(1000 + i * 0.2), bound: false) == LidAction.None, $"A neighbour packet {i} reopened the popup");

// An open case is quiet between bursts, so silence may only suspend the cycle.
var watchdog = new LidStateMachine { CloseLockout = TimeSpan.FromSeconds(3) };
Expect(watchdog.Handle(true, true, 3, caseAddr, At(0)) == LidAction.None, "Watchdog fixture arms");
Expect(watchdog.Handle(true, true, 3, caseAddr, At(0.2)) == LidAction.Open, "Watchdog fixture opens");
Expect(!watchdog.ShouldTimeout(At(3.5)), "The watchdog must not fire while the stream is alive");
Expect(watchdog.Handle(true, true, 3, caseAddr, At(3.5)) == LidAction.Update, "The stream keeps the popup alive");
// 0.8.47: the case said its piece inside the first 20 s, so the quiet tail is
// 6 s (it used to be 10 s): the card goes away soon after the case falls quiet.
Expect(!watchdog.ShouldTimeout(At(9)), "A case that pauses for a few seconds must keep its popup");
Expect(!watchdog.ShouldTimeout(At(9.4)), "A fresh packet must postpone the timeout");
Expect(watchdog.ShouldTimeout(At(9.6)), "Six quiet seconds take the popup down");
watchdog.SuspendForSilence(At(9.6));
Expect(!watchdog.IsOpen, "Silence must leave the machine closed");
Expect(watchdog.IsSuspended, "Silence must mark the cycle as spent");

// 0.8.47: no pop-back. A case left lying open keeps talking in bursts; the card
// already showed for this cycle, so those bursts may not bring it back again.
Expect(watchdog.Handle(true, true, 3, caseAddr, At(20)) == LidAction.None, "A later burst of a spent cycle stays quiet");
Expect(watchdog.Handle(true, true, 3, caseAddr, At(20.15)) == LidAction.None, "0.8.47: no pop-back for a case lying open");
Expect(watchdog.IsSuspended, "The spent mark must survive the bursts");

// A lone stale packet is not a burst and may never resurrect a suspended cycle.
var lonely = new LidStateMachine();
Expect(lonely.Handle(true, true, 5, caseAddr, At(0)) == LidAction.None, "Lonely fixture arms");
Expect(lonely.Handle(true, true, 5, caseAddr, At(0.15)) == LidAction.Open, "Lonely fixture opens");
lonely.SuspendForSilence(At(11));
for (int i = 1; i <= 30; i++)
    Expect(lonely.Handle(true, true, 5, caseAddr, At(11 + i * 3.0)) == LidAction.None, $"A single stale packet {i} resurrected a suspended popup");

// The close button, however, still ends the cycle for good.
watchdog.ForceClosed(At(21));
// 0.8.47: closing by hand also marks the cycle as spent (by hand), so it stays buried.
Expect(!watchdog.IsOpen && watchdog.IsSuspended, "ForceClosed must bury the cycle as spent by hand");
for (int i = 1; i <= 60; i++)
    Expect(watchdog.Handle(true, true, 3, caseAddr, At(21 + i * 0.15)) == LidAction.None, $"Packet {i} of a dismissed cycle came back");
Expect(watchdog.Handle(true, true, 4, caseAddr, At(40)) == LidAction.None, "A genuinely new cycle arms");
Expect(watchdog.Handle(true, true, 4, caseAddr, At(40.2)) == LidAction.Open, "A genuinely new cycle must still open the popup");

// 0.8.3 shipped a settings box that could put a whole minute in front of a real
// opening. Whatever an old settings.json still says, it may never do that again.
var clamped = new LidStateMachine { CloseLockout = TimeSpan.FromSeconds(60) };
Expect(clamped.CloseLockout <= TimeSpan.FromSeconds(3), "A saved 60s cooldown must be clamped away");
Expect(clamped.Handle(true, true, 7, caseAddr, At(0)) == LidAction.None, "Clamped fixture arms");
Expect(clamped.Handle(true, true, 7, caseAddr, At(0.15)) == LidAction.Open, "Clamped fixture opens");
Expect(clamped.Handle(true, false, 7, caseAddr, At(1)) == LidAction.Close, "Clamped fixture closes");
Expect(clamped.Handle(true, true, 8, caseAddr, At(4.2)) == LidAction.None, "A new cycle arms after the clamped lockout");
Expect(clamped.Handle(true, true, 8, caseAddr, At(4.35)) == LidAction.Open, "Reopening must never wait longer than three seconds");

// 0.8.8: when the radio timestamp proves a packet is seconds new it cannot be part of the
// backlog Windows replays, so one packet of a new cycle is enough and the popup is instant.
var fresh88 = new LidStateMachine();
Expect(fresh88.Handle(true, true, 2, caseAddr, At(0), provenFresh: true) == LidAction.Open, "A proven-fresh packet of a new cycle must show the popup at once");
Expect(fresh88.Handle(true, true, 2, caseAddr, At(0.13), provenFresh: true) == LidAction.Update, "The rest of that burst only refreshes");
Expect(fresh88.Handle(true, false, 2, caseAddr, At(3), provenFresh: true) == LidAction.Close, "A closed packet still hides it");
for (int i = 1; i <= 40; i++)
    Expect(fresh88.Handle(true, true, 2, caseAddr, At(3 + i * 2.5), provenFresh: true) == LidAction.None, $"Fresh echo {i} of the closed cycle went instant");
Expect(fresh88.Handle(true, true, 3, caseAddr, At(120), provenFresh: true) == LidAction.Open, "The next real opening must be instant again");

// Freshness is the whole licence. Without it the two-packet proof is untouched.
var slow88 = new LidStateMachine();
Expect(slow88.Handle(true, true, 1, caseAddr, At(0)) == LidAction.None, "A packet that cannot prove its age may not open on its own");
Expect(slow88.Handle(true, true, 1, caseAddr, At(0.15)) == LidAction.Open, "Two of them still open the popup");

// A replayed backlog is never trusted, whatever it claims about its age.
var queued88 = new LidStateMachine();
for (int i = 1; i <= 60; i++)
    Expect(queued88.Handle(true, true, 4, caseAddr, At(i * 0.03), trusted: false, provenFresh: true) == LidAction.None, $"Queued packet {i} opened the popup instantly");

// Neither is a neighbour, however close and however fresh.
var stranger88 = new LidStateMachine();
for (int i = 1; i <= 60; i++)
    Expect(stranger88.Handle(true, true, 4, caseAddr2, At(i * 0.2), bound: false, provenFresh: true) == LidAction.None, $"Neighbour packet {i} opened the popup instantly");

// The close button still wins: a buried cycle may not come back on a fresh packet.
var dismissed88 = new LidStateMachine();
Expect(dismissed88.Handle(true, true, 5, caseAddr, At(0), provenFresh: true) == LidAction.Open, "Dismissed fixture opens");
dismissed88.ForceClosed(At(1));
for (int i = 1; i <= 40; i++)
    Expect(dismissed88.Handle(true, true, 5, caseAddr, At(1 + i * 0.15), provenFresh: true) == LidAction.None, $"Dismissed cycle came back instantly on packet {i}");
Expect(dismissed88.Handle(true, true, 6, caseAddr, At(20), provenFresh: true) == LidAction.Open, "A new cycle after a dismissal is instant");

// ===================================================================
// The filter, in the one case that cost a real symptom (0.8.9)
// ===================================================================
Expect(PacketFilter.Allow(true, true, -90, true, false, true), "A faint packet from the case of a paired pair must pass");
Expect(PacketFilter.Allow(true, true, -50, false, false, false), "A case on this desk passes even before pairing is known");
Expect(!PacketFilter.Allow(true, true, -60, false, false, false), "A stranger's case stays out until pairing is known");
Expect(PacketFilter.Allow(false, true, -60, true, false, true), "An earbud packet of a paired pair passes");
Expect(!PacketFilter.Allow(false, true, -100, true, false, true), "A far earbud packet of a disconnected pair does not");
// Twelve packets like this appear in the captures of 2026-08-29 and every one repeated a
// lid byte already announced. The clearest is 21:41:07.949, "lid closed, cycle 1" with no
// reading, while the case went on announcing cycle 1 as open 750 ms later.
Expect(!PacketFilter.Allow(true, true, PacketFilter.UnknownRssi, true, true, true), "A packet with no signal reading is not evidence of anything");

// LidSignal: a level in the explicit range, an event everywhere else.
// 7C6934E7440F sent 0x11 one hundred and seventy times on 2026-08-29 without ever changing
// it. Reading that as "lid open" would pin the popup on the screen forever.
DateTimeOffset signalOrigin = new(2026, 8, 29, 22, 26, 0, TimeSpan.Zero);
LidSignal frozenSignal = new();
string frozenWhy = "";
for (int i = 0; i < 170; i++)
    Expect(!frozenSignal.Believe(0x7C6934E7440FUL, 0x02F88F, 0x11, false, signalOrigin.AddSeconds(i), out frozenWhy), "The frozen 0x11 stream must never be believed");
Expect(frozenWhy == "static", "A repeat from an unconfirmed address is static");

// 508E6E99E5BD is the case. It announced the lid without bit 5 for 41 seconds and then
// switched to the explicit range mid-cycle, which is what 0.8.9 could not follow.
LidSignal rotatingSignal = new();
Expect(!rotatingSignal.Believe(0x508E6E99E5BDUL, 0x24FA93, 0x11, false, signalOrigin, out string baselineWhy) && baselineWhy == "baseline", "The first byte from an address decides nothing");
Expect(rotatingSignal.Believe(0x508E6E99E5BDUL, 0x24FA93, 0x19, false, signalOrigin.AddSeconds(8), out string changeWhy) && changeWhy == "change", "A changed nibble outside the explicit range is a real lid event");
Expect(rotatingSignal.Believe(0x508E6E99E5BDUL, 0x24FA93, 0x19, false, signalOrigin.AddSeconds(9), out string holdWhy) && holdWhy == "hold", "A confirmed address keeps the stream alive with its repeats");
Expect(rotatingSignal.Believe(0x508E6E99E5BDUL, 0x24FA93, 0x3A, true, signalOrigin.AddSeconds(41), out string explicitWhy) && explicitWhy == "explicit", "The explicit range is believed whatever came before it");
rotatingSignal.Reset();
Expect(rotatingSignal.Believe(0x508E6E99E5BDUL, 0x24FA93, 0x19, false, signalOrigin.AddSeconds(60), out string resetWhy) && resetWhy == "known", "A reset preserves learned shapes for the first opening");

// 0.8.11. The case renames itself every minute or two, and 0.8.10 threw away the first
// thing every new address said - which is exactly the lid being opened: 5.32 s lost at
// 23:01:01, 2.69 s at 23:02:16, 3.30 s at 23:03:16. Payload bytes 5 to 7 tell the living
// case from the frozen copy in all four recorded sessions, and the copy even carried its
// own triple across an address change of its own at 23:07:24. Only an explicit packet may
// lend a fingerprint onward, because the copy has never sent one.
const uint caseIdentity = 0x24FA93;   // the triple the case wakes up with
const uint copyIdentity = 0x02F78F;   // the triple that has never moved
LidSignal rotation = new();
Expect(!rotation.Believe(0x4B45E954BDBBUL, caseIdentity, 0x11, false, signalOrigin, out string coldWhy) && coldWhy == "baseline", "An untrusted fingerprint may not raise the popup on its own");
Expect(rotation.Believe(0x455A43108D8FUL, caseIdentity, 0x38, true, signalOrigin.AddSeconds(30), out _), "An explicit packet is believed and lends its fingerprint");
Expect(rotation.Believe(0x453A6FB0A387UL, caseIdentity, 0x11, false, signalOrigin.AddSeconds(66), out string freshWhy) && freshWhy == "fresh", "After a rotation the case's first word is the lid opening");
Expect(!rotation.Believe(0x6F0A5E515EDFUL, copyIdentity, 0x11, false, signalOrigin.AddSeconds(90), out string copyWhy) && copyWhy == "baseline", "A new address alone proves nothing: the frozen copy rotates too");
Expect(rotation.Believe(0x67E7A7D03420UL, caseIdentity, 0x11, false, signalOrigin.AddMinutes(45), out string staleWhy) && staleWhy == "known", "The fingerprint expires but the actively used shape remains valid");

// 0.8.41: payload byte 5 says where the transmitting earbud is. realPro2 above comes from
// earbuds in the ears (status 0x0B); 02/03/11/13 come from the worn earbud while the other
// one is in the case; 04/24/53/71/73 come from an earbud inside the case. Only the last
// group may speak for the lid, open or closed.
Expect(CaseSignalClassifier.OutsideCase(second!), "0.8.41: status 0x0B (both earbuds in the ears) is outside the case");
{
    var v41Signal = new LidSignal();
    for (int n41 = 0; n41 < 50; n41++)
        Expect(!CaseSignalClassifier.Believe(v41Signal, second!, 0x1C, At(n41), true, true, true, true, -64, out string v41Why)
            && v41Why == "out-of-case", $"0.8.41: in-ear packet {n41} was believed as lid state");
    foreach (int v41Status in new[] { 0x02, 0x03, 0x11, 0x13, 0x22, 0x33, 0x0B, 0x2B })
    {
        byte[] v41Out = (byte[])realPro2.Clone();
        v41Out[5] = (byte)v41Status;
        v41Out[8] = 0x19;
        Expect(AirPodsAdvertisementParser.TryParse(v41Out, out ParsedAirPodsData? v41OutData) && v41OutData is not null
            && CaseSignalClassifier.OutsideCase(v41OutData), $"0.8.41: status {v41Status:X2} was taken for the case");
    }
    foreach (int v41Status in new[] { 0x04, 0x24, 0x53, 0x71, 0x73 })
    {
        byte[] v41In = (byte[])realPro2.Clone();
        v41In[5] = (byte)v41Status;
        Expect(AirPodsAdvertisementParser.TryParse(v41In, out ParsedAirPodsData? v41InData) && v41InData is not null
            && !CaseSignalClassifier.OutsideCase(v41InData), $"0.8.41: status {v41Status:X2} was refused as outside the case");
    }
    // The earbud in the case is full, so its charge nibble reads 8 where the learned
    // signature has 9: shape 0x38 against 0x39. It is still the case, on its first word.
    // The worn earbud sending the very same shape is not.
    var v41Family = new LidSignal();
    v41Family.Seed("39", At(0));
    byte[] v41Full = (byte[])realPro2.Clone();
    v41Full[5] = 0x73; v41Full[6] = 0xA5; v41Full[7] = 0x83; v41Full[8] = 0x11;
    Expect(AirPodsAdvertisementParser.TryParse(v41Full, out ParsedAirPodsData? v41FullData) && v41FullData is not null
        && LidSignal.Shape(v41FullData.Identity) == 0x38, "0.8.41: the full-earbud fixture is not shape 0x38");
    Expect(CaseSignalClassifier.Believe(v41Family, v41FullData!, 0x2A, At(1), true, true, true, true, -60, out string v41FullWhy)
        && v41FullWhy == "known", $"0.8.41: a full earbud in the case was not believed on its first word ({v41FullWhy})");
    byte[] v41Worn = (byte[])v41Full.Clone();
    v41Worn[5] = 0x33;
    Expect(AirPodsAdvertisementParser.TryParse(v41Worn, out ParsedAirPodsData? v41WornData) && v41WornData is not null
        && !CaseSignalClassifier.Believe(v41Family, v41WornData, 0x2B, At(2), true, true, true, true, -60, out string v41WornWhy)
        && v41WornWhy == "out-of-case", "0.8.41: the worn earbud spoke for the lid");
}
CoreRegressions.Run(Expect);
PopupLatencyRegressions.Run(Expect);
Release29Regressions.Run(Expect);
Release31Regressions.Run(Expect);
Release33Regressions.Run(Expect);
Release34Regressions.Run(Expect);
Release35Regressions.Run(Expect);
ReplayRunner.Run(Expect);
Console.WriteLine($"PodsView smoke tests: PASS ({checks} assertions; real C# sources, simulated radio/time)");
