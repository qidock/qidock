using System.Windows;
using System.Windows.Media;
using QiDock.Models;

namespace QiDock.Services;

/// <summary>
/// A look for the dock, the widgets and the panels ("Glass", "Black", "Lo-fi"…): a set of colours laid over the
/// dark or light palette. Glass is the palette itself; every other style replaces its materials and text colours.
/// </summary>
public sealed record ThemeStyle(
    string Id,
    string NameTr,
    string NameEn,
    AppTheme Base,
    string DockTop,
    string DockBottom,
    string Tile,
    string TileStroke,
    string Text,
    string TextSecondary,
    string Accent,
    string Popup,
    string Edge,
    string[] Swatch)
{
    public string Name => UiText.Current.Language == AppLanguage.English ? NameEn : NameTr;
}

public static class ThemeStyles
{
    public const string Glass = "glass";

    public static readonly IReadOnlyList<ThemeStyle> All =
    [
        new(Glass, "Cam", "Glass", AppTheme.Dark, "", "", "", "", "", "", "", "", "", ["#FF8EC5FF", "#FFFFB86B", "#FF7EE0A1"]),
        new("black", "Siyah", "Black", AppTheme.Dark, "#FF050505", "#FF000000", "#FF262628", "#14FFFFFF", "#FFF5F5F7", "#FF98989D", "#FF0A84FF", "#FF151517", "#26FFFFFF",
            ["#FF0A84FF", "#FFFF9F0A", "#FF30D158"]),
        new("lofi", "Lo-fi", "Lo-fi", AppTheme.Dark, "#FF33271F", "#FF261C16", "#FF46372D", "#26F6E1C6", "#FFF7E8D4", "#FFC6A98E", "#FFE8915B", "#FF2E231C", "#33F6E1C6",
            ["#FFC9A47E", "#FFE8915B", "#FF8C6A55"]),
        new("pixelnight", "Piksel Gece", "Pixel Night", AppTheme.Dark, "#FF1B1538", "#FF100C24", "#FF2A2154", "#407B6CFF", "#FFEFEAFF", "#FFA99DDA", "#FFFF5FA2", "#FF18122F", "#557B6CFF",
            ["#FF7B6CFF", "#FFFF5FA2", "#FF4DE1FF"]),
        new("christmas", "Yılbaşı", "Christmas", AppTheme.Dark, "#FF113321", "#FF0A2015", "#FF1D4630", "#40E8C26A", "#FFFFF8EC", "#FFBAD4C3", "#FFE0343F", "#FF0E2A1B", "#55E8C26A",
            ["#FFE0343F", "#FF2F9E5A", "#FFE8C26A"]),
        new("ocean", "Okyanus", "Ocean", AppTheme.Dark, "#FF0D2840", "#FF07192A", "#FF16395A", "#333FC1FF", "#FFE8F6FF", "#FF8FB9D6", "#FF3FC1FF", "#FF0B2135", "#443FC1FF",
            ["#FF3FC1FF", "#FF1B6CA8", "#FF7FE3D6"]),
        new("sunset", "Gün Batımı", "Sunset", AppTheme.Dark, "#FF331734", "#FF221024", "#FF4A2342", "#33FF8A5B", "#FFFFF1EA", "#FFDDAABB", "#FFFF8A5B", "#FF2A1329", "#44FF8A5B",
            ["#FFFF8A5B", "#FFFF5C8A", "#FFFFC857"]),
        new("forest", "Orman", "Forest", AppTheme.Dark, "#FF1A2A1E", "#FF111D14", "#FF27402E", "#338BD17C", "#FFEFF7EA", "#FFA9C2A2", "#FF8BD17C", "#FF16241A", "#448BD17C",
            ["#FF8BD17C", "#FF4E8C5A", "#FFD9C27A"]),
        new("graphite", "Grafit", "Graphite", AppTheme.Dark, "#FF2E3034", "#FF232528", "#FF3D4045", "#1FFFFFFF", "#FFF0F1F3", "#FFA4A8AF", "#FFB4BBC6", "#FF26282C", "#33FFFFFF",
            ["#FFB4BBC6", "#FF6E737B", "#FFE6E8EB"]),
        new("neon", "Neon", "Neon", AppTheme.Dark, "#FF0B0B12", "#FF050508", "#FF161620", "#6600F0FF", "#FFF4FEFF", "#FF8FB7C0", "#FF00F0FF", "#FF0C0C14", "#8800F0FF",
            ["#FF00F0FF", "#FFFF2BD6", "#FFB6FF3B"]),
        new("retro", "Retro", "Retro", AppTheme.Dark, "#FF2F2B22", "#FF221F18", "#FF433D30", "#33F2B33D", "#FFF6EBCB", "#FFC4B48E", "#FFF2B33D", "#FF29251D", "#44F2B33D",
            ["#FFF2B33D", "#FFD9603B", "#FF6BA292"]),
        new("mocha", "Moka", "Mocha", AppTheme.Dark, "#FF1E1A26", "#FF15121B", "#FF2D2738", "#26CBA6F7", "#FFE8E0F5", "#FFA89FBA", "#FFCBA6F7", "#FF1A1621", "#33CBA6F7",
            ["#FFCBA6F7", "#FFF5C2E7", "#FF89DCEB"]),
        new("paper", "Kâğıt", "Paper", AppTheme.Light, "#FFF6F3EC", "#FFEDE8DE", "#FFFFFFFF", "#22000000", "#FF23201B", "#FF777063", "#FF2F6FEB", "#FFFBF9F4", "#26000000",
            ["#FFEDE8DE", "#FF2F6FEB", "#FF23201B"]),
        new("sakura", "Sakura", "Sakura", AppTheme.Light, "#FFFBEAF0", "#FFF3D9E3", "#FFFFFFFF", "#26E85D8E", "#FF3A2430", "#FF8C6A78", "#FFE85D8E", "#FFFFF6F9", "#33E85D8E",
            ["#FFF3D9E3", "#FFE85D8E", "#FF9BC88B"])
    ];

