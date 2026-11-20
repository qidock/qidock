using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QiDock.Services;

/// <summary>
/// What makes each theme's dock its own beyond its colours: a faint texture over the whole dock (snow, film grain,
/// brushed metal, ruled paper…) and a band along its bottom edge (waves, pine trees, sunset lines, 70s stripes…).
/// Each is one small tile drawn once when the theme is chosen and repeated by the dock, so it costs a few kilobytes
/// and nothing while the dock moves.
/// </summary>
internal static class ThemePatterns
{
    /// <summary>Puts the theme's texture ("DockPattern") and bottom band ("DockBand", "DockBandHeight") into <paramref name="d"/>.</summary>
    public static void Add(ResourceDictionary d, ThemeStyle style)
    {
        var (texture, band, bandHeight) = Make(style);
        d["DockPattern"] = texture ?? Brushes.Transparent;
        d["DockBand"] = band ?? Brushes.Transparent;
        d["DockBandHeight"] = band is null ? 0.0 : bandHeight;
    }

    private static (Brush? Texture, Brush? Band, double BandHeight) Make(ThemeStyle s)
    {
        var accent = C(s.Accent);
        var one = C(s.Swatch[0]);
        var two = C(s.Swatch.Length > 1 ? s.Swatch[1] : s.Accent);
        var three = C(s.Swatch.Length > 2 ? s.Swatch[2] : s.Accent);
        var text = C(s.Text);
        switch (s.Id)
        {
            case "black": // piano black: a glossy sheen across the top
                return (Tile(4, 120, dc => dc.DrawRectangle(Vertical(A(Colors.White, 0x2A), A(Colors.White, 0x00), 0, 0.16), null, new Rect(0, 0, 4, 120))), null, 0);

            case "lofi": // film grain
                return (Tile(48, 48, dc => Scatter(dc, 48, 48, 70, 7, (x, y, r, i) => dc.DrawEllipse(Solid(A(text, (byte)(0x16 + i % 3 * 6))), null, new Point(x, y), r * 0.45 + 0.35, r * 0.45 + 0.35))), null, 0);

            case "pixelnight": // pixel stars, and a stepped pixel skyline along the bottom
                return (Tile(64, 40, dc =>
                    {
                        Scatter(dc, 64, 40, 7, 11, (x, y, r, i) =>
                        {
                            var colour = (i % 3) switch { 0 => three, 1 => two, _ => Colors.White };
                            var size = i % 4 == 0 ? 2.0 : 1.4;
                            dc.DrawRectangle(Solid(A(colour, 0x8C)), null, new Rect(Math.Round(x), Math.Round(y), size, size));
                        });
                    }),
                    Tile(36, 9, dc =>
                    {
                        double[] heights = [3, 6, 4, 8, 5, 3, 7, 4, 6, 3, 5, 8];
                        for (var i = 0; i < heights.Length; i++)
                            dc.DrawRectangle(Solid(A(one, 0x46)), null, new Rect(i * 3, 9 - heights[i], 3, heights[i]));
                    }), 9);

            case "christmas": // falling snow, and a candy-cane stripe along the bottom
                return (Tile(56, 44, dc => Scatter(dc, 56, 44, 9, 23, (x, y, r, i) => dc.DrawEllipse(Solid(A(Colors.White, (byte)(0x40 + i % 3 * 0x14))), null, new Point(x, y), 0.8 + r * 0.5, 0.8 + r * 0.5))),
                    Tile(12, 4, dc =>
                    {
                        dc.DrawRectangle(Solid(A(Colors.White, 0xB0)), null, new Rect(0, 0, 12, 4));
                        var stripe = new StreamGeometry();
                        using (var g = stripe.Open())
                        {
                            g.BeginFigure(new Point(0, 4), true, true);
                            g.LineTo(new Point(4, 0), true, false);
                            g.LineTo(new Point(10, 0), true, false);
                            g.LineTo(new Point(6, 4), true, false);
                        }
                        stripe.Freeze();
                        dc.DrawGeometry(Solid(A(one, 0xE0)), null, stripe);
                    }), 4);

            case "ocean": // two waves along the bottom
                return (null, Tile(48, 14, dc =>
                    {
                        dc.DrawGeometry(Solid(A(three, 0x26)), null, Wave(48, 14, 5, 2.4, 12));
                        dc.DrawGeometry(Solid(A(one, 0x3A)), null, Wave(48, 14, 9, 1.8, 0));
                    }), 14);

            case "sunset": // the striped lower half of a retro sun
                return (null, Tile(4, 16, dc =>
                    {
                        dc.DrawRectangle(Solid(A(three, 0x55)), null, new Rect(0, 3, 4, 1.2));
                        dc.DrawRectangle(Solid(A(one, 0x60)), null, new Rect(0, 7, 4, 2));
                        dc.DrawRectangle(Solid(A(two, 0x70)), null, new Rect(0, 11.5, 4, 3));
                    }), 16);

            case "forest": // a row of pine trees
                return (null, Tile(46, 17, dc =>
                    {
                        (double X, double H)[] trees = [(5, 12), (13, 16), (21, 9), (30, 14), (39, 11)];
                        foreach (var (x, h) in trees)
                        {
                            var tree = new StreamGeometry();
                            using (var g = tree.Open())
                            {
                                g.BeginFigure(new Point(x, 17 - h), true, true);
                                g.LineTo(new Point(x + h * 0.36, 17), true, false);
                                g.LineTo(new Point(x - h * 0.36, 17), true, false);
                            }
                            tree.Freeze();
                            dc.DrawGeometry(Solid(A(two, 0x70)), null, tree);
                        }
                    }), 17);

            case "graphite": // brushed metal
                return (Tile(140, 7, dc => Scatter(dc, 140, 7, 9, 5, (x, y, r, i) => dc.DrawRectangle(Solid(A(Colors.White, (byte)(0x08 + i % 3 * 4))), null, new Rect(x, Math.Round(y), 20 + r * 30, 0.6)))), null, 0);

            case "neon": // a faint synthwave grid, and a glowing magenta line along the bottom
                return (Tile(22, 22, dc =>
                    {
                        var line = Solid(A(accent, 0x1C));
                        dc.DrawRectangle(line, null, new Rect(0, 21.4, 22, 0.6));
                        dc.DrawRectangle(line, null, new Rect(21.4, 0, 0.6, 22));
                    }),
                    Tile(4, 5, dc =>
                    {
                        dc.DrawRectangle(Solid(A(two, 0x30)), null, new Rect(0, 1, 4, 4));
                        dc.DrawRectangle(Solid(A(two, 0xD0)), null, new Rect(0, 2.6, 4, 1.4));
                    }), 5);

            case "retro": // three 70s stripes
                return (null, Tile(4, 10, dc =>
                    {
                        dc.DrawRectangle(Solid(A(one, 0xC0)), null, new Rect(0, 0.5, 4, 2.4));
                        dc.DrawRectangle(Solid(A(two, 0xC0)), null, new Rect(0, 3.7, 4, 2.4));
                        dc.DrawRectangle(Solid(A(three, 0xC0)), null, new Rect(0, 6.9, 4, 2.4));
                    }), 10);

            case "mocha": // soft polka dots
                return (Tile(18, 18, dc =>
                    {
                        dc.DrawEllipse(Solid(A(one, 0x26)), null, new Point(4.5, 4.5), 1.5, 1.5);
                        dc.DrawEllipse(Solid(A(two, 0x1E)), null, new Point(13.5, 13.5), 1.5, 1.5);
                    }), null, 0);

            case "paper": // ruled paper with its red margin line
                return (Tile(8, 9, dc => dc.DrawRectangle(Solid(A(accent, 0x22)), null, new Rect(0, 8.3, 8, 0.7))),
                    Tile(4, 3, dc => dc.DrawRectangle(Solid(A(Color.FromRgb(0xE0, 0x4B, 0x4B), 0x55)), null, new Rect(0, 1, 4, 1))), 3);

            case "sakura": // falling blossoms and petals
                return (Tile(60, 46, dc =>
                    {
                        Blossom(dc, new Point(12, 13), 3.2, A(accent, 0x55));
                        Blossom(dc, new Point(43, 31), 2.6, A(accent, 0x44));
                        dc.DrawEllipse(Solid(A(accent, 0x3A)), null, new Point(32, 8), 1.6, 1.0);
                        dc.DrawEllipse(Solid(A(accent, 0x30)), null, new Point(9, 37), 1.4, 0.9);
                        dc.DrawEllipse(Solid(A(accent, 0x36)), null, new Point(53, 18), 1.2, 0.8);
                    }), null, 0);

            default:
                return (null, null, 0);
        }
    }

