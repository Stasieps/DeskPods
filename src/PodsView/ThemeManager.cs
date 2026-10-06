using System.Linq;

namespace PodsView;

/// <summary>
/// Eight interchangeable looks. A theme swaps the palette, the accent and the dot
/// pattern only: the measured geometry stays untouched, so no layout can break.
///
/// 0.8.18 adds two things. The popup card, its rim and its glow are palette entries
/// now - they used to be literals inside CasePopupWindow.xaml, so a theme switch
/// repainted the dots while the card stayed black (and old dots stayed on that
/// window). And <see cref="Changed"/> lets an open window repaint the
/// brushes it sets from code instead of waiting for the next packet.
/// </summary>
internal static class ThemeManager
{
    internal sealed class Palette
    {
        public string Id = "mono";
        public string Label = "MONO+";
        public string Canvas = "#050506";
        public string Surface = "#08090A";
        public string Raised = "#101113";
        public string Elevated = "#17191C";
        public string Hairline = "#25272B";
        public string Border = "#34363B";
        public string TagBorder = "#3B3D42";
        public string PrimaryText = "#F5F5F3";
        public string SecondaryText = "#9A9CA2";
        public string MutedText = "#5F6269";
        public string Accent = "#FF5147";
        public string Warn = "#F0B84C";
        public string Ok = "#F5F5F3";
        public string Info = "#9A9CA2";
        public string Live = "#37F712";
        /// <summary>Popup card fill. Eight hex digits: the first pair is opacity.</summary>
        public string PopupCanvas = "#FA07080A";
        /// <summary>Hairline rim around the popup card.</summary>
        public string PopupRim = "#45FFFFFF";
        /// <summary>Halo behind the "case open" dot.</summary>
        public string PopupGlow = "#37F712";
        public string? Dot = "#828892";
        public double DotTile = 5d;
        public double DotRadius = 0.62d;
    }

    /// <summary>Raised after every palette swap, on the UI thread.</summary>
    internal static event Action? Changed;
    internal static string CurrentId { get; private set; } = "mono";
    internal static bool IsRefined => CurrentId == "refined";

