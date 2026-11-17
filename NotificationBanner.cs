using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QiDock.Services;
using QiDock.Services.Notifications;
using QiDock.Widgets;

namespace QiDock;

/// <summary>
/// Phone-style notification banners: each new Windows notification slides down from above the
/// top center of the screen as a frosted-glass card (app icon, app name, time, title, text),
/// stays for a few seconds and slides back up. Up to three stack, newest on top; the others
/// glide down to make room and up to close a gap. Hovering keeps them; a click opens the
/// notification's app; dragging a card up or its × sends it away. Never takes focus.
/// Each card is its own small window, because the frosted glass covers a whole window.
/// </summary>
public sealed class NotificationBanner
{
    private const int MaxCards = 3;
    private const double Gap = 8;
    private const double TopMargin = 10;
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(6);

    // Newest first; cards on their way out are no longer in the list.
    private readonly List<BannerCard> _cards = [];
    private bool _hovered;

    /// <summary>A card was clicked: its app has been opened; the owner marks it read.</summary>
    public event Action<AppNotification>? Opened;

    public void Present(IReadOnlyList<AppNotification> notifications)
    {
        if (IsFullScreenAppInFront()) return; // games and videos are not interrupted; the badge still counts
        foreach (var notification in notifications.OrderBy(n => n.Arrived).TakeLast(MaxCards))
            Add(notification);
    }

    public void Close()
    {
        foreach (var card in _cards.ToList()) card.Close();
        _cards.Clear();
    }

    private void Add(AppNotification notification)
    {
        while (_cards.Count >= MaxCards) Dismiss(_cards[^1]);
        var card = new BannerCard(notification);
        card.Dismissed += () => Dismiss(card);
        card.Tapped += () =>
        {
            Dismiss(card);
            NotificationApps.Activate(notification);
            Opened?.Invoke(notification);
        };
        card.HoverChanged += OnHoverChanged;
        card.Timer.Interval = Linger;
        card.Timer.Tick += (_, _) => Dismiss(card);
        _cards.Insert(0, card);
        var area = SystemParameters.WorkArea;
        card.Appear(area.Left + (area.Width - card.Width) / 2, area.Top - 20);
        Arrange();
        if (!_hovered) card.Timer.Start();
    }

    /// <summary>Sends a card up and out of the screen; the rest close the gap.</summary>
    private void Dismiss(BannerCard card)
    {
        if (!_cards.Remove(card)) return;
        card.Timer.Stop();
        card.Leave();
        Arrange();
        OnHoverChanged(); // the card under the pointer may be the one that left
    }

    /// <summary>Glides every card to its place: newest at the top, each below the one before.</summary>
    private void Arrange()
    {
        var top = SystemParameters.WorkArea.Top + TopMargin;
        foreach (var card in _cards)
        {
            card.GlideTo(top);
            top += card.ActualHeight + Gap;
        }
    }

    private void OnHoverChanged()
    {
        var hovered = _cards.Any(c => c.IsMouseOver);
        if (hovered == _hovered) return;
        _hovered = hovered;
        foreach (var card in _cards)
        {
            if (hovered) card.Timer.Stop();
            else
            {
                card.Timer.Interval = TimeSpan.FromSeconds(2.5);
                card.Timer.Start();
            }
        }
    }

