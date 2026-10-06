using System.IO;
using System.Text.Json;

namespace PodsView;

public sealed class AppSettings
{
    private static readonly object SaveGate = new();
    internal const string DefaultHotkey = "Ctrl+Alt+P";
    internal static readonly string[] ThemeIds = { "refined", "mono", "glass", "swiss", "terminal", "carbon", "neon", "eink" };
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool LowBatteryNotifications { get; set; } = true;
    public int LowBatteryThreshold { get; set; } = 20;
    public bool AllowNearbyWhenDisconnected { get; set; }
    public bool TrayHintShown { get; set; }
    public string Language { get; set; } = "uk";
    public string Theme { get; set; } = "refined";
    public string PreviousTheme { get; set; } = "mono";
    public string ShowCardHotkey { get; set; } = DefaultHotkey;
    public string CaseShapes { get; set; } = "";
    public string CaseShapesDeviceKey { get; set; } = "";
    public int PopupCooldownSeconds { get; set; } = 3; // Legacy field, not a lid rule.
    public int? LastCaseBattery { get; set; }
    public string LastCaseDeviceKey { get; set; } = "";
    // 0.8.46: the last live charge of each earbud. An earbud resting in a shut case does
    // not broadcast, so without this the row printed "-" until the lid was opened again.
    public int? LastLeftBattery { get; set; }
    public int? LastRightBattery { get; set; }
    public string LastEarbudsDeviceKey { get; set; } = "";
    public bool CheckForUpdates { get; set; } = true;
    public ushort LastModelCode { get; set; }
    public string LastModelDeviceKey { get; set; } = "";
    public int SettingsRevision { get; set; } = 9;
    public static string SettingsDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PodsView");
    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public AppSettings Clone()
    {
        lock (SaveGate) return (AppSettings)MemberwiseClone();
    }

    /// <summary>Only user preferences are committed; live telemetry is not part of a dialog transaction.</summary>
    internal void ApplyPreferences(AppSettings p)
    {
        lock (SaveGate)
        {
            StartWithWindows = p.StartWithWindows;
            StartMinimized = p.StartMinimized;
            MinimizeToTray = p.MinimizeToTray;
            LowBatteryNotifications = p.LowBatteryNotifications;
            LowBatteryThreshold = p.LowBatteryThreshold;
            AllowNearbyWhenDisconnected = p.AllowNearbyWhenDisconnected;
            CheckForUpdates = p.CheckForUpdates;
            Language = NormalizeLanguage(p.Language);
            Theme = NormalizeTheme(p.Theme);
            ShowCardHotkey = NormalizeHotkey(p.ShowCardHotkey);
            // CaseShapes, LastCaseBattery, their keys, revision and TrayHintShown stay current.
        }
    }

    internal string ReadCaseShapes(string key)
    {
        lock (SaveGate)
            return CaseShapesDeviceKey.Length == 0 || CaseShapesDeviceKey == key ? CaseShapes : "";
    }
    internal void RememberShapes(string key, string state)
    {
        lock (SaveGate) { CaseShapesDeviceKey = key; CaseShapes = state; }
    }
    internal bool RememberCase(int? value, string key)
    {
        value = ValidBattery(value);
        lock (SaveGate)
        {
            if (LastCaseBattery == value && LastCaseDeviceKey == key) return false;
            LastCaseBattery = value;
            LastCaseDeviceKey = key;
            return true;
        }
    }
    internal bool RememberEarbuds(int? left, int? right, string key)
    {
        left = ValidBattery(left);
        right = ValidBattery(right);
        lock (SaveGate)
        {
            if (LastLeftBattery == left && LastRightBattery == right && LastEarbudsDeviceKey == key) return false;
            LastLeftBattery = left;
            LastRightBattery = right;
            LastEarbudsDeviceKey = key;
            return true;
        }
    }
    internal bool RememberModel(ushort code, string key)
    {
        if (code == 0) return false;
        lock (SaveGate)
        {
            if (LastModelCode == code && LastModelDeviceKey == key) return false;
            LastModelCode = code; LastModelDeviceKey = key; return true;
        }
    }
    internal static int? ValidBattery(int? value) => value is >= 0 and <= 100 && value.Value % 10 == 0 ? value : null;
    internal static string NormalizeLanguage(string? value) => value?.ToLowerInvariant() is "ru" or "en" ? value.ToLowerInvariant() : "uk";
    internal static string NormalizeTheme(string? value) => ThemeIds.FirstOrDefault(t => string.Equals(t, value, StringComparison.OrdinalIgnoreCase)) ?? "refined";
    internal static string NormalizeHotkey(string? value) => value?.Trim() ?? DefaultHotkey;

    public static AppSettings Load(string? path = null)
    {
        try
        {
            path ??= SettingsPath;
            if (!File.Exists(path)) return new();
            AppSettings value = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new();
            value.LowBatteryThreshold = Math.Clamp(((value.LowBatteryThreshold + 2) / 5) * 5, 10, 50);
            value.Language = NormalizeLanguage(value.Language);
            value.Theme = NormalizeTheme(value.Theme);
            if (value.SettingsRevision < 9)
            {
                value.PreviousTheme = value.Theme;
                value.Theme = "refined";
            }
            value.PreviousTheme = NormalizeTheme(value.PreviousTheme);
            value.ShowCardHotkey = NormalizeHotkey(value.ShowCardHotkey);
            value.CaseShapes ??= "";
            value.CaseShapesDeviceKey ??= "";
            value.LastCaseDeviceKey ??= "";
            value.LastModelDeviceKey ??= "";
            value.LastCaseBattery = ValidBattery(value.LastCaseBattery);
            value.LastEarbudsDeviceKey ??= "";
            value.LastLeftBattery = ValidBattery(value.LastLeftBattery);
            value.LastRightBattery = ValidBattery(value.LastRightBattery);
            value.PopupCooldownSeconds = 3;
            value.SettingsRevision = 9;
            return value;
        }
        catch (Exception ex) { Logger.Error("Settings load failed", ex); return new(); }
    }

    /// <summary>Serializes writers and replaces the file only after the new file is complete.</summary>
    public bool Save(string? path = null)
    {
        string? temporary = null;
        lock (SaveGate)
        {
            try
            {
                path = Path.GetFullPath(path ?? SettingsPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                string json = JsonSerializer.Serialize(Clone(), new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(temporary, json);
                File.Move(temporary, path, true);
                return true;
            }
            catch (Exception ex) { Logger.Error("Settings save failed", ex); return false; }
            finally { if (temporary is not null) { try { File.Delete(temporary); } catch { } } }
        }
    }
}
