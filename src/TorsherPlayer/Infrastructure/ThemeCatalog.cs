using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace TorsherPlayer.Infrastructure;

public static class ThemeCatalog
{
    public static readonly string[] Names = [
        "Midnight", "OLED", "Ocean", "Amber", "Forest", "Crimson", "Violet", "Nord", "Rose", "Cobalt",
        "Light", "Paper", "Ivory"
    ];

    private sealed record Palette(
        string Background,
        string Surface,
        string SurfaceAlt,
        string Panel,
        string PanelAlt,
        string Foreground,
        string Muted,
        string Accent,
        string Focus,
        string FocusText,
        string Border,
        string Seek,
        bool IsLight = false);

    private static readonly Dictionary<string, Palette> Palettes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Midnight"] = new("#0A0B0E", "#16181D", "#202329", "#E6121418", "#F2181A20", "#F5F6F8", "#A9AFB8", "#F5F6F8", "#F5F6F8", "#101114", "#515761", "#E7E9ED"),
        ["OLED"]     = new("#000000", "#090909", "#121212", "#F0000000", "#FA080808", "#FFFFFF", "#A8A8A8", "#FFFFFF", "#FFFFFF", "#000000", "#3E3E3E", "#FFFFFF"),
        ["Ocean"]    = new("#061018", "#0B1B26", "#102A39", "#ED07131C", "#F20B1B26", "#F4FAFF", "#9DB4C4", "#83D5FF", "#57C7FF", "#041019", "#31566A", "#83D5FF"),
        ["Amber"]    = new("#100C06", "#20170B", "#2A1F10", "#EF120D06", "#F21D150A", "#FFF9EE", "#BBAF99", "#FFD180", "#FFC04D", "#1A1003", "#624A26", "#FFD180"),
        ["Forest"]   = new("#07100C", "#0F1D16", "#152A20", "#ED08130E", "#F20E1C15", "#F2FAF5", "#9FB5A8", "#8DE0B0", "#65D394", "#06120B", "#315B43", "#8DE0B0"),
        ["Crimson"]  = new("#12070A", "#211015", "#2B151C", "#EF16090D", "#F21E0E13", "#FFF4F5", "#C4A4AA", "#FF8798", "#FF5F78", "#23070D", "#67313B", "#FF8798"),
        ["Violet"]   = new("#0D0914", "#191126", "#241936", "#EF100A18", "#F2181024", "#F9F5FF", "#B8AAC9", "#C9A7FF", "#AD7CFF", "#160B25", "#523A6C", "#C9A7FF"),
        ["Nord"]     = new("#0B1017", "#141C26", "#1C2733", "#EF0D141D", "#F2131C27", "#EEF5FA", "#9FB1C0", "#88C0D0", "#81A1C1", "#09121B", "#3B5365", "#88C0D0"),
        ["Rose"]     = new("#120B10", "#21131C", "#2C1A27", "#EF150D13", "#F21D111A", "#FFF5FA", "#C2A6B5", "#F2A7CF", "#E98BBE", "#210A17", "#633B51", "#F2A7CF"),
        ["Cobalt"]   = new("#070B18", "#0F1730", "#162143", "#EF091024", "#F20E1730", "#F4F7FF", "#A3AED0", "#7EA2FF", "#5E8CFF", "#06102B", "#344A79", "#7EA2FF"),
        ["Light"]    = new("#F2F3F5", "#FFFFFF", "#E7E9ED", "#EEF4F5F7", "#F8FFFFFF", "#15171A", "#626A75", "#1B6EF3", "#1B6EF3", "#FFFFFF", "#BCC2CA", "#1B6EF3", true),
        ["Paper"]    = new("#E9E4DA", "#FBF8F2", "#DDD6CA", "#F4FBF8F2", "#FCFFFDF8", "#25221D", "#746F67", "#496A81", "#385E78", "#FFFFFF", "#BEB6AA", "#496A81", true),
        ["Ivory"]    = new("#F6F0E5", "#FFF9EF", "#EEE3D3", "#F4FFF9EF", "#FAFFFDF7", "#2C261E", "#746A5C", "#B66A2C", "#C97B36", "#FFFFFF", "#D7C6AF", "#B66A2C", true)
    };

    public static string Normalize(string? name) =>
        !string.IsNullOrWhiteSpace(name) && Palettes.ContainsKey(name) ?
            Names.First(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) : "Midnight";

    public static string Next(string current, int direction)
    {
        var normalized = Normalize(current);
        var index = Array.IndexOf(Names, normalized);
        index = (index + Math.Sign(direction) + Names.Length) % Names.Length;
        return Names[index];
    }

    public static void Apply(Application? app, string? themeName)
    {
        if (app is null) return;
        var name = Normalize(themeName);
        var p = Palettes[name];
        app.RequestedThemeVariant = p.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        var r = app.Resources;
        r["AppBackgroundBrush"] = Brush(p.Background);
        r["SurfaceBrush"] = Brush(p.Surface);
        r["SurfaceAltBrush"] = Brush(p.SurfaceAlt);
        r["PanelBrush"] = Brush(p.Panel);
        r["Panel2Brush"] = Brush(p.PanelAlt);
        r["ControlForegroundBrush"] = Brush(p.Foreground);
        r["MutedBrush"] = Brush(p.Muted);
        r["AccentBrush"] = Brush(p.Accent);
        r["FocusBrush"] = Brush(p.Focus);
        r["FocusTextBrush"] = Brush(p.FocusText);
        r["ControlBorderBrush"] = Brush(p.Border);
        r["SeekBrush"] = Brush(p.Seek);

        // Derive the OSD fade from each palette instead of forcing every light
        // theme through the same grey gradient. This keeps Paper cool and Ivory
        // warm while preserving the same readability curve.
        var osdBase = Color.Parse(p.Background);
        r["OsdGradientTopColor"] = Color.FromArgb(0x00, osdBase.R, osdBase.G, osdBase.B);
        r["OsdGradientMidColor"] = Color.FromArgb(p.IsLight ? (byte)0xE8 : (byte)0xE6, osdBase.R, osdBase.G, osdBase.B);
        r["OsdGradientBottomColor"] = Color.FromArgb(p.IsLight ? (byte)0xFC : (byte)0xFA, osdBase.R, osdBase.G, osdBase.B);
    }

    private static SolidColorBrush Brush(string color) => new(Color.Parse(color));
}
