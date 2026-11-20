using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QiDock.Services.Widgets;

/// <summary>
/// One recipe for frosted glass, shared by the desktop cards, the dock and the cards that grow out of the dock: the
/// softened wallpaper behind the surface, a dark veil (thicker over a bright picture so white text and icons keep
/// their contrast), a soft sheen from above and a thin rim that catches the light.
/// </summary>
internal static class GlassLook
{
    public static readonly Brush Rim = MakeRim();
    public static readonly Brush Sheen = MakeSheen();

    /// <summary>The veil the dock wears now, or null when the dock is not glass; the cards on the dock wear the same.</summary>
    public static Color? DockVeil { get; set; }

    /// <summary>
    /// A window stands behind the dock. The dock cannot see it (it only knows the wallpaper), so it then wears a plain
    /// grey frost instead of the wallpaper's colours, which would not match what is behind it.
    /// </summary>
    public static bool WindowBehind { get; set; }

    /// <summary>The dock's glass changed (veil, wallpaper or a window behind it): cards standing on it repaint.</summary>
    public static event Action? DockChanged;

    public static void RaiseDockChanged() => DockChanged?.Invoke();

    private static readonly Brush Neutral = MakeNeutral();
    private static (Color Veil, BitmapSource Image, Brush Brush)? _screen;

    /// <summary>
    /// Glass for the dock and what grows out of it, the same picture the desktop cards show: the wallpaper of the
    /// whole screen is drawn once (half size) under the dock's veil, and each surface only places that picture with
    /// <paramref name="localToScreen"/> (its own coordinates to screen units), so moving, growing or animating a
    /// surface never draws the glass again. Null when the dock is not glass.
    /// </summary>
    public static Brush? Dock(Matrix localToScreen)
    {
        if (DockVeil is not { } veil) return null;
        if (WindowBehind) return Neutral;
        if (WallpaperGlass.Grey is not { } image || !localToScreen.HasInverse) return null;
        if (_screen is not { } screen || screen.Veil != veil || !ReferenceEquals(screen.Image, image))
        {
            var area = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
            // Nearly grey: in the wallpaper's own colours a pink or purple picture dyed the whole dock.
            if (Surface(area, area, veil, sheen: false, frost: true) is not { } baked) return null;
            _screen = screen = (veil, image, baked);
        }
        localToScreen.Invert();
        var brush = screen.Brush.CloneCurrentValue();
        brush.Transform = new MatrixTransform(localToScreen);
        brush.Freeze();
        return brush;
    }

    private static Brush MakeNeutral()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0xD0, 0x30, 0x30, 0x35));
        brush.Freeze();
        return brush;
    }

    // The theme's tiles are solid (black for the Black style); on glass they turn into a thin frost of white.
    private static readonly (string Key, Brush Brush)[] GlassTiles =
    [
        ("WidgetTileFill", Frozen(0x1A)), ("WidgetTileStroke", Frozen(0x1F)),
        ("DockTileFill", Frozen(0x1C)), ("DockTileStroke", Frozen(0x14)),
        ("PanelTile", Frozen(0x1A)), ("PanelTileHover", Frozen(0x2B)), ("PanelTileStroke", Frozen(0x1F)),
    ];

    /// <summary>Glass tiles in <paramref name="resources"/> (a window's own), or the theme's tiles again.</summary>
    public static void Tiles(ResourceDictionary resources, bool glass)
    {
        if (resources.Contains(GlassTiles[0].Key) == glass) return;
        foreach (var (key, brush) in GlassTiles)
        {
            if (glass) resources[key] = brush;
            else resources.Remove(key);
        }
    }

    private static Brush Frozen(byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));
        brush.Freeze();
        return brush;
    }

    public static Color Veil(double light)
    {
        var alpha = Math.Clamp(0.16 + 0.55 * light, 0.16, 0.62);
        // In steps, so a slightly different patch of wallpaper does not mean drawing the glass again.
        var step = Math.Round(alpha * 255 / 6) * 6;
        return Color.FromArgb((byte)Math.Clamp(step, 0, 255), 0x0C, 0x0C, 0x10);
    }

    /// <summary>
    /// Glass for a surface whose own coordinates cover <paramref name="local"/> and which sits at
    /// <paramref name="screen"/> on the screen; null when there is no wallpaper to show. The layers are drawn once
    /// into a small picture (half size: it is soft anyway), so an animated shape or a growing icon only copies
    /// pixels instead of scaling the wallpaper again on every frame. <paramref name="frost"/>: the nearly grey
    /// picture (the dock, what grows out of it, the app screen); otherwise the wallpaper's own colours, toned down
    /// (the desktop cards).
    /// </summary>
    public static Brush? Surface(Rect local, Rect screen, Color veil, bool sheen, Transform? transform = null, bool frost = true)
    {
        if ((frost ? WallpaperGlass.Grey : WallpaperGlass.Image) is not { } image || local.Width <= 0 || local.Height <= 0) return null;
        const double Resolution = 0.5;
        const double Bleed = 4; // drawn a little larger, so the soft edge of the scaled picture stays outside the shape
        var area = Rect.Inflate(local, Bleed, Bleed);
        var behind = Rect.Inflate(screen, Bleed, Bleed);
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(Resolution, Resolution));
            dc.PushTransform(new TranslateTransform(-area.X, -area.Y));
            dc.DrawRectangle(new ImageBrush(image) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = WallpaperGlass.Behind(behind), Stretch = Stretch.Fill }, null, area);
            dc.DrawRectangle(new SolidColorBrush(veil), null, area);
            if (sheen) dc.DrawRectangle(Sheen, null, local);
        }
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(area.Width * Resolution)), Math.Max(1, (int)Math.Ceiling(area.Height * Resolution)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap)
        {
            Stretch = Stretch.Fill,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(area.X, area.Y, bitmap.PixelWidth / Resolution, bitmap.PixelHeight / Resolution)
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.Linear);
        if (transform is not null) brush.Transform = transform;
        brush.Freeze();
        return brush;
    }

    private static Brush MakeRim()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF), 0.32));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF), 0.62));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF), 1));
        brush.Freeze();
        return brush;
    }

    private static Brush MakeSheen()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF), 0.45));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1));
        brush.Freeze();
        return brush;
    }
}
