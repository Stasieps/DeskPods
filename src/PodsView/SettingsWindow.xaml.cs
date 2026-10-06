using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace PodsView;

public partial class SettingsWindow : Window
{
    private sealed record LanguageOption(string Code, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record ThemeOption(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly string _themeOnEntry;
    private bool _saved;
    // Both fields are read by the selection handlers, and WPF raises SelectionChanged
    // while this constructor is still filling the combo boxes. A field that is null
    // there is a NullReferenceException here, which the dispatcher swallows and the
    // window silently never opens. Initialise at the declaration, not later.
    private string _hotkey = string.Empty;
    private string _language = "uk";
    // Set last. Until the constructor finishes, a selection event is not a user choice.
    private bool _ready;

    public AppSettings Result { get; }

    internal SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Result = settings;
        SourceInitialized += (_, _) => WindowAppearance.Apply(this);
        Height = Math.Min(700, Math.Max(400, SystemParameters.WorkArea.Height - 48));
        ThemeManager.Changed += ApplyWindowTheme;
        Closed += (_, _) => ThemeManager.Changed -= ApplyWindowTheme;

        LanguageComboBox.ItemsSource = new[]
        {
            new LanguageOption("uk", "Українська"),
            new LanguageOption("ru", "Русский"),
            new LanguageOption("en", "English")
        };
        _language = Localization.NormalizeLanguage(settings.Language);
        LanguageComboBox.SelectedItem = LanguageComboBox.Items.OfType<LanguageOption>().First(option => option.Code == _language);

        _themeOnEntry = ThemeManager.Normalize(settings.Theme);
        ThemeComboBox.ItemsSource = ThemeManager.Menu.Select(theme => new ThemeOption(theme.Id, theme.Label)).ToArray();
        ThemeComboBox.SelectedItem = ThemeComboBox.Items.OfType<ThemeOption>().FirstOrDefault(option => option.Id == _themeOnEntry);
        // Leaving without saving must not keep a previewed theme.
        Closing += (_, _) => { if (!_saved) ThemeManager.Apply(_themeOnEntry); };

        // 0.8.22: the battery format dropdown is gone, on request. The radio sends one
        // nibble per element, so a step of 2 means "somewhere in 20-29"; the app prints
        // the middle of that step - 25 %, the number MagicPods shows, wrong by at most
        // five. The only alternative was the raw step, and two ways of printing one
        // nibble was a decision nobody needed to make.

        _hotkey = HotkeyService.Normalize(settings.ShowCardHotkey);
        HotkeyBox.Text = _hotkey.Length == 0 ? Localization.T("hotkeyOff", _language) : _hotkey;

        // Five-point steps, because the alert fires on a value the radio can actually report.
        ThresholdComboBox.ItemsSource = new[] { 10, 15, 20, 25, 30, 35, 40, 45, 50 };
        StartWithWindowsCheckBox.IsChecked = settings.StartWithWindows;
        StartMinimizedCheckBox.IsChecked = settings.StartMinimized;
        MinimizeToTrayCheckBox.IsChecked = settings.MinimizeToTray;
        NotificationsCheckBox.IsChecked = settings.LowBatteryNotifications;
        ThresholdComboBox.SelectedItem = settings.LowBatteryThreshold;
        NearbyCheckBox.IsChecked = settings.AllowNearbyWhenDisconnected;
        UpdatesCheckBox.IsChecked = settings.CheckForUpdates;
        ApplyLanguage(_language);
        RefreshEnabledStates();
        _ready = true;
    }

    private void ApplyLanguage(string language)
    {
        _language = language;
        Title = Localization.T("settingsTitle", language) + " · DeskPods";
        TitleText.Text = Localization.T("settingsTitle", language);
        AboutSectionText.Text = Localization.T("about", language);
        ushort model = App.CurrentApp.MonitorStatus?.IdentifiedModelCode ?? 0;
        if (model == 0) model = App.CurrentApp.Settings.LastModelCode;
        AboutModelText.Text = model == 0 ? "DeskPods " + App.Version : DeviceCatalog.DisplayName(model) + " · DeskPods " + App.Version;
        AboutHintText.Text = Localization.T("localPrivacy", language);
        SupportButtonText.Text = Localization.T("supportDeveloper", language);
        SupportButton.IsEnabled = SupportLink.Read() is not null;
        SupportButton.ToolTip = SupportButton.IsEnabled ? "Ko-fi" : Localization.T("supportNotConfigured", language);
        LanguageSectionText.Text = Localization.T("language", language);
        LanguageLabelText.Text = Localization.T("languageLabel", language);
        LanguageHintText.Text = Localization.T("languageHint", language);
        ThemeLabelText.Text = Localization.T("themeLabel", language);
        ThemeHintText.Text = Localization.T("themeHint", language);
        CardSectionText.Text = Localization.T("card", language);
        HotkeyLabelText.Text = Localization.T("hotkeyLabel", language);
        HotkeyHintText.Text = Localization.T("hotkeyHint", language);
        if (_hotkey.Length == 0) HotkeyBox.Text = Localization.T("hotkeyOff", language);
        BehaviorSectionText.Text = Localization.T("behavior", language);
        StartWithWindowsCheckBox.Content = Localization.T("startWindows", language);
        StartWithWindowsHintText.Text = Localization.T("startWindowsHint", language);
        StartMinimizedCheckBox.Content = Localization.T("startTray", language);
        StartMinimizedHintText.Text = Localization.T("startTrayHint", language);
        MinimizeToTrayCheckBox.Content = Localization.T("closeTray", language);
        MinimizeToTrayHintText.Text = Localization.T("closeTrayHint", language);
        NotificationsSectionText.Text = Localization.T("notifications", language);
        NotificationsCheckBox.Content = Localization.T("lowBattery", language);
        NotificationsHintText.Text = Localization.T("lowBatteryHint", language);
        NearbyCheckBox.Content = Localization.T("nearby", language);
        NearbyHintText.Text = Localization.T("nearbyHint", language);
        UpdatesCheckBox.Content = Localization.T("updatesSetting", language);
        UpdatesHintText.Text = Localization.T("updatesSettingHint", language);
        ExportButtonText.Text = Localization.T("exportLogs", language);
        CancelButtonText.Text = Localization.T("cancel", language);
        SaveButtonText.Text = Localization.T("save", language);
    }

    /// <summary>Applies the theme straight away so the choice can be judged, not imagined.</summary>
    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (ThemeComboBox?.SelectedItem is ThemeOption option && TitleText is not null)
            ThemeManager.Apply(option.Id);
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (LanguageComboBox.SelectedItem is LanguageOption option && TitleText is not null)
            ApplyLanguage(option.Code);
    }

