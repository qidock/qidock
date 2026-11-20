using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QiDock.Services.Widgets;

/// <summary>
/// A text colour taken from the wallpaper behind a spot of the screen, as a phone's lock screen tints its clock: a
/// soft, light shade of the picture's own colour (a dark one over a very bright picture). The wallpaper is read once
/// at 96 pixels wide and again only when Windows changes it.
/// </summary>
internal static class WallpaperTint
{
    private static string _stamp = "";
    private static byte[]? _pixels;
    private static int _width, _height;

    /// <summary>The current wallpaper as Windows stores it for the desktop (also when a slideshow changes it).</summary>
    internal static string? WallpaperFile()
    {
#if DEBUG
        if (Promo.Wallpaper is { } promo) return promo;
#endif
        var transcoded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
        if (File.Exists(transcoded)) return transcoded;
        var path = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", null) as string;
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
    }

    private static bool Load()
    {
        var file = WallpaperFile();
        if (file is null) return false;
        try
        {
            var info = new FileInfo(file);
            var stamp = file + info.LastWriteTimeUtc.Ticks + info.Length;
            if (stamp == _stamp && _pixels is not null) return true;
            var image = new BitmapImage();
            using (var stream = new MemoryStream(File.ReadAllBytes(file)))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 96;
                image.StreamSource = stream;
                image.EndInit();
            }
            var pixels = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            _width = pixels.PixelWidth;
            _height = pixels.PixelHeight;
            _pixels = new byte[_width * _height * 4];
            pixels.CopyPixels(_pixels, _width * 4, 0);
            _stamp = stamp;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The tint for text over <paramref name="area"/> (screen units of the primary screen), or null when the wallpaper
    /// cannot be read (a plain colour, a picture that was removed).
    /// </summary>
    public static Color? For(Rect area)
    {
        if (!Load() || _pixels is null || _width == 0 || _height == 0) return null;
        // The wallpaper fills the screen ("fill"): scaled to cover it, the overflow cut off equally on both sides.
        var screen = new Size(SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        var scale = Math.Max(screen.Width / _width, screen.Height / _height);
        var offsetX = (_width * scale - screen.Width) / 2;
        var offsetY = (_height * scale - screen.Height) / 2;
        int Px(double x) => Math.Clamp((int)((x + offsetX) / scale), 0, _width - 1);
        int Py(double y) => Math.Clamp((int)((y + offsetY) / scale), 0, _height - 1);
        int x0 = Px(area.Left), x1 = Px(area.Right), y0 = Py(area.Top), y1 = Py(area.Bottom);

        double r = 0, g = 0, b = 0, count = 0, light = 0;
        // The most colourful pixels decide the hue (a grey sky over a blue sea still reads as blue).
        double cr = 0, cg = 0, cb = 0, weight = 0;
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var i = (y * _width + x) * 4;
            double pb = _pixels[i] / 255.0, pg = _pixels[i + 1] / 255.0, pr = _pixels[i + 2] / 255.0;
            r += pr; g += pg; b += pb; count++;
            light += 0.2126 * pr + 0.7152 * pg + 0.0722 * pb;
            var saturation = Math.Max(pr, Math.Max(pg, pb)) - Math.Min(pr, Math.Min(pg, pb));
            var w = saturation * saturation;
            cr += pr * w; cg += pg * w; cb += pb * w; weight += w;
        }
        if (count == 0) return null;
        light /= count;
        var (hue, sat, _) = weight > 0.02 * count / 50 ? Hsl(cr / weight, cg / weight, cb / weight) : Hsl(r / count, g / count, b / count);
        // Light text over most pictures; over a very bright one, a deep shade of the same colour.
        return light > 0.72
            ? FromHsl(hue, Math.Min(0.55, sat * 1.1), 0.24)
            : FromHsl(hue, Math.Min(0.6, sat * 1.2 + 0.08), 0.86);
    }

    private static (double H, double S, double L) Hsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max - min < 1e-6) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double Channel(double t)
        {
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            return t < 1.0 / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p;
        }
        byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return s <= 0 ? Color.FromRgb(B(l), B(l), B(l)) : Color.FromRgb(B(Channel(h + 1.0 / 3)), B(Channel(h)), B(Channel(h - 1.0 / 3)));
    }
}