    /// <summary>One tile, drawn at twice the size so it stays sharp on a high-density screen, repeated by the brush.</summary>
    private static ImageBrush Tile(double width, double height, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) draw(dc);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * 2), (int)Math.Ceiling(height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.Fill,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, width, height)
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>Points spread over a tile the same way every time (a fixed seed), so the texture never flickers.</summary>
    private static void Scatter(DrawingContext dc, double width, double height, int count, int seed, Action<double, double, double, int> each)
    {
        var random = new Random(seed);
        for (var i = 0; i < count; i++) each(random.NextDouble() * width, random.NextDouble() * height, random.NextDouble(), i);
    }

    private static Geometry Wave(double width, double height, double baseline, double amplitude, double shift)
    {
        var wave = new StreamGeometry();
        using (var g = wave.Open())
        {
            g.BeginFigure(new Point(0, height), true, true);
            for (var x = 0.0; x <= width; x += 1)
                g.LineTo(new Point(x, baseline + amplitude * Math.Sin((x + shift) / width * 2 * Math.PI)), true, false);
            g.LineTo(new Point(width, height), true, false);
        }
        wave.Freeze();
        return wave;
    }

    private static void Blossom(DrawingContext dc, Point centre, double size, Color colour)
    {
        var fill = Solid(colour);
        for (var i = 0; i < 5; i++)
        {
            var angle = i * 2 * Math.PI / 5 - Math.PI / 2;
            dc.DrawEllipse(fill, null, new Point(centre.X + Math.Cos(angle) * size * 0.75, centre.Y + Math.Sin(angle) * size * 0.75), size * 0.55, size * 0.55);
        }
        dc.DrawEllipse(Solid(A(Colors.White, 0x70)), null, centre, size * 0.3, size * 0.3);
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static Color A(Color colour, byte alpha) => Color.FromArgb(alpha, colour.R, colour.G, colour.B);

    private static Brush Solid(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    private static Brush Vertical(Color top, Color bottom, double from, double to)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop(top, from));
        brush.GradientStops.Add(new GradientStop(bottom, to));
        brush.Freeze();
        return brush;
    }
}
