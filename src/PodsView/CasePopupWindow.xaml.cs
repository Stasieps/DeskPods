using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
// UseWindowsForms pulls in a global "using System.Drawing", so these WPF types
// must be pinned explicitly or they collide with their WinForms namesakes.
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace PodsView;

public partial class CasePopupWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    // 0.8.39 presentation constants (see CHANGELOG.md, 0.8.39).
    private const int FirstVerifyMs = 150;
    private const int LateVerifyMs = 1000;
    private const int MaxRepairs = 2;
    /// <summary>Mean per-channel difference (0-255) between the screen under the card and
    /// the card WPF rendered, above which the card is treated as not really on screen.</summary>
    private const int PixelTolerance = 28;
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HardKeepAliveInterval = TimeSpan.FromMinutes(60);

    private int? _left;
    private int? _right;
    private int? _lastKnownCase;
    private bool _caseFreshForCurrentCycle;
    private bool _leftCharging;
    private bool _rightCharging;
    private bool _caseCharging;
    private bool _leftCached, _rightCached, _manual;
    // 0.8.46: which channel raised a low-battery card ("left", "right", "case"), or null.
    private string? _alertKey;
    private bool _caseLow;
    private bool _caseOpenConfirmed;

    private bool _dismissedForCurrentOpenCycle;
    private readonly PopupPresentationProbe _presentation;
    private long _diagnosticSequence;
    private int _diagnosticPeer;

    // ---- 0.8.39 presentation state -------------------------------------------------
    // The card is no longer created on screen at the moment a packet arrives. It is shown
    // once at startup, parked far outside every monitor, and kept rendered there. "Show"
    // moves an already painted window into the corner and raises it; "hide" parks it
    // again. A first paint after hours of idle GPU/display power-down is therefore never
    // on the critical path, which is what made the first opening after a long idle show
    // nothing although WPF and IsWindowVisible both said "visible".
    private IntPtr _hwnd;
    private bool _shown;
    private bool _parkedMode = true;
    private long _presentOp;
    private int _repairs;
    /// <summary>The pixel comparison is the only verification signal that can be wrong on
    /// a healthy card (odd themes, HDR, exotic colour pipelines, a window animating over
    /// the card). If it keeps failing on presentations that pass every other check, it is
    /// switched off for the session instead of flickering the card forever.</summary>
    private bool _pixelRepairEnabled = true;
    private int _pixelFutile;
    private readonly System.Diagnostics.Stopwatch _showClock = new();
    private long _faultsAtShow = -1;
    private EventHandler? _firstFrame;
    private DateTimeOffset _lastHiddenAt;
    private DateTimeOffset _lastHardRefreshAt = DateTimeOffset.UtcNow;
    private DispatcherTimer? _keepAlive;
    private IntPtr _displayNotification;
    private bool _closing;

    internal int NativeVisibilityForVerification => _presentation.NativeVisibility;
    /// <summary>True while the card is meant to be on screen. <see cref="UIElement.IsVisible"/>
    /// is no longer that signal: the parked card is a visible window outside every monitor.</summary>
    internal bool IsShown => _shown;
    internal bool ParkedForVerification => !_shown && (!_parkedMode ? !IsVisible : Math.Abs(Top - ParkTop()) < 1 || App.IsVerificationRun);
    internal bool ParkedModeForVerification => _parkedMode;

    /// <summary>Raised when the user closes the popup by hand.</summary>
    internal event Action? Dismissed;

    public CasePopupWindow()
    {
        InitializeComponent();
        _presentation = new PopupPresentationProbe(this, () => _shown, () => _parkedMode && !_shown);
        _lastKnownCase = App.CurrentApp.Settings.LastCaseBattery;
        SourceInitialized += (_, _) => { ApplyNoActivateStyle(); OnSourceReady(); };
        PopupContent.SizeChanged += (_, _) =>
        {
            if (PopupContent.ActualWidth <= 0 || PopupContent.ActualHeight <= 0) return;
            var clip = new RectangleGeometry(new Rect(0, 0, PopupContent.ActualWidth, PopupContent.ActualHeight), 7, 7);
            clip.Freeze(); PopupContent.Clip = clip;
        };
        // 0.8.18: the card, its rim and this glow used to be literals in the XAML, so a
        // theme switch repainted the dots and left the card black. Everything comes from
        // the palette now, and this repaints the brushes that are set from code.
        ThemeManager.Changed += OnThemeChanged;
        RenderCapability.TierChanged += OnRenderTierChanged;
    }

    private void OnThemeChanged()
    {
        ApplyGlow();
        Repaint();
    }

    /// <summary>The halo around the live dot follows the palette instead of staying green.</summary>
    private void ApplyGlow()
    {
        if (Token("PopupGlowBrush") is SolidColorBrush glow) PopupLiveGlow.Color = glow.Color;
    }

    /// <summary>
    /// 0.8.39: the card renders in software. It is 272x120; the CPU cost is nothing, and a
    /// software-rendered window does not depend on a Direct3D device that the display
    /// driver may have lost while the monitor slept. The window hook listens for the
    /// display and composition events that historically break a WPF window's surface.
    /// </summary>
    private void OnSourceReady()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        PopupNative.ApplyCardCorners(_hwnd);
        string mode = "hw";
        try
        {
            if (HwndSource.FromHwnd(_hwnd) is HwndSource source)
            {
                source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
                source.AddHook(WndProc);
                mode = source.CompositionTarget.RenderMode == RenderMode.SoftwareOnly ? "sw" : "hw";
            }
        }
        catch (Exception ex) { Logger.Debug("Popup render mode could not be set: " + ex.Message); }
        try
        {
            Guid display = ConsoleDisplayState;
            _displayNotification = RegisterPowerSettingNotification(_hwnd, ref display, 0);
        }
        catch { _displayNotification = IntPtr.Zero; }
        Logger.Trace($"sys event=popup-source sw={(mode == "sw" ? 1 : 0)} tier={RenderCapability.Tier >> 16} displayNotify={(_displayNotification != IntPtr.Zero ? 1 : 0)} dpi={PopupNative.Dpi(_hwnd)}");
    }

    internal void Prepare()
    {
        Opacity = 1;
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        ApplyNoActivateStyle();
        ApplyLanguage();
        ApplyGlow();
        Repaint();
        // Show once, outside every monitor, so the window exists, is laid out and has a
        // painted surface long before the first packet needs it.
        try
        {
            Park();
            Show();
            PopupNative.RaiseTopmost(_hwnd);
            UpdateLayout();
            Logger.Trace($"present phase=park reason=prepare path=parked shown=0 parked=1");
        }
        catch (Exception ex)
        {
            _parkedMode = false;
            try { if (IsVisible) Hide(); } catch { }
            Logger.Error("Parked popup could not be prepared; falling back to show/hide", ex);
            Logger.Trace("present phase=park reason=prepare path=classic shown=0 parked=0");
        }
        _keepAlive = new DispatcherTimer(DispatcherPriority.Normal) { Interval = KeepAliveInterval };
        _keepAlive.Tick += (_, _) => KeepAlive();
        _keepAlive.Start();
    }

    /// <summary>Refreshes the localized strings after the language setting changes.</summary>
    internal void ApplyLanguage()
    {
        PopupOpenText.Text = _alertKey is not null
            ? Localization.T("lowBatteryHeader") + " · " + Localization.T(_alertKey)
            : Localization.T(_manual ? "lastKnown" : _caseOpenConfirmed ? "caseOpen" : "caseActivity");
        if (_alertKey is not null) PopupOpenText.Foreground = Token("AccentBrush");
        else PopupOpenText.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        PopupStatusDot.Visibility = _manual ? Visibility.Collapsed : Visibility.Visible;
        PopupCloseButton.ToolTip = Localization.T("close");
        System.Windows.Automation.AutomationProperties.SetName(PopupCloseButton, Localization.T("close"));
        PopupLeftLabelText.ToolTip = Localization.T("left");
        PopupRightLabelText.ToolTip = Localization.T("right");
        PopupCaseLabelText.ToolTip = Localization.T("case");
    }

    internal void ShowOrUpdate(ParsedAirPodsData data, string deviceName, bool newOpenCycle, long sequence=0, int peer=0)
    {
        _diagnosticSequence = sequence; _diagnosticPeer = peer;
        if (newOpenCycle)
        {
            _dismissedForCurrentOpenCycle = false;
            _left = null;
            _right = null;
            _caseFreshForCurrentCycle = false;
        }

        _manual = false;
        _alertKey = null;
        _caseOpenConfirmed = data.IsCaseOpen;
        PopupDeviceNameText.Text = DeviceCatalog.DisplayName(data);
        PopupDeviceNameText.ToolTip = PopupDeviceNameText.Text;
        ApplyLanguage();
        _left = data.LeftBattery;
        _right = data.RightBattery;
        _leftCached = data.LeftCached;
        _rightCached = data.RightCached;
        _caseFreshForCurrentCycle = data.CaseBattery is not null && !data.CaseCached;
        _lastKnownCase = data.CaseBattery;
        if (data.CaseBattery is int caseValue)
        {
            _lastKnownCase = caseValue;
            _caseFreshForCurrentCycle = !data.CaseCached;
        }

        _leftCharging = data.LeftCharging;
        _rightCharging = data.RightCharging;
        _caseCharging = data.CaseCharging;

        Repaint();

        if (_dismissedForCurrentOpenCycle) return;
        Present("automatic");
    }

    /// <summary>Repaints the three numbers from the values already held.</summary>
    private void Repaint()
    {
        int threshold = App.CurrentApp?.Settings.LowBatteryThreshold ?? 20;
        bool alerts = App.CurrentApp?.Settings.LowBatteryNotifications != false;
        bool Low(string key, int? value, bool stale, bool charging) =>
            _alertKey == key || (alerts && BatteryFormat.ShouldAlert(value, charging, stale, threshold));
        bool leftStale = _manual || _leftCached, rightStale = _manual || _rightCached, caseStale = !_caseFreshForCurrentCycle;
        SetBattery(PopupLeftValueText, PopupLeftScale, PopupLeftFill, _left, stale: leftStale, charging: _leftCharging,
            low: Low("left", _left, leftStale, _leftCharging), label: PopupLeftLabelText);
        SetBattery(PopupRightValueText, PopupRightScale, PopupRightFill, _right, stale: rightStale, charging: _rightCharging,
            low: Low("right", _right, rightStale, _rightCharging), label: PopupRightLabelText);
        _caseLow = _lastKnownCase is not null && Low("case", _lastKnownCase, caseStale, _caseCharging);
        SetBattery(PopupCaseValueText, PopupCaseScale, PopupCaseFill, _lastKnownCase, stale: caseStale, charging: _caseCharging,
            low: _caseLow, label: PopupCaseLabelText);
        UpdateCaseFreshness();
    }

    /// <summary>
    /// Shows the card because the user asked for it (hotkey or tray), with the last
    /// numbers the radio gave. In the available captures, 0 of 28 052
    /// recorded packets carried a case battery with both earbuds out - so on demand is
    /// the only way to see it then. This never touches the lid machine or the
    /// dismissed flag of a real open cycle; the caller hides it again.
    /// </summary>
    internal void ShowManual(ParsedAirPodsData? data, string deviceName, string? alertKey = null)
    {
        _diagnosticSequence = 0; _diagnosticPeer = 0;
        _manual = true;
        _alertKey = alertKey;
        _left = data?.LeftBattery;
        _right = data?.RightBattery;
        _lastKnownCase = data is null ? App.CurrentApp.Settings.LastCaseBattery : data.CaseBattery;
        _leftCharging = _rightCharging = _caseCharging = false;

        // A card opened by hand never claims the case number is live.
        _caseFreshForCurrentCycle = false;
        PopupDeviceNameText.Text = data is null ? FormatDeviceName(deviceName) : DeviceCatalog.DisplayName(data);
        PopupDeviceNameText.ToolTip = PopupDeviceNameText.Text;
        ApplyLanguage();
        // ApplyLanguage already uses the manual, non-live state.
        ApplyGlow();
        Repaint();
        Present("manual");
    }

    internal void HideForClosedCase(long sequence=0, int peer=0) => HideImmediately("closed-case", sequence, peer);

    internal void HideForSignalTimeout() => HideImmediately("signal-timeout");

    internal void CloseForExit()
    {
        _closing = true;
        _presentation.Begin("exit");
        _presentOp++;
        StopFirstFrameWatch();
        _keepAlive?.Stop();
        ThemeManager.Changed -= OnThemeChanged;
        RenderCapability.TierChanged -= OnRenderTierChanged;
        try { if (_displayNotification != IntPtr.Zero) UnregisterPowerSettingNotification(_displayNotification); } catch { }
        _displayNotification = IntPtr.Zero;
        _shown = false;
        Close();
        _presentation.Complete("hide-return");
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Dismissed?.Invoke();
        _dismissedForCurrentOpenCycle = true;
        HideImmediately("user-dismissed");
    }

    /// <summary>
    /// The case battery is only broadcast on some packets, so the label states
    /// whether the number is live or the last value seen in this open cycle.
    /// </summary>
    private void UpdateCaseFreshness()
    {
        if (_caseFreshForCurrentCycle)
        {
            PopupCaseLabelText.Text = "C";
            PopupCaseLabelText.Foreground = Token(_caseLow ? "AccentBrush" : "SecondaryTextBrush");
        }
        else if (_lastKnownCase is not null)
        {
            PopupCaseLabelText.Text = "C LAST";
            PopupCaseLabelText.Foreground = Token(_caseLow ? "AccentBrush" : "SecondaryTextBrush");
        }
        else
        {
            PopupCaseLabelText.Text = "C";
            PopupCaseLabelText.Foreground = Token(_caseLow ? "AccentBrush" : "SecondaryTextBrush");
        }
    }

    // ---------------------------------------------------------------------------------
    // 0.8.39 presentation: park / present / verify / repair / refresh
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Puts the card on screen. Already shown: only re-assert position and z-order.
    /// Otherwise the parked, already painted window moves into the corner and is raised
    /// to the top of the topmost band, and a verification is scheduled that looks at the
    /// real screen and repairs the presentation if the card is not there.
    /// </summary>
    private void Present(string reason)
    {
        if (_closing) return;
        if (_shown)
        {
            PositionInCorner();
            PopupNative.RaiseTopmost(_hwnd);
            return;
        }

        long op = ++_presentOp;
        _repairs = 0;
        _presentation.Begin(reason, _diagnosticSequence, _diagnosticPeer);
        var (faults, workingSetMb) = PopupNative.Memory();
        _faultsAtShow = faults;
        int idleMin = _lastHiddenAt == default ? -1 : (int)(DateTimeOffset.UtcNow - _lastHiddenAt).TotalMinutes;
        _showClock.Restart();
        _shown = true;
        Opacity = 1;
        if (_parkedMode)
        {
            try
            {
                if (!IsVisible) Show();
                PositionInCorner();
            }
            catch (Exception ex)
            {
                Logger.Error("Parked popup could not be presented; switching to show/hide", ex);
                _parkedMode = false;
            }
        }
        if (!_parkedMode)
        {
            PositionInCorner();
            if (!IsVisible) Show();
        }
        bool raised = PopupNative.RaiseTopmost(_hwnd);
        Topmost = true;
        PopupFrame.InvalidateVisual();
        long moveMs = _showClock.ElapsedMilliseconds;
        _presentation.Complete("show-return");
        Logger.Trace($"present phase=show op={op} reason={reason} path={(_parkedMode ? "parked" : "classic")} moveMs={moveMs} raised={(raised ? 1 : 0)} idleMin={idleMin} wsMb={workingSetMb} dpi={PopupNative.Dpi(_hwnd)}");
        WatchFirstFrame(op);
        var scheduled = System.Diagnostics.Stopwatch.StartNew();
        _ = Dispatcher.InvokeAsync(() => Logger.Trace($"render scheduledMs={scheduled.ElapsedMilliseconds} popup={(_shown ? 1 : 0)}"), DispatcherPriority.Loaded);
        ScheduleVerify(op, FirstVerifyMs, late: false);
    }

    private void HideImmediately(string reason, long sequence=0, int peer=0)
    {
        bool wasShown = _shown;
        // A closed packet arrives up to eight times a second while the card is already
        // parked. Those calls change nothing and must not flood the trace or move a window.
        if (!wasShown && (_parkedMode ? IsParkedNow() : !IsVisible)) return;
        _presentation.Begin(reason, sequence, peer);
        _shown = false;
        _presentOp++; // Cancels any pending verification of the previous presentation.
        StopFirstFrameWatch();
        if (_parkedMode)
        {
            try { Park(); }
            catch { if (IsVisible) Hide(); }
        }
        else if (IsVisible) Hide();
        if (wasShown)
        {
            _lastHiddenAt = DateTimeOffset.UtcNow;
            Logger.Trace($"present phase=hide reason={reason} path={(_parkedMode ? "parked" : "classic")} shownMs={_showClock.ElapsedMilliseconds}");
        }
        _presentation.Complete("hide-return");
    }

    /// <summary>First real frame after the show, and how many page faults it cost.</summary>
    private void WatchFirstFrame(long op)
    {
        StopFirstFrameWatch();
        _firstFrame = (_, _) =>
        {
            if (op != _presentOp) { StopFirstFrameWatch(); return; }
            long ms = _showClock.ElapsedMilliseconds;
            var (faults, _) = PopupNative.Memory();
            long delta = faults >= 0 && _faultsAtShow >= 0 ? faults - _faultsAtShow : -1;
            StopFirstFrameWatch();
            Logger.Trace($"present phase=frame op={op} frameMs={ms} faults={delta}");
        };
        CompositionTarget.Rendering += _firstFrame;
    }

    private void StopFirstFrameWatch()
    {
        if (_firstFrame is null) return;
        CompositionTarget.Rendering -= _firstFrame;
        _firstFrame = null;
    }

    private void ScheduleVerify(long op, int delayMs, bool late)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(delayMs) };
        timer.Tick += (_, _) => { timer.Stop(); Verify(op, late); };
        timer.Start();
    }

    /// <summary>
    /// Looks at what the user can actually see. The card counts as present when DWM does
    /// not cloak it, it lies on a monitor, at least three of five points inside it resolve
    /// to the card itself, and the screen pixels there match the card WPF rendered. When
    /// that fails the presentation is rebuilt - the "close and open again" the user had to
    /// do by hand, done by the app within a fraction of a second - at most twice.
    /// </summary>
    private void Verify(long op, bool late)
    {
        if (_closing || op != _presentOp || !_shown) return;
        if (_firstFrame is not null && _showClock.ElapsedMilliseconds >= LateVerifyMs)
        {
            StopFirstFrameWatch();
            Logger.Trace($"present phase=frame op={op} frameMs=-1 faults=-1");
        }
        if (App.IsVerificationRun) return; // Off-screen by design in the UI smoke run.
        int cloaked = PopupNative.Cloaked(_hwnd);
        int topmost = PopupNative.Topmost(_hwnd);
        int hit = PopupNative.HitCount(_hwnd);
        int onScreen = PopupNative.OnScreen(_hwnd);
        int diff = PopupNative.PixelDiff(this, _hwnd);
        string why = cloaked == 1 ? "cloaked"
            : onScreen == 0 ? "offscreen"
            : hit >= 0 && hit < 3 ? "covered"
            : _pixelRepairEnabled && diff > PixelTolerance ? "pixels"
            : "-";
        bool ok = why == "-";
        Logger.Trace($"present phase=verify op={op} at={_showClock.ElapsedMilliseconds} attempt={_repairs} ok={(ok ? 1 : 0)} why={why} cloaked={cloaked} topmost={topmost} hit={hit} onScreen={onScreen} pixelDiff={diff} pixelCheck={(_pixelRepairEnabled ? 1 : 0)} sw={(IsSoftware() ? 1 : 0)} tier={RenderCapability.Tier >> 16}");
        if (!ok && _repairs < MaxRepairs)
        {
            _repairs++;
            Repair(op, why);
            ScheduleVerify(op, FirstVerifyMs, late);
            return;
        }
        if (!ok)
        {
            Logger.Info($"Popup verification still fails after {_repairs} repairs ({why}); the card may not be visible");
            // Every structural check passed and only the pixels disagree, repeatedly. The
            // card is almost certainly fine and the comparison is not usable here.
            if (why == "pixels" && ++_pixelFutile >= 3)
            {
                _pixelRepairEnabled = false;
                Logger.Trace($"sys event=pixel-check-advisory state=off why=futile pixelDiff={diff}");
                Logger.Info("The screen-pixel check disagreed with every other visibility check three times; it is now only logged, not acted on");
            }
        }
        else if (late) _pixelFutile = 0;
        if (!late) ScheduleVerify(op, Math.Max(50, LateVerifyMs - (int)_showClock.ElapsedMilliseconds), late: true);
    }

    private void Repair(long op, string why)
    {
        // Attempt 1 is deliberately gentle: re-assert z-order and repaint without touching
        // the window's visibility, so a card the user *is* looking at never flickers. A
        // cloaked window cannot be fixed that way, and a second failure has earned the
        // heavier path, so both re-create the visibility of the parked/shown window.
        bool hard = _repairs >= 2 || why == "cloaked" || why == "offscreen";
        Logger.Trace($"present phase=retry op={op} attempt={_repairs} mode={(hard ? "hard" : "soft")} why={why}");
        Logger.Info($"Popup was not really on screen ({why}); {(hard ? "rebuilding its presentation" : "re-asserting it")}, attempt {_repairs}");
        try
        {
            if (hard)
            {
                if (IsVisible) Hide();
                Show();
            }
            PositionInCorner();
            PopupNative.RaiseTopmost(_hwnd);
            Topmost = true;
            PopupFrame.InvalidateVisual();
            UpdateLayout();
        }
        catch (Exception ex) { Logger.Error("Popup repair failed", ex); }
    }

    /// <summary>
    /// Proactive repaint of the parked card after the events that break window surfaces:
    /// display power-on, unlock, resume, DWM restarts, display changes and render-tier
    /// changes. "hard" re-creates the window's visibility (Hide/Show while parked),
    /// "soft" only invalidates. A shown card is re-verified instead.
    /// </summary>
    internal void Refresh(string reason, bool hard)
    {
        if (_closing) return;
        Logger.Trace($"present phase=refresh reason={reason} mode={(hard ? "hard" : "soft")} shown={(_shown ? 1 : 0)} path={(_parkedMode ? "parked" : "classic")}");
        if (_shown)
        {
            PositionInCorner();
            PopupNative.RaiseTopmost(_hwnd);
            _repairs = 0;
            ScheduleVerify(_presentOp, 1, late: true);
            return;
        }
        if (!_parkedMode) return;
        try
        {
            if (hard)
            {
                _lastHardRefreshAt = DateTimeOffset.UtcNow;
                if (IsVisible) Hide();
                Park();
                Show();
                PopupNative.RaiseTopmost(_hwnd);
            }
            else Park();
            PopupFrame.InvalidateVisual();
            UpdateLayout();
        }
        catch (Exception ex) { Logger.Error("Popup refresh failed", ex); }
    }

    internal void ScheduleRefresh(string reason, bool hard, int delayMs)
    {
        if (_closing) return;
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(Math.Max(1, delayMs)) };
        timer.Tick += (_, _) => { timer.Stop(); Refresh(reason, hard); };
        timer.Start();
    }

    /// <summary>Keeps the parked surface and the render path warm while the case sleeps.</summary>
    private void KeepAlive()
    {
        if (_shown || !_parkedMode || _closing) return;
        bool hard = DateTimeOffset.UtcNow - _lastHardRefreshAt >= HardKeepAliveInterval;
        Refresh("keepalive", hard);
    }

    private bool IsSoftware()
    {
        try { return HwndSource.FromHwnd(_hwnd)?.CompositionTarget?.RenderMode == RenderMode.SoftwareOnly; }
        catch { return false; }
    }

    private void OnRenderTierChanged(object? sender, EventArgs e)
    {
        Logger.Trace($"sys event=render-tier tier={RenderCapability.Tier >> 16}");
        ScheduleRefresh("render-tier", true, 500);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        try
        {
            switch (msg)
            {
                case WmPowerBroadcast when wParam.ToInt64() == PbtPowerSettingChange && lParam != IntPtr.Zero:
                    Guid setting = Marshal.PtrToStructure<Guid>(lParam);
                    if (setting == ConsoleDisplayState)
                    {
                        int state = Marshal.ReadInt32(lParam, 20);
                        Logger.Trace($"sys event=display state={(state == 0 ? "off" : state == 1 ? "on" : state == 2 ? "dim" : "unknown")}");
                        if (state == 1)
                        {
                            // The graphics stack is not always ready at the notification
                            // itself (Microsoft KB for layered WPF windows after resume),
                            // so repaint once shortly after and once more a little later.
                            ScheduleRefresh("display-on", true, 1500);
                            ScheduleRefresh("display-on", false, 5000);
                        }
                    }
                    break;
                case WmDisplayChange:
                    Logger.Trace("sys event=display-change");
                    ScheduleRefresh("display-change", true, 800);
                    break;
                case WmDwmCompositionChanged:
                    Logger.Trace("sys event=dwm");
                    ScheduleRefresh("dwm", true, 800);
                    break;
                case WmDpiChanged:
                    Logger.Trace($"sys event=dpi dpi={(int)(wParam.ToInt64() & 0xFFFF)}");
                    break;
                case WmSettingChange when wParam.ToInt64() == SpiSetWorkArea:
                    Logger.Trace("sys event=work-area");
                    if (_shown) PositionInCorner(); else if (_parkedMode) Park();
                    break;
            }
        }
        catch { }
        return IntPtr.Zero;
    }

    private bool IsParkedNow() => IsVisible && Math.Abs(Top - ParkTop()) < 1;

    /// <summary>Far above the whole virtual screen, in the column of the corner position.</summary>
    private double ParkTop() => App.IsVerificationRun ? -10000 : SystemParameters.VirtualScreenTop - Height - 4000;

    private void Park()
    {
        if (App.IsVerificationRun) { Left = -10000; Top = -10000; return; }
        Rect work = SystemParameters.WorkArea;
        Left = Math.Max(work.Left, work.Right - Width - 18);
        Top = ParkTop();
    }

    private void PositionInCorner()
    {
        if (App.IsVerificationRun) { Left = -10000; Top = -10000; return; }
        Rect work = SystemParameters.WorkArea;
        double left = Math.Max(work.Left, work.Right - Width - 18);
        double top = work.Top + 18;
        if (Math.Abs(Left - left) >= 0.5) Left = left;
        if (Math.Abs(Top - top) >= 0.5) Top = top;
    }

    internal static string FormatDeviceName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "AirPods Pro";
        string clean = value.Trim().Replace('_', ' ');
        if (clean.Contains("AirPods Pro", StringComparison.OrdinalIgnoreCase)) clean = "AirPods Pro";
        else if (clean.Contains("AirPods Max", StringComparison.OrdinalIgnoreCase)) clean = "AirPods Max";
        else if (clean.Contains("AirPods", StringComparison.OrdinalIgnoreCase)) clean = "AirPods";
        // Mixed case on purpose: the product name is the one warm, human line in
        // an otherwise monospaced readout.
        return clean;
    }

    private void SetBattery(TextBlock text, ScaleTransform scale, Border fill, int? value, bool stale, bool charging = false,
        bool low = false, TextBlock? label = null)
    {
        if (value is null) low = false;
        // 0.8.46: the channel that is low blinks in red, its letter included, so the card
        // never pops up without saying why.
        LowBatteryPulse.Set(low, text, fill, label);
        if (label is not null)
        {
            if (low) label.Foreground = Token("AccentBrush");
            else label.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        }
        if (value is not int battery)
        {
            Brush muted = Token("SecondaryTextBrush");
            text.Text = "—";
            text.Foreground = muted;
            fill.Background = muted;
            scale.ScaleX = 0;
            text.FontSize = (double)FindResource("FontValue");
            return;
        }

        Brush accent = low
            ? Token("AccentBrush")
            : stale
            ? Token("SecondaryTextBrush")
            : charging
                ? Token("LiveBrush")
                : Token("OkBrush");

        // The radio sends one nibble per element, so the number is a step of ten either
        // way; BatteryFormat only decides whether to print the step or the range it means.
        text.Text = BatteryFormat.Percent(battery);
        text.Foreground = accent;
        // "80–89%" is three glyphs longer than "80%", so it steps one token down instead
        // of colliding with the neighbouring column.
        text.FontSize = (double)FindResource(text.Text.Length > 4 ? "FontTitle" : "FontValue");
        fill.Background = accent;
        scale.ScaleX = Math.Clamp(BatteryFormat.Bar(battery), 0, 1);
    }

    /// <summary>Resolves a shared design token from App.xaml.</summary>
    private Brush Token(string key) => (Brush)FindResource(key);

    private void ApplyNoActivateStyle()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(handle, GwlExStyle);
        _ = SetWindowLong(handle, GwlExStyle, style | WsExToolWindow | WsExNoActivate);
    }

    private const int WmPowerBroadcast = 0x0218;
    private const int PbtPowerSettingChange = 0x8013;
    private const int WmDisplayChange = 0x007E;
    private const int WmDwmCompositionChanged = 0x031E;
    private const int WmDpiChanged = 0x02E0;
    private const int WmSettingChange = 0x001A;
    private const int SpiSetWorkArea = 0x002F;
    private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
