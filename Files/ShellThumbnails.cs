using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace QiDock.Files;

/// <summary>
/// Thumbnails (photos, videos, PDFs…) and sharp icons for files, the way Explorer draws them, read by two background
/// threads so a big folder never stalls the window. The newest requests are served first (what is on screen now).
/// </summary>
internal static class ShellThumbnails
{
    private sealed record Request(string Path, int Size, Action<BitmapSource?> Done, Dispatcher Dispatcher);

    private static readonly ConcurrentStack<Request> Queue = new();
    private static readonly SemaphoreSlim Signal = new(0);
    private static readonly Dictionary<string, BitmapSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> Order = new();
    // Kept small on purpose: about 24 MB of pictures at most (a 160-pixel thumbnail is ~100 KB), dropped when the
    // window closes.
    private const long CacheBytes = 24L * 1024 * 1024;
    private static long _bytes;
    private static bool _started;

    private static long BytesOf(BitmapSource? image) => image is null ? 0 : (long)image.PixelWidth * image.PixelHeight * 4;

    /// <summary>Lets every picture go (the window closed).</summary>
    public static void Clear()
    {
        Queue.Clear();
        lock (Cache)
        {
            Cache.Clear();
            Order.Clear();
            _bytes = 0;
        }
    }

    public static BitmapSource? Cached(string path, int size)
    {
        lock (Cache) return Cache.TryGetValue(Key(path, size), out var image) ? image : null;
    }

    public static void Load(string path, int size, Action<BitmapSource?> done)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(Key(path, size), out var cached))
            {
                done(cached);
                return;
            }
            if (!_started)
            {
                _started = true;
                for (var i = 0; i < 2; i++)
                {
                    var thread = new Thread(Work) { IsBackground = true, Name = "QiDock thumbnails", Priority = ThreadPriority.BelowNormal };
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.Start();
                }
            }
        }
        Queue.Push(new Request(path, size, done, Dispatcher.CurrentDispatcher));
        Signal.Release();
    }

    /// <summary>Drops what has not been read yet (the folder changed).</summary>
    public static void CancelPending() => Queue.Clear();

    private static string Key(string path, int size) => size + "|" + path;

    private static void Work()
    {
        while (true)
        {
            Signal.Wait();
            if (!Queue.TryPop(out var request)) continue;
            var key = Key(request.Path, request.Size);
            BitmapSource? image;
            lock (Cache) Cache.TryGetValue(key, out image);
            image ??= Read(request.Path, request.Size);
            lock (Cache)
            {
                if (!Cache.ContainsKey(key))
                {
                    Cache[key] = image;
                    Order.AddLast(key);
                    _bytes += BytesOf(image);
                    while (_bytes > CacheBytes && Order.Count > 1)
                    {
                        var oldest = Order.First!.Value;
                        _bytes -= BytesOf(Cache.GetValueOrDefault(oldest));
                        Cache.Remove(oldest);
                        Order.RemoveFirst();
                    }
                }
            }
            request.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => request.Done(image));
        }
    }

    private static BitmapSource? Read(string path, int size)
    {
        var bitmap = nint.Zero;
        try
        {
            var id = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, 0, ref id, out var factory) != 0 || factory is null) return null;
            try
            {
                if (factory.GetImage(new NativeSize { Width = size, Height = size }, 0 /* SIIGBF_RESIZETOFIT */, out bitmap) != 0 || bitmap == 0)
                    return null;
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
            return ToBitmapSource(bitmap);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            if (bitmap != 0) DeleteObject(bitmap);
        }
    }

    /// <summary>Copies the bitmap's pixels top-down with their transparency (WPF's own conversion drops it).</summary>
    private static BitmapSource? ToBitmapSource(nint bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<NativeBitmap>(), out var info) == 0 || info.Width <= 0 || info.Height <= 0) return null;
        var width = info.Width;
        var height = info.Height;
        var header = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height, // top-down rows
            Planes = 1,
            BitCount = 32
        };
        var pixels = new byte[width * height * 4];
        var screen = GetDC(0);
        try
        {
            if (GetDIBits(screen, bitmap, 0, (uint)height, pixels, ref header, 0) == 0) return null;
        }
        finally
        {
            ReleaseDC(0, screen);
        }
        // A bitmap without transparency (most photo thumbnails) comes back with every alpha at zero.
        var anyAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] == 0) continue;
            anyAlpha = true;
            break;
        }
        if (!anyAlpha)
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        source.Freeze();
        return source;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out nint bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public ushort Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid id, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);

    [DllImport("gdi32.dll")] private static extern int GetObject(nint handle, int size, out NativeBitmap bitmap);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
}
