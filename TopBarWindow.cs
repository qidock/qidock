using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace QiDock;

/// <summary>
/// Glass widget strip at the top of the primary screen. It stays on screen while the desktop is
/// clean (no app window open on the primary monitor) and steps aside as soon as a window is open.
/// With "peek" on, resting the pointer on the top edge brings it down until the pointer leaves.
/// While hidden nothing in it renders, and the widgets drop their fast timers (see <see cref="LiveChanged"/>).
/// </summary>
public sealed class TopBarWindow : Window
{
    private const double TopGap = 8;
    private const double ShadowRoom = 18;
    private static readonly TimeSpan EdgeDwell = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan LeaveDelay = TimeSpan.FromMilliseconds(400);

    private readonly Border _surface;
    private readonly Border _tabs;
    private readonly TranslateTransform _slide = new();
    private readonly DispatcherTimer _edgeWatch = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
    private Func<bool> _isLocked = () => false;
    private bool _desktopClean;
    private bool _suppressed;
    private bool _hasContent;
    private bool _peekEnabled = true;
    private bool _peeking;
    private bool _shown;
    private DateTime _edgeSince;
    private DateTime _leftSince;
    private NativePoint _edgePoint;

    /// <summary>True while the bar is on screen; the widgets only need their live timers then.</summary>
    public event EventHandler<bool>? LiveChanged;

    /// <summary>Right-click on the bar (the dock's menu: widgets, edit mode, city and so on).</summary>
    public event EventHandler? MenuRequested;

    public TopBarWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Title = "QiDock Widgets";
        SetResourceReference(FontFamilyProperty, "InterfaceFont");

