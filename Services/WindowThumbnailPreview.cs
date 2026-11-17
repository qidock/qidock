using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using QiDock.Models;

namespace QiDock.Services;

public sealed class WindowThumbnailPreview : IDisposable
{
    private const uint DwmThumbnailRectDestination = 0x00000001;
    private const uint DwmThumbnailOpacity = 0x00000004;
    private const uint DwmThumbnailVisible = 0x00000008;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly nint HwndTopmost = new(-1);

    private readonly Style _closeButtonStyle;
    private readonly Window _window;
    private readonly Window _closeButtonWindow;
    private readonly StackPanel _surfaceContainer;
    private readonly StackPanel _overlayContainer;
    private readonly TextBlock _placeholder;
    private readonly DispatcherTimer _entranceTimer;
    private readonly double _entranceDurationMs;
    private readonly List<(nint Source, nint Thumbnail, NativeRect TargetRect)> _activeThumbnails = [];
    private long _entranceStarted;
    private nint _destinationHandle;
    private nint _sourceHandle;
    private bool _disposed;
    private bool _windowClosed;
    private bool _closeWindowClosed;

    public WindowThumbnailPreview(Style closeButtonStyle)
    {
        _closeButtonStyle = closeButtonStyle;
        var resources = Application.Current.Resources;
        var popupBrush = (Brush)resources["SurfacePopup"];
        var strokeBrush = (Brush)resources["StrokeSubtle"];
        _entranceDurationMs = (double)resources["PreviewDurationMs"];
        _entranceTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _entranceTimer.Tick += EntranceTimer_Tick;

        _placeholder = new TextBlock
        {
            Text = UiText.Get("PreviewUnavailable"),
            Foreground = (Brush)resources["TextSecondary"],
            FontFamily = (FontFamily)resources["InterfaceFont"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _surfaceContainer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(6)
        };

        var surfaceGrid = new Grid();
        surfaceGrid.Children.Add(_surfaceContainer);
        surfaceGrid.Children.Add(_placeholder);

        var surface = new Border
        {
            Background = popupBrush,
            BorderThickness = new Thickness(1),
            BorderBrush = strokeBrush,
            CornerRadius = new CornerRadius(10),
            Child = surfaceGrid
        };
        surface.SetResourceReference(Border.BackgroundProperty, "SurfacePopup");
        surface.SetResourceReference(Border.BorderBrushProperty, "StrokeSubtle");
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        _window = new Window
        {
            Width = (double)resources["PreviewWidth"],
            Height = (double)resources["PreviewHeight"],
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Background = popupBrush,
            UseLayoutRounding = true,
            Cursor = System.Windows.Input.Cursors.Hand,
            Content = surface
        };
        _window.Closed += Window_Closed;
        _window.SetResourceReference(Window.BackgroundProperty, "SurfacePopup");
        _window.SourceInitialized += PreviewWindow_SourceInitialized;
        _window.MouseEnter += Window_MouseEnter;
        _window.MouseLeave += Window_MouseLeave;

        _overlayContainer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(6)
        };

        _closeButtonWindow = new Window
        {
            Width = _window.Width,
            Height = _window.Height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = _overlayContainer
        };
        _closeButtonWindow.Closed += CloseButtonWindow_Closed;
        _closeButtonWindow.SourceInitialized += PreviewWindow_SourceInitialized;
        _closeButtonWindow.MouseEnter += Window_MouseEnter;
        _closeButtonWindow.MouseLeave += Window_MouseLeave;
    }

    public event EventHandler? PointerEntered;

    public event EventHandler? PointerLeft;

    public event EventHandler? PreviewClicked;

    public event EventHandler? CloseRequested;

    public bool IsVisible => _window.IsVisible;
    public bool AnimationsEnabled { get; set; } = true;

    public nint SourceHandle => _sourceHandle;

    public IReadOnlyList<nint> ActiveHandles => _activeThumbnails.Select(t => t.Source).ToList();

    public bool ShowFor(nint sourceHandle, FrameworkElement anchor, string windowTitle, BitmapSource? icon)
    {
        return ShowForWindows([new TrackedWindow(sourceHandle, windowTitle, null)], anchor, icon);
    }

