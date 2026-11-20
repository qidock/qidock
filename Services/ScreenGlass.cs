using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace QiDock.Services;

/// <summary>
/// Frosted glass made from what is on the screen right now: the pixels under a surface are copied once, made small
/// and blurred, and darkened by a veil. The picture is about a sixth of the size (it is soft anyway), so a whole
/// panel's glass costs a few hundred kilobytes and is drawn once, when the surface opens.
/// </summary>
internal static class ScreenGlass
{
    private const int Shrink = 6;

    /// <summary>
    /// The blurred, veiled picture of <paramref name="area"/> (screen pixels), or null when the screen could not be
    /// read. Call it before the surface itself is on the screen, or the picture shows the surface.
    /// </summary>
    public static BitmapSource? Capture(Int32Rect area, Color veil, double blur = 9)
    {
        if (area.Width < Shrink || area.Height < Shrink) return null;
        var sharp = Copy(area);
        if (sharp is null) return null;
        try
        {
            var small = new CachedBitmap(new TransformedBitmap(sharp, new ScaleTransform(1.0 / Shrink, 1.0 / Shrink)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            int width = small.PixelWidth, height = small.PixelHeight;
            var picture = new DrawingVisual { Effect = new BlurEffect { Radius = blur, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality } };
            using (var dc = picture.RenderOpen())
            {
                // Drawn a little larger, so the blur does not pull the dark outside into the edges.
                dc.DrawImage(small, new Rect(-blur, -blur, width + 2 * blur, height + 2 * blur));
            }
            var tint = new DrawingVisual();
            using (var dc = tint.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(veil), null, new Rect(0, 0, width, height));
            var both = new ContainerVisual();
            both.Children.Add(picture);
            both.Children.Add(tint);
            var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            result.Render(both);
            result.Freeze();
            return result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or COMException or OutOfMemoryException)
        {
            return null;
        }
    }

    private static BitmapSource? Copy(Int32Rect area)
    {
        var screen = GetDC(0);
        if (screen == 0) return null;
        var memory = CreateCompatibleDC(screen);
        var header = new BitmapInfoHeader { Size = 40, Width = area.Width, Height = -area.Height, Planes = 1, BitCount = 32 };
        var dib = CreateDIBSection(screen, ref header, 0, out var bits, 0, 0);
        var previous = dib != 0 ? SelectObject(memory, dib) : 0;
        try
        {
            if (dib == 0 || bits == 0 || !BitBlt(memory, 0, 0, area.Width, area.Height, screen, area.X, area.Y, 0x00CC0020 /* SRCCOPY */)) return null;
            GdiFlush();
            var image = BitmapSource.Create(area.Width, area.Height, 96, 96, PixelFormats.Bgr32, null, bits, area.Width * area.Height * 4, area.Width * 4);
            image.Freeze();
            return image;
        }
        finally
        {
            if (previous != 0) SelectObject(memory, previous);
            if (dib != 0) DeleteObject(dib);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader header, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sx, int sy, uint op);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}