    internal static readonly Palette[] All =
    {
        new Palette
        {
            Id = "refined", Label = "Refined + Case",
            Canvas = "#090A0B", Surface = "#111315", Raised = "#17191C", Elevated = "#202328",
            Hairline = "#24272B", Border = "#555B64", TagBorder = "#555B64",
            PrimaryText = "#F4F5F7", SecondaryText = "#AFB5BE", MutedText = "#9098A3",
            Accent = "#F37A73", Warn = "#E3B86F", Ok = "#F4F5F7", Info = "#AFB5BE", Live = "#79C99E",
            PopupCanvas = "#FF090A0B", PopupRim = "#24272B", PopupGlow = "#79C99E",
            Dot = null,
        },
        new Palette { Id = "mono", Label = "Classic - MONO+" },
        new Palette
        {
            Id = "glass", Label = "2  GLASS",
            Canvas = "#0C1018", Surface = "#121620", Raised = "#1A2030", Elevated = "#223047",
            Hairline = "#2A3247", Border = "#3A4664", TagBorder = "#3A4664",
            PrimaryText = "#F2F6FF", SecondaryText = "#A8B4CC", MutedText = "#6C7A93",
            Accent = "#5B8CFF", Warn = "#FFC46B", Ok = "#7DD3FC", Info = "#A8B4CC", Live = "#38D6C4",
            PopupCanvas = "#F00E1420", PopupRim = "#4A9FB4D8", PopupGlow = "#38D6C4",
            Dot = null,
        },
        new Palette
        {
            Id = "swiss", Label = "3  SWISS  (light)",
            Canvas = "#F2F0EC", Surface = "#FFFFFF", Raised = "#EAE7E1", Elevated = "#DFDBD3",
            Hairline = "#C9C4BA", Border = "#111111", TagBorder = "#111111",
            PrimaryText = "#111111", SecondaryText = "#4A4A46", MutedText = "#7A7A74",
            Accent = "#E8380D", Warn = "#B26A00", Ok = "#111111", Info = "#4A4A46", Live = "#1B7F2E",
            PopupCanvas = "#FAFFFFFF", PopupRim = "#66111111", PopupGlow = "#1B7F2E",
            Dot = null,
        },
        new Palette
        {
            Id = "terminal", Label = "4  TERMINAL",
            Canvas = "#05080A", Surface = "#070C0E", Raised = "#0B1417", Elevated = "#102020",
            Hairline = "#153A2A", Border = "#1D5A3C", TagBorder = "#1D5A3C",
            PrimaryText = "#35FF6A", SecondaryText = "#1FBF52", MutedText = "#147A38",
            Accent = "#35FF6A", Warn = "#FFD447", Ok = "#35FF6A", Info = "#1FBF52", Live = "#8CFFB0",
            PopupCanvas = "#FA050B0A", PopupRim = "#5A1FBF52", PopupGlow = "#8CFFB0",
            Dot = "#123D24", DotTile = 3d, DotRadius = 0.5d,
        },
        new Palette
        {
            Id = "carbon", Label = "5  CARBON",
            Canvas = "#16181A", Surface = "#1B1E20", Raised = "#212528", Elevated = "#2A2F33",
            Hairline = "#33383D", Border = "#43494F", TagBorder = "#43494F",
            PrimaryText = "#F0F1F2", SecondaryText = "#A9AFB5", MutedText = "#71787E",
            Accent = "#FF7A1A", Warn = "#FFC24D", Ok = "#F0F1F2", Info = "#A9AFB5", Live = "#FF9A45",
            PopupCanvas = "#FA1A1D1F", PopupRim = "#4AFFFFFF", PopupGlow = "#FF9A45",
            Dot = "#3A4046", DotTile = 4d, DotRadius = 0.7d,
        },
        new Palette
        {
            Id = "neon", Label = "6  NEON",
            Canvas = "#0A0620", Surface = "#120A2E", Raised = "#1B0F42", Elevated = "#251657",
            Hairline = "#33206E", Border = "#4A2E9A", TagBorder = "#4A2E9A",
            PrimaryText = "#F2ECFF", SecondaryText = "#B9A6FF", MutedText = "#7E6BC4",
            Accent = "#FF4FD8", Warn = "#FFD166", Ok = "#7AF5FF", Info = "#B9A6FF", Live = "#4ADE80",
            PopupCanvas = "#F41A0F3E", PopupRim = "#59FF4FD8", PopupGlow = "#FF4FD8",
            Dot = "#4A2E9A", DotTile = 6d, DotRadius = 0.9d,
        },
        new Palette
        {
            Id = "eink", Label = "7  E-INK  (paper)",
            Canvas = "#EEE9DD", Surface = "#F6F2E9", Raised = "#E4DECF", Elevated = "#D9D2C1",
            Hairline = "#C3BBA8", Border = "#23211C", TagBorder = "#23211C",
            PrimaryText = "#23211C", SecondaryText = "#55503F", MutedText = "#857F6C",
            Accent = "#8A2B12", Warn = "#8A6A12", Ok = "#23211C", Info = "#55503F", Live = "#2E6B34",
            PopupCanvas = "#FAF6F2E9", PopupRim = "#6623211C", PopupGlow = "#2E6B34",
            Dot = "#B9B09C", DotTile = 5d, DotRadius = 0.62d,
        },
    };

    internal static IEnumerable<(string Id, string Label)> Menu => All.Select(item => (item.Id, item.Label));

    internal static string Normalize(string? id)
    {
        Palette? match = All.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        return match?.Id ?? "mono";
    }

