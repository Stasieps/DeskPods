using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseButtonState = System.Windows.Input.MouseButtonState;
using Brush = System.Windows.Media.Brush;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace PodsView;

/// <summary>
/// One window, three numbers. 0.8.18 removed the detail view together with its
/// telemetry, balance, signal and copy panels: none of them told the user anything
/// the three channels do not, and every one of them was another place a theme
/// could be forgotten.
/// </summary>
public partial class MainWindow : Window
{
    private DeviceStatus _status = DeviceStatus.Initial;
    private int? _left, _right, _case;
    private bool _leftCharging, _rightCharging, _caseCharging;
    private bool _caseIsLastKnown;
    private bool _leftIsLastKnown, _rightIsLastKnown;
    private DateTimeOffset _readingAt;
    private ushort _modelCode;
    private readonly ProductTurntablePlayer _productPlayer;
    internal ProductTurntablePlayer ProductPlayerForVerification => _productPlayer;

    public MainWindow()
    {
        InitializeComponent();
        // The version comes from the built assembly, whose version is set by the project.
        VersionLabel.Text = "v" + App.Version;
        SourceInitialized += (_, _) => WindowAppearance.Apply(this);
        // 0.8.18: the connection dot and the battery fills are painted from code, so
        // without this they kept the colours of the previous theme until the next
        // packet arrived.
        ThemeManager.Changed += RefreshVisuals;
        Closed += (_, _) => ThemeManager.Changed -= RefreshVisuals;
        // 0.8.23: the case broadcasts its own charge only in the packets it sends while
        // the lid is open; with the buds out and the lid shut, that nibble is 15 - "no
        // value" - so the card had nothing to draw and printed a dash. The last number
        // the radio really sent is remembered, restored here and drawn in grey, so the
        // row says "45 % the last time the case spoke" instead of "-".
        if (App.CurrentApp?.Settings.LastCaseBattery is int remembered)
        {
            _case = remembered;
            _caseIsLastKnown = true;
        }
        // 0.8.46: the same for the earbuds. One of them sitting in a shut case says
        // nothing, so the last charge it really reported is shown in grey instead of "-".
        if (App.CurrentApp?.Settings is { } saved &&
            (saved.LastEarbudsDeviceKey.Length == 0 || saved.LastCaseDeviceKey.Length == 0 || saved.LastEarbudsDeviceKey == saved.LastCaseDeviceKey))
        {
            if (saved.LastLeftBattery is int l) { _left = l; _leftIsLastKnown = true; }
            if (saved.LastRightBattery is int r) { _right = r; _rightIsLastKnown = true; }
        }
        Loaded += (_, _) => ApplyLanguage();
        _productPlayer = new ProductTurntablePlayer(this, ProductImage);
        AppearanceContent.SizeChanged += (_, _) => UpdateAppearanceClip();
        ApplyAppearance();
    }

    /// <summary>Resolves a shared design token from App.xaml.</summary>
    private Brush Token(string key) => (Brush)FindResource(key);

    internal void ApplyStatus(DeviceStatus status)
    {
        SetStatus(status);
        RefreshVisuals();
    }

    private void SetStatus(DeviceStatus status)
    {
        _status = status;
        if (!string.IsNullOrWhiteSpace(status.DeviceName))
        {
            _modelCode = status.IdentifiedModelCode;
            CompactDeviceNameText.Text = DeviceCatalog.DisplayName(_modelCode,
                CasePopupWindow.FormatDeviceName(status.DeviceName));
            CompactDeviceNameText.ToolTip = status.MultiplePairedCandidates
                ? Localization.T("deviceAmbiguous") : Localization.T("modelFromSignal");
        }
    }

    internal void ApplyPacket(AirPodsPacket packet, DeviceStatus? status = null)
    {
        if (!packet.Bound || !packet.Trusted) return;
        if (status is not null) SetStatus(status);
        ParsedAirPodsData data = packet.Data;
        _modelCode = data.ModelCode;
        CompactDeviceNameText.Text = DeviceCatalog.DisplayName(data);
        _readingAt = packet.ReceivedAt;
        _left = data.LeftBattery;
        _right = data.RightBattery;
        _case = data.CaseBattery;
        _leftCharging = data.LeftCharging && !data.LeftCached;
        _rightCharging = data.RightCharging && !data.RightCached;
        _caseCharging = data.CaseCharging && !data.CaseCached;
        _leftIsLastKnown = data.LeftCached;
        _rightIsLastKnown = data.RightCached;
        _caseIsLastKnown = data.CaseCached;
        RefreshVisuals();
    }

