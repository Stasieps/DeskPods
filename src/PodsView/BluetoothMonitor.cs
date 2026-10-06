using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace PodsView;

internal sealed class BluetoothMonitor : IDisposable
{
    private const ushort AppleCompanyId = 0x004C;
    private readonly Func<bool> _allowNearby;
    private readonly Func<string> _rememberedDeviceKey;
    private HashSet<string> _connectedFamilies = new(StringComparer.OrdinalIgnoreCase);
    private ushort _identifiedCode;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly SemaphoreSlim _restartLock = new(1, 1);
    private bool _standbyPaused;
    private Task? _scheduledRestart;
    private BluetoothLEAdvertisementWatcher? _watcher;
    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private HashSet<string> _pairedFamilies = new(StringComparer.OrdinalIgnoreCase);
    private DeviceStatus _status = DeviceStatus.Initial;
    private bool _disposed;
    private int _restartPending;
    private DateTimeOffset _watcherStartedAt = DateTimeOffset.MinValue;
    private int _staleStreak;
    private bool _freshSeen;
    private readonly DeviceTracker _devices = new();
    private LidSignal _lidSignal = new();
    private readonly CaseProfileHistory _profileTraffic = new();
    private string _activeDeviceKey = string.Empty;
    private readonly DateTimeOffset _createdAt = DateTimeOffset.UtcNow;
    private int _traced;
    private long _observationSequence;
    private long _callbackCount, _appleCount, _parsedCount, _rejectedCount;
    // Lock-free diagnostics; the UI watchdog must never wait for the radio lock.
    /// <summary>0.8.39: raw callback count, for the case-silence gap trace.</summary>
    internal long CallbackCount => Interlocked.Read(ref _callbackCount);
    internal string DiagnosticCounters => $"callbacks={Interlocked.Read(ref _callbackCount)} apple={Interlocked.Read(ref _appleCount)} parsed={Interlocked.Read(ref _parsedCount)} rejected={Interlocked.Read(ref _rejectedCount)}";
    private int _nextTracePeer;
    private readonly Dictionary<ulong, int> _tracePeers = new();
    private DateTimeOffset _lastCaseDataAt = DateTimeOffset.MinValue;
    private DateTimeOffset _caseSilenceLoggedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _summaryStartedAt = DateTimeOffset.MinValue;
    private int _packetsSinceCaseData;
    private int _summaryPackets;
    private int _summaryCaseData;
    private int _summaryBelievable;
    private int _lastCaseBattery = -1;
    private readonly Func<string, string> _readCaseShapes;
    private readonly Action<string, string> _saveCaseShapes;
    private DateTimeOffset _lastShapeSave;
    private readonly EarbudCache _earbudCache = new();
    private readonly StartupObservationBuffer _startupObservations = new();
    private bool _pairingCatalogReady;
    private string _savedShapes = string.Empty;
    /// <summary>
    /// Windows queues the advertisements it received while the machine sat in
    /// Modern Standby and hands the whole backlog over at once on wake-up. That
    /// behaviour is documented on BluetoothLEAdvertisementWatcher.Start and it is
    /// exactly what made the popup reappear minutes after the lid was shut.
    /// A backlog packet still carries its original radio timestamp, so comparing
    /// that timestamp with the wall clock separates it from a live broadcast.
    /// </summary>
    private static readonly TimeSpan StaleAdvertisement = TimeSpan.FromSeconds(3);

    /// <summary>
    /// A packet younger than this cannot belong to the backlog Windows replays after
    /// standby, which is minutes old. It is the licence the state machine needs to show the
    /// popup on the very first case packet instead of waiting for a second one.
    /// </summary>
    private const int InstantAgeMs = 2000;

    public BluetoothMonitor(Func<bool> allowNearby, Func<string, string>? readCaseShapes = null, Action<string, string>? saveCaseShapes = null, Func<string>? rememberedDeviceKey = null)
    {
        _allowNearby = allowNearby;
        _rememberedDeviceKey = rememberedDeviceKey ?? (() => "");
        _readCaseShapes = readCaseShapes ?? (_ => string.Empty);
        _saveCaseShapes = saveCaseShapes ?? ((_, _) => { });
    }
    public event Action<AirPodsPacket>? PacketReceived;
    public event Action<DeviceStatus>? StatusChanged;

    public DeviceStatus CurrentStatus { get { lock (_sync) return _status; } }

