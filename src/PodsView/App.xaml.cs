using System.ComponentModel;
using Microsoft.Win32;
using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace PodsView;

public partial class App : System.Windows.Application
{
    private const string MutexName = "Local\\PodsView.SingleInstance.7C91B2C8";
    private const string ShowEventName = "Local\\PodsView.ShowWindow.7C91B2C8";
    /// <summary>Stamped into the log header so a report always names the build it came from.</summary>
    internal static string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "dev";
    internal static bool IsVerificationRun { get; set; }
    private readonly BatteryMemory _batteryMemory = new();
    private bool _hotkeyUnavailable;
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showRegistration;
    private BluetoothMonitor? _monitor;
    private Forms.NotifyIcon? _tray;
    private string _trayAppearanceKey = "";
    private MainWindow? _window;
    private CasePopupWindow? _casePopup;
    // 0.8.39 radio-gap trace state (logging only).
    private DateTimeOffset _gapLastAt;
    private int _gapLastCycle = -1;
    private bool _gapLastOpen;
    private long _gapLastCallbacks = -1;
    private bool _gapLastActive;
    private DispatcherTimer? _timer;
    private DispatcherTimer? _caseWatchdog;
    private bool _exiting;
    private LidStateMachine _lid = new();
    private DateTimeOffset _popupEpoch;
    private string _lidDeviceKey = "";
    private DateTimeOffset _cycleFirstOpenAt;
    private DateTimeOffset _lastHeartbeatAt;
    private readonly System.Diagnostics.Stopwatch _boot = System.Diagnostics.Stopwatch.StartNew();
    private bool _uiReady;
    private bool _startMinimized;
    private readonly object _packetGate = new();
    private AirPodsPacket? _pendingPacket;
    private DateTimeOffset _lastWatchdogAt;
    private long _watchdogSequence;
    private string _lastWatchdogState = string.Empty; // 0.8.40
    private (int? Battery, bool Connected, bool Live)? _trayState;
    private string _trayText = string.Empty;
    private int _settingsSavePending;
    private int _settingsWriterRunning;
    private readonly HashSet<string> _alerts = new(StringComparer.OrdinalIgnoreCase);
    private HotkeyService? _hotkeys;
    private DispatcherTimer? _manualCardTimer;
    /// <summary>Last packet drawn, so the card can be shown on demand when the case is silent.</summary>
    private ParsedAirPodsData? _lastData;
    private enum BalloonAction { None, ShowWindow, Update }
    private BalloonAction _balloonAction;
    private UpdateInfo? _update;
    private bool _updating;
    private DispatcherTimer? _updateTimer;
    private string _notifiedUpdate = "";

    internal static App CurrentApp => (App)Current;
    internal AppSettings Settings { get; private set; } = new();
    internal DeviceStatus? MonitorStatus => _monitor?.CurrentStatus;

