using System.Drawing;
using System.IO;
using Microsoft.Win32;

namespace PodsView;

internal static class TrayAppearance
{
    // 0.8.49: the tray shows AirPods Pro as a flat one-colour glyph in the pixel style of the 0.8.45
    // "dp" glyph - white on a dark taskbar, near-black (26,26,26) on a light one, like the other icons
    // by the clock; the same shape for both, only the colour flips. The real outline is traced from the
    // user's photo (12.10.2026): the left earbud stem up with its tip to the left, the right one stem
    // down with its tip to the right. The user chose preview v4b #6: ear-tip seams at least 1 px wide,
    // pixel grille cut-outs at 16-24 px, traced grille cut-outs and a stem-end ring on larger frames.
    // The desktop/app logo (deskpods.ico, DeskPods.Brand.Icon) is
    // unchanged, and charge or connection never replaces the glyph (the tooltip has the charge).
    // The .ico files live in Assets/Tray as base64 text (*.ico.b64): the tools that commit to this
    // repository write text files only. Frames: 16, 20, 24, 28, 32, 36, 40, 48, 56 and 64 px.
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
    internal static string Key => LightTaskbar ? "deskpods-tray-airpods-light-taskbar" : "deskpods-tray-airpods-dark-taskbar";
    internal static string ResourceName(bool lightTaskbar) => lightTaskbar ? "DeskPods.Tray.LightTaskbar" : "DeskPods.Tray.DarkTaskbar";
    /// <summary>The tray .ico for a light or dark taskbar, decoded from its base64 resource.</summary>
    internal static Stream? OpenIcon(bool lightTaskbar)
    {
        using Stream? text = typeof(TrayAppearance).Assembly.GetManifestResourceStream(ResourceName(lightTaskbar));
        if (text is null) return null;
        using var reader = new StreamReader(text);
        return new MemoryStream(Convert.FromBase64String(reader.ReadToEnd()), writable: false);
    }
    internal static Icon Create(int? battery, bool connected, bool live)
    {
        var assembly = typeof(TrayAppearance).Assembly;
        using Stream stream = OpenIcon(LightTaskbar)
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