    internal void Tick() => RefreshVisuals();
    internal void ShowCompact() { }
    internal string BuildTrayText() => $"DeskPods · L {FormatValue(_left)} · R {FormatValue(_right)} · C {FormatValue(_case)}";
    internal int? LowestEarbudBattery() => _left is int l && _right is int r ? Math.Min(l, r) : _left ?? _right;
    private static string FormatValue(int? value) => value is int battery ? BatteryFormat.Percent(battery) : "-";

    internal void ApplyLanguage()
    {
        RefreshVisuals();
        ShellSettingsButton.ToolTip = Localization.T("settings") + " (S)";
        ShellCloseButton.ToolTip = Localization.T("close");
        System.Windows.Automation.AutomationProperties.SetName(ShellSettingsButton, Localization.T("settings"));
        System.Windows.Automation.AutomationProperties.SetName(ShellCloseButton, Localization.T("close"));
        CompactLeftLabel.ToolTip = Localization.T("left");
        CompactRightLabel.ToolTip = Localization.T("right");
        CompactCaseLabel.ToolTip = Localization.T("case");
        if (_pendingUpdate is not null) SetUpdateAvailable(_pendingUpdate);
    }

    /// <summary>
    /// 0.8.22: three numbers and nothing around them. The captions over the bars and
    /// the freshness line in the corner were both unnecessary, and
    /// the code behind them - the tracking helper, the case staleness clock and the
    /// packet clock - left with them.
    /// </summary>
    private void RefreshVisuals()
    {
        ApplyAppearance();
        UpdateConnection();
        bool old = _readingAt == default || DateTimeOffset.UtcNow - _readingAt > TimeSpan.FromSeconds(20)
            || _status.Mode is MonitorMode.Disconnected or MonitorMode.BluetoothUnavailable or MonitorMode.Error;
        UpdateBattery(_left, _leftCharging, CompactLeftValueText, CompactLeftScale, CompactLeftFill, old || _leftIsLastKnown, CompactLeftLabel, "left");
        UpdateBattery(_right, _rightCharging, CompactRightValueText, CompactRightScale, CompactRightFill, old || _rightIsLastKnown, CompactRightLabel, "right");
        UpdateBattery(_case, _caseCharging, CompactCaseValueText, CompactCaseScale, CompactCaseFill, old || _caseIsLastKnown, CompactCaseLabel, "case");
    }

    private void UpdateConnection()
    {
        string text;
        Brush accent;
        switch (_status.Mode)
        {
            case MonitorMode.Connected: text = Localization.T("connected"); accent = Token("LiveBrush"); break;
            case MonitorMode.Scanning: text = _status.IsPaired ? Localization.T("waitingSignal") : Localization.T("searching"); accent = Token("InfoBrush"); break;
            case MonitorMode.Disconnected: text = Localization.T("disconnected"); accent = Token("WarnBrush"); break;
            case MonitorMode.BluetoothUnavailable: text = Localization.T("bluetoothUnavailable"); accent = Token("AccentBrush"); break;
            case MonitorMode.Error: text = Localization.T("checkNeeded"); accent = Token("AccentBrush"); break;
            default: text = Localization.T("starting"); accent = Token("InfoBrush"); break;
        }
        CompactConnectionText.Text = ThemeManager.IsRefined ? text : text.ToUpperInvariant();
        CompactConnectionDot.Background = accent;
    }