    /// <summary>
    /// 0.8.17: the radio goes first. Measured on the six sessions of 0.8.12-0.8.15, the BLE
    /// watcher only started 602, 1016, 3303, 1135, 633 and 1769 ms after launch, and until it
    /// runs the app is deaf: a case opened in that window is never heard, so right after start-up
    /// the popup came late. Between the header and the
    /// watcher stood the theme, the main window, the tray and the single-instance event -
    /// 408, 833, 2533, 956, 439 and 1216 ms of work no packet needs.
    ///
    /// Now only what the popup itself needs runs before the watcher: settings (they carry the
    /// learned case signature), the theme, the popup window and the 250 ms watchdog. The main
    /// window, the tray and the one-second refresh are built afterwards, at Background
    /// priority, so they can never stand in front of a packet again.
    /// </summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (IsVerificationRun) return; // The UI smoke runner must not touch Bluetooth, user settings or global shortcuts.
        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first) { SignalFirstInstance(); Shutdown(); return; }
        RegisterErrorHandlers();
        _startMinimized = e.Args.Any(arg => arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        Settings = AppSettings.Load();
        _batteryMemory.Seed(Settings.LastCaseBattery, Settings.LastCaseDeviceKey,
            Settings.LastLeftBattery, Settings.LastRightBattery, Settings.LastEarbudsDeviceKey);
        _lastData = _batteryMemory.Data;
        LogSessionHeader();
        // 0.8.39: a tray app that must answer a radio packet within milliseconds is not an
        // "efficiency mode" workload; opt out of EcoQoS and timer coalescing.
        bool throttleOff = PowerThrottling.OptOut();
        Logger.Trace($"sys event=throttle ok={(throttleOff ? 1 : 0)}");
        ThemeManager.Apply(Settings.Theme);
        // The font tokens in App.xaml name families that are optional on Windows 10.
        // This resolves them against the fonts the machine really has and logs the
        // winner, so "the fonts did not change" is answerable from the log.
        FontProbe.Apply(Resources);

        // The popup is the only window a packet needs, so it is ready before the radio is.
        _casePopup = new CasePopupWindow();
        _casePopup.Prepare();
        // The popup already owns a window handle here, so it can carry the global
        // shortcut without a second hidden window.
        _hotkeys = new HotkeyService(_casePopup, () => Dispatcher.InvokeAsync(ShowCardManually));
        _hotkeyUnavailable = !_hotkeys.Rebind(Settings.ShowCardHotkey);
        // Dismissing the popup by hand finishes the open cycle, so no later packet revives it.
        // 0.8.47: only a card the lid machine owns. A card shown by hand (shortcut, tray) has
        // no cycle behind it, and burying one anyway swallowed the next real opening.
        _casePopup.Dismissed += () =>
        {
            bool owned = _lid.IsOpen;
            if (owned) _lid.ForceClosed(DateTimeOffset.UtcNow);
            _cycleFirstOpenAt = default;
            Logger.Info(owned ? "Popup hidden: closed by hand, so this cycle is finished" : "Card closed by hand");
            Logger.Trace("hide reason=user-dismissed");
        };

        _lastWatchdogAt = DateTimeOffset.UtcNow;
        _caseWatchdog = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(250) };
        _caseWatchdog.Tick += (_, _) => WatchdogTick();
        _caseWatchdog.Start();

        DeviceTracker.Trace = message => Logger.Info(message);
        _monitor = new BluetoothMonitor(
            () => Settings.AllowNearbyWhenDisconnected,
            key => Settings.ReadCaseShapes(key),
            (key, state) => { _ = Dispatcher.InvokeAsync(() =>
            {
                if (_exiting) return;
                Settings.RememberShapes(key, state);
                QueueSettingsSave();
            }, DispatcherPriority.Background); },
            () => Settings.LastModelDeviceKey.Length > 0 ? Settings.LastModelDeviceKey : Settings.LastCaseDeviceKey);
        _monitor.StatusChanged += status => Dispatcher.InvokeAsync(() =>
        {
            _window?.ApplyStatus(status);
            UpdateTrayIcon(status.IsConnected, status.LastPacketAt is not null && DateTimeOffset.UtcNow - status.LastPacketAt.Value < TimeSpan.FromSeconds(20));
        }, DispatcherPriority.Background);
        _monitor.PacketReceived += packet =>
        {
            if (!packet.Trusted || !packet.Bound || _exiting) return;
            // Lid state gets the highest UI priority. Heavy main-window and tray work must never delay the popup.
            // 0.8.37: post the lid decision, never block on it. The synchronous
            // Dispatcher.Invoke of the instant-popup patch ran the whole show/hide path on
            // the radio callback thread at Send priority: an 8 Hz burst then held the UI
            // thread above every Normal/Background item, so the main window and the tray
            // (built at Background priority) could not be created at all and the app looked
            // frozen. Send priority already puts this ahead of the queued UI work.
            _ = Dispatcher.InvokeAsync(() => HandleCasePopup(packet), DispatcherPriority.Send);
            // 0.8.17: the main window, the tray icon, the tooltip and the low-battery check no
            // longer run once per packet. The newest packet waits here and the watchdog draws
            // it, so the burst of 27 packets in 3.5 s recorded on 09-01 costs four redraws
            // instead of 27 - and none of them can queue in front of the popup.
            lock (_packetGate) _pendingPacket = packet;
        };

        Logger.Info($"Popup path ready {_boot.ElapsedMilliseconds} ms after launch, starting the BLE watcher now");
        Logger.Trace($"startup phase=popup ms={_boot.ElapsedMilliseconds}");

        // The window is built when the watcher is armed, and after 1500 ms in any case, so a
        // Bluetooth failure can never leave the app without a window.
        var uiFallback = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(1500) };
        uiFallback.Tick += (_, _) => { uiFallback.Stop(); BuildUi(); };
        uiFallback.Start();

        try { await _monitor.StartAsync(); }
        catch (Exception ex)
        {
            Logger.Error("Monitor startup failed", ex);
            BuildUi();
            _window?.ApplyStatus(DeviceStatus.Initial with { Mode = MonitorMode.Error, Detail = "Не вдалося запустити Bluetooth" });
            return;
        }
        Logger.Info($"Initial device discovery finished {_boot.ElapsedMilliseconds} ms after launch; watcher was started earlier");
        Logger.Trace($"startup phase=discovery-finished ms={_boot.ElapsedMilliseconds}");
        _ = Dispatcher.InvokeAsync(BuildUi, DispatcherPriority.Normal);
    }

    /// <summary>
    /// Everything a packet does not need: the main window, the tray, the one-second refresh
    /// and the single-instance event. Runs once, either when the watcher is armed or from the
    /// fallback timer.
    /// </summary>
    private void BuildUi()
    {
        if (_uiReady) return;
        _uiReady = true;

        StartupManager.MigrateLegacyMinimizedArgument();
        Settings.StartWithWindows = StartupManager.IsEnabled();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showRegistration = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.InvokeAsync(ShowMainWindow), null, Timeout.Infinite, false);

        _window = new MainWindow();
        _window.Closing += OnWindowClosing;
        MainWindow = _window;
        CreateTray();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _window?.Tick();
            DeviceStatus? status = _monitor?.CurrentStatus;
            UpdateTrayIcon(status?.IsConnected == true, status?.LastPacketAt is DateTimeOffset at && DateTimeOffset.UtcNow - at < TimeSpan.FromSeconds(20));
            UpdateTrayTooltip();
            TraceHeartbeat();
        };
        _timer.Start();
        StartUpdateChecks();

        if (_monitor is not null) _window.ApplyStatus(_monitor.CurrentStatus);
        if (_batteryMemory.Packet is { } cached) _window.ApplyPacket(cached, _monitor?.CurrentStatus);
        if (_hotkeyUnavailable) _balloonAction = BalloonAction.None;
        if (_hotkeyUnavailable) _tray?.ShowBalloonTip(5000, "DeskPods", Localization.T("hotkeyUnavailable"), Forms.ToolTipIcon.Warning);
        if (!_startMinimized && !Settings.StartMinimized) _window.Show();

        Logger.Info($"Window and tray ready {_boot.ElapsedMilliseconds} ms after launch (start with Windows: {Settings.StartWithWindows})");
        Logger.Trace($"startup phase=ui ms={_boot.ElapsedMilliseconds}");
    }

    /// <summary>
    /// The 250 ms heartbeat. It does three things, in this order: report a late tick, because
    /// a stall on the UI thread is otherwise invisible in a log; draw the newest packet, at
    /// most four times a second; and ask the lid state machine whether the popup has run out
    /// of time.
    /// </summary>
    private void WatchdogTick()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int lateMs = (int)(now - _lastWatchdogAt).TotalMilliseconds;
        _lastWatchdogAt = now;
        long tickId = ++_watchdogSequence;
        // 0.8.40: write the begin/end pair only when the state changed or the tick was late.
        // Four identical pairs a second filled the 6 MB trace in well under an hour.
        bool wdShown = _casePopup?.IsShown == true;
        bool wdTimeoutDue = _lid.ShouldTimeout(now);
        string wdState = $"{wdShown}|{wdTimeoutDue}|{_lid.IsOpen}|{_lid.IsSuspended}";
        bool wdLog = lateMs > 750 || wdState != _lastWatchdogState;
        _lastWatchdogState = wdState;
        if (wdLog) Logger.Trace($"watchdog phase=begin tickId={tickId} ms={lateMs} popup={(_casePopup?.IsShown == true ? 1 : 0)} timeoutDue={(_lid.ShouldTimeout(now) ? 1 : 0)} {_monitor?.DiagnosticCounters} {_lid.Describe(now)}");
        if (lateMs > 750)
        {
            Logger.Trace($"stall ms={lateMs}");
            if (lateMs > 2000)
                Logger.Info($"The app was busy for {lateMs} ms: the 250 ms watchdog could not run, so anything the case said in that time was handled late");
        }

        AirPodsPacket? fresh;
        lock (_packetGate) { fresh = _pendingPacket; _pendingPacket = null; }
        if (fresh is { Bound: true, Trusted: true } packet && packet.ReceivedAt >= _popupEpoch && now - packet.ReceivedAt <= TimeSpan.FromSeconds(3))
        {
            // Usually already applied by the higher-priority lid handler. Applying twice is idempotent.
            _batteryMemory.Apply(packet);
            _lastData = _batteryMemory.Data;
            bool save = Settings.RememberCase(_batteryMemory.LastCase, _batteryMemory.DeviceKey);
            save |= Settings.RememberEarbuds(_batteryMemory.LastLeft, _batteryMemory.LastRight, _batteryMemory.DeviceKey);
            save |= Settings.RememberModel(packet.Data.ModelCode, packet.DeviceKey);
            if (save) QueueSettingsSave();
            if (_batteryMemory.Packet is { } display) _window?.ApplyPacket(display, _monitor?.CurrentStatus);
            UpdateTrayIcon(_monitor?.CurrentStatus.IsConnected == true, true);
            UpdateTrayTooltip();
            CheckLowBattery(packet.Data);
        }

        // The stopwatch for the show latency may not outlive its cycle. Without this, an open
        // packet that never produced a popup poisoned the next reading: the 09-02 log carries
        // latencyMs=285939 and latencyMs=2512680 for popups that in fact appeared instantly.
        if (_cycleFirstOpenAt != default && !_lid.IsOpen && now - _cycleFirstOpenAt > LidStateMachine.StreamTimeout)
            _cycleFirstOpenAt = default;

        CheckCaseStateTimeout();
        if (wdLog) Logger.Trace($"watchdog phase=end tickId={tickId} popup={(_casePopup?.IsShown == true ? 1 : 0)} {_lid.Describe(DateTimeOffset.UtcNow)}");
    }

    private void QueueSettingsSave()
    {
        Interlocked.Exchange(ref _settingsSavePending, 1);
        if (Interlocked.CompareExchange(ref _settingsWriterRunning, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                while (Interlocked.Exchange(ref _settingsSavePending, 0) != 0) Settings.Save();
            }
            finally
            {
                Interlocked.Exchange(ref _settingsWriterRunning, 0);
                if (Volatile.Read(ref _settingsSavePending) != 0) QueueSettingsSave();
            }
        });
    }

    internal async Task RestartScanAsync()
    {
        if (_monitor is null) return;
        try { await _monitor.RestartAsync(); }
        catch (Exception ex)
        {
            Logger.Error("Manual restart failed", ex);
            MessageBox.Show(Localization.T("restartError"), "DeskPods", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal void OpenSettings()
    {
        if (_window is null) return;
        ShowMainWindow();
        var dialog = new SettingsWindow(Settings.Clone()) { Owner = _window };
        if (dialog.ShowDialog() != true) return;
        AppSettings updated = dialog.Result;
        if (updated.StartWithWindows != Settings.StartWithWindows)
        {
            if (!StartupManager.SetEnabled(updated.StartWithWindows))
                MessageBox.Show(Localization.T("startupError", updated.Language), "DeskPods", MessageBoxButton.OK, MessageBoxImage.Warning);
            updated.StartWithWindows = StartupManager.IsEnabled();
        }
        if (_hotkeys is not null && !_hotkeys.Rebind(updated.ShowCardHotkey))
        {
            updated.ShowCardHotkey = Settings.ShowCardHotkey;
            MessageBox.Show(Localization.T("hotkeyConflict", updated.Language), "DeskPods", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Settings.ApplyPreferences(updated);
        if (!Settings.Save()) MessageBox.Show(Localization.T("saveSettingsError"), "DeskPods", MessageBoxButton.OK, MessageBoxImage.Warning);
        _alerts.Clear();
        _window.ApplyLanguage();
        _casePopup?.ApplyLanguage();
        ThemeManager.Apply(Settings.Theme);
        RebuildTrayMenu();
        UpdateTrayTooltip();
    }

    /// <summary>
    /// Shows the card because the user asked (shortcut or tray), not because a packet
    /// arrived. When no fresh case packet is available, this is a way to
    /// see the numbers at that moment. It reads the lid machine but never drives it.
    /// </summary>
    internal void ShowCardManually()
    {
        if (_casePopup is null) return;
        string name = _monitor?.CurrentStatus.DeviceName ?? "AirPods";
        _casePopup.ShowManual(_lastData, name);
        Logger.Info(_lastData is null
            ? "Card shown by hand, but no packet has been seen yet, so the numbers are empty"
            : "Card shown by hand");
        Logger.Trace("show reason=manual");

        if (_manualCardTimer is null)
        {
            _manualCardTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _manualCardTimer.Tick += (_, _) =>
            {
                _manualCardTimer!.Stop();
                // A real open cycle may own the popup by now. That one hides itself on the
                // closed packet, so this timer must keep its hands off it.
                if (_lid.IsOpen) return;
                _casePopup?.HideForSignalTimeout();
                Logger.Trace("hide reason=manual-timeout");
            };
        }
        _manualCardTimer.Stop();
        _manualCardTimer.Start();
    }

    internal void ShowMainWindow()
    {
        if (_window is null) return;
        _window.ShowCompact();
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true; _window.Topmost = false; _window.Focus();
    }

    private void CreateTray()
    {
        _tray = new Forms.NotifyIcon { Visible = true, Text = $"DeskPods · {Localization.T("starting")}", Icon = TrayAppearance.Create(null, false, false) };
        _tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) Dispatcher.InvokeAsync(ShowMainWindow); };
        // The action is read on the click itself: BalloonTipClosed can run before the
        // dispatched handler and would otherwise wipe it.
        _tray.BalloonTipClicked += (_, _) => { BalloonAction action = _balloonAction; _balloonAction = BalloonAction.None; Dispatcher.InvokeAsync(() => OnBalloonClicked(action)); };
        _tray.BalloonTipClosed += (_, _) => _balloonAction = BalloonAction.None;
        RebuildTrayMenu();
    }

    private void RebuildTrayMenu()
    {
        if (_tray is null) return;
        _tray.ContextMenuStrip?.Dispose();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Localization.T("openPodsView"), null, (_, _) => Dispatcher.InvokeAsync(ShowMainWindow));
        menu.Items.Add(Localization.T("refreshScan"), null, async (_, _) => await RestartScanAsync());
        menu.Items.Add(Localization.T("showCard"), null, (_, _) => Dispatcher.InvokeAsync(ShowCardManually));
        menu.Items.Add(new Forms.ToolStripSeparator());
        var startup = new Forms.ToolStripMenuItem(Localization.T("startWindows")) { Checked = Settings.StartWithWindows, CheckOnClick = true };
        startup.Click += (_, _) =>
        {
            bool requested = startup.Checked;
            if (!StartupManager.SetEnabled(requested)) startup.Checked = !requested;
            Settings.StartWithWindows = startup.Checked; Settings.Save();
        };
        menu.Items.Add(startup);
        menu.Items.Add(BuildThemeMenu());
        menu.Items.Add(Localization.T("settings"), null, (_, _) => Dispatcher.InvokeAsync(OpenSettings));
        // 0.8.46: one item for updates - "install vX" once a newer release is known,
        // otherwise a manual check.
        if (_update is { } update)
        {
            var install = new Forms.ToolStripMenuItem(string.Format(Localization.T("updateInstall"), update.Display), null,
                (_, _) => Dispatcher.InvokeAsync(InstallUpdate)) { Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold) };
            menu.Items.Add(install);
        }
        else menu.Items.Add(Localization.T("updateCheck"), null, (_, _) => Dispatcher.InvokeAsync(() => CheckForUpdates(manual: true)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Localization.T("exit"), null, (_, _) => Dispatcher.InvokeAsync(ExitApplication));
        _tray.ContextMenuStrip = menu;
    }

    private Forms.ToolStripMenuItem BuildThemeMenu()
    {
        var root = new Forms.ToolStripMenuItem(Localization.T("designTheme"));
        foreach ((string id, string label) in ThemeManager.Menu)
        {
            string captured = id;
            var item = new Forms.ToolStripMenuItem(label) { Checked = string.Equals(Settings.Theme, id, StringComparison.OrdinalIgnoreCase) };
            item.Click += (_, _) => Dispatcher.InvokeAsync(() => ApplyTheme(captured));
            root.DropDownItems.Add(item);
        }
        return root;
    }

    private void ApplyTheme(string id)
    {
        Settings.Theme = ThemeManager.Normalize(id);
        Settings.Save();
        ThemeManager.Apply(Settings.Theme);
        RebuildTrayMenu();
        Logger.Info($"Design theme switched to {Settings.Theme}");
    }

    /// <summary>
    /// Windows parks a process during Modern Standby and then delivers every
    /// advertisement it buffered meanwhile in one burst. That burst is what kept
    /// resurrecting the popup long after the lid was shut, so on every wake-up
    /// and unlock the lid state is reset, the popup is hidden, the cached
    /// payloads are dropped and the watcher is restarted from scratch.
    /// </summary>
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            _monitor?.PauseForStandby();
            _ = Dispatcher.InvokeAsync(() => { _popupEpoch = DateTimeOffset.UtcNow; _lid.ResetForSystem(_popupEpoch); lock (_packetGate) _pendingPacket = null; _cycleFirstOpenAt = default; _casePopup?.HideForClosedCase(); Logger.Trace("lifecycle reason=suspend"); });
            return;
        }
        if (e.Mode == PowerModes.Resume) HandleSystemResume("power resume");
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        Logger.Trace($"sys event={e.Reason switch { SessionSwitchReason.SessionLock => "lock", SessionSwitchReason.SessionUnlock => "unlock", SessionSwitchReason.SessionLogon => "logon", SessionSwitchReason.ConsoleConnect => "console-connect", SessionSwitchReason.ConsoleDisconnect => "console-disconnect", _ => "session-other" }}");
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon or SessionSwitchReason.ConsoleConnect)
            HandleSystemResume($"session {e.Reason}");
    }

    private void HandleSystemResume(string reason)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (_exiting) return;
            Logger.Info($"System resumed ({reason}); dropping any queued advertisements");
            _popupEpoch = DateTimeOffset.UtcNow; _lid.ResetForSystem(_popupEpoch); lock (_packetGate) _pendingPacket = null;
            _cycleFirstOpenAt = default;
            Logger.Trace("lifecycle reason=resume");
            _casePopup?.HideForClosedCase();
            // 0.8.39: repaint the parked card once the graphics stack has settled.
            _casePopup?.ScheduleRefresh("resume", true, 1500);
            _monitor?.FlushReplayState();
            if (_monitor is null) return;
            try { await _monitor.RestartAsync(); }
            catch (Exception ex) { Logger.Error("Restart after resume failed", ex); }
        });
    }

    /// <summary>
    /// Routes one packet through <see cref="LidStateMachine"/>. All lid debouncing,
    /// stale-packet rejection, and reopen lockouts live in that class so they can
    /// be covered by the smoke tests.
    /// </summary>
    private void HandleCasePopup(AirPodsPacket packet)
    {
        if (_exiting) return;
        if (packet.ReceivedAt < _popupEpoch || DateTimeOffset.UtcNow - packet.ReceivedAt > TimeSpan.FromSeconds(3))
        {
            Logger.Trace($"drop reason=ui-queue-stale seq={packet.Sequence} peer={packet.TracePeer}");
            return;
        }
        // 0.8.38: the battery cache may no longer veto a lid decision. BatteryMemory.Apply
        // returns false for any packet whose ReceivedAt is older than the last one applied,
        // and BLE delivery is not ordered - inside an 8 Hz burst with address rotation an
        // out-of-order packet is normal. Returning here threw that packet away before the
        // lid state machine ever saw it: an out-of-order OPEN meant no popup at all, an
        // out-of-order CLOSE meant the card hung until the silence timeout. That is the
        // "sometimes it works, sometimes it does not" behaviour. The cache still ignores the
        // stale reading; only the lid path is now independent of it.
        _batteryMemory.Apply(packet);
        if (_batteryMemory.Data is null) return;
        if (packet.DeviceKey.Length > 0 && _lidDeviceKey != packet.DeviceKey)
        {
            _lidDeviceKey = packet.DeviceKey;
            _lid = new LidStateMachine();
            _cycleFirstOpenAt = default;
            _casePopup?.HideForClosedCase(packet.Sequence, packet.TracePeer);
        }
        _lastData = _batteryMemory.Data;
        if (packet.ListeningSince is { } since) _lid.BeginListening(since, packet.ReceivedAt);
        // 0.8.7: the popup delay setting is gone. It fed a lockout that stood in front of a
        // genuine opening, and a saved value of 60 was exactly the minute of waiting.
        // How long the packet waited between the radio thread and this handler. The only
        // honest measure of "the app is lagging": everything else in the popup path is
        // measured from here on.
        int queueMs = (int)(DateTimeOffset.UtcNow - packet.ReceivedAt).TotalMilliseconds;
        bool wasOpen = _lid.IsOpen;
        LidAction action = _lid.Handle(packet.LidBelievable, packet.Data.IsCaseOpen, packet.Data.LidOpenCounter, packet.BluetoothAddress, packet.ReceivedAt, packet.Trusted, packet.Bound, packet.ProvenFresh, packet.Data.CarriesLidState);
        // The stopwatch for "how long did the popup take" starts at the first open packet of
        // a cycle, not at the moment the state machine finally believes it.
        // A wake-up counts as the first open packet of its cycle too, so the log reports the
        // latency a human would feel rather than -1.
        if (!wasOpen && packet.Bound && packet.Trusted && packet.LidBelievable && _cycleFirstOpenAt == default
            && (packet.Data.IsCaseOpen || action == LidAction.Open))
            _cycleFirstOpenAt = packet.ReceivedAt;

        Logger.Trace($"case seq={packet.Sequence} peer={packet.TracePeer} believable={(packet.LidBelievable ? 1 : 0)} block={_lid.BlockCode} closeEdge={(_lid.LastObservedClosingEdge ? 1 : 0)} addr={packet.BluetoothAddress:X12} lid=0x{packet.Data.LidByte:X2} open={(packet.Data.IsCaseOpen ? 1 : 0)} cycle={packet.Data.LidOpenCounter} rssi={packet.Rssi} bound={(packet.Bound ? 1 : 0)} trusted={(packet.Trusted ? 1 : 0)} fresh={(packet.ProvenFresh ? 1 : 0)} queueMs={queueMs} action={action} popup={(_casePopup?.IsShown == true ? 1 : 0)} {_lid.Describe(packet.ReceivedAt)} why={(_lid.LastBlockReason.Length == 0 ? "-" : _lid.LastBlockReason)}");

        TraceCaseGap(packet);

        // Only the followed case may move this window. Letting every pair in range do it
        // is what made the popup appear on its own and then lag behind the real lid.
        if (!packet.Bound) return;

        if (packet.LidBelievable && !packet.Data.IsCaseOpen && packet.Trusted && !_lid.HoldsClosedWord(packet.ReceivedAt))
        {
            // Belt and braces: no code path may end with the popup visible while
            // the packet in hand says the lid is shut.
            _casePopup?.HideForClosedCase(packet.Sequence, packet.TracePeer);
        }
        switch (action)
        {
            case LidAction.Open:
                int latencyMs = _cycleFirstOpenAt == default ? -1 : (int)(DateTimeOffset.UtcNow - _cycleFirstOpenAt).TotalMilliseconds;
                var paint = System.Diagnostics.Stopwatch.StartNew();
                _casePopup?.ShowOrUpdate(_lastData!, ResolveDeviceName(packet), newOpenCycle: true, sequence: packet.Sequence, peer: packet.TracePeer);
                paint.Stop();
                if (!packet.Data.IsCaseOpen)
                {
                    int silentMs = _lid.LastSilenceMs;
                    Logger.Info(silentMs < 0
                        ? $"The case spoke for the first time since listening began, with a shut word (lid 0x{packet.Data.LidByte:X2}): a hand is on it, so the popup is shown now"
                        : $"The case broke {silentMs / 1000} s of silence with the state it had before going quiet (lid 0x{packet.Data.LidByte:X2}), so the popup is shown now instead of waiting for the case to say it again");
                }
                Logger.Info($"Popup shown {latencyMs} ms after the first classified open packet (ShowOrUpdate call took {paint.ElapsedMilliseconds} ms)");
                Logger.Trace($"show seq={packet.Sequence} peer={packet.TracePeer} cycle={packet.Data.LidOpenCounter} decisionMs={queueMs} latencyMs={latencyMs} paintMs={paint.ElapsedMilliseconds} queueMs={queueMs}");
                _cycleFirstOpenAt = default;
                break;
            case LidAction.Update:
                // A refresh may never bring a hidden window back. Only LidAction.Open
                // is allowed to put the popup on screen.
                if (_casePopup?.IsShown == true)
                    _casePopup.ShowOrUpdate(_lastData!, ResolveDeviceName(packet), newOpenCycle: false, sequence: packet.Sequence, peer: packet.TracePeer);
                break;
            case LidAction.Close:
                Logger.Info("Popup hidden: the case sent a closed packet");
                Logger.Trace($"hide reason=closed-packet seq={packet.Sequence} peer={packet.TracePeer} cycle={packet.Data.LidOpenCounter}");
                _cycleFirstOpenAt = default;
                _casePopup?.HideForClosedCase(packet.Sequence, packet.TracePeer);
                break;
        }
    }

    /// <summary>
    /// 0.8.39: the radio side of "sometimes it does not pop up". The 2026-09-25 trace had
    /// 6.2 s in which the case said nothing while other devices kept arriving, and the lid
    /// counter jumped by a whole open/close in that time. Two numeric lines make that
    /// visible instead of inferred: "gap phase=case-silence" when an active cycle went
    /// quiet for 1.5 s or more (others = advertisements from anyone else in the gap, so
    /// a deaf scanner and a mute case can be told apart), and "gap phase=missed-open"
    /// when the lid counter (low three bits, +1 per opening) proves openings were never
    /// heard. Logging only; no decision depends on it.
    /// </summary>
    private void TraceCaseGap(AirPodsPacket packet)
    {
        if (!packet.Bound || !packet.LidBelievable || !packet.Data.CarriesLidState) return;
        long callbacks = _monitor?.CallbackCount ?? -1;
        int cycle = packet.Data.LidOpenCounter & 7;
        bool open = packet.Data.IsCaseOpen;
        if (_gapLastCycle >= 0 && _gapLastAt != default)
        {
            int gapMs = (int)(packet.ReceivedAt - _gapLastAt).TotalMilliseconds;
            long others = callbacks >= 0 && _gapLastCallbacks >= 0 ? Math.Max(0, callbacks - _gapLastCallbacks - 1) : -1;
            if (gapMs >= 1500 && gapMs <= 120_000 && _gapLastActive)
                Logger.Trace($"gap phase=case-silence gapMs={gapMs} others={others} cycleFrom={_gapLastCycle} cycleTo={cycle} openFrom={(_gapLastOpen ? 1 : 0)} openTo={(open ? 1 : 0)}");
            if (gapMs >= 0 && gapMs <= 120_000)
            {
                int openings = ((cycle - _gapLastCycle) % 8 + 8) % 8;
                int missed = openings - (open && openings >= 1 ? 1 : 0);
                if (missed > 0)
                {
                    Logger.Trace($"gap phase=missed-open missed={missed} gapMs={gapMs} others={others} cycleFrom={_gapLastCycle} cycleTo={cycle} openFrom={(_gapLastOpen ? 1 : 0)} openTo={(open ? 1 : 0)}");
                    Logger.Info($"The case was opened {missed} time(s) without a single packet reaching this PC ({gapMs} ms of silence from the case, {others} other advertisements heard meanwhile)");
                }
            }
        }
        _gapLastAt = packet.ReceivedAt;
        _gapLastCycle = cycle;
        _gapLastOpen = open;
        _gapLastCallbacks = callbacks;
        _gapLastActive = open || _lid.IsOpen || _casePopup?.IsShown == true;
    }

    /// <summary>
    /// The case bursts when it has news and then goes quiet, so silence is not proof of a
    /// shut lid. Ten seconds of it now only suspends the cycle: the popup leaves the screen,
    /// and the next burst of that same cycle puts it straight back.
    /// </summary>
    private void CheckCaseStateTimeout()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (!_lid.ShouldTimeout(now)) return;
        bool unconfirmed = _lid.WakeUnconfirmed;
        bool saidItsPiece = _lid.SaidItsPiece;
        string state = _lid.Describe(now);
        // An unproven wake-up leaves no cycle behind, or its own suspension would stop the
        // next wake-up from firing.
        if (unconfirmed) _lid.EndUnconfirmedWake(now);
        else _lid.SuspendForSilence(now);
        _cycleFirstOpenAt = default;
        Logger.Info(unconfirmed
            ? $"Popup hidden: the case woke up, never said the lid was open, and has now been quiet for {(int)LidStateMachine.WakeQuiet.TotalMilliseconds} ms"
            : saidItsPiece
                ? $"Popup hidden: the case has been quiet for {(int)LidStateMachine.QuietTail.TotalSeconds}s within the first {(int)LidStateMachine.BurstWindow.TotalSeconds}s of the popup; this cycle is spent, the next opening shows it again"
                : $"Popup hidden: the case has said nothing for {(int)LidStateMachine.StreamTimeout.TotalSeconds}s; this cycle is spent, the next opening shows it again");
        Logger.Trace($"hide reason={(unconfirmed ? "wake-quiet" : saidItsPiece ? "quiet-tail" : "silence")} {state}");
        _casePopup?.HideForSignalTimeout();
    }

    /// <summary>
    /// The first lines of every log: build, machine, and the whole of settings.json. Without
    /// them a log says what happened but never which app it happened in.
    /// </summary>
    private void LogSessionHeader()
    {
        Logger.Info("--------------------------------------------------------------");
        Logger.Info($"DeskPods v{Version} starting on Windows {Environment.OSVersion.Version} ({(Environment.Is64BitProcess ? "x64" : "x86")})");
        Logger.Info($"Settings: language={Settings.Language} theme={Settings.Theme} startup={Settings.StartWithWindows} minimized={Settings.StartMinimized} tray={Settings.MinimizeToTray} lowBattery={Settings.LowBatteryNotifications}@{Settings.LowBatteryThreshold} nearby={Settings.AllowNearbyWhenDisconnected} revision={Settings.SettingsRevision}");
        Logger.Info($"Popup rules: a fresh open word from the followed case shows the popup at once (one without a radio timestamp needs a second); a closed word hides it at once; silence hides it after {(int)LidStateMachine.QuietTail.TotalSeconds}s within the first {(int)LidStateMachine.BurstWindow.TotalSeconds}s, {(int)LidStateMachine.StreamTimeout.TotalSeconds}s after that, and that cycle is then spent (no pop-back); watchdog=250ms");
        Logger.Info($"Wake-up rules: the first case word after {(int)LidStateMachine.WakeSilenceSeconds}s of silence, or the first since listening began, shows the popup whatever it says; its own repeats cannot take it away; it ends {(int)LidStateMachine.WakeQuiet.TotalMilliseconds} ms after the case stops talking unless the case confirms the lid is open; a spent cycle never wakes");
        Logger.Info($"Packet trace: {Logger.TracePath}");
        var parts = (typeof(App).Assembly.GetName().Version ?? new System.Version(0, 0, 0));
        Logger.Trace($"session start v{Version} major={parts.Major} minor={parts.Minor} patch={parts.Build}");
    }

    /// <summary>
    /// 0.8.40: one line every ten seconds while a cycle is alive, so the log shows the countdown to the
    /// silence timeout instead of only the moment it fired.
    /// </summary>
    private void TraceHeartbeat()
    {
        bool visible = _casePopup?.IsShown == true;
        if (!visible && !_lid.IsOpen && !_lid.IsSuspended) return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - _lastHeartbeatAt < TimeSpan.FromSeconds(10)) return;
        _lastHeartbeatAt = now;
        Logger.Trace($"heartbeat popup={(visible ? 1 : 0)} {_lid.Describe(now)}");
    }

    private string ResolveDeviceName(AirPodsPacket packet)
    {
        string? name = _monitor?.CurrentStatus.DeviceName;
        return string.IsNullOrWhiteSpace(name) ? packet.Data.Model : name;
    }

    private void CheckLowBattery(ParsedAirPodsData data)
    {
        if (!Settings.LowBatteryNotifications || _tray is null) return;
        var items = new[]
        {
            (Key: "left", Label: Localization.T("left"), Value: data.LeftBattery, Charging: data.LeftCharging, Cached: data.LeftCached),
            (Key: "right", Label: Localization.T("right"), Value: data.RightBattery, Charging: data.RightCharging, Cached: data.RightCached),
            (Key: "case", Label: Localization.T("case"), Value: data.CaseBattery, Charging: data.CaseCharging, Cached: data.CaseCached)
        };
        foreach (var item in items) if (!item.Cached && item.Value is int value && BatteryFormat.Value(value) > Settings.LowBatteryThreshold + 10) _alerts.Remove(item.Key);
        var low = items.FirstOrDefault(item => BatteryFormat.ShouldAlert(item.Value, item.Charging, item.Cached, Settings.LowBatteryThreshold) && !_alerts.Contains(item.Key));
        if (string.IsNullOrWhiteSpace(low.Key) || low.Value is not int battery) return;
        _alerts.Add(low.Key);
        // 0.8.46: the balloon says what is low, how low, and against which threshold, and a
        // click on it opens the window where that row blinks. Before, "Кейс: 15 %" under a
        // generic title left the user guessing why anything had popped up at all.
        _balloonAction = BalloonAction.ShowWindow;
        _tray.ShowBalloonTip(8000, Localization.T("lowBatteryTitle") + " · " + low.Label,
            string.Format(Localization.T("lowBatteryBody"), low.Label, BatteryFormat.Percent(battery), Settings.LowBatteryThreshold),
            Forms.ToolTipIcon.Warning);
        // 0.8.47: the balloon is the whole alert. 0.8.41 also raised the card by itself for a
        // low earbud, and in the 2026-10-09 trace that was the only popup of the night - at
        // 00:37:40, with the case shut on the desk: a card nobody asked for. The window the
        // balloon opens still blinks the low row.
        Logger.Info($"Low battery alert: {low.Key} {BatteryFormat.Percent(battery)} threshold {Settings.LowBatteryThreshold} %");
    }

    private void OnBalloonClicked(BalloonAction action)
    {
        if (action == BalloonAction.Update) InstallUpdate();
        else if (action == BalloonAction.ShowWindow) ShowMainWindow();
    }

    /// <summary>
    /// 0.8.46: the first check runs half a minute after launch, so it never competes with the
    /// radio start-up, then every six hours for a PC that is never switched off.
    /// </summary>
    private void StartUpdateChecks()
    {
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer!.Interval = TimeSpan.FromHours(6);
            if (Settings.CheckForUpdates) CheckForUpdates(manual: false);
        };
        _updateTimer.Start();
    }

    internal async void CheckForUpdates(bool manual)
    {
        if (_updating || _exiting) return;
        UpdateInfo? found;
        try { found = await UpdateService.CheckAsync(); }
        catch (Exception ex)
        {
            Logger.Info("Update check failed: " + ex.Message);
            if (manual) ShowInfoBalloon(Localization.T("updateCheckFailed"), Forms.ToolTipIcon.Warning);
            return;
        }
        if (_exiting) return;
        _update = found;
        _window?.SetUpdateAvailable(found?.Display);
        RebuildTrayMenu();
        if (found is null)
        {
            Logger.Info($"Update check: v{Version} is the latest release");
            if (manual) ShowInfoBalloon(string.Format(Localization.T("updateLatest"), Version), Forms.ToolTipIcon.Info);
            return;
        }
        Logger.Info($"Update available: v{found.Display} (installer {(found.InstallerUrl is null ? "missing" : "found")})");
        if (!manual && _notifiedUpdate == found.Display) return;
        _notifiedUpdate = found.Display;
        if (_tray is null) return;
        _balloonAction = BalloonAction.Update;
        _tray.ShowBalloonTip(10000, string.Format(Localization.T("updateTitle"), found.Display),
            Localization.T("updateBody"), Forms.ToolTipIcon.Info);
    }

    private void ShowInfoBalloon(string text, Forms.ToolTipIcon icon)
    {
        if (_tray is null) return;
        _balloonAction = BalloonAction.None;
        _tray.ShowBalloonTip(5000, "DeskPods", text, icon);
    }

    /// <summary>Downloads the newer setup, starts it silently and leaves, so it can replace the files.</summary>
    internal async void InstallUpdate()
    {
        if (_update is not { } update || _updating || _exiting) return;
        if (update.InstallerUrl is null || !UpdateService.IsInstalledCopy)
        {
            Logger.Info($"Update v{update.Display}: {(update.InstallerUrl is null ? "no installer in the release" : "portable copy")}, opening the release page");
            UpdateService.OpenReleasePage(update.PageUrl);
            return;
        }
        _updating = true;
        _window?.SetUpdateProgress(0);
        try
        {
            var progress = new Progress<int>(percent => _window?.SetUpdateProgress(percent));
            string setup = await UpdateService.DownloadAsync(update, progress);
            Logger.Info($"Update v{update.Display} downloaded, starting the setup and exiting");
            UpdateService.RunInstaller(setup);
            ExitApplication();
        }
        catch (Exception ex)
        {
            Logger.Error("Update failed", ex);
            _updating = false;
            _window?.SetUpdateAvailable(update.Display);
            MessageBox.Show(Localization.T("updateFailed"), "DeskPods", MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateService.OpenReleasePage(update.PageUrl);
        }
    }

    private void UpdateTrayIcon(bool connected, bool live)
    {
        if (_tray is null) return;
        int? battery = _window?.LowestEarbudBattery();
        // 0.8.17: drawing the icon is GDI work plus a Win32 call, so it only happens when the
        // picture would actually change - not on every packet of a burst.
        string appearanceKey = TrayAppearance.Key;
        if (_trayState is not null && _trayAppearanceKey == appearanceKey) return;
        _trayAppearanceKey = appearanceKey;
        _trayState = (battery, connected, live);
        Icon next = TrayAppearance.Create(battery, connected, live);
        Icon? previous = _tray.Icon; _tray.Icon = next; previous?.Dispose();
    }

    private void UpdateTrayTooltip()
    {
        if (_tray is null) return;
        string text = _window?.BuildTrayText() ?? $"DeskPods · {Localization.T("waitingSignal")}";
        text = text.Length <= 63 ? text : text[..60] + "…";
        if (text == _trayText) return;
        _trayText = text;
        _tray.Text = text;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        if (!Settings.MinimizeToTray) { ExitApplication(); return; }
        _window?.Hide();
        if (!Settings.TrayHintShown && _tray is not null)
        {
            _balloonAction = BalloonAction.None;
            _tray.ShowBalloonTip(3500, Localization.T("backgroundTitle"), Localization.T("backgroundBody"), Forms.ToolTipIcon.Info);
            Settings.TrayHintShown = true; Settings.Save();
        }
    }

    private void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        _manualCardTimer?.Stop();
        _updateTimer?.Stop();
        _hotkeys?.Dispose();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _timer?.Stop();
        _caseWatchdog?.Stop();
        _monitor?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.ContextMenuStrip?.Dispose(); _tray.Dispose(); _tray = null; }
        _casePopup?.CloseForExit(); _casePopup = null;
        if (_window is not null) { _window.Closing -= OnWindowClosing; _window.Close(); }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showRegistration?.Unregister(null); _showEvent?.Dispose(); _mutex?.Dispose(); Logger.Info("DeskPods stopped"); Logger.Shutdown(); base.OnExit(e);
    }

    private void RegisterErrorHandlers()
    {
        DispatcherUnhandledException += (_, args) => { Logger.Error("Unhandled UI exception", args.Exception); args.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => { if (args.ExceptionObject is Exception ex) Logger.Error("Unhandled process exception", ex); };
        TaskScheduler.UnobservedTaskException += (_, args) => { Logger.Error("Unobserved task exception", args.Exception); args.SetObserved(); };
    }

    private static void SignalFirstInstance()
    {
        try { using EventWaitHandle show = EventWaitHandle.OpenExisting(ShowEventName); show.Set(); }
        catch { }
    }
}