    /// <summary>The app icon, the toast's own logo when the app has none, or a bell.</summary>
    internal static FrameworkElement IconFor(AppNotification notification, double size)
    {
        ImageSource? source = NotificationApps.Icon(notification.AppId);
        if (source is null && notification.LogoPath is { } path)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path);
                image.DecodePixelWidth = (int)(size * 2);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                source = image;
            }
            catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or UriFormatException or ArgumentException) { }
        }
        if (source is not null)
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size * 0.24),
                Background = new ImageBrush(source) { Stretch = Stretch.Uniform }
            };
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.24),
            Background = Ui.Diagonal(Color.FromRgb(0xFF, 0x5E, 0x57), Color.FromRgb(0xE5, 0x2D, 0x27)),
            Child = Ui.Icon("IconBell", size * 0.5, Brushes.White)
        };
    }

    /// <summary>A game, video or presentation covers the whole monitor.</summary>
    private static bool IsFullScreenAppInFront()
    {
        var window = GetForegroundWindow();
        if (window == 0 || !GetWindowRect(window, out var rect)) return false;
        var className = new System.Text.StringBuilder(64);
        GetClassName(window, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        var monitor = MonitorFromWindow(window, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        return rect.Left <= info.Monitor.Left && rect.Top <= info.Monitor.Top && rect.Right >= info.Monitor.Right && rect.Bottom >= info.Monitor.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, System.Text.StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}

/// <summary>One banner: a small frosted window that glides into its place and away.</summary>
internal sealed class BannerCard : Window
{
    private const double CardWidth = 360;
    private readonly Border _sheet;
    private readonly FrameworkElement _content;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private (double From, double To, double Start, double Duration, IEasingFunction Ease, Action? Done)? _glide;
    private bool _ticking, _placed, _leaving;
    private double _slot;
    private double? _pressedAt, _pressedTop;

    public BannerCard(AppNotification notification)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        GpuTransparency.Prepare(this);
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = CardWidth;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "QiDock Notification";
        SetResourceReference(FontFamilyProperty, "InterfaceFont");
        UseLayoutRounding = true;
        _content = Layout(notification, out var close);
        _sheet = new Border { Child = _content, Padding = new Thickness(12, 10, 14, 11), CornerRadius = new CornerRadius(16), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        Content = _sheet;
        close.Click += (_, _) => Dismissed?.Invoke();
        MouseEnter += (_, _) =>
        {
            close.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
            HoverChanged?.Invoke();
        };
        MouseLeave += (_, _) =>
        {
            close.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)));
            HoverChanged?.Invoke();
        };
        // Click opens the app; dragging the card up sends it away.
        _sheet.MouseLeftButtonDown += (_, e) =>
        {
            if (IsInsideButton(e.OriginalSource as DependencyObject) || _leaving) return;
            _pressedAt = CursorY();
            _pressedTop = Top;
            _glide = null;
            _sheet.CaptureMouse();
            e.Handled = true;
        };
        _sheet.MouseMove += (_, _) =>
        {
            if (_pressedAt is not { } start || _pressedTop is not { } top || !_sheet.IsMouseCaptured) return;
            Top = top + Math.Min(0, CursorY() - start);
        };
        _sheet.MouseLeftButtonUp += (_, _) =>
        {
            if (_pressedAt is not { } start) return;
            var dy = CursorY() - start;
            _pressedAt = _pressedTop = null;
            _sheet.ReleaseMouseCapture();
            if (dy < -28) Dismissed?.Invoke();
            else if (Math.Abs(dy) < 6) Tapped?.Invoke();
            else GlideTo(_slot);
        };
    }

#if DEBUG
    /// <summary>Debug snapshot run: hands out the card's content without showing the window.</summary>
    internal FrameworkElement Rehearse()
    {
        Content = null;
        _sheet.Width = CardWidth;
        _content.Opacity = 1;
        return _sheet;
    }
