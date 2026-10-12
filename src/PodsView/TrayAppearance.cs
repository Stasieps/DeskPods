using System.Drawing;
using System.IO;
using Microsoft.Win32;

namespace PodsView;

internal static class TrayAppearance
{
    // 0.8.49: the tray shows AirPods Pro as a clean product-style picture - glossy white shells,
    // white silicone ear tips, black grilles, a silver stem end, like new earbuds in a store photo,
    // not a photo of worn ones - laid out as in the user's photo (12.10.2026): the left earbud stem
    // up with its tip to the left, the right one stem down with its tip to the right (the layout
    // the 0.8.45 "dp" glyph abstracted). It is a picture, not a monochrome glyph, so it does not
    // flip colour with the taskbar: on a dark taskbar it is drawn as is (a dark rim keeps the two
    // earbuds apart at 16 px), on a light taskbar the same earbuds come slightly greyer with a thin
    // grey outline so white does not vanish into white. The desktop/app logo (deskpods.ico,
    // DeskPods.Brand.Icon) is unchanged, and charge or connection never replaces the picture (the
    // tooltip has the charge).
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
