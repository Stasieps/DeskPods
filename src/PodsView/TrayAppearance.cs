using System.Drawing;
using System.IO;
using Microsoft.Win32;

namespace PodsView;

internal static class TrayAppearance
{
    // 0.8.45: the tray has its own monochrome "dp" glyph ("speaker mesh" style).
    // The desktop/app logo (deskpods.ico, DeskPods.Brand.Icon) is unchanged. Because the glyph is
    // monochrome it follows the taskbar tone: white on a dark taskbar, dark on a light taskbar.
    // Battery/connection state never replaces the glyph.
    internal static bool? LightTaskbarForVerification { get; set; }
    private static bool _cachedLight;
    private static long _cachedAt = long.MinValue;
    internal static bool LightTaskbar
    {
        get
        {
            if (LightTaskbarForVerification is bool forced) return forced;
            long now = Environment.TickCount64;
            if (now - _cachedAt > 2000) { _cachedLight = ReadLightTaskbar(); _cachedAt = now; }
            return _cachedLight;
        }
    }
    internal static string Key => LightTaskbar ? "deskpods-tray-dp13-dark" : "deskpods-tray-dp13-light";
    internal static string ResourceName(bool lightTaskbar) => lightTaskbar ? "DeskPods.Tray.Dark" : "DeskPods.Tray.Light";
    internal static Icon Create(int? battery, bool connected, bool live)
    {
        var assembly = typeof(TrayAppearance).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName(LightTaskbar))
            ?? assembly.GetManifestResourceStream("DeskPods.Brand.Icon")
            ?? throw new InvalidOperationException("Missing DeskPods tray icon");
        using var icon = new Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
        return (Icon)icon.Clone();
    }
    private static bool ReadLightTaskbar()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
        }
        catch
        {
            return false;
        }
    }
}