    public bool ShowForWindows(IReadOnlyList<TrackedWindow> windows, FrameworkElement anchor, BitmapSource? defaultIcon)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _placeholder.Text = UiText.Get("PreviewUnavailable");
        var validWindows = windows.Where(w => w.Handle != 0 && IsWindow(w.Handle)).Take(6).ToList();
        if (validWindows.Count == 0)
        {
            Hide();
            return false;
        }

        var isSingle = validWindows.Count == 1;
        var resources = Application.Current.Resources;
        var popupBrush = (Brush)resources["SurfacePopup"];
        var textBrush = (Brush)resources["TextPrimary"];
        var strokeBrush = (Brush)resources["StrokeSubtle"];
        var focusStroke = (Brush)resources["StrokeFocus"];

        double cardWidth = isSingle ? (double)resources["PreviewWidth"] : 250;
        double cardHeight = isSingle ? (double)resources["PreviewHeight"] : 180;
        double headerHeight = isSingle ? (double)resources["PreviewHeaderHeight"] : 36;
        double cardGap = 8;
        double margin = 6;

        var totalWidth = validWindows.Count * cardWidth + (validWindows.Count - 1) * cardGap + margin * 2;
        var totalHeight = cardHeight + margin * 2;

        _window.Width = totalWidth;
        _window.Height = totalHeight;
        _closeButtonWindow.Width = totalWidth;
        _closeButtonWindow.Height = totalHeight;

        var animateEntrance = AnimationsEnabled && SystemParameters.ClientAreaAnimation
            && (!_window.IsVisible || !_activeThumbnails.Select(t => t.Source).SequenceEqual(validWindows.Select(w => w.Handle)));

        _entranceTimer.Stop();
        _closeButtonWindow.BeginAnimation(UIElement.OpacityProperty, null);
        _closeButtonWindow.Opacity = 1;

        ReleaseThumbnails();
        _sourceHandle = validWindows[0].Handle;

        _surfaceContainer.Children.Clear();
        _overlayContainer.Children.Clear();

        if (!_window.IsVisible)
        {
            _placeholder.Visibility = Visibility.Visible;
            _window.Show();
        }

        _destinationHandle = new WindowInteropHelper(_window).Handle;
        if (_destinationHandle == 0 || !PositionWindow(anchor, totalWidth, totalHeight))
        {
            _placeholder.Visibility = Visibility.Visible;
            return false;
        }

        if (!PositionOverlayWindow())
        {
            _placeholder.Visibility = Visibility.Visible;
            return false;
        }

        var dpi = GetDpiForWindow(_destinationHandle);
        if (dpi == 0) dpi = 96;
        var scale = dpi / 96d;

        for (var i = 0; i < validWindows.Count; i++)
        {
            var tracked = validWindows[i];
            var handle = tracked.Handle;

            // Surface card frame
            var surfaceCard = new Border
            {
                Width = cardWidth,
                Height = cardHeight,
                Background = new SolidColorBrush(Color.FromArgb(1, 18, 21, 25)),
                BorderBrush = strokeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, i < validWindows.Count - 1 ? cardGap : 0, 0)
            };
            _surfaceContainer.Children.Add(surfaceCard);