#endif

    public DispatcherTimer Timer { get; } = new(DispatcherPriority.Normal);

    public event Action? Dismissed;
    public event Action? Tapped;
    public event Action? HoverChanged;

    /// <summary>Shows the card just above the top of the screen (its bottom at <paramref name="above"/>).</summary>
    public void Appear(double left, double above)
    {
        Left = left;
        Top = -4000;
        Show();
        UpdateLayout();
        var tint = TryFindResource("PanelFrostTint") is Color color ? color : Color.FromArgb(0xC8, 0x19, 0x1A, 0x1F);
        if (!GpuTransparency.Frost(this, tint))
        {
            _sheet.SetResourceReference(Border.BackgroundProperty, "WidgetPopupFill");
            _sheet.SetResourceReference(Border.BorderBrushProperty, "DockBorderMaterial");
            _sheet.BorderThickness = new Thickness(1);
        }
        Top = above - ActualHeight;
        _content.Opacity = 0;
    }

    /// <summary>Glides to <paramref name="top"/>: the first time with a soft settle, later straight.</summary>
    public void GlideTo(double top)
    {
        if (_leaving) return;
        _slot = top;
        if (_pressedAt is not null) return; // being dragged: it goes back there when let go
        var first = !_placed;
        _placed = true;
        if (first)
        {
            // Start after the first frame, so a slow first draw never eats the start of the slide.
            Motion.AfterFirstFrame(() =>
            {
                if (_leaving) return;
                Glide(_slot, 520, Motion.Settle);
                _content.Opacity = 1;
                _content.BeginAnimation(OpacityProperty, Motion.FromTo(0, 1, 260, Motion.Out, 60));
            });
            return;
        }
        Glide(top, 380, Motion.Out);
    }

    /// <summary>Slides up and out of the screen, fading its content, then closes.</summary>
    public void Leave()
    {
        if (_leaving) return;
        _leaving = true;
        IsHitTestVisible = false;
        Timer.Stop();
        _content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = Motion.In });
        Glide(SystemParameters.WorkArea.Top - ActualHeight - 30, 320, Motion.In, Close);
    }

    private void Glide(double to, double milliseconds, IEasingFunction ease, Action? done = null)
    {
        _glide = (Top, to, _clock.Elapsed.TotalMilliseconds, milliseconds, ease, done);
        if (_ticking) return;
        _ticking = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (_glide is not { } glide)
        {
            _ticking = false;
            CompositionTarget.Rendering -= OnFrame;
            return;
        }
        var t = Math.Clamp((_clock.Elapsed.TotalMilliseconds - glide.Start) / glide.Duration, 0, 1);
        Top = Math.Round(glide.From + (glide.To - glide.From) * glide.Ease.Ease(t), 2);
        if (t < 1) return;
        _glide = null;
        glide.Done?.Invoke();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _glide = null;
        if (_ticking) CompositionTarget.Rendering -= OnFrame;
        _ticking = false;
        Timer.Stop();
    }

    /// <summary>The pointer's height on screen in this window's units.</summary>
    private double CursorY()
    {
        GetCursorPos(out var point);
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice.M22 ?? 1;
        return point.Y * scale;
    }

    private static FrameworkElement Layout(AppNotification notification, out Button close)
    {
        var icon = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 11, 0) };
        icon.Child = NotificationBanner.IconFor(notification, 34);

        var app = Ui.Text(NotificationApps.Name(notification.AppId), 11, FontWeights.SemiBold, "TextSecondary");
        app.TextTrimming = TextTrimming.CharacterEllipsis;
        var time = Ui.Text(Ui.Ago(notification.Arrived.ToLocalTime()), 11, FontWeights.Medium, "TextSecondary");
        time.Margin = new Thickness(8, 0, 0, 0);
        var header = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(time, Dock.Right);
        header.Children.Add(time);
        header.Children.Add(app);

        var title = Ui.Text(notification.Title, 13, FontWeights.SemiBold);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.Margin = new Thickness(0, 1, 0, 0);
        var text = new StackPanel();
        text.Children.Add(header);
        text.Children.Add(title);
        if (notification.Body.Length > 0)
        {
            var body = Ui.Text(notification.Body, 12.5);
            body.TextWrapping = TextWrapping.Wrap;
            body.TextTrimming = TextTrimming.CharacterEllipsis;
            body.MaxHeight = 12.5 * 1.4 * 3;
            text.Children.Add(body);
        }

        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);

        close = new Button { Width = 20, Height = 20, Content = Ui.Icon("IconClose", 8), ToolTip = UiText.Get("NotificationDismiss"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-6, -5, 0, 0), Opacity = 0, Cursor = Cursors.Hand };
        close.Template = CloseTemplate();
        var content = new Grid();
        content.Children.Add(row);
        content.Children.Add(close);
        return content;
    }

    private static ControlTemplate CloseTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        border.SetResourceReference(Border.BackgroundProperty, "PanelTileHover");
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        return template;
    }

    private static bool IsInsideButton(DependencyObject? node)
    {
        for (; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is Button) return true;
        return false;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is not HwndSource source) return;
        GpuTransparency.Apply(this);
        // Tool window that never activates: clicking a banner must not steal focus from the user's app.
        var style = GetWindowLongPtr(source.Handle, -20).ToInt64();
        SetWindowLongPtr(source.Handle, -20, new nint((style | 0x00000080 | 0x08000000) & ~0x00040000));
        source.AddHook((nint _, int message, nint _, nint _, ref bool handled) =>
        {
            if (message != 0x0021 /* WM_MOUSEACTIVATE */) return 0;
            handled = true;
            return 3; // MA_NOACTIVATE
        });
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}
