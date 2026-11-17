using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace QiDock.Services;

/// <summary>
/// The "genie" effect for windows opened from or minimized into the dock.
/// Windows cannot warp another process's window, so the effect is staged: the window's image is
/// captured with PrintWindow, the real window is made fully transparent (layered alpha 0) while it
/// restores or minimizes, and the image is drawn on a GPU-composited overlay as a textured mesh that
/// pours out of / into the dock tile. The real window is made opaque again as soon as the effect ends.
/// </summary>
public sealed class GenieAnimator : IDisposable
{
    // A fine grid keeps the per-triangle (affine) texture mapping from shearing the content.
    private const int Rows = 96;
    private const int Columns = 24;
    private static readonly TimeSpan EffectDuration = TimeSpan.FromMilliseconds(480);
    private static readonly TimeSpan LaunchWindow = TimeSpan.FromSeconds(8);

    private readonly Dictionary<nint, int> _hiddenWindows = [];
    // Image taken when a window was minimized, reused to start its restore instantly.
    private const int MaxTextureSide = 1600;
    private readonly Dictionary<nint, (BitmapSource Image, NativeRect Frame)> _snapshots = [];
    private readonly List<nint> _snapshotOrder = [];
    private Window? _overlay;
    private nint _overlayHandle;
    private Viewport3D? _viewport;
    private OrthographicCamera? _camera;
    private MeshGeometry3D? _mesh;
    private DiffuseMaterial? _material;
    private Rect _fromRect;
    private Rect _iconRect;
    private bool _disposed;

    private string? _pendingLaunchPath;
    private Rect _pendingLaunchIcon;
    private DateTime _pendingLaunchUntil;
    private nint _showHook;
    private WinEventDelegate? _hookProc;
    private DispatcherTimer? _launchTimer;

    public bool IsBusy { get; private set; }

    /// <summary>Restores a minimized window, pouring it out of the dock tile.</summary>
    public bool TryRestore(nint window, Rect iconPixels, Func<nint, bool> activate)
    {
        if (_disposed || IsBusy || !IsWindow(window) || !IsIconic(window)) return false;
        if (!TryHide(window, out var originalStyle)) return false;
        IsBusy = true;
        if (!activate(window))
        {
            Reveal(window, originalStyle);
            IsBusy = false;
            return false;
        }

        _ = RunOpenAsync(window, iconPixels, originalStyle);
        return true;
    }

    /// <summary>Minimizes a visible window, sucking it into the dock tile.</summary>
    public bool TryMinimize(nint window, Rect iconPixels)
    {
        if (_disposed || IsBusy || !IsWindow(window) || IsIconic(window) || !IsWindowVisible(window)) return false;
        var image = Capture(window, out var frame);
        if (image is null) return false;
        IsBusy = true;
        _snapshots[window] = (image, frame);
        _snapshotOrder.Remove(window);
        _snapshotOrder.Add(window);
        while (_snapshotOrder.Count > 3)
        {
            _snapshots.Remove(_snapshotOrder[0]);
            _snapshotOrder.RemoveAt(0);
        }

        _ = RunMinimizeAsync(window, image, frame, iconPixels);
        return true;
    }

    /// <summary>The next app window of <paramref name="executablePath"/> that appears opens with the effect.</summary>
    public void ExpectLaunch(string executablePath, Rect iconPixels)
    {
        if (_disposed) return;
        _pendingLaunchPath = executablePath;
        _pendingLaunchIcon = iconPixels;
        _pendingLaunchUntil = DateTime.UtcNow + LaunchWindow;
        if (_showHook == 0)
        {
            _hookProc ??= OnWindowShown;
            _showHook = SetWinEventHook(EventObjectShow, EventObjectShow, 0, _hookProc, 0, 0,
                WinEventOutOfContext | WinEventSkipOwnProcess);
        }

        _launchTimer ??= new DispatcherTimer { Interval = LaunchWindow };
        _launchTimer.Tick -= LaunchTimer_Tick;
        _launchTimer.Tick += LaunchTimer_Tick;
        _launchTimer.Stop();
        _launchTimer.Start();
    }

    private void LaunchTimer_Tick(object? sender, EventArgs e) => StopWaitingForLaunch();

    private void StopWaitingForLaunch()
    {
        _launchTimer?.Stop();
        _pendingLaunchPath = null;
        if (_showHook != 0)
        {
            UnhookWinEvent(_showHook);
            _showHook = 0;
        }
    }

