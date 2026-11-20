using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QiDock.Services.Widgets;

/// <summary>
/// The wallpaper as frosted glass sees it: read once, small (256 pixels wide), softened and a little more colourful,
/// shared by every desktop card. A card shows the part of it that lies behind the card, so the glass looks through
/// to the picture without any live blur (nothing is redrawn while the desktop stays the same).
/// </summary>
internal static class WallpaperGlass
{
    private const int DecodeWidth = 256;
    private static string _stamp = "";
    private static byte[]? _pixels;
    private static int _width, _height;

    /// <summary>The softened wallpaper, or null when there is none to read (a plain colour).</summary>
    public static BitmapSource? Image { get; private set; }

    /// <summary>Goes up each time the picture changes, so every user of it can tell (not only the first to ask).</summary>
    public static int Version { get; private set; }

    /// <summary>Reads the wallpaper again if Windows changed it. True when the picture is new.</summary>
    public static bool Refresh()
    {
        var file = WallpaperTint.WallpaperFile();
        if (file is null)
        {
            var had = Image is not null;
            Image = null;
            if (had) Version++;
            _pixels = null;
            _stamp = "";
            return had;
        }
        try
        {
            var info = new FileInfo(file);
            var stamp = file + info.LastWriteTimeUtc.Ticks + info.Length;
            if (stamp == _stamp && Image is not null) return false;
            var image = new BitmapImage();
            using (var stream = new MemoryStream(File.ReadAllBytes(file)))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = DecodeWidth;
                image.StreamSource = stream;
                image.EndInit();
            }
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            int w = converted.PixelWidth, h = converted.PixelHeight;
            var pixels = new byte[w * h * 4];
            converted.CopyPixels(pixels, w * 4, 0);
            // Three box passes come close to a gaussian blur; then a touch more colour, as glass over a picture has.
            for (var pass = 0; pass < 3; pass++)
            {
                BoxBlur(pixels, w, h, 2, horizontal: true);
                BoxBlur(pixels, w, h, 2, horizontal: false);
            }
            Saturate(pixels, 1.3);
            var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            bitmap.Freeze();
            Image = bitmap;
            Version++;
            _pixels = pixels;
            _width = w;
            _height = h;
            _stamp = stamp;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException
                                       or ArgumentException or FormatException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    /// <summary>
    /// The part of <see cref="Image"/> behind <paramref name="area"/> (screen units of the primary screen), for an
    /// image brush's viewbox. The wallpaper fills the screen: scaled to cover it, the overflow cut off equally.
    /// </summary>
    public static Rect Behind(Rect area)
    {
        var (scale, offsetX, offsetY) = Mapping();
        return new Rect((area.Left + offsetX) / scale, (area.Top + offsetY) / scale, area.Width / scale, area.Height / scale);
    }

    /// <summary>How bright the glass behind <paramref name="area"/> is, 0 (black) to 1 (white).</summary>
    public static double Light(Rect area)
    {
        if (_pixels is null || _width == 0) return 0.3;
        var box = Behind(area);
        int x0 = Math.Clamp((int)box.Left, 0, _width - 1), x1 = Math.Clamp((int)Math.Ceiling(box.Right), 0, _width - 1);
        int y0 = Math.Clamp((int)box.Top, 0, _height - 1), y1 = Math.Clamp((int)Math.Ceiling(box.Bottom), 0, _height - 1);
        double light = 0;
        var count = 0;
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var i = (y * _width + x) * 4;
            light += (0.0722 * _pixels[i] + 0.7152 * _pixels[i + 1] + 0.2126 * _pixels[i + 2]) / 255;
            count++;
        }
        return count == 0 ? 0.3 : light / count;
    }

    private static (double Scale, double OffsetX, double OffsetY) Mapping()
    {
        var screen = new Size(SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        if (_width == 0 || _height == 0) return (1, 0, 0);
        var scale = Math.Max(screen.Width / _width, screen.Height / _height);
        return (scale, (_width * scale - screen.Width) / 2, (_height * scale - screen.Height) / 2);
    }

    private static void BoxBlur(byte[] pixels, int width, int height, int radius, bool horizontal)
    {
        var lines = horizontal ? height : width;
        var length = horizontal ? width : height;
        var line = new int[length * 4];
        for (var l = 0; l < lines; l++)
        {
            int Index(int p) => horizontal ? (l * width + p) * 4 : (p * width + l) * 4;
            for (var p = 0; p < length; p++)
            {
                var i = Index(p);
                for (var c = 0; c < 4; c++) line[p * 4 + c] = pixels[i + c];
            }
            for (var p = 0; p < length; p++)
            {
                var i = Index(p);
                for (var c = 0; c < 4; c++)
                {
                    var sum = 0;
                    for (var k = -radius; k <= radius; k++) sum += line[Math.Clamp(p + k, 0, length - 1) * 4 + c];
                    pixels[i + c] = (byte)(sum / (2 * radius + 1));
                }
            }
        }
    }

    private static void Saturate(byte[] pixels, double amount)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            double b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            var grey = 0.0722 * b + 0.7152 * g + 0.2126 * r;
            pixels[i] = (byte)Math.Clamp(grey + (b - grey) * amount, 0, 255);
            pixels[i + 1] = (byte)Math.Clamp(grey + (g - grey) * amount, 0, 255);
            pixels[i + 2] = (byte)Math.Clamp(grey + (r - grey) * amount, 0, 255);
            pixels[i + 3] = 255;
        }
    }
}