            // Overlay card
            var closeBtn = new Button
            {
                Style = _closeButtonStyle,
                Content = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M2,2 L10,10 M10,2 L2,10"),
                    Stroke = textBrush,
                    StrokeThickness = 1.5,
                    Width = 10,
                    Height = 10
                },
                Width = 24,
                Height = 24,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = UiText.Get("CloseWindow")
            };
            AutomationProperties.SetName(closeBtn, UiText.Get("CloseWindow"));
            closeBtn.Click += (_, e) =>
            {
                e.Handled = true;
                _sourceHandle = handle;
                CloseRequested?.Invoke(this, EventArgs.Empty);
            };

            var iconImg = new Image
            {
                Width = 18,
                Height = 18,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 6, 0),
                Source = defaultIcon
            };

            var titleBlock = new TextBlock
            {
                Text = tracked.Title,
                Foreground = textBrush,
                FontFamily = (FontFamily)resources["InterfaceFont"],
                FontSize = (double)resources["TextSizeCaption"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };

            var headerPanel = new DockPanel
            {
                LastChildFill = true,
                Margin = new Thickness(8, 4, 6, 4),
                Children = { closeBtn, iconImg, titleBlock }
            };
            DockPanel.SetDock(closeBtn, Dock.Right);
            DockPanel.SetDock(iconImg, Dock.Left);

            var headerSurface = new Border
            {
                Height = headerHeight,
                Background = popupBrush,
                BorderBrush = strokeBrush,
                BorderThickness = new Thickness(1, 1, 1, 0),
                CornerRadius = new CornerRadius(8, 8, 0, 0),
                Child = headerPanel
            };

            var bodySurface = new Border
            {
                Height = cardHeight - headerHeight,
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                BorderBrush = strokeBrush,
                BorderThickness = new Thickness(1, 0, 1, 1),
                CornerRadius = new CornerRadius(0, 0, 8, 8)
            };

            var cardContent = new StackPanel();
            cardContent.Children.Add(headerSurface);
            cardContent.Children.Add(bodySurface);

            var overlayCard = new Border
            {
                Width = cardWidth,
                Height = cardHeight,
                Background = Brushes.Transparent,
                Margin = new Thickness(0, 0, i < validWindows.Count - 1 ? cardGap : 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = cardContent
            };

            overlayCard.MouseEnter += (_, _) =>
            {
                headerSurface.BorderBrush = focusStroke;
                bodySurface.BorderBrush = focusStroke;
            };
            overlayCard.MouseLeave += (_, _) =>
            {
                headerSurface.BorderBrush = strokeBrush;
                bodySurface.BorderBrush = strokeBrush;
            };
            overlayCard.MouseLeftButtonDown += (_, e) =>
            {
                if (!closeBtn.IsMouseOver)
                {
                    e.Handled = true;
                    _sourceHandle = handle;
                    PreviewClicked?.Invoke(this, EventArgs.Empty);
                }
            };

            _overlayContainer.Children.Add(overlayCard);

            if (DwmRegisterThumbnail(_destinationHandle, handle, out var thumbnail) == 0 && thumbnail != 0)
            {
                if (DwmQueryThumbnailSourceSize(thumbnail, out var sourceSize) == 0
                    && sourceSize.Width > 0 && sourceSize.Height > 0)
                {
                    var cardLeft = margin + i * (cardWidth + cardGap);
                    var cardTop = margin + headerHeight;
                    var cLeft = (int)Math.Round((cardLeft + 2) * scale);
                    var cTop = (int)Math.Round((cardTop + 2) * scale);
                    var cRight = (int)Math.Round((cardLeft + cardWidth - 2) * scale);
                    var cBottom = (int)Math.Round((margin + cardHeight - 2) * scale);

                    var clientRect = new NativeRect(cLeft, cTop, cRight, cBottom);
                    var destination = FitThumbnail(sourceSize, clientRect);
                    var properties = new DwmThumbnailProperties
                    {
                        Flags = DwmThumbnailRectDestination | DwmThumbnailOpacity | DwmThumbnailVisible,
                        Destination = destination,
                        Opacity = byte.MaxValue,
                        Visible = true,
                        SourceClientAreaOnly = false
                    };

                    if (animateEntrance)
                    {
                        var initialProps = properties;
                        initialProps.Opacity = 0;
                        DwmUpdateThumbnailProperties(thumbnail, ref initialProps);
                    }
                    else
                    {
                        DwmUpdateThumbnailProperties(thumbnail, ref properties);
                    }

                    _activeThumbnails.Add((handle, thumbnail, destination));
                }
                else
                {
                    _ = DwmUnregisterThumbnail(thumbnail);
                }
            }
        }

        _placeholder.Visibility = _activeThumbnails.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (animateEntrance)
        {
            _entranceStarted = Stopwatch.GetTimestamp();
            _closeButtonWindow.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(_entranceDurationMs))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            _entranceTimer.Start();
        }

        return true;
    }

    private void EntranceTimer_Tick(object? sender, EventArgs e)
    {
        if (_activeThumbnails.Count == 0 || !_window.IsVisible)
        {
            _entranceTimer.Stop();
            return;
        }

        var progress = Math.Clamp(Stopwatch.GetElapsedTime(_entranceStarted).TotalMilliseconds / _entranceDurationMs, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        var opacity = (byte)Math.Round(byte.MaxValue * eased);

        foreach (var (_, thumb, target) in _activeThumbnails)
        {
            var insetX = (int)Math.Round((target.Right - target.Left) * 0.025 * (1 - eased));
            var insetY = (int)Math.Round((target.Bottom - target.Top) * 0.025 * (1 - eased));
            var frame = new DwmThumbnailProperties
            {
                Flags = DwmThumbnailRectDestination | DwmThumbnailOpacity | DwmThumbnailVisible,
                Destination = new NativeRect(target.Left + insetX, target.Top + insetY,
                    target.Right - insetX, target.Bottom - insetY),
                Opacity = opacity,
                Visible = true
            };
            DwmUpdateThumbnailProperties(thumb, ref frame);
        }

        if (progress >= 1) _entranceTimer.Stop();
    }

    public void Hide()
    {
        _entranceTimer.Stop();
        _closeButtonWindow.BeginAnimation(UIElement.OpacityProperty, null);
        ReleaseThumbnails();
        _sourceHandle = 0;
        _surfaceContainer.Children.Clear();
        _overlayContainer.Children.Clear();
        if (_closeButtonWindow.IsVisible)
        {
            _closeButtonWindow.Hide();
        }

        if (_window.IsVisible)
        {
            _window.Hide();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Hide();
        _disposed = true;
        _entranceTimer.Tick -= EntranceTimer_Tick;
        _window.Closed -= Window_Closed;
        _closeButtonWindow.Closed -= CloseButtonWindow_Closed;
        _closeButtonWindow.MouseEnter -= Window_MouseEnter;
        _closeButtonWindow.MouseLeave -= Window_MouseLeave;
        if (!_closeWindowClosed)
        {
            _closeButtonWindow.Close();
        }

        if (!_windowClosed)
        {
            _window.Close();
        }
    }

    private bool PositionWindow(FrameworkElement anchor, double totalWidth, double totalHeight)
    {
        // The anchor's inner side (its top on a bottom dock): the dock is turned, the anchor's frame is not.
        var anchorTop = anchor.PointToScreen(new Point(anchor.ActualWidth / 2, 0));
        var dpi = GetDpiForWindow(_destinationHandle);
        if (dpi == 0)
        {
            return false;
        }

        var width = (int)Math.Ceiling(totalWidth * dpi / 96d);
        var height = (int)Math.Ceiling(totalHeight * dpi / 96d);
        var anchorWindow = Window.GetWindow(anchor);
        var anchorHandle = anchorWindow is null ? _destinationHandle : new WindowInteropHelper(anchorWindow).Handle;
        var monitor = MonitorFromWindow(anchorHandle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return false;
        }

        width = Math.Min(width, monitorInfo.Monitor.Right - monitorInfo.Monitor.Left);
        height = Math.Min(height, monitorInfo.Monitor.Bottom - monitorInfo.Monitor.Top);

        var left = (int)Math.Round(anchorTop.X) - width / 2;
        left = Math.Clamp(left, monitorInfo.Monitor.Left, monitorInfo.Monitor.Right - width);
        var gapDips = Application.Current?.Resources["PreviewGap"] is double g ? g : 18.0;
        // The hovered dock tile is magnified upward; keep the preview clear of its grown top edge.
        var resources = Application.Current?.Resources;
        if (resources?["DockTileSize"] is double tile && resources["DockHoverScale"] is double hoverScale && hoverScale > 1)
        {
            gapDips += tile * (hoverScale - 1);
        }
        var gap = (int)Math.Round(gapDips * dpi / 96d);
        var monitorBox = monitorInfo.Monitor;
        int top;
        switch (DockGeometry.Edge)
        {
            case Models.DockEdge.Top:
                top = Math.Min((int)Math.Round(anchorTop.Y) + gap, monitorBox.Bottom - height);
                break;
            case Models.DockEdge.Left:
            case Models.DockEdge.Right:
                // Beside the tile, centred on it, on the screen's inner side.
                top = Math.Clamp((int)Math.Round(anchorTop.Y) - height / 2, monitorBox.Top, monitorBox.Bottom - height);
                left = DockGeometry.Edge == Models.DockEdge.Left
                    ? Math.Min((int)Math.Round(anchorTop.X) + gap, monitorBox.Right - width)
                    : Math.Max((int)Math.Round(anchorTop.X) - width - gap, monitorBox.Left);
                break;
            default:
                top = (int)Math.Round(anchorTop.Y) - height - gap;
                if (top < monitorBox.Top)
                {
                    var anchorHeight = (int)Math.Ceiling(anchor.ActualHeight * dpi / 96d);
                    top = (int)Math.Round(anchorTop.Y) + anchorHeight + gap;
                }
                top = Math.Min(top, monitorBox.Bottom - height);
                break;
        }
        return SetWindowPos(_destinationHandle, HwndTopmost, left, top, width, height, SwpNoActivate);
    }

    private bool PositionOverlayWindow()
    {
        var overlayInterop = new WindowInteropHelper(_closeButtonWindow);
        if (!_closeButtonWindow.IsVisible)
        {
            overlayInterop.Owner = _destinationHandle;
            _closeButtonWindow.Show();
        }

        var overlayHandle = overlayInterop.Handle;
        if (overlayHandle == 0 || !GetWindowRect(_destinationHandle, out var previewRect))
        {
            return false;
        }

        var width = previewRect.Right - previewRect.Left;
        var height = previewRect.Bottom - previewRect.Top;
        return SetWindowPos(overlayHandle, HwndTopmost,
            previewRect.Left, previewRect.Top, width, height,
            SwpNoActivate | SwpNoOwnerZOrder);
    }

    private static NativeRect FitThumbnail(NativeSize source, NativeRect client)
    {
        const int inset = 2;
        var availableWidth = Math.Max(1, client.Right - client.Left - inset * 2);
        var availableHeight = Math.Max(1, client.Bottom - client.Top - inset * 2);
        var scale = Math.Min((double)availableWidth / source.Width, (double)availableHeight / source.Height);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var left = client.Left + (client.Right - client.Left - width) / 2;
        var top = client.Top + (client.Bottom - client.Top - height) / 2;
        return new NativeRect(left, top, left + width, top + height);
    }

    private void ReleaseThumbnails()
    {
        _entranceTimer.Stop();
        foreach (var (_, thumbnail, _) in _activeThumbnails)
        {
            if (thumbnail != 0)
            {
                _ = DwmUnregisterThumbnail(thumbnail);
            }
        }
        _activeThumbnails.Clear();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _windowClosed = true;
        ReleaseThumbnails();
        _destinationHandle = 0;
        _sourceHandle = 0;
        if (!_closeWindowClosed)
        {
            _closeButtonWindow.Close();
        }
    }

    private void CloseButtonWindow_Closed(object? sender, EventArgs e)
    {
        _closeWindowClosed = true;
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        PointerEntered?.Invoke(this, EventArgs.Empty);
    }

    private static void PreviewWindow_SourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window window && PresentationSource.FromVisual(window) is HwndSource source)
        {
            source.AddHook(PreviewWindowProc);
        }
    }

    private static nint PreviewWindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int wmMouseActivate = 0x0021;
        const int maNoActivate = 3;
        if (message == wmMouseActivate)
        {
            handled = true;
            return maNoActivate;
        }

        return 0;
    }

    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        PointerLeft?.Invoke(this, EventArgs.Empty);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmThumbnailProperties
    {
        public uint Flags;
        public NativeRect Destination;
        public NativeRect Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool SourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmQueryThumbnailSourceSize(nint thumbnail, out NativeSize size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(nint thumbnail, ref DwmThumbnailProperties properties);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(nint thumbnail);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint windowHandle, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);
}