        _surface = new Border
        {
            Margin = new Thickness(ShadowRoom),
            Padding = new Thickness(6, 5, 6, 5),
            CornerRadius = new CornerRadius(22),
            BorderThickness = new Thickness(1),
            RenderTransform = _slide,
            Opacity = 0,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.34, RenderingBias = RenderingBias.Performance }
        };
        _surface.SetResourceReference(Border.BackgroundProperty, "DockMaterial");
        _surface.SetResourceReference(Border.BorderBrushProperty, "DockBorderMaterial");

        // Profile tabs sit under the bar and fade in while the pointer is over it. Fully transparent
        // pixels let clicks through, so the hidden strip never blocks the desktop.
        _tabs = new Border
        {
            Margin = new Thickness(ShadowRoom, -10, ShadowRoom, 10),
            Padding = new Thickness(4, 3, 4, 3),
            CornerRadius = new CornerRadius(13),
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0,
            IsHitTestVisible = false
        };
        _tabs.SetResourceReference(Border.BackgroundProperty, "DockMaterial");
        var layout = new StackPanel();
        layout.Children.Add(_surface);
        layout.Children.Add(_tabs);
        Content = layout;
        MouseEnter += (_, _) => ShowTabs(true);
        Widgets.WidgetPopup.AnyOpened += (_, _) => ShowTabs(false);
        MouseLeave += (_, _) => ShowTabs(_isLocked());

        SizeChanged += (_, _) => Reposition();
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea)) Dispatcher.InvokeAsync(Reposition);
        };
        MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            MenuRequested?.Invoke(this, EventArgs.Empty);
        };
        _edgeWatch.Tick += (_, _) => WatchPointer();
    }

    public UIElement? Bar
    {
        get => _surface.Child;
        set => _surface.Child = value;
    }

    public nint Handle { get; private set; }

    public UIElement? Tabs
    {
        get => _tabs.Child;
        set => _tabs.Child = value;
    }

    public void ShowTabs(bool show)
    {
        _tabs.IsHitTestVisible = show;
        _tabs.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 140 : 220)) { BeginTime = show ? TimeSpan.Zero : TimeSpan.FromMilliseconds(250) });
    }

    /// <summary>
    /// Where a card grows out of a tile of this bar (screen DIPs): from the bar's bottom edge, downwards, like a card
    /// grows out of a dock on the top edge. Null while the bar is not on screen.
    /// </summary>
    public EdgePanel.DockAnchor? AnchorOf(FrameworkElement element)
    {
        if (!_shown || !IsVisible || element.ActualWidth <= 0 || !element.IsDescendantOf(this)) return null;
        if (VisualTreeHelper.GetParent(_surface) is not Visual parent) return null;
        // Where the bar rests, not where its slide has it now.
        var tile = element.TransformToAncestor(this).TransformBounds(new Rect(element.RenderSize));
        tile.Offset(0, -_slide.Y);
        var surface = parent.TransformToAncestor(this).TransformBounds(new Rect((Point)VisualTreeHelper.GetOffset(_surface), _surface.RenderSize));
        var round = _surface.CornerRadius.BottomLeft + 6;
        return new EdgePanel.DockAnchor(Models.DockEdge.Top, Left + (tile.Left + tile.Right) / 2, tile.Width, Top + surface.Bottom,
            Left + surface.Left + round, Left + surface.Right - round);
    }

    /// <summary>Leaves the bar's bottom edge out under a card standing on it (screen DIPs on the card's foot line), or draws it whole (null).</summary>
    public void CutSeam((Point From, Point To)? seam)
    {
        if (seam is not { } line || _surface.ActualWidth <= 0)
        {
            _surface.OpacityMask = null;
            return;
        }
        var a = TranslatePoint(new Point(line.From.X - Left, line.From.Y - Top), _surface);
        var b = TranslatePoint(new Point(line.To.X - Left, line.To.Y - Top), _surface);
        var size = new Size(_surface.ActualWidth, _surface.ActualHeight);
        var depth = Math.Max(0, size.Height - Math.Min(a.Y, b.Y));
        _surface.OpacityMask = EdgePanel.NotchMask(size, Math.Min(a.X, b.X), Math.Max(a.X, b.X), depth, bottom: true);
    }

    private Effect? _shadow;

    /// <summary>While a card stands on the bar the two are one solid piece: no edge line and no shadow.</summary>
    public void SetJoined(Brush? solid)
    {
        if (solid is not null)
        {
            // The bar's material is lightest at its top, the card's at its foot: turned over, the two meet in one colour.
            if (solid is LinearGradientBrush gradient)
            {
                var turned = gradient.Clone();
                (turned.StartPoint, turned.EndPoint) = (gradient.EndPoint, gradient.StartPoint);
                turned.Freeze();
                solid = turned;
            }
            _surface.Background = solid;
            _surface.BorderBrush = solid;
            _shadow ??= _surface.Effect;
            _surface.Effect = null;
            return;
        }
        _surface.SetResourceReference(Border.BackgroundProperty, "DockMaterial");
        _surface.SetResourceReference(Border.BorderBrushProperty, "DockBorderMaterial");
        if (_shadow is not null && App.Shadows) _surface.Effect = _shadow;
    }

    /// <summary>Things that must keep the bar on screen (open popup, menu, edit mode).</summary>
    public void SetLock(Func<bool> isLocked) => _isLocked = isLocked;

    public void SetDesktopClean(bool clean)
    {
        if (_desktopClean == clean) return;
        _desktopClean = clean;
        if (clean) _peeking = false;
        Update();
    }

    /// <summary>Full-screen apps (games, video) hide the bar entirely.</summary>
    public void SetSuppressed(bool suppressed)
    {
        if (_suppressed == suppressed) return;
        _suppressed = suppressed;
        if (suppressed) _peeking = false;
        Update();
    }

    private bool _filesMode;

    /// <summary>While the desktop shows its files (icons), the bar stays out of the way.</summary>
    public void SetFilesMode(bool on)
    {
        if (_filesMode == on) return;
        _filesMode = on;
        Update();
    }

    public void SetHasContent(bool hasContent)
    {
        if (_hasContent == hasContent) return;
        _hasContent = hasContent;
        Update();
    }

    public void SetPeekEnabled(bool enabled)
    {
        _peekEnabled = enabled;
        if (!enabled) _peeking = false;
        Update();
    }

    /// <summary>Re-evaluates visibility (call when a lock such as edit mode or a popup changes).</summary>
    public void Update()
    {
        var locked = _shown && _isLocked();
        var target = _hasContent && !_suppressed && (ForceVisible || locked || (!_filesMode && (_desktopClean || _peeking)));
        if (target && !_shown) Reveal();
        else if (!target && _shown) Conceal();

        // Only poll the pointer when the edge can bring the bar in, or to send a peeked bar away.
        var watch = !_suppressed && _hasContent && ((_peekEnabled && !_desktopClean && !_shown) || _peeking);
        if (watch) _edgeWatch.Start();
        else _edgeWatch.Stop();
    }

    /// <summary>Keeps the bar visible regardless of windows (edit mode started from the dock menu).</summary>
    public bool ForceVisible { get; set; }

    /// <summary>True while the bar is on screen (or sliding in).</summary>
    public bool IsShown => _shown;

    private void Reveal()
    {
        _shown = true;
        if (!IsVisible)
        {
            Show();
            Reposition();
        }
        LiveChanged?.Invoke(this, true);
        Animate(1, 0, 170, new CubicEase { EasingMode = EasingMode.EaseOut }, null);
    }

    private void Conceal()
    {
        _shown = false;
        LiveChanged?.Invoke(this, false);
        Animate(0, -14, 140, new CubicEase { EasingMode = EasingMode.EaseIn }, () =>
        {
            if (!_shown) Hide();
        });
    }

    private void Animate(double opacity, double y, int milliseconds, IEasingFunction ease, Action? done)
    {
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var fade = new DoubleAnimation(opacity, duration) { EasingFunction = ease };
        if (done is not null) fade.Completed += (_, _) => done();
        _surface.BeginAnimation(OpacityProperty, fade);
        if (opacity > 0 && _slide.Y >= 0 && _surface.Opacity <= 0.01) _slide.Y = -14;
        _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, duration) { EasingFunction = ease });
    }

    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        if (ActualWidth <= 0) return;
        Left = Math.Round(area.Left + (area.Width - ActualWidth) / 2);
        Top = Math.Round(area.Top + TopGap - ShadowRoom);
    }

    private void WatchPointer()
    {
        if (!GetCursorPos(out var cursor)) return;
        var now = DateTime.UtcNow;
        if (!_peeking)
        {
            // Rest on the top edge, over the bar's span, without clicking (browser tabs live up there too).
            var atEdge = cursor.Y <= 1 && WithinBarSpan(cursor.X);
            if (!atEdge || Math.Abs(cursor.X - _edgePoint.X) > 24)
            {
                _edgeSince = atEdge ? now : default;
                _edgePoint = cursor;
                return;
            }
            if (_edgeSince == default || now - _edgeSince < EdgeDwell) return;
            _edgeSince = default;
            _peeking = true;
            _leftSince = default;
            Update();
            return;
        }

        if (_isLocked() || OverSurface(cursor))
        {
            _leftSince = default;
            return;
        }
        if (_leftSince == default)
        {
            _leftSince = now;
            return;
        }
        if (now - _leftSince < LeaveDelay) return;
        _peeking = false;
        Update();
    }

    private bool WithinBarSpan(int x)
    {
        var screenWidth = GetSystemMetrics(0);
        var halfSpan = Math.Max(screenWidth * 0.18, ActualWidth * DpiScale() / 2);
        return Math.Abs(x - screenWidth / 2.0) <= halfSpan;
    }

    private bool OverSurface(NativePoint cursor)
    {
        if (!IsVisible || PresentationSource.FromVisual(_surface) is null) return false;
        var topLeft = _surface.PointToScreen(new Point(-24, -40));
        var bottomRight = _surface.PointToScreen(new Point(_surface.ActualWidth + 24, _surface.ActualHeight + 48));
        return cursor.X >= topLeft.X && cursor.X <= bottomRight.X && cursor.Y >= topLeft.Y && cursor.Y <= bottomRight.Y;
    }

    private double DpiScale() => PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is not HwndSource source) return;
        Handle = source.Handle;
        source.AddHook(WindowProc);
        // Tool window (no Alt+Tab, no taskbar button) that never takes focus from the user's app.
        var style = GetWindowLongPtr(Handle, -20).ToInt64();
        SetWindowLongPtr(Handle, -20, new nint((style | 0x00000080 | 0x08000000) & ~0x00040000));
    }

    private static IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmMouseActivate = 0x0021;
        const int MaNoActivate = 3;
        if (message != WmMouseActivate) return IntPtr.Zero;
        handled = true;
        return new IntPtr(MaNoActivate);
    }

    protected override void OnClosed(EventArgs e)
    {
        _edgeWatch.Stop();
        base.OnClosed(e);
    }

    private static bool AnyMouseButtonDown() =>
        (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}