    private void OnWindowShown(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (objectId != 0 || window == 0 || _pendingLaunchPath is null) return;
        if (DateTime.UtcNow > _pendingLaunchUntil)
        {
            StopWaitingForLaunch();
            return;
        }

        if (GetAncestor(window, GaRoot) != window || GetWindow(window, GwOwner) != 0 || !IsWindowVisible(window)) return;
        if ((GetWindowLong(window, GwlExStyle) & WsExToolWindow) != 0) return;
        if (!GetWindowRect(window, out var rect) || rect.Right - rect.Left < 240 || rect.Bottom - rect.Top < 160) return;
        if (!string.Equals(NativeWindowTracker.ExecutablePathOf(window), _pendingLaunchPath, StringComparison.OrdinalIgnoreCase)) return;

        var icon = _pendingLaunchIcon;
        StopWaitingForLaunch();
        if (IsBusy || IsIconic(window) || !TryHide(window, out var originalStyle)) return;
        IsBusy = true;
        _ = RunOpenAsync(window, icon, originalStyle);
    }

    private async Task RunOpenAsync(nint window, Rect iconPixels, int originalStyle)
    {
        try
        {
            // The window is invisible but restored; give it a moment to paint before capturing.
            BitmapSource? image = null;
            NativeRect frame = default;
            if (_snapshots.Remove(window, out var snapshot))
            {
                _snapshotOrder.Remove(window);
                // Only valid when the window comes back where it was.
                if (DwmGetWindowAttribute(window, DwmwaExtendedFrameBounds, out var current, Marshal.SizeOf<NativeRect>()) == 0
                    && current.Left == snapshot.Frame.Left && current.Top == snapshot.Frame.Top
                    && current.Right == snapshot.Frame.Right && current.Bottom == snapshot.Frame.Bottom)
                {
                    image = snapshot.Image;
                    frame = snapshot.Frame;
                }
            }

            for (var attempt = 0; attempt < 8 && image is null; attempt++)
            {
                await Task.Delay(attempt == 0 ? 35 : 45);
                if (!IsWindow(window) || IsIconic(window)) return;
                image = Capture(window, out frame);
            }

            if (image is null) return;
            Prepare(window, image, frame, iconPixels);
            UpdateMesh(1);
            ShowOverlay();
            await AnimateAsync(1, 0);
            // Real window back first, then drop the overlay that shows the identical image.
            Reveal(window, originalStyle);
            await NextFrameAsync();
            await Task.Delay(16);
        }
        finally
        {
            Reveal(window, originalStyle);
            HideOverlay();
            IsBusy = false;
        }
    }

    private async Task RunMinimizeAsync(nint window, BitmapSource image, NativeRect frame, Rect iconPixels)
    {
        var hidden = false;
        var originalStyle = 0;
        try
        {
            Prepare(window, image, frame, iconPixels);
            UpdateMesh(0);
            ShowOverlay();
            // The overlay is freshly created; let it put its first frame on screen before the real
            // window disappears, so nothing flickers.
            await NextFrameAsync();
            await NextFrameAsync();
            await NextFrameAsync();
            await Task.Delay(15);
            hidden = TryHide(window, out originalStyle);
            ShowWindowAsync(window, SwMinimize);
            await AnimateAsync(0, 1);
        }
        finally
        {
            HideOverlay();
            for (var i = 0; i < 20 && IsWindow(window) && !IsIconic(window); i++)
            {
                await Task.Delay(25);
            }

            if (hidden) Reveal(window, originalStyle);
            IsBusy = false;
        }
    }