    /// <summary>
    /// Paints one battery channel. Healthy values stay white, so the single red
    /// accent always means "needs attention". The number itself is formatted by
    /// <see cref="BatteryFormat"/>, which never invents a digit the radio did not send.
    /// </summary>
    private void UpdateBattery(int? battery, bool charging, TextBlock value, ScaleTransform scale, Border fill, bool lastKnown = false,
        TextBlock? tag = null, string channel = "")
    {
        if (battery is not int level)
        {
            LowBatteryPulse.Set(false, value, fill, tag);
            if (tag is not null) tag.ClearValue(TextBlock.ForegroundProperty);
            value.ToolTip = null;
            Brush muted = Token("MutedTextBrush");
            value.Text = "-";
            value.Foreground = muted;
            fill.Background = muted;
            scale.ScaleX = 0;
            value.FontSize = (double)FindResource("FontValue");
            return;
        }

        // A remembered value keeps its digits but not its colour: grey means "this is
        // the last thing the case said", and only a live reading is allowed to look live.
        // 0.8.40: a live low reading is red, by the same rule as the tray alert.
        int lowThreshold = App.CurrentApp?.Settings.LowBatteryThreshold ?? 20;
        bool low = BatteryFormat.ShouldAlert(level, charging, lastKnown, lowThreshold);
        Brush accent = lastKnown ? Token("SecondaryTextBrush") : low ? Token("AccentBrush") : charging ? Token("LiveBrush") : Token("OkBrush");
        string text = BatteryFormat.Percent(level);
        value.Text = text;
        value.Foreground = accent;
        // "100 %" is one glyph longer than "25 %", and only that one steps down a token
        // instead of running into the track.
        value.FontSize = (double)FindResource(text.Length > 5 ? "FontTitle" : "FontValue");
        fill.Background = accent;
        scale.ScaleX = Math.Clamp(BatteryFormat.Bar(level), 0, 1);
        // 0.8.46: the low channel blinks and its letter turns red, with the reason on hover,
        // so a low-battery alert always points at the earbud or the case that raised it.
        LowBatteryPulse.Set(low, value, fill, tag);
        if (tag is not null)
        {
            if (low) tag.Foreground = Token("AccentBrush");
            else tag.ClearValue(TextBlock.ForegroundProperty);
        }
        value.ToolTip = low
            ? string.Format(Localization.T("lowBatteryReason"), Localization.T(channel.Length > 0 ? channel : "battery"), text, lowThreshold)
            : lastKnown ? Localization.T("lastKnown") : null;
    }

    /// <summary>
    /// 0.8.23: no button any more. Restarting the scan cannot make a silent case talk,
    /// so the button promised something the radio cannot deliver; the key stays for the
    /// rare case where the Bluetooth stack itself stops delivering advertisements.
    /// </summary>
    private async void RestartScan()
    {
        try { await App.CurrentApp.RestartScanAsync(); }
        catch (Exception error) { Logger.Error("Manual rescan failed", error); }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => App.CurrentApp.OpenSettings();

    private string? _pendingUpdate;

    /// <summary>0.8.46: null puts the plain version back; a version turns the pill into a button.</summary>
    internal void SetUpdateAvailable(string? version)
    {
        _pendingUpdate = version;
        if (version is null)
        {
            VersionLabel.Text = "v" + App.Version;
            VersionLabel.ClearValue(TextBlock.ForegroundProperty);
            VersionChip.ClearValue(Border.BorderBrushProperty);
            VersionChip.Cursor = null;
            VersionChip.ToolTip = null;
            return;
        }
        VersionLabel.Text = $"v{App.Version}  ·  ⬆ " + string.Format(Localization.T("updateButton"), version);
        VersionLabel.Foreground = Token("LiveBrush");
        VersionChip.BorderBrush = Token("LiveBrush");
        VersionChip.Cursor = System.Windows.Input.Cursors.Hand;
        VersionChip.ToolTip = Localization.T("updateHint");
    }

    internal void SetUpdateProgress(int percent)
    {
        VersionLabel.Text = $"v{App.Version}  ·  " + string.Format(Localization.T("updateDownloading"), Math.Clamp(percent, 0, 100));
        VersionChip.Cursor = null;
    }

    private void VersionChip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_pendingUpdate is null) return; // a plain version pill still drags the window
        e.Handled = true;
        App.CurrentApp.InstallUpdate();
    }

    /// <summary>The frameless shell has no title bar, so the whole card drags.</summary>
    private void Shell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); }
        catch (InvalidOperationException) { /* the button was already released */ }
    }

    private void ShellClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.R) { RestartScan(); e.Handled = true; }
        else if (e.Key == Key.S) { App.CurrentApp.OpenSettings(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }
}