    /// <summary>
    /// Records a combination instead of typing its name. Modifiers alone are ignored,
    /// Esc switches the shortcut off, and the field is read-only so nothing else can
    /// land in it.
    /// </summary>
    private void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            _hotkey = string.Empty;
            HotkeyBox.Text = Localization.T("hotkeyOff", _language);
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System) return;

        var parts = new System.Collections.Generic.List<string>(4);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        // Windows only hands a global hotkey to an app when at least one modifier is
        // held, so a bare letter is refused here instead of failing silently later.
        if (parts.Count == 0) return;
        parts.Add(key.ToString());
        _hotkey = string.Join("+", parts);
        HotkeyBox.Text = _hotkey;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result.Language = LanguageComboBox.SelectedItem is LanguageOption option ? option.Code : "uk";
        Result.Theme = ThemeComboBox?.SelectedItem is ThemeOption theme ? theme.Id : _themeOnEntry;
        _saved = true;
        Result.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        Result.StartMinimized = StartMinimizedCheckBox.IsChecked == true;
        Result.MinimizeToTray = MinimizeToTrayCheckBox.IsChecked == true;
        Result.LowBatteryNotifications = NotificationsCheckBox.IsChecked == true;
        Result.LowBatteryThreshold = ThresholdComboBox.SelectedItem is int threshold ? threshold : 20;
        Result.AllowNearbyWhenDisconnected = NearbyCheckBox.IsChecked == true;
        Result.CheckForUpdates = UpdatesCheckBox.IsChecked == true;
        Result.ShowCardHotkey = _hotkey;
        DialogResult = true;
    }

    private void ApplyWindowTheme() => WindowAppearance.Apply(this);

    private void Support_Click(object sender, RoutedEventArgs e)
    {
        string? url = SupportLink.Read();
        if (url is null) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            Logger.Error("Could not open support link", ex);
            System.Windows.MessageBox.Show(Localization.T("supportOpenError", _language), "DeskPods");
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void StartupCheckChanged(object sender, RoutedEventArgs e) => RefreshEnabledStates();

    private void RefreshEnabledStates()
    {
        if (StartMinimizedCheckBox is not null)
            StartMinimizedCheckBox.IsEnabled = true; // Applies to manual launch as well as startup.
    }

    /// <summary>
    /// 0.8.18: one button instead of two. "Open the log" and "Diagnostics" did the same
    /// job from different ends, so this packs both logs, the previous trace and
    /// settings.json into a single zip on the desktop and selects it in Explorer.
    /// </summary>
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button) return;
        button.IsEnabled = false;
        try
        {
            string archive = await System.Threading.Tasks.Task.Run(Logger.CollectDiagnostics);
            // Quoting by hand: "/select," needs the path in real quotes or Explorer opens Documents.
            string argument = "/select," + '"' + archive + '"';
            Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error("Could not collect diagnostics", ex);
            try
            {
                Directory.CreateDirectory(Logger.LogDirectory);
                Process.Start(new ProcessStartInfo(Logger.LogDirectory) { UseShellExecute = true });
            }
            catch (Exception fallback) { Logger.Error("Could not open log directory either", fallback); }
            System.Windows.MessageBox.Show(Localization.T("collectLogsFail", _language), "DeskPods", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        finally { button.IsEnabled = true; }
    }
}