    public static ThemeStyle Find(string? id) => All.FirstOrDefault(s => s.Id == id) ?? All[0];

    /// <summary>The overlay for <paramref name="style"/>, or null for Glass (the palette alone).</summary>
    public static ResourceDictionary? Overlay(ThemeStyle style)
    {
        if (style.Id == Glass) return null;
        var light = style.Base == AppTheme.Light;
        var d = new ResourceDictionary();
        static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
        static SolidColorBrush S(string hex) { var b = new SolidColorBrush(C(hex)); b.Freeze(); return b; }
        static LinearGradientBrush V(string top, string bottom) { var b = new LinearGradientBrush(C(top), C(bottom), 90); b.Freeze(); return b; }
        static string Alpha(string hex, byte a) => $"#{a:X2}{hex[3..]}";
        var overlay = light ? "#FF000000" : "#FFFFFFFF";

        d["DockMaterial"] = V(style.DockTop, style.DockBottom);
        d["DockBorderMaterial"] = V(style.Edge, Alpha(style.Edge, 0x10));
        d["MenuMaterial"] = V(style.Popup, style.Popup);
        d["WidgetPopupFill"] = V(style.Popup, style.Popup);
        d["SurfacePopup"] = S(style.Popup);
        d["MenuStroke"] = S(style.Edge);
        d["MenuDivider"] = S(Alpha(style.Text, 0x22));
        d["TextPrimary"] = S(style.Text);
        d["TextSecondary"] = S(style.TextSecondary);
        d["WidgetTileFill"] = S(style.Tile);
        d["WidgetTileStroke"] = S(style.TileStroke);
        d["WidgetHoverOverlay"] = S(Alpha(overlay, 0x16));
        d["WidgetPressedOverlay"] = S(Alpha(overlay, 0x26));
        d["WidgetTrack"] = S(Alpha(style.Text, 0x30));
        d["WidgetAccent"] = S(style.Accent);
        d["DockTileFill"] = S(Alpha(style.Tile, 0xCC));
        d["DockTileStroke"] = S(style.TileStroke);
        d["SurfaceHover"] = S(Alpha(overlay, 0x1E));
        d["SurfacePressed"] = S(Alpha(overlay, 0x30));
        d["IndicatorBrush"] = S(Alpha(style.Text, 0xB0));
        d["NotificationBrush"] = S(style.Accent);
        d["PanelFrostTint"] = C(Alpha(style.DockBottom, 0xD8));
        d["PanelTile"] = S(Alpha(style.Tile, 0xE6));
        d["PanelTileHover"] = S(style.Tile);
        d["PanelTileStroke"] = S(style.TileStroke);
        ThemePatterns.Add(d, style);
        return d;
    }
}