    internal static void Apply(string? id)
    {
        System.Windows.ResourceDictionary? resources = System.Windows.Application.Current?.Resources;
        if (resources is null) return;
        string wanted = Normalize(id);
        CurrentId = wanted;
        Palette palette = All.FirstOrDefault(item => item.Id == wanted) ?? All[0];
        Set(resources, "Canvas", palette.Canvas);
        Set(resources, "Surface", palette.Surface);
        Set(resources, "Raised", palette.Raised);
        Set(resources, "Elevated", palette.Elevated);
        Set(resources, "Hairline", palette.Hairline);
        Set(resources, "Border", palette.Border);
        Set(resources, "TagBorder", palette.TagBorder);
        Set(resources, "PrimaryText", palette.PrimaryText);
        Set(resources, "SecondaryText", palette.SecondaryText);
        Set(resources, "MutedText", palette.MutedText);
        Set(resources, "Accent", palette.Accent);
        Set(resources, "Warn", palette.Warn);
        Set(resources, "Ok", palette.Ok);
        Set(resources, "Info", palette.Info);
        Set(resources, "Live", palette.Live);
        Set(resources, "PopupCanvas", palette.PopupCanvas);
        Set(resources, "PopupRim", palette.PopupRim);
        Set(resources, "PopupGlow", palette.PopupGlow);
        resources["DotPattern"] = BuildDots(palette.Dot, palette.DotTile, palette.DotRadius);
        resources["ProductGlow"] = BuildGlow(palette.Elevated);
        // Windows paint some brushes from code (the connection dot, the battery fills,
        // the popup glow). Without this they kept the previous theme until the next
        // packet arrived, which is what the leftover dots looked like.
        try { Changed?.Invoke(); }
        catch (Exception ex) { Logger.Error("Theme listeners failed", ex); }
    }

    private static void Set(System.Windows.ResourceDictionary resources, string name, string hex)
    {
        System.Windows.Media.Color color = Parse(hex);
        resources[name + "Color"] = color;
        resources[name + "Brush"] = new System.Windows.Media.SolidColorBrush(color);
    }

    private static System.Windows.Media.Brush BuildDots(string? hex, double tile, double radius)
    {
        if (string.IsNullOrWhiteSpace(hex)) return System.Windows.Media.Brushes.Transparent;
        var geometry = new System.Windows.Media.EllipseGeometry(new System.Windows.Point(tile / 2d, tile / 2d), radius, radius);
        var drawing = new System.Windows.Media.GeometryDrawing(new System.Windows.Media.SolidColorBrush(Parse(hex)), null, geometry);
        return new System.Windows.Media.DrawingBrush(drawing)
        {
            TileMode = System.Windows.Media.TileMode.Tile,
            Viewport = new System.Windows.Rect(0d, 0d, tile, tile),
            ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute,
            Stretch = System.Windows.Media.Stretch.None,
        };
    }

    /// <summary>Soft halo behind the case photo, built from the palette so it never stays a dark blob on a light theme.</summary>
    private static System.Windows.Media.Brush BuildGlow(string hex)
    {
        System.Windows.Media.Color color = Parse(hex);
        var brush = new System.Windows.Media.RadialGradientBrush();
        brush.GradientStops.Add(new System.Windows.Media.GradientStop(color, 0d));
        brush.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromArgb(0, color.R, color.G, color.B), 1d));
        return brush;
    }

    /// <summary>Accepts #RRGGBB and #AARRGGBB, because the popup card needs its opacity.</summary>
    private static System.Windows.Media.Color Parse(string hex)
    {
        string value = hex.TrimStart('#');
        if (value.Length == 8)
        {
            byte alpha = Convert.ToByte(value.Substring(0, 2), 16);
            byte red = Convert.ToByte(value.Substring(2, 2), 16);
            byte green = Convert.ToByte(value.Substring(4, 2), 16);
            byte blue = Convert.ToByte(value.Substring(6, 2), 16);
            return System.Windows.Media.Color.FromArgb(alpha, red, green, blue);
        }
        byte r = Convert.ToByte(value.Substring(0, 2), 16);
        byte g = Convert.ToByte(value.Substring(2, 2), 16);
        byte b = Convert.ToByte(value.Substring(4, 2), 16);
        return System.Windows.Media.Color.FromRgb(r, g, b);
    }
}