    public async Task StartAsync()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cancellation is not null) return;
            _cancellation = new CancellationTokenSource();
            // BLE-first startup is deliberately preserved.
            StartWatcher();
            _loop = MonitorLoopAsync(_cancellation.Token);
        }
        await RefreshDevicesAsync().ConfigureAwait(false);
    }

    public async Task RestartAsync(bool resumeFromStandby = true)
    {
        await _restartLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            CancellationToken token = _cancellation?.Token ?? CancellationToken.None;
            lock (_sync)
            {
                if (resumeFromStandby) _standbyPaused = false;
                if (_standbyPaused) return;
            }
            StopWatcher();
            await Task.Delay(80, token).ConfigureAwait(false);
            // Resume listening BEFORE potentially slow Windows device enumeration.
            if (!token.IsCancellationRequested) StartWatcher();
            await RefreshDevicesAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposed) { }
        finally { _restartLock.Release(); }
    }

    private async Task MonitorLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                lock (_sync) { if (_standbyPaused || _disposed) continue; }
                await RefreshDevicesAsync().ConfigureAwait(false);
                BluetoothLEAdvertisementWatcher? watcher;
                lock (_sync) watcher = _watcher;
                if (watcher is null || watcher.Status is BluetoothLEAdvertisementWatcherStatus.Aborted or BluetoothLEAdvertisementWatcherStatus.Stopped)
                    ScheduleRestart();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { Logger.Error("Bluetooth monitor loop failed", ex); }
        }
    }

    private async Task RefreshDevicesAsync()
    {
        if (!await _refreshLock.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var found = new List<(string Name, string Family, bool Connected)>();
            DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelector());
            // Pairing names are available before the slower per-device connection queries.
            // Release only still-fresh buffered observations through the normal filters.
            PrimePairingCatalog(devices);
            foreach (DeviceInformation info in devices)
            {
                if (string.IsNullOrWhiteSpace(info.Name) || !IsSupportedName(info.Name)) continue;
                bool deviceConnected = false;
                try
                {
                    using BluetoothDevice? device = await BluetoothDevice.FromIdAsync(info.Id);
                    deviceConnected = device?.ConnectionStatus == BluetoothConnectionStatus.Connected;
                }
                catch (Exception ex) { Logger.Debug($"Connection query failed for {info.Name}: {ex.Message}"); }
                found.Add((info.Name.Trim(), FamilyFromName(info.Name), deviceConnected));
            }

            var selected = found.FirstOrDefault(item => item.Connected);
            if (string.IsNullOrWhiteSpace(selected.Name)) selected = found.FirstOrDefault();
            lock (_sync)
            {
                _pairedFamilies = found.Select(item => item.Family).ToHashSet(StringComparer.OrdinalIgnoreCase);
                _connectedFamilies = found.Where(item => item.Connected).Select(item => item.Family).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            bool paired = found.Count > 0;
            bool anyConnected = found.Any(item => item.Connected);
            bool watcherRunning = IsWatcherRunning();
            DeviceStatus previous = CurrentStatus;
            Publish(previous with
            {
                Mode = anyConnected ? MonitorMode.Connected : watcherRunning ? MonitorMode.Scanning : MonitorMode.Disconnected,
                DeviceName = string.IsNullOrWhiteSpace(selected.Name) ? "AirPods" : selected.Name,
                Family = string.IsNullOrWhiteSpace(selected.Family) ? null : selected.Family,
                IsPaired = paired,
                IsConnected = anyConnected,
                MultiplePairedCandidates = found.Count > 1,
                IdentifiedModelCode = string.IsNullOrWhiteSpace(selected.Family)
                    || AirPodsAdvertisementParser.FamilyForModel(DeviceCatalog.DisplayName(previous.IdentifiedModelCode)) == selected.Family
                    ? previous.IdentifiedModelCode : (ushort)0,
                WatcherRunning = watcherRunning,
                Detail = anyConnected ? "AirPods підключено — слухаю BLE-пакети" : paired ? "AirPods знайдено — очікую сигнал" : "Спочатку підключи AirPods у Windows"
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.Error("Bluetooth access denied", ex);
            Publish(CurrentStatus with { Mode = MonitorMode.Error, Detail = "Windows не дозволив доступ до Bluetooth" });
        }
        catch (Exception ex)
        {
            Logger.Error("Bluetooth device query failed", ex);
            Publish(CurrentStatus with { Mode = MonitorMode.BluetoothUnavailable, Detail = "Bluetooth недоступний або вимкнений" });
        }
        finally { _refreshLock.Release(); }
    }

    private void PrimePairingCatalog(DeviceInformationCollection devices)
    {
        lock (_sync)
        {
            if (_disposed || _pairingCatalogReady) return;
            var supported = devices.Where(info => !string.IsNullOrWhiteSpace(info.Name) && IsSupportedName(info.Name)).ToArray();
            _pairedFamilies = supported.Select(info => FamilyFromName(info.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _pairingCatalogReady = true;
            var first = supported.FirstOrDefault();
            Publish(_status with
            {
                IsPaired = supported.Length > 0,
                DeviceName = first?.Name ?? "AirPods",
                Family = first is null ? null : FamilyFromName(first.Name)
            });
            int pending = _startupObservations.Count;
            RadioObservation[] observations = _startupObservations.Drain(DateTimeOffset.UtcNow);
            Logger.Trace($"startup phase=pairing-ready pending={pending} fresh={observations.Length} expired={pending-observations.Length}");
            if (_standbyPaused || _watcher is null) return;
            foreach (RadioObservation observation in observations)
            {
                try { ProcessObservation(observation); }
                catch (Exception ex) { Logger.Error("Buffered startup packet processing failed", ex); }
            }
        }
    }

    private void StartWatcher()
    {
        lock (_sync)
        {
            if (_disposed || _standbyPaused || _watcher is not null) return;
            try
            {
                var watcher = new BluetoothLEAdvertisementWatcher
                {
                    ScanningMode = BluetoothLEScanningMode.Active,
                    AllowExtendedAdvertisements = true
                };
                // Explicit zero coalescing. This requests all observations; it cannot
                // override the adapter/Windows radio duty cycle or fabricate missing packets.
                watcher.SignalStrengthFilter.SamplingInterval = TimeSpan.Zero;
                watcher.Received += OnReceived;
                watcher.Stopped += OnStopped;
                _watcher = watcher;
                _watcherStartedAt = DateTimeOffset.UtcNow;
                watcher.Start();
                Logger.Trace("startup phase=radio-started");
                Publish(_status with { Mode = _status.IsConnected ? MonitorMode.Connected : MonitorMode.Scanning, WatcherRunning = true });
                Logger.Info("BLE watcher started (active scan, extended advertisements, no sampling interval)");
            }
            catch (Exception ex)
            {
                StopWatcher();
                Logger.Error("BLE watcher start failed", ex);
                Publish(_status with { Mode = MonitorMode.BluetoothUnavailable, WatcherRunning = false, Detail = "Bluetooth scan could not start" });
                ScheduleRestart();
            }
        }
    }

    private void StopWatcher()
    {
        lock (_sync)
        {
            BluetoothLEAdvertisementWatcher? watcher = _watcher;
            _watcher = null;
            _lidSignal.Reset(); _profileTraffic.Reset();
            _startupObservations.Clear();
            if (watcher is null) return;
            try
            {
                watcher.Received -= OnReceived;
                watcher.Stopped -= OnStopped;
                if (watcher.Status is BluetoothLEAdvertisementWatcherStatus.Started or BluetoothLEAdvertisementWatcherStatus.Created) watcher.Stop();
            }
            catch (Exception ex) { Logger.Debug($"BLE watcher stop returned {ex.Message}"); }
        }
    }

    /// <summary>Numeric size of a saved signature list, for the exported trace only.</summary>
    private static int CountShapes(string shapes) => shapes.Length == 0 ? 0 : shapes.Split(',').Length;

    private void SelectSignalMemory(string key, DateTimeOffset observedAt)
    {
        if (_activeDeviceKey == key) return;
        _activeDeviceKey = key;
        _lidSignal = new LidSignal();
        _profileTraffic.Reset();
        _lidSignal.Seed(_readCaseShapes(key), observedAt);
        _savedShapes = _lidSignal.LearnedShapes();
        _lastShapeSave = default;
        _earbudCache.Clear();
        Logger.Info($"Following signal memory for {key}: {_savedShapes}");
        // 0.8.35: the exported trace only carries Trace lines, so the Info line above could
        // never prove whether the saved signatures actually came back. This one can, and it
        // is numeric only - no device key, no address, no hex.
        Logger.Trace($"shapes phase=seed count={CountShapes(_savedShapes)}");
    }

    private void PersistCaseShapes(DateTimeOffset now)
    {
        string shapes = _lidSignal.LearnedShapes();
        if (shapes.Length == 0) return;
        bool changed = !string.Equals(shapes, _savedShapes, StringComparison.Ordinal);
        if (!changed && _lastShapeSave != default && now - _lastShapeSave < TimeSpan.FromMinutes(1)) return;
        _savedShapes = shapes;
        _lastShapeSave = now;
        if (changed) Logger.Info($"Case signature learned: {shapes}");
        Logger.Trace($"shapes phase=save count={CountShapes(shapes)}");
        try { _saveCaseShapes(_activeDeviceKey, _lidSignal.ExportState()); }
        catch (Exception ex) { Logger.Error("Case signature save failed", ex); }
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        Interlocked.Increment(ref _callbackCount);
        DateTimeOffset callbackAt = DateTimeOffset.UtcNow;
        lock (_sync)
        {
        if (_disposed || _standbyPaused || !ReferenceEquals(sender, _watcher)) return;
        try
        {
            foreach (BluetoothLEManufacturerData section in args.Advertisement.ManufacturerData)
            {
                if (section.CompanyId != AppleCompanyId) continue;
                Interlocked.Increment(ref _appleCount);
                long sequence = ++_observationSequence;
                if (!_tracePeers.TryGetValue(args.BluetoothAddress, out int peer))
                {
                    if (_tracePeers.Count >= 128) _tracePeers.Clear();
                    peer = ++_nextTracePeer; _tracePeers[args.BluetoothAddress] = peer;
                }
                // This record precedes buffer decoding AND parser acceptance. Every Apple
                // manufacturer section gets a seq, including rejected/unknown formats.
                Logger.Trace($"adv seq={sequence} peer={peer} byteCount={section.Data.Length} rssi={args.RawSignalStrengthInDBm} ageMs={RadioAgeMs(args, DateTimeOffset.UtcNow)} callbackMs={(int)(DateTimeOffset.UtcNow-callbackAt).TotalMilliseconds}");
                byte[] payload;
                try { payload = ReadBuffer(section.Data); }
                catch
                {
                    Interlocked.Increment(ref _rejectedCount);
                    Logger.Trace($"parse seq={sequence} peer={peer} parsed=0 reason=read-error");
                    throw; // Preserve the existing outer error handler; never accept a bad buffer.
                }
                string header = AdvertisementDiagnostics.Header(payload);
                if (!AirPodsAdvertisementParser.TryParse(payload, out ParsedAirPodsData? data) || data is null)
                {
                    Interlocked.Increment(ref _rejectedCount);
                    Logger.Trace($"parse seq={sequence} peer={peer} parsed=0 {header} reason={AdvertisementDiagnostics.FailureReason(payload)}");
                    continue;
                }
                Interlocked.Increment(ref _parsedCount);
                Logger.Trace($"parse seq={sequence} peer={peer} parsed=1 {header} reason=accepted");
                DateTimeOffset now = DateTimeOffset.UtcNow;
                int radioAgeMs = RadioAgeMs(args, now);
                // Before ANY eligibility/classification gate, including after hours idle.
                // Anonymous peer numbers are session-local. No MAC, alias or encrypted payload.
                // 0.8.40: status is payload byte 5, the in-case/in-ear flag set. It is what tells a
                // stale lid word from an earbud apart from a real word from the case.
                Logger.Trace($"rx seq={sequence} peer={peer} modelCode={data.ModelCode} lidByte={data.LidByte} shape={LidSignal.Shape(data.Identity)} status={(data.Identity >> 16) & 0xFF} explicit={(data.CarriesLidState ? 1 : 0)} open={(data.IsCaseOpen ? 1 : 0)} cycle={data.LidOpenCounter} rssi={args.RawSignalStrengthInDBm} ageMs={radioAgeMs} callbackMs={(int)(now-callbackAt).TotalMilliseconds}");
                // A reading of -127 means the radio attached no measurement at all, and every
                // packet like that in the captures repeated a lid byte announced earlier. It is
                // dropped before the lid tracker sees it, so a replayed byte can never be
                // mistaken for a lid change.
                if (args.RawSignalStrengthInDBm <= PacketFilter.UnknownRssi)
                {
                    if (data.CarriesLidState || ShouldTrace(now))
                        Logger.Trace($"drop reason=no-reading seq={sequence} peer={peer} addr={args.BluetoothAddress:X12} model={data.Model} lid=0x{data.LidByte:X2} open={(data.IsCaseOpen ? 1 : 0)} cycle={data.LidOpenCounter}");
                    continue;
                }
                var observation = new RadioObservation(data, args.BluetoothAddress, args.RawSignalStrengthInDBm,
                    Convert.ToHexString(payload), now, radioAgeMs) { Sequence = sequence, TracePeer = peer };
                if (!_pairingCatalogReady && !StartupEligibility.CanProcess(observation, _rememberedDeviceKey(), _allowNearby()))
                {
                    _startupObservations.Add(observation);
                    Logger.Trace($"startup-buffer seq={observation.Sequence} peer={observation.TracePeer} addr={observation.Address:X12} open={(data.IsCaseOpen ? 1 : 0)} ageMs={radioAgeMs}");
                    continue;
                }
                if (ProcessObservation(observation))
                {
                    _startupObservations.Remove(observation.Address);
                }
            }
        }
        catch (Exception ex) { Logger.Error("BLE packet processing failed", ex); }
        }
    }

    // Called only while holding _sync. Buffered and live observations share all guards.
    private bool ProcessObservation(RadioObservation observation)
    {
        if (_disposed || _standbyPaused || _watcher is null) return false;
        ParsedAirPodsData data = observation.Data;
        DateTimeOffset now = observation.ReceivedAt;
        double waited = Math.Max(0, (DateTimeOffset.UtcNow - now).TotalMilliseconds);
        int radioAgeMs = observation.RadioAgeMs < 0 ? -1
            : (int)Math.Min(int.MaxValue, observation.RadioAgeMs + waited);
        bool radioFresh = radioAgeMs < 0 || radioAgeMs <= (int)StaleAdvertisement.TotalMilliseconds;
        bool trusted = IsLiveObservation(radioFresh);
        bool provenFresh = radioAgeMs >= 0 && radioAgeMs <= InstantAgeMs && TimestampsUsable();
        if (!trusted)
        {
            Logger.Trace($"drop reason=queued seq={observation.Sequence} peer={observation.TracePeer} addr={observation.Address:X12} ageMs={radioAgeMs}");
            return false;
        }
        // This is a permissive pre-filter only. The existing exact PacketFilter is
        // applied after classifying the lid. Challengers still reach DeviceTracker.
        if (!PacketAllowed(data, lidBelievable: true, observation.Rssi))
        {
            Logger.Trace($"drop reason=eligibility seq={observation.Sequence} peer={observation.TracePeer} addr={observation.Address:X12} rssi={observation.Rssi} pairedCatalog={_pairingCatalogReady}");
            return false;
        }
        string key = DeviceCatalog.Key(data);
        string family = AirPodsAdvertisementParser.FamilyForModel(data.Model);
        // A connected family is preferred to unrelated nearby families. Identical pairs
        // remain indistinguishable in this payload; do not claim unique ownership.
        if (_connectedFamilies.Count == 1 && !_connectedFamilies.Contains(family))
        { Logger.Trace($"drop reason=other-family seq={observation.Sequence} peer={observation.TracePeer}"); return false; }
        bool bound = _devices.Observe(key, observation.Address, observation.Rssi, now, out string boundReason);
        if (!bound)
        {
            Logger.Trace($"drop reason=other-device seq={observation.Sequence} peer={observation.TracePeer} key={key} addr={observation.Address:X12} decision={boundReason}");
            return false;
        }
        SelectSignalMemory(key, observation.Previous?.ReceivedAt ?? now);
        // Both snapshots belong to this bound address/model/color and passed the startup
        // age limit. The earlier one teaches the classifier only; it must never flash UI.
        if (observation.Previous is { } previousObservation
            && PacketAllowed(previousObservation.Data, lidBelievable: true, previousObservation.Rssi)
            // 0.8.41: an earbud outside the case may not teach the classifier either.
            && !CaseSignalClassifier.OutsideCase(previousObservation.Data))
            _lidSignal.Believe(previousObservation.Address, previousObservation.Data.Identity,
                previousObservation.Data.LidByte, previousObservation.Data.CarriesLidState,
                previousObservation.ReceivedAt, out _);
        bool pairEvidence = key == _rememberedDeviceKey();
        TimeSpan observedQuiet = _profileTraffic.Observe(data.Identity, now, _watcherStartedAt);
        bool lidBelievable = CaseSignalClassifier.Believe(_lidSignal, data, observation.Address,
            now, trusted, bound, provenFresh, pairEvidence, observation.Rssi, out string lidSource, observedQuiet);
        if (!DeviceCatalog.HasLidProtocol(data.ModelCode)) { lidBelievable = false; lidSource = "battery-only-model"; }
        _identifiedCode = data.ModelCode;
        if (!PacketAllowed(data, lidBelievable, observation.Rssi))
        {
            Logger.Trace($"drop reason=post-filter seq={observation.Sequence} peer={observation.TracePeer} src={lidSource}");
            return false;
        }
        string hex = observation.Hex;
        // Only the case may vote on polarity; the earbuds hold bit 3 clear all day
        // and would win every vote by sheer volume.
        string polarity = "n/a";
        if (data.CarriesLidState)
            polarity = LidPolarity.Observe((data.LidByte & 0b0000_1000) != 0, now, out bool inverted)
                ? (inverted ? "clear=open" : "set=open")
                : "learning";

        // Every case packet is traced, always. The earbuds are sampled: the same line
        // hundreds of times an hour teaches nothing and only buries the case.
        Logger.Trace($"air seq={observation.Sequence} peer={observation.TracePeer} modelCode={data.ModelCode} shape={LidSignal.Shape(data.Identity)} lidByte={data.LidByte} addr={observation.Address:X12} rssi={observation.Rssi} key={key} bound={(bound ? 1 : 0)} ({boundReason}) id={data.Identity:X6} lid=0x{data.LidByte:X2} case={(lidBelievable ? 1 : 0)} src={lidSource} open={(data.IsCaseOpen ? 1 : 0)} cycle={data.LidOpenCounter} trusted={(trusted ? 1 : 0)} fresh={(provenFresh ? 1 : 0)} ageMs={radioAgeMs} polarity={polarity} hex={hex}");
        string? caseNote = NoteCaseTraffic(data, lidBelievable, now);
        PersistCaseShapes(now);
        // What the popup will show. The lid decision above is made on the packet as it
        // arrived; only the numbers on screen are completed from the earbuds' own stream.
        ParsedAirPodsData shown = _earbudCache.Merge(key, data, now);
        DateTimeOffset? listeningSince = key == _rememberedDeviceKey() && _watcherStartedAt != DateTimeOffset.MinValue
            ? _watcherStartedAt : null;
        var packet = new AirPodsPacket(shown, now, observation.Rssi, observation.Address, hex, trusted, bound, key, provenFresh, lidBelievable, listeningSince, observation.Sequence, observation.TracePeer);
        DeviceStatus previous = CurrentStatus;
        Publish(previous with { LastRssi = packet.Rssi, LastPacketAt = packet.ReceivedAt, WatcherRunning = true,
            IdentifiedModelCode = _identifiedCode, Detail = caseNote ?? "Отримано актуальні дані батареї" }, notify: false);
        PacketReceived?.Invoke(packet);
        return true;
    }

    /// <summary>False when the radio timestamp is not reliable enough for the instant path.</summary>
    private static bool TimestampsUsable() => true;

    /// <summary>
    /// Marks a packet as live or queued. Windows hands a process the whole
    /// backlog of advertisements it buffered during Modern Standby, sleep or a
    /// lock, and each of those still carries its original radio timestamp, so the
    /// timestamp is the one honest signal. Nothing else is filtered here any
    /// more: payload-identity bans blocked genuine reopens, because a real case
    /// often reopens with byte-for-byte the same payload.
    ///
    /// Missing timestamps retain the conservative burst-proof path. Stale timestamps
    /// are never promoted to live packets simply because the backlog is long.
    /// </summary>
    private bool IsLiveObservation(bool radioFresh)
    {
        lock (_sync)
        {
            if (radioFresh)
            {
                _staleStreak = 0;
                _freshSeen = true;
                return true;
            }
            _staleStreak++;
            if (!_freshSeen && _staleStreak >= 25)
            {
                Logger.Info("Radio timestamps remain stale; backlog rejected. Check adapter clock/driver diagnostics.");
                _staleStreak = 0;
                return false;
            }
            return false;
        }
    }

    /// <summary>
    /// How long ago the radio really received this advertisement, in milliseconds, or -1 when
    /// Windows offers no usable timestamp. Logging the age turns "trusted=0" from a verdict
    /// into evidence: a packet replayed after standby is minutes old, a live one is a few
    /// milliseconds old, and the log now shows which of the two we refused.
    /// </summary>
    private static int RadioAgeMs(BluetoothLEAdvertisementReceivedEventArgs args, DateTimeOffset now)
    {
        try
        {
            double age = (now - args.Timestamp).TotalMilliseconds;
            return age < -250 ? -1 : age < 0 ? 0 : age > int.MaxValue ? int.MaxValue : (int)age;
        }
        catch { return -1; }
    }

    /// <summary>Called on wake-up and unlock, right before Windows flushes its queue.</summary>
    public void FlushReplayState()
    {
        lock (_sync)
        {
            _staleStreak = 0;
            _watcherStartedAt = DateTimeOffset.UtcNow;
        }
    }

    private bool PacketAllowed(ParsedAirPodsData data, bool lidBelievable, short rssi)
    {
        string family = AirPodsAdvertisementParser.FamilyForModel(data.Model);
        DeviceStatus status;
        bool familyIsPaired;
        lock (_sync)
        {
            status = _status;
            familyIsPaired = _pairedFamilies.Contains(family)
                || (!_pairingCatalogReady && DeviceCatalog.Key(data) == _rememberedDeviceKey());
        }
        bool hasBatteryData = data.LeftBattery is not null || data.RightBattery is not null || data.CaseBattery is not null;
        // Every rule lives in PacketFilter, which the smoke tests replay against recorded
        // air. Nothing about this decision may be reinvented here.
        return PacketFilter.Allow(lidBelievable, hasBatteryData, rssi, familyIsPaired, status.IsConnected && string.Equals(status.Family, family, StringComparison.OrdinalIgnoreCase), _allowNearby());
    }

    private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        lock (_sync)
        {
            if (_disposed || _standbyPaused || !ReferenceEquals(_watcher, sender)) return;
            _watcher = null;
            Logger.Trace($"radio reason=stopped errorCode={(int)args.Error}");
            sender.Received -= OnReceived;
            sender.Stopped -= OnStopped;
        }
        Publish(CurrentStatus with
        {
            Mode = args.Error == BluetoothError.RadioNotAvailable ? MonitorMode.BluetoothUnavailable : MonitorMode.Error,
            WatcherRunning = false,
            Detail = args.Error == BluetoothError.RadioNotAvailable ? "Bluetooth вимкнений або адаптер недоступний" : $"Bluetooth-сканування зупинилося: {args.Error}"
        });
        ScheduleRestart();
    }

    private void ScheduleRestart()
    {
        lock (_sync)
        {
            if (_disposed || _standbyPaused) return;
            CancellationToken token = _cancellation?.Token ?? CancellationToken.None;
            if (token.IsCancellationRequested || Interlocked.Exchange(ref _restartPending, 1) == 1) return;
            _scheduledRestart = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested) await RestartAsync(resumeFromStandby: false).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Logger.Error("BLE auto-restart failed", ex); }
                finally { Interlocked.Exchange(ref _restartPending, 0); }
            });
        }
    }

    /// <summary>
    /// Sampling for the earbuds' own chatter only: everything for the first ten minutes
    /// after start, then one packet in twenty-five. Case packets ignore this entirely and
    /// are always traced - they are the whole point of the file.
    /// </summary>
    /// <summary>
    /// How long the app may keep hearing AirPods packets that say nothing about the case
    /// before it writes that down. The popup is always a reaction to a case advertisement,
    /// so this is the line between a broken popup and a case that never spoke.
    /// </summary>
    private static readonly TimeSpan CaseSilence = TimeSpan.FromSeconds(60);

    /// <summary>How often the silence is repeated in the log while it lasts.</summary>
    private static readonly TimeSpan CaseSilenceRepeat = TimeSpan.FromMinutes(5);

    /// <summary>Window of the one-line health summary written to the packet trace.</summary>
    private static readonly TimeSpan SummaryWindow = TimeSpan.FromMinutes(1);

    /// <summary>Only the case itself ever sets this bit, so it proves the case transmitted.</summary>
    private const byte ExplicitLidBit = 0b0010_0000;

    /// <summary>
    /// Names the one failure the logs could not explain before 0.8.12. In the capture of
    /// 30 Aug 2026, 16:40-16:49, the app received 520 packets from three addresses, every
    /// single one with the case battery nibble set to F and not one with the explicit lid
    /// bit: the radio worked, the earbuds were heard, and the case itself never transmitted,
    /// so no popup could possibly appear. Earlier the same day, at 12:14, the same build
    /// received 120 explicit case packets and the popup showed four times. Nothing in the
    /// app log distinguished those two situations, which is how a silent case looks exactly
    /// like a broken popup. This method writes the difference down. It only reports what the
    /// radio delivered and never touches the popup path.
    /// </summary>
    private string? NoteCaseTraffic(ParsedAirPodsData data, bool lidBelievable, DateTimeOffset now)
    {
        bool caseSpoke = (data.LidByte & ExplicitLidBit) != 0 || data.CaseBattery is not null;
        if (_lastCaseDataAt == DateTimeOffset.MinValue) _lastCaseDataAt = _createdAt;
        if (_summaryStartedAt == DateTimeOffset.MinValue) _summaryStartedAt = now;

        _summaryPackets++;
        if (caseSpoke) _summaryCaseData++;
        if (lidBelievable) _summaryBelievable++;
        if (now - _summaryStartedAt >= SummaryWindow)
        {
            Logger.Trace($"minute packets={_summaryPackets} caseData={_summaryCaseData} believed={_summaryBelievable} quietMs={(long)(now - _lastCaseDataAt).TotalMilliseconds} lastCaseBattery={_lastCaseBattery}");
            _summaryStartedAt = now;
            _summaryPackets = 0;
            _summaryCaseData = 0;
            _summaryBelievable = 0;
        }

        if (caseSpoke)
        {
            TimeSpan silence = now - _lastCaseDataAt;
            if (silence >= CaseSilence)
            {
                string battery = data.CaseBattery.HasValue ? data.CaseBattery.Value.ToString() + "%" : "unknown";
                Logger.Info($"Case back on air after {silence.TotalSeconds:F0} s of silence ({_packetsSinceCaseData} earbud packets meanwhile, case battery {battery})");
            }
            _lastCaseDataAt = now;
            _caseSilenceLoggedAt = DateTimeOffset.MinValue;
            _packetsSinceCaseData = 0;
            if (data.CaseBattery.HasValue) _lastCaseBattery = data.CaseBattery.Value;
            return null;
        }

        _packetsSinceCaseData++;
        TimeSpan quiet = now - _lastCaseDataAt;
        if (quiet < CaseSilence) return null;
        if (_caseSilenceLoggedAt == DateTimeOffset.MinValue || now - _caseSilenceLoggedAt >= CaseSilenceRepeat)
        {
            _caseSilenceLoggedAt = now;
            string last = _lastCaseBattery >= 0
                ? $"last case battery {_lastCaseBattery}%"
                : "the case has not transmitted once since this session started";
            Logger.Info($"Case silent for {quiet.TotalSeconds:F0} s: {_packetsSinceCaseData} AirPods packets arrived and not one carried a case battery or the explicit lid bit ({last}). The popup only ever follows a case advertisement, so it cannot appear until the case transmits again - charge the case or put the earbuds back into it.");
        }
        return $"Кейс мовчить {FormatQuiet(quiet)} — вікно не має від чого спливти";
    }

    private static string FormatQuiet(TimeSpan quiet)
        => quiet.TotalMinutes >= 1 ? $"{quiet.TotalMinutes:F0} хв" : $"{quiet.TotalSeconds:F0} с";

    private bool ShouldTrace(DateTimeOffset now)
    {
        if (now - _createdAt < TimeSpan.FromMinutes(10)) return true;
        return Interlocked.Increment(ref _traced) % 25 == 0;
    }

    /// <summary>
    /// Microsoft's guidance on BluetoothLEAdvertisementWatcher.Start is explicit: an app
    /// that keeps scanning through Modern Standby can be paused and then handed the whole
    /// queue of advertisements it missed. So the scanner is stopped on the way into
    /// standby instead of filtering the backlog afterwards.
    /// </summary>
    public void PauseForStandby()
    {
        lock (_sync) _standbyPaused = true;
        StopWatcher();
        FlushReplayState();
        Logger.Trace("radio reason=suspend");
        Logger.Info("BLE watcher stopped for standby");
    }

    private bool IsWatcherRunning() { lock (_sync) return _watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started; }
    private void Publish(DeviceStatus status, bool notify = true)
    {
        bool changed;
        lock (_sync) { if (_disposed) return; changed = !Equals(_status, status); _status = status; }
        if (changed && notify) StatusChanged?.Invoke(status);
    }
    private static byte[] ReadBuffer(IBuffer buffer) { using DataReader reader = DataReader.FromBuffer(buffer); var bytes = new byte[checked((int)buffer.Length)]; reader.ReadBytes(bytes); return bytes; }
    private static bool IsSupportedName(string name) => name.Contains("AirPods", StringComparison.OrdinalIgnoreCase) || name.Contains("Beats", StringComparison.OrdinalIgnoreCase) || name.Contains("Powerbeats", StringComparison.OrdinalIgnoreCase);
    private static string FamilyFromName(string name) => name.Contains("AirPods", StringComparison.OrdinalIgnoreCase) ? name.Contains("Max", StringComparison.OrdinalIgnoreCase) ? "AirPods Max" : name.Contains("Pro", StringComparison.OrdinalIgnoreCase) ? "AirPods Pro" : "AirPods" : "Beats";

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            cancellation = _cancellation;
            cancellation?.Cancel();
            StopWatcher();
        }
        // In-flight async operations still own the gates and must be able to Release().
        // SemaphoreSlim has no native wait handle here; let GC reclaim it after those owners finish.
        _ = Task.WhenAll(_loop ?? Task.CompletedTask, _scheduledRestart ?? Task.CompletedTask)
            .ContinueWith(_ => cancellation?.Dispose(), TaskScheduler.Default);
    }
}