    private Task AnimateAsync(double from, double to)
    {
        var completion = new TaskCompletionSource();
        var clock = Stopwatch.StartNew();
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            var t = Math.Min(1, clock.Elapsed / EffectDuration);
            UpdateMesh(from + (to - from) * t);
            if (t >= 1)
            {
                CompositionTarget.Rendering -= handler;
                completion.TrySetResult();
            }
        };
        CompositionTarget.Rendering += handler;
        return completion.Task;
    }

    private static Task NextFrameAsync()
    {
        var completion = new TaskCompletionSource();
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            CompositionTarget.Rendering -= handler;
            completion.TrySetResult();
        };
        CompositionTarget.Rendering += handler;
        return completion.Task;
    }

    /// <summary>
    /// Shape at progress <paramref name="p"/>: 0 = the window itself, 1 = inside the dock tile.
    /// First the lower edge bends toward the tile (an S-curve), then the image slides down the curve.
    /// </summary>
    private void UpdateMesh(double p)
    {
        if (_mesh is null || _viewport is null) return;
        var w = _fromRect;
        var icon = _iconRect;
        var bend = Smooth(Clamp01(p / 0.42));
        var bottomT = EaseOutCubic(Clamp01(p / 0.62));
        var topT = EaseInCubic(Clamp01((p - 0.16) / 0.84));
        var top = Lerp(w.Top, icon.Top, topT);
        var bottom = Lerp(w.Bottom, icon.Bottom, bottomT);
        var span = Math.Max(1, icon.Top - w.Top);

        var points = new Point3DCollection((Rows + 1) * (Columns + 1));
        for (var row = 0; row <= Rows; row++)
        {
            var y = Lerp(top, bottom, (double)row / Rows);
            var shape = bend * Smooth(Clamp01((y - w.Top) / span));
            var left = Lerp(w.Left, icon.Left, shape);
            var right = Lerp(w.Right, icon.Right, shape);
            for (var column = 0; column <= Columns; column++)
            {
                points.Add(new Point3D(Lerp(left, right, (double)column / Columns), -y, 0));
            }
        }

        points.Freeze();
        _mesh.Positions = points;
        _viewport.Opacity = p > 0.86 ? 1 - Clamp01((p - 0.86) / 0.14) * 0.85 : 1;
    }

    private void Prepare(nint window, BitmapSource image, NativeRect frame, Rect iconPixels)
    {
        EnsureOverlay();
        var monitorInfo = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(MonitorFromWindow(window, MonitorDefaultToNearest), ref monitorInfo);
        var monitor = monitorInfo.rcMonitor;
        SetWindowPos(_overlayHandle, HwndTopmost, monitor.Left, monitor.Top,
            monitor.Right - monitor.Left, monitor.Bottom - monitor.Top, SwpNoActivate);
        var dpi = GetDpiForWindow(_overlayHandle);
        var scale = (dpi == 0 ? 96 : dpi) / 96d;
        var width = (monitor.Right - monitor.Left) / scale;
        var height = (monitor.Bottom - monitor.Top) / scale;
        _camera!.Width = width;
        _camera.Position = new Point3D(width / 2, -height / 2, 10);
        _fromRect = new Rect((frame.Left - monitor.Left) / scale, (frame.Top - monitor.Top) / scale,
            (frame.Right - frame.Left) / scale, (frame.Bottom - frame.Top) / scale);
        _iconRect = new Rect((iconPixels.Left - monitor.Left) / scale, (iconPixels.Top - monitor.Top) / scale,
            iconPixels.Width / scale, iconPixels.Height / scale);
        _material!.Brush = new ImageBrush(image);
    }

    private void ShowOverlay()
    {
        if (_overlayHandle == 0) return;
        SetWindowPos(_overlayHandle, HwndTopmost, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize | SwpShowWindow);
    }

    private void HideOverlay()
    {
        if (_overlayHandle == 0) return;
        SetWindowPos(_overlayHandle, 0, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize | SwpNoZOrder | SwpHideWindow);
        // Destroy the overlay between effects: WPF keeps a full-screen 3D surface's graphics memory
        // (hundreds of MB) for as long as the window exists. Recreating it costs a few milliseconds.
        var overlay = _overlay;
        _overlay = null;
        _overlayHandle = 0;
        _viewport = null;
        _camera = null;
        _mesh = null;
        _material = null;
        overlay?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => overlay.Close());
        // Hand the texture and render surfaces back right away instead of whenever the GC next runs.
        overlay?.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        });
    }

    private void EnsureOverlay()
    {
        if (_overlay is not null) return;
        var textureCoordinates = new PointCollection((Rows + 1) * (Columns + 1));
        var indices = new Int32Collection(Rows * Columns * 6);
        for (var row = 0; row <= Rows; row++)
        {
            var v = (double)row / Rows;
            for (var column = 0; column <= Columns; column++)
            {
                textureCoordinates.Add(new Point((double)column / Columns, v));
                if (row == Rows || column == Columns) continue;
                var a = row * (Columns + 1) + column;
                var below = a + Columns + 1;
                indices.Add(a); indices.Add(below); indices.Add(a + 1);
                indices.Add(a + 1); indices.Add(below); indices.Add(below + 1);
            }
        }

        _mesh = new MeshGeometry3D { TextureCoordinates = textureCoordinates, TriangleIndices = indices };
        _material = new DiffuseMaterial();
        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Colors.White));
        group.Children.Add(new GeometryModel3D(_mesh, _material) { BackMaterial = _material });
        _camera = new OrthographicCamera
        {
            LookDirection = new Vector3D(0, 0, -1),
            UpDirection = new Vector3D(0, 1, 0),
            NearPlaneDistance = 0.1,
            FarPlaneDistance = 100
        };
        _viewport = new Viewport3D { Camera = _camera, IsHitTestVisible = false };
        _viewport.Children.Add(new ModelVisual3D { Content = group });
        RenderOptions.SetBitmapScalingMode(_viewport, BitmapScalingMode.Linear);
        // No multisampling: an MSAA full-screen 3D surface is costly on integrated GPUs; the dense mesh
        // and the short duration keep the edges looking clean.
        RenderOptions.SetEdgeMode(_viewport, EdgeMode.Aliased);

        // Not a layered window: the client area is composited by DWM with per-pixel alpha
        // (frame extended over the whole window), which stays on the GPU even at full-screen size.
        _overlay = new Window
        {
            Title = "QiDock Genie",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = false,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            Topmost = true,
            Left = -32000,
            Top = -32000,
            Width = 1,
            Height = 1,
            Content = _viewport
        };
        _overlay.SourceInitialized += (_, _) =>
        {
            var source = (HwndSource)PresentationSource.FromVisual(_overlay);
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            var handle = source.Handle;
            SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle)
                | WsExToolWindow | WsExNoActivate | WsExTransparent);
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(handle, ref margins);
            var noRounding = 1;
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref noRounding, sizeof(int));
            var noBorder = unchecked((int)0xFFFFFFFE);
            DwmSetWindowAttribute(handle, DwmwaBorderColor, ref noBorder, sizeof(int));
            var noTransitions = 1;
            DwmSetWindowAttribute(handle, DwmwaTransitionsForceDisabled, ref noTransitions, sizeof(int));
        };
        _overlay.Show();
        _overlayHandle = new WindowInteropHelper(_overlay).Handle;
        SetWindowPos(_overlayHandle, 0, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize | SwpNoZOrder | SwpHideWindow);
    }

    private bool TryHide(nint window, out int originalStyle)
    {
        originalStyle = GetWindowLong(window, GwlExStyle);
        // Windows that are already layered manage their own alpha; leave them alone.
        if (originalStyle == 0 || (originalStyle & WsExLayered) != 0) return false;
        SetWindowLong(window, GwlExStyle, originalStyle | WsExLayered);
        if ((GetWindowLong(window, GwlExStyle) & WsExLayered) == 0) return false;
        if (!SetLayeredWindowAttributes(window, 0, 0, LwaAlpha))
        {
            SetWindowLong(window, GwlExStyle, originalStyle);
            return false;
        }

        _hiddenWindows[window] = originalStyle;
        return true;
    }

    private void Reveal(nint window, int originalStyle)
    {
        if (!_hiddenWindows.Remove(window) || !IsWindow(window)) return;
        SetLayeredWindowAttributes(window, 0, 255, LwaAlpha);
        SetWindowLong(window, GwlExStyle, originalStyle);
        RedrawWindow(window, 0, 0, RdwErase | RdwInvalidate | RdwFrame | RdwAllChildren);
    }

    /// <summary>Captures the visible frame of a window (no invisible resize borders) as an opaque bitmap.</summary>
    private static BitmapSource? Capture(nint window, out NativeRect frame)
    {
        frame = default;
        if (!GetWindowRect(window, out var bounds)) return null;
        if (DwmGetWindowAttribute(window, DwmwaExtendedFrameBounds, out frame, Marshal.SizeOf<NativeRect>()) != 0)
        {
            frame = bounds;
        }

        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        var offsetX = Math.Clamp(frame.Left - bounds.Left, 0, width);
        var offsetY = Math.Clamp(frame.Top - bounds.Top, 0, height);
        var frameWidth = Math.Min(frame.Right - frame.Left, width - offsetX);
        var frameHeight = Math.Min(frame.Bottom - frame.Top, height - offsetY);
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384 || frameWidth <= 8 || frameHeight <= 8) return null;
        frame = new NativeRect(bounds.Left + offsetX, bounds.Top + offsetY,
            bounds.Left + offsetX + frameWidth, bounds.Top + offsetY + frameHeight);

        var screenDc = GetDC(0);
        var memoryDc = CreateCompatibleDC(screenDc);
        var info = new BitmapInfoHeader
        {
            biSize = 40,
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32
        };
        var dib = CreateDIBSection(screenDc, ref info, 0, out var bits, 0, 0);
        var previous = dib != 0 ? SelectObject(memoryDc, dib) : 0;
        try
        {
            if (dib == 0 || bits == 0 || !PrintWindow(window, memoryDc, PwRenderFullContent)) return null;
            GdiFlush();
            var pixels = new int[frameWidth * frameHeight];
            for (var row = 0; row < frameHeight; row++)
            {
                Marshal.Copy(bits + ((offsetY + row) * width + offsetX) * 4, pixels, row * frameWidth, frameWidth);
            }

            var samples = 0;
            var painted = 0;
            for (var i = 0; i < pixels.Length; i++)
            {
                pixels[i] |= unchecked((int)0xFF000000);
                if (i % 97 == 0)
                {
                    samples++;
                    if ((pixels[i] & 0xFFFFFF) != 0) painted++;
                }
            }

            // Nothing painted yet (all black): let the caller retry.
            if (painted * 50 < samples) return null;
            if (!IsZoomed(window))
            {
                var dpi = GetDpiForWindow(window);
                RoundCorners(pixels, frameWidth, frameHeight, (int)Math.Round(8 * (dpi == 0 ? 96 : dpi) / 96d));
            }

            BitmapSource bitmap = BitmapSource.Create(frameWidth, frameHeight, 96, 96, PixelFormats.Pbgra32, null, pixels, frameWidth * 4);
            // The window is only ever seen bent and moving, so a texture up to MaxTextureSide is plenty;
            // a full 4K-ish window would otherwise hold ~20 MB per copy.
            var shrink = Math.Min(1, MaxTextureSide / (double)Math.Max(frameWidth, frameHeight));
            if (shrink < 0.99)
            {
                var scaled = new TransformedBitmap(bitmap, new ScaleTransform(shrink, shrink));
                var scaledWidth = scaled.PixelWidth;
                var scaledHeight = scaled.PixelHeight;
                var scaledPixels = new int[scaledWidth * scaledHeight];
                scaled.CopyPixels(scaledPixels, scaledWidth * 4, 0);
                bitmap = BitmapSource.Create(scaledWidth, scaledHeight, 96, 96, PixelFormats.Pbgra32, null, scaledPixels, scaledWidth * 4);
            }

            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            if (previous != 0) SelectObject(memoryDc, previous);
            if (dib != 0) DeleteObject(dib);
            DeleteDC(memoryDc);
            ReleaseDC(0, screenDc);
        }
    }

    private static void RoundCorners(int[] pixels, int width, int height, int radius)
    {
        radius = Math.Min(radius, Math.Min(width, height) / 2);
        for (var y = 0; y < radius; y++)
        {
            for (var x = 0; x < radius; x++)
            {
                var dx = radius - x - 0.5;
                var dy = radius - y - 0.5;
                if (dx * dx + dy * dy <= radius * radius) continue;
                pixels[y * width + x] = 0;
                pixels[y * width + (width - 1 - x)] = 0;
                pixels[(height - 1 - y) * width + x] = 0;
                pixels[(height - 1 - y) * width + (width - 1 - x)] = 0;
            }
        }
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);
    private static double Lerp(double from, double to, double t) => from + (to - from) * t;
    private static double Smooth(double t) => t * t * (3 - 2 * t);
    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);
    private static double EaseInCubic(double t) => t * t * t;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopWaitingForLaunch();
        foreach (var (window, style) in _hiddenWindows.ToList())
        {
            Reveal(window, style);
        }

        _overlay?.Close();
        _overlay = null;
    }

    private const int GwlExStyle = -20;
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint LwaAlpha = 0x2;
    private const uint PwRenderFullContent = 0x2;
    private const int SwMinimize = 6;
    private const uint SwpNoSize = 0x1;
    private const uint SwpNoMove = 0x2;
    private const uint SwpNoZOrder = 0x4;
    private const uint SwpNoActivate = 0x10;
    private const uint SwpShowWindow = 0x40;
    private const uint SwpHideWindow = 0x80;
    private static readonly nint HwndTopmost = new(-1);
    private const uint RdwInvalidate = 0x1;
    private const uint RdwErase = 0x4;
    private const uint RdwAllChildren = 0x80;
    private const uint RdwFrame = 0x400;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaTransitionsForceDisabled = 3;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const uint MonitorDefaultToNearest = 2;
    private const uint EventObjectShow = 0x8002;
    private const uint WinEventOutOfContext = 0x0;
    private const uint WinEventSkipOwnProcess = 0x2;
    private const uint GaRoot = 2;
    private const uint GwOwner = 4;

    private delegate void WinEventDelegate(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
        // Room for the (unused) colour table of a BITMAPINFO.
        public uint bmiColors;
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RedrawWindow(nint window, nint rect, nint region, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventDelegate callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint gdiObject);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint gdiObject);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out NativeRect value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint window, ref Margins margins);
}
