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

/// <summary>Shared timing and easing so every panel moves the same calm way.</summary>
public static class Motion
{
    public static readonly IEasingFunction Out = new PowerEase { Power = 4, EasingMode = EasingMode.EaseOut };
    public static readonly IEasingFunction In = new PowerEase { Power = 3, EasingMode = EasingMode.EaseIn };
    public static readonly IEasingFunction Settle = new BackEase { Amplitude = 0.18, EasingMode = EasingMode.EaseOut };

    public static DoubleAnimation To(double to, double milliseconds, IEasingFunction? ease = null, double delay = 0) =>
        new(to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = ease ?? Out, BeginTime = TimeSpan.FromMilliseconds(delay) };

    /// <summary>
    /// Runs from <paramref name="from"/> to <paramref name="to"/> and then lets go (the property's own
    /// value must already be <paramref name="to"/>). A delayed one holds <paramref name="from"/> while waiting,
    /// so nothing flashes in its final place first.
    /// </summary>
    public static AnimationTimeline FromTo(double from, double to, double milliseconds, IEasingFunction? ease = null, double delay = 0)
    {
        if (delay <= 0)
            return new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = ease ?? Out, FillBehavior = FillBehavior.Stop };
        var frames = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
        frames.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + milliseconds)), ease ?? Out));
        return frames;
    }

    /// <summary>The element's own translate transform (created on first use).</summary>
    /// <summary>
    /// With QIDOCK_FRAMES=1, writes how long each frame took for a second after a movement
    /// starts (to %LOCALAPPDATA%\QiDock\frames.log), to find stutter.
    /// </summary>
    public static void Measure(string what)
    {
        if (Environment.GetEnvironmentVariable("QIDOCK_FRAMES") != "1") return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var last = TimeSpan.Zero;
        var gaps = new List<double>();
        var slow = new List<string>();
        EventHandler? tick = null;
        tick = (_, args) =>
        {
            var now = ((RenderingEventArgs)args).RenderingTime;
            if (last != TimeSpan.Zero && now != last)
            {
                var gap = (now - last).TotalMilliseconds;
                gaps.Add(gap);
                if (gap > 25) slow.Add($"{watch.ElapsedMilliseconds}ms:{gap:0}");
            }
            last = now;
            if (watch.ElapsedMilliseconds < 1000) return;
            CompositionTarget.Rendering -= tick;
            gaps.Sort();
            var line = $"{DateTime.Now:HH:mm:ss} {what}: frames={gaps.Count} median={(gaps.Count > 0 ? gaps[gaps.Count / 2] : 0):0.0}ms max={(gaps.Count > 0 ? gaps[^1] : 0):0.0}ms over25={gaps.Count(g => g > 25)} at [{string.Join(", ", slow)}]";
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QiDock", "frames.log"), line + Environment.NewLine); }
            catch (System.IO.IOException) { }
        };
        CompositionTarget.Rendering += tick;
    }

    /// <summary>Runs <paramref name="action"/> once the next frame has been put on screen.</summary>
    public static void AfterFirstFrame(Action action)
    {
        // Rendering can be raised more than once per frame; count frames by their time.
        TimeSpan? first = null;
        EventHandler? tick = null;
        tick = (_, args) =>
        {
            var time = ((RenderingEventArgs)args).RenderingTime;
            if (first is null)
            {
                first = time;
                return;
            }
            if (time == first) return;
            CompositionTarget.Rendering -= tick;
            action();
        };
        CompositionTarget.Rendering += tick;
    }

    public static TranslateTransform Shift(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform shift && !shift.IsFrozen) return shift;
        if (element.RenderTransform is TransformGroup { IsFrozen: false } group && group.Children.OfType<TranslateTransform>().FirstOrDefault() is { } inner) return inner;
        shift = new TranslateTransform();
        element.RenderTransform = shift;
        return shift;
    }

    /// <summary>
    /// Draws the element once into a bitmap that the GPU then only moves and fades, so moving
    /// cards with shadows never re-renders them frame by frame.
    /// </summary>
    public static void Cache(UIElement element)
    {
        var scale = Application.Current?.MainWindow is { } main && PresentationSource.FromVisual(main)?.CompositionTarget is { } target ? target.TransformToDevice.M11 : 1.5;
        element.CacheMode = new BitmapCache(scale) { SnapsToDevicePixels = true };
    }
}

/// <summary>
/// A frosted panel that slides in from the right edge of the screen (notification center,
/// control center): one sheet of blurred, tinted glass with rounded corners,
/// holding light tiles. It is only as tall as its tiles and grows or shrinks smoothly with them,
/// never takes focus from the app you are in and closes when you click anywhere else; opening
/// one closes the other. Rebuilding the tiles moves every tile from where it was to where it goes.
/// It can also grow out of the dock instead (<see cref="ToggleFromDock"/>): a small bump rises from the
/// clicked tile, swells into the panel with a soft overshoot and stays joined to the dock by inward
/// curves, in the dock's own material; closing pulls it back into the tile.
/// </summary>
public abstract class EdgePanel : Window
{
    /// <summary>
    /// The dock tile a panel grows out of (screen DIPs): the edge the dock stands on, the tile's centre and length
    /// along the dock, and the dock's inner edge (its top on the bottom edge, its right side on the left edge…).
    /// </summary>
    /// <remarks><paramref name="DockStart"/>..<paramref name="DockEnd"/> is the straight part of the dock (its rounded ends left
    /// out): the panel's foot always stands inside it, never past the dock's end.</remarks>
    public readonly record struct DockAnchor(Models.DockEdge Edge, double TileCenter, double TileWidth, double Inner,
        double DockStart = double.NegativeInfinity, double DockEnd = double.PositiveInfinity);

    // Dock-grown shape: inward curve where it meets the dock, top corner radius, room for the overshoot,
    // and how far it reaches down over the dock's top edge so the two read as one piece.
    private const double Fillet = 14;
    private const double DockCorner = 20;
    private const double Slack = 10;
    private const double Overlap = 2;

    /// <summary>How much of the dock's top the dock leaves out under a panel (its edge line): exactly what the panel covers.</summary>
    public const double SeamDepth = Overlap;

    /// <summary>
    /// Supplied by the main window: leaves the dock's top edge out between two screen x positions (DIPs) while a
    /// panel stands on the dock there, or draws it whole again (null).
    /// </summary>
    /// <remarks>
    /// The two points (screen DIPs) lie on the line where the panel's foot ends, on a device-pixel boundary: the dock
    /// cuts its inner edge away between them down to exactly that line, so no row of pixels is left between the windows.
    /// </remarks>
    public static Action<(Point From, Point To)?> DockSeam { get; set; } = _ => { };

    /// <summary>Device pixels per DIP on the dock's screen.</summary>
    private static double DeviceScale =>
        Application.Current?.MainWindow is { } main && PresentationSource.FromVisual(main) is not null ? VisualTreeHelper.GetDpi(main).DpiScaleY : 1;

    /// <summary>
    /// Sizes and places a dock-grown panel around content <paramref name="along"/> long (along the dock) and
    /// <paramref name="depth"/> deep, centred on the tile and kept on screen, with its foot ending on a whole device
    /// pixel just past the dock's inner edge.
    /// </summary>
    private void PlaceOnDock(DockAnchor dock, double along, double depth)
    {
        var scale = DeviceScale;
        var area = SystemParameters.WorkArea;
        var side = Fillet + Slack;
        var frameAlong = along + 2 * side;
        var depthPixels = Math.Ceiling((depth + Slack + Overlap) * scale);
        var frameDepth = depthPixels / scale;
        var vertical = dock.Edge is Models.DockEdge.Left or Models.DockEdge.Right;
        var (start, end) = vertical ? (area.Top, area.Bottom) : (area.Left, area.Right);
        // The foot (frame minus Slack at each end) stands on the dock's straight part; then the screen's edges.
        var low = Math.Max(start + EdgeGap - side, dock.DockStart - Slack);
        var high = Math.Min(end - frameAlong - EdgeGap + side, dock.DockEnd + Slack - frameAlong);
        var centred = dock.TileCenter - frameAlong / 2;
        var alongStart = Math.Round((high >= low ? Math.Clamp(centred, low, high) : (low + high) / 2) * scale) / scale;
        // The foot's end, on a pixel boundary, Overlap past the dock's inner edge.
        var footEnd = dock.Edge is Models.DockEdge.Bottom or Models.DockEdge.Right
            ? Math.Round((dock.Inner + Overlap) * scale) / scale
            : Math.Round((dock.Inner - Overlap) * scale) / scale;
        _foot = Math.Max(0.5, Math.Abs(footEnd - dock.Inner));
        _frameSize = new Size(frameAlong, frameDepth);
        if (vertical)
        {
            Width = frameDepth;
            Height = frameAlong;
            Top = alongStart;
            Left = dock.Edge == Models.DockEdge.Left ? footEnd : footEnd - frameDepth;
        }
        else
        {
            Width = frameAlong;
            Height = frameDepth;
            Left = alongStart;
            Top = dock.Edge == Models.DockEdge.Top ? footEnd : footEnd - frameDepth;
        }
        // The frame's margins (side, Slack, side, foot) turned to the real edge.
        _sheet.Margin = dock.Edge switch
        {
            Models.DockEdge.Top => new Thickness(side, Overlap, side, Slack),
            Models.DockEdge.Left => new Thickness(Overlap, side, Slack, side),
            Models.DockEdge.Right => new Thickness(Slack, side, Overlap, side),
            _ => new Thickness(side, Slack, side, Overlap)
        };
        _skin.Width = frameAlong;
        _skin.Height = frameDepth;
        _skin.LayoutTransform = new MatrixTransform(Services.DockGeometry.FrameToScreen(dock.Edge));
        _contentExtent = vertical ? along : depth;
    }

    private double _foot = Overlap;
    private Size _frameSize;
    private double _contentExtent;

    /// <summary>A point of the bottom-edge frame in window coordinates.</summary>
    private Point ToWindow(Point p) => (_dock?.Edge ?? Models.DockEdge.Bottom) switch
    {
        Models.DockEdge.Top => new Point(p.X, _frameSize.Height - p.Y),
        Models.DockEdge.Left => new Point(_frameSize.Height - p.Y, p.X),
        Models.DockEdge.Right => new Point(p.Y, p.X),
        _ => p
    };

    /// <summary>An opacity mask that hides a strip <paramref name="depth"/> deep along the top (or the bottom) of an element between two x positions.</summary>
    public static Brush? NotchMask(Size size, double from, double to, double depth, bool bottom = false)
    {
        if (to <= from || size.Width <= 0 || size.Height <= 0) return null;
        var bounds = new Rect(size);
        var strip = bottom ? new Rect(from, size.Height - depth, to - from, depth + 1) : new Rect(from, -1, to - from, depth + 1);
        var shape = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(bounds), new RectangleGeometry(strip));
        var mask = new DrawingBrush(new GeometryDrawing(Brushes.Black, null, shape))
        {
            Stretch = Stretch.None,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = bounds,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = bounds
        };
        mask.Freeze();
        return mask;
    }

    /// <summary>Space between the glass edge and the tiles.</summary>
    protected const double Pad = 10;
    private const double EdgeGap = 10;
    private static EdgePanel? _open;
    private static EdgePanel? _onDock;

    /// <summary>A panel stands on the dock (opening, open or sinking back): the dock keeps still under it.</summary>
    public static bool StandsOnDock => _onDock is not null;

    /// <summary>
    /// Supplied by the main window: where a panel grows out of this dock element (the tiles settle first),
    /// or null when the element is not in the dock.
    /// </summary>
    public static Func<FrameworkElement, DockAnchor?> DockAnchorFor { get; set; } = _ => null;

    /// <summary>Opens out of <paramref name="element"/> when it sits in the dock, otherwise at the screen edge.</summary>
    public void ToggleFrom(FrameworkElement element)
    {
        if (IsOpen)
        {
            Close(animated: true);
            return;
        }
        Toggle(DockAnchorFor(element));
    }

    /// <summary>Supplied by the main window: the space to leave at the top (under the widget bar).</summary>
    public static Func<double> TopInset { get; set; } = () => 12;

    /// <summary>Raised when any panel opens or closes (the dock stays up while one is open).</summary>
    public static event EventHandler? AnyVisibilityChanged;

    public static bool AnyOpen => _open is { IsOpen: true };

    private static readonly DependencyProperty ScrollOffsetProperty = DependencyProperty.Register(
        "ScrollOffset", typeof(double), typeof(EdgePanel), new PropertyMetadata(0.0, (d, e) => ((EdgePanel)d)._scroll.ScrollToVerticalOffset((double)e.NewValue)));

    protected readonly double CardWidth;
    protected readonly StackPanel Cards = new();

    /// <summary>A row that stays put above the scrolling tiles (the notification center's title); empty by default.</summary>
    protected readonly ContentControl Header = new() { Focusable = false, Visibility = Visibility.Collapsed };
    private readonly ScrollViewer _scroll;
    private readonly Border _sheet;
    private readonly Grid _frame = new();
    // The dock-grown shape, drawn as if the dock were at the bottom and turned to its real edge.
    private readonly Grid _skin = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    private readonly System.Windows.Shapes.Path _shape = new() { Visibility = Visibility.Collapsed };
    private readonly System.Windows.Shapes.Path _outline = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false, StrokeThickness = 1 };
    private DockAnchor? _dock;
    private double _morph, _sheetMax = double.PositiveInfinity;
    private Tween? _morphTween;
    private readonly DispatcherTimer _outsideClick = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private Tween? _leftTween;
    private readonly DispatcherTimer _shrink = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(520) };
    private bool _ticking;
    private double _scrollTarget, _restLeft, _awayLeft;
    private bool _buttonWasDown, _closing, _frosted;
    private DateTime _closedAt;

    /// <summary>Where a new tile comes from; <paramref name="Continues"/> when it is the same card seen there before (no fade).</summary>
    protected readonly record struct Origin(string Key, bool Continues = false);

    private sealed record Tween(double From, double To, double Start, double Duration, IEasingFunction Ease, Action? Done);

    protected EdgePanel(double cardWidth, string title)
    {
        CardWidth = cardWidth;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Services.GpuTransparency.Prepare(this);
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;
        Title = title;
        SetResourceReference(FontFamilyProperty, "InterfaceFont");
        UseLayoutRounding = true;
        Cards.Margin = new Thickness(Pad);
        _scroll = new ScrollViewer
        {
            Content = Cards,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false
        };
        _scroll.PreviewMouseWheel += SmoothWheel;
        // Transparent over the frosted glass; an opaque rounded sheet where Windows can't frost.
        Header.Margin = new Thickness(Pad, Pad, Pad, 0);
        var layout = new DockPanel();
        DockPanel.SetDock(Header, Dock.Top);
        layout.Children.Add(Header);
        layout.Children.Add(_scroll);
        _sheet = new Border { Child = layout, Background = Brushes.Transparent, CornerRadius = new CornerRadius(14), ClipToBounds = true };
        _skin.Children.Add(_shape);
        _skin.Children.Add(_outline);
        _frame.Children.Add(_skin);
        _frame.Children.Add(_sheet);
        _frame.SizeChanged += (_, _) => DrawMorph(_morph);
        Content = _frame;
        _outsideClick.Tick += (_, _) => WatchOutsideClick();
        _shrink.Tick += (_, _) =>
        {
            _shrink.Stop();
            if (IsOpen) SetSheetHeight(ContentHeight());
        };
    }

    public bool IsOpen => IsVisible && !_closing;

    /// <summary>How much of the screen's height the panel may take before it scrolls.</summary>
    protected double HeightShare { get; init; } = 1;

    /// <summary>Opens the panel at the right edge, or closes it when open (ignores the click that just closed it).</summary>
    public void Toggle() => Toggle(null);

    /// <summary>Opens the panel growing out of a dock tile, or closes it back into the tile.</summary>
    public void ToggleFromDock(DockAnchor anchor) => Toggle(anchor);

    private void Toggle(DockAnchor? dock)
    {
        if (IsOpen)
        {
            Close(animated: true);
            return;
        }
        if (DateTime.UtcNow - _closedAt < TimeSpan.FromMilliseconds(350)) return; // the same click closed it
        Open(dock);
    }

    /// <summary>Fills <see cref="Cards"/>. Give each top-level tile a key in its Tag so it can be followed when rebuilt.</summary>
    protected abstract void Build();

    protected virtual void OnOpened() { }

    /// <summary>The panel has finished closing.</summary>
    protected virtual void OnHidden() { }

    /// <summary>Fits the glass again after the content changed size by itself.</summary>
    protected void Refit()
    {
        if (IsOpen) Fit(ContentHeight());
    }

#if DEBUG
    /// <summary>Debug snapshot run: builds the panel's content without showing it and hands it out.</summary>
    internal FrameworkElement Rehearse()
    {
        Build();
        TrimLastGap();
        if (_sheet.Parent is Panel holder) holder.Children.Remove(_sheet);
        _sheet.Margin = new Thickness(0);
        _sheet.Clip = null;
        _sheet.Width = CardWidth + 2 * Pad;
        _scroll.Opacity = 1;
        return _sheet;
    }

    private (double Width, double Height, double TileX)? _rehearsal;
    private Border? _rehearsalDock;

    /// <summary>Debug snapshot run: the dock-grown panel over a stand-in dock, drawn by <see cref="RehearseMorph"/>.</summary>
    internal FrameworkElement RehearseDock()
    {
        Build();
        TrimLastGap();
        if (_sheet.Parent is Panel holder && !ReferenceEquals(holder, _frame)) holder.Children.Remove(_sheet);
        else if (_sheet.Parent is Border border) border.Child = null;
        if (_sheet.Parent is null) _frame.Children.Add(_sheet);
        if (_frame.Parent is Panel old) old.Children.Remove(_frame);
        Content = null;
        _sheet.Width = double.NaN;
        var side = Fillet + Slack;
        var sheetWidth = CardWidth + 2 * Pad;
        _sheetMax = 520;
        var sheetHeight = ContentHeight();
        var width = sheetWidth + 2 * side + 120;
        var height = sheetHeight + Slack + Overlap;
        _sheet.Margin = new Thickness(side + 60, Slack, side + 60, Overlap);
        _frame.Width = width;
        _frame.Height = height;
        _frame.VerticalAlignment = VerticalAlignment.Top;
        _skin.Width = width;
        _skin.Height = height;
        _frameSize = new Size(width, height);
        PaintDockShape();
        _shape.Visibility = Visibility.Visible;
        _outline.Visibility = Visibility.Collapsed; // no edge line: panel and dock read as one solid piece
        _rehearsal = (width, height, width / 2 + 90);

        var dock = new Border { Height = 62, CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1), Margin = new Thickness(0, height - Overlap, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        dock.SetResourceReference(Border.BackgroundProperty, "DockMaterial");
        dock.SetResourceReference(Border.BorderBrushProperty, "DockBorderMaterial");
        var tile = new Border { Width = 50, Height = 50, CornerRadius = new CornerRadius(12), Background = Brushes.DimGray, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(width / 2 + 90 - 25 - 1, 5, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        dock.Child = new Grid { Children = { tile } };
        _rehearsalDock = dock;
        var stage = new Grid { Width = width, Height = height - Overlap + 62 };
        stage.Children.Add(dock);
        stage.Children.Add(_frame);
        return stage;
    }

    /// <summary>Draws the rehearsed dock-grown panel at <paramref name="progress"/> (0 = inside the tile, 1 = open).</summary>
    internal void RehearseMorph(double progress)
    {
        if (_rehearsal is not { } stage) return;
        var side = Fillet + Slack + 60;
        ApplyMorph(progress, stage.Width, stage.Height, stage.TileX, 50, side,
            range => _rehearsalDock!.OpacityMask = range is { } r ? NotchMask(new Size(stage.Width, 62), r.Left, r.Right, SeamDepth) : null);
        _scroll.Opacity = Math.Clamp((progress - 0.55) / 0.4, 0, 1);
    }
#endif

    private void Open(DockAnchor? dock)
    {
        if (_open is { } other && !ReferenceEquals(other, this)) other.Close(animated: false);
        _open = this;
        _closing = false;
        _dock = dock;
        Build();
        TrimLastGap();
        if (dock is { } anchor)
        {
            OpenFromDock(anchor);
            return;
        }
        _shape.Visibility = _outline.Visibility = Visibility.Collapsed;
        Services.Widgets.GlassLook.Tiles(Resources, false); // the glass tiles belong to the card on the dock
        _sheet.Margin = new Thickness(0);
        _sheet.Clip = null;
        var area = SystemParameters.WorkArea;
        var top = area.Top + Math.Max(0, TopInset());
        Width = CardWidth + 2 * Pad;
        _sheetMax = Math.Max(220, (area.Bottom - top - EdgeGap) * HeightShare);
        Height = ContentHeight();
        Top = top;
        _restLeft = area.Right - Width - EdgeGap;
        _awayLeft = area.Right + 2; // just past the screen edge
        Left = _awayLeft;
        _scrollTarget = 0;
        _scroll.ScrollToVerticalOffset(0);
        _scroll.Opacity = 0;
        Show();
        Frost();
        // The first frame is drawn off screen, so the slide never starts with a skip.
        Motion.AfterFirstFrame(() =>
        {
            if (!IsOpen) return;
            Slide(_restLeft, 420, Motion.Out);
            _scroll.BeginAnimation(OpacityProperty, Motion.FromTo(0, 1, 260, delay: 60));
            _scroll.Opacity = 1;
            // Tiles drift in one after another, a few pixels each.
            var delay = 40.0;
            foreach (UIElement card in Cards.Children)
            {
                if (delay > 200) break;
                Motion.Shift(card).BeginAnimation(TranslateTransform.XProperty, Motion.FromTo(14 + delay / 10, 0, 460, delay: delay));
                delay += 26;
            }
            Motion.Measure(GetType().Name + " open");
        });
        Opened();
    }

    private void Opened()
    {
        _buttonWasDown = AnyButtonDown();
        _outsideClick.Start();
        OnOpened();
        AnyVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The panel rises out of the tile: a small bump on the dock swells into the full panel.</summary>
    private bool _glassHooked;

    /// <summary>The card on the dock keeps its glass in place while it moves, grows or the dock's glass changes.</summary>
    private void HookDockGlass()
    {
        if (_glassHooked) return;
        _glassHooked = true;
        void Repaint()
        {
            if (ReferenceEquals(_onDock, this) && Services.Widgets.GlassLook.DockVeil is not null) PaintDockShape();
        }
        LocationChanged += (_, _) => Repaint();
        SizeChanged += (_, _) => Repaint();
        Services.Widgets.GlassLook.DockChanged += Repaint;
        Closed += (_, _) => Services.Widgets.GlassLook.DockChanged -= Repaint;
    }

    private void OpenFromDock(DockAnchor dock)
    {
        HookDockGlass();
        var area = SystemParameters.WorkArea;
        var top = area.Top + Math.Max(0, TopInset());
        var side = Fillet + Slack;
        _sheetMax = Math.Max(220, dock.Edge switch
        {
            Models.DockEdge.Top => area.Bottom - dock.Inner - EdgeGap - Slack,
            Models.DockEdge.Left or Models.DockEdge.Right => Math.Min(area.Height - 2 * (EdgeGap + side), dock.DockEnd - dock.DockStart - 2 * Fillet),
            _ => (dock.Inner - top - EdgeGap - Slack) * HeightShare
        });
        PlaceSheet(ContentHeight());
        _scrollTarget = 0;
        _scroll.ScrollToVerticalOffset(0);
        _scroll.BeginAnimation(OpacityProperty, null);
        _scroll.Opacity = 0;
        PaintDockShape();
        // No edge line: a light rim round the grown shape kept the card and its tile from reading as one piece.
        _shape.Visibility = Visibility.Visible;
        _outline.Visibility = Visibility.Collapsed;
        _onDock = this;
        _morphTween = null;
        _morph = 0;
        DrawMorph(0);
        Show();
        Services.GpuTransparency.Unfrost(this);
        _frosted = false;
        _sheet.Background = Brushes.Transparent;
        _sheet.BorderThickness = new Thickness(0);
        Motion.AfterFirstFrame(() =>
        {
            if (!IsOpen) return;
            Morph(1, 430, Motion.Settle);
            // The content shows once the shape is nearly full, rising a little with it.
            _scroll.BeginAnimation(OpacityProperty, Motion.FromTo(0, 1, 220, delay: 170));
            _scroll.Opacity = 1;
            var delay = 170.0;
            foreach (UIElement card in Cards.Children)
            {
                if (delay > 300) break;
                // Rising out of the dock: from the dock's side, a few pixels.
                var (property, from) = dock.Edge switch
                {
                    Models.DockEdge.Top => (TranslateTransform.YProperty, -12.0),
                    Models.DockEdge.Left => (TranslateTransform.XProperty, -12.0),
                    Models.DockEdge.Right => (TranslateTransform.XProperty, 12.0),
                    _ => (TranslateTransform.YProperty, 12.0)
                };
                Motion.Shift(card).BeginAnimation(property, Motion.FromTo(from, 0, 380, delay: delay));
                delay += 24;
            }
            Motion.Measure(GetType().Name + " open from dock");
        });
        Opened();
    }

    private static Color Opaque(Color color) => Color.FromArgb(255, color.R, color.G, color.B);

    /// <summary>The dock's material made solid: while a panel stands on the dock both are solid, so nothing behind
    /// them shows through as lines and the two read as one piece.</summary>
    public static Brush? SolidDockMaterial()
    {
        if (Application.Current?.TryFindResource("DockMaterial") is not GradientBrush { GradientStops.Count: > 0 } dock) return null;
        var solid = dock.Clone();
        foreach (var stop in solid.GradientStops) stop.Color = Opaque(stop.Color);
        solid.Freeze();
        return solid;
    }

    /// <summary>The dock's material for the shape (its gradient turned over so the colours meet at the seam) and its edge.</summary>
    private void PaintDockShape()
    {
        var dockGlass = DockGlass();
        Services.Widgets.GlassLook.Tiles(Resources, dockGlass is not null);
        if (dockGlass is { } glass)
        {
            _shape.Fill = glass;
            _outline.Stroke = Services.Widgets.GlassLook.Rim;
            return;
        }
        if (TryFindResource("DockMaterial") is GradientBrush { GradientStops.Count: > 0 } dock)
        {
            var stops = dock.GradientStops.OrderBy(stop => stop.Offset).ToList();
            var fill = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            fill.GradientStops.Add(new GradientStop(Opaque(stops[^1].Color), 0));
            fill.GradientStops.Add(new GradientStop(Opaque(stops[0].Color), 1));
            fill.Freeze();
            _shape.Fill = fill;
        }
        else _shape.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "DockMaterial");
        if (TryFindResource("DockBorderMaterial") is GradientBrush { GradientStops.Count: > 0 } edge)
            _outline.Stroke = new SolidColorBrush(edge.GradientStops.OrderBy(stop => stop.Offset).First().Color);
        else _outline.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "DockBorderMaterial");
    }

    /// <summary>
    /// The dock's glass for the grown shape: the wallpaper behind this window under the dock's own veil, so card and
    /// dock read as one piece of glass. Drawn in window coordinates and turned into the frame's (the shape is drawn as
    /// if the dock were at the bottom). Null when the dock is not glass.
    /// </summary>
    private Brush? DockGlass()
    {
        if (Services.Widgets.GlassLook.DockVeil is null || _frameSize.Width <= 0 || _frameSize.Height <= 0) return null;
        var o = ToWindow(new Point(0, 0));
        var x = ToWindow(new Point(1, 0));
        var y = ToWindow(new Point(0, 1));
        var toScreen = new Matrix(x.X - o.X, x.Y - o.Y, y.X - o.X, y.Y - o.Y, o.X, o.Y);
        toScreen.Translate(Left, Top);
        return Services.Widgets.GlassLook.Dock(toScreen);
    }

    private void Morph(double to, double milliseconds, IEasingFunction ease, double delay = 0, Action? done = null)
    {
        _morphTween = new Tween(_morph, to, _clock.Elapsed.TotalMilliseconds + delay, milliseconds, ease, done);
        StartTicking();
    }

    private void DrawMorph(double progress)
    {
        if (_dock is not { } dock || !IsVisible && progress > 0 && _frame.ActualWidth <= 0) return;
        var vertical = dock.Edge is Models.DockEdge.Left or Models.DockEdge.Right;
        var origin = new Point(Left, Top);
        var tileX = dock.TileCenter - (vertical ? Top : Left);
        var depth = _frameSize.Height;
        ApplyMorph(progress, _frameSize.Width, depth, tileX, dock.TileWidth, Fillet + Slack, range =>
        {
            if (range is not { } r)
            {
                DockSeam(null);
                return;
            }
            var a = ToWindow(new Point(r.Left, depth));
            var b = ToWindow(new Point(r.Right, depth));
            DockSeam((new Point(origin.X + a.X, origin.Y + a.Y), new Point(origin.X + b.X, origin.Y + b.Y)));
        });
    }

    /// <summary>
    /// Shapes the panel for <paramref name="progress"/>: from a bump as wide as part of the tile, centred on it,
    /// to the full panel; <paramref name="side"/> is the room left and right of the content.
    /// </summary>
    private void ApplyMorph(double progress, double width, double height, double tileX, double tileWidth, double side, Action<(double Left, double Right)?>? seam)
    {
        var fullWidth = width - 2 * side;
        var fullHeight = height - Slack; // down to the window's bottom, over the dock's edge
        if (fullWidth <= 0 || fullHeight <= 0) return;
        var startWidth = Math.Min(tileWidth * 0.62, fullWidth);
        var w = Math.Clamp(startWidth + (fullWidth - startWidth) * progress, 0, fullWidth + 2 * Slack - 2);
        var h = Math.Clamp(fullHeight * progress, 0, fullHeight + Slack - 1);
        var along = Math.Clamp(progress, 0, 1);
        var centre = tileX + (width / 2 - tileX) * along;
        var bottom = height;
        var topY = bottom - h;
        var left = centre - w / 2;
        var right = centre + w / 2;
        var foot = _foot; // the part standing over the dock's edge strip
        var rise = h - foot; // height above the dock's top edge
        var fillet = Math.Max(0, Math.Min(Fillet, rise * 0.45));
        var corner = Math.Max(0, Math.Min(Math.Min(DockCorner, w / 2), rise - fillet));

        if (rise < 0.5)
        {
            _shape.Data = _outline.Data = Geometry.Empty;
            seam?.Invoke(null);
            _sheet.Clip = Geometry.Empty;
            return;
        }
        _shape.Data = Outline(left, right, topY, bottom - foot, corner, fillet, closed: true, foot: foot);
        _outline.Data = Outline(left + 0.5, right - 0.5, topY + 0.5, bottom - foot, Math.Max(0, corner - 0.5), fillet, closed: false);
        // The dock leaves out its top edge where the panel stands on it, so the two read as one piece.
        seam?.Invoke((left - fillet, right + fillet));
        var clipHeight = bottom - foot - topY;
        if (clipHeight <= 0)
        {
            _sheet.Clip = Geometry.Empty;
            return;
        }
        // The frame's rectangle turned to the real edge, in the sheet's own coordinates.
        var a = ToWindow(new Point(left, topY));
        var b = ToWindow(new Point(left + w, topY + clipHeight));
        var clip = new Rect(a, b);
        clip.Offset(-_sheet.Margin.Left, -_sheet.Margin.Top);
        _sheet.Clip = new RectangleGeometry(clip, corner, corner);
    }

    /// <summary>Rounded top, straight sides, and inward curves that flare out into the dock at the bottom.</summary>
    /// <param name="foot">How far the filled shape continues straight down over the dock's edge strip.</param>
    private static Geometry Outline(double left, double right, double top, double bottom, double corner, double fillet, bool closed, double foot = 0)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(left - fillet, bottom + foot), closed, closed);
            if (foot > 0) g.LineTo(new Point(left - fillet, bottom), true, true);
            if (fillet > 0) g.ArcTo(new Point(left, bottom - fillet), new Size(fillet, fillet), 0, false, SweepDirection.Counterclockwise, true, true);
            g.LineTo(new Point(left, top + corner), true, true);
            if (corner > 0) g.ArcTo(new Point(left + corner, top), new Size(corner, corner), 0, false, SweepDirection.Clockwise, true, true);
            g.LineTo(new Point(right - corner, top), true, true);
            if (corner > 0) g.ArcTo(new Point(right, top + corner), new Size(corner, corner), 0, false, SweepDirection.Clockwise, true, true);
            g.LineTo(new Point(right, bottom - fillet), true, true);
            if (fillet > 0) g.ArcTo(new Point(right + fillet, bottom), new Size(fillet, fillet), 0, false, SweepDirection.Counterclockwise, true, true);
            if (foot > 0) g.LineTo(new Point(right + fillet, bottom + foot), true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    public void Close(bool animated)
    {
        if (!IsVisible || _closing) return;
        _closing = true;
        StopTyping();
        _outsideClick.Stop();
        _closedAt = DateTime.UtcNow;
        if (ReferenceEquals(_open, this)) _open = null;
        if (!animated)
        {
            Finish();
            return;
        }
        if (_dock is not null)
        {
            // Content first, then the shape sinks back into the tile.
            _scroll.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(110)) { EasingFunction = Motion.In });
            Morph(0, 250, Motion.In, delay: 60, done: () =>
            {
                if (_closing) Finish();
            });
            return;
        }
        _scroll.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) { EasingFunction = Motion.In });
        Slide(_awayLeft, 240, Motion.In, () =>
        {
            if (_closing) Finish();
        });
    }

    private void Finish()
    {
        _leftTween = null;
        _morphTween = null;
        if (_dock is not null) DockSeam(null);
        if (ReferenceEquals(_onDock, this)) _onDock = null;
        _shrink.Stop();
        Hide();
        _closing = false;
        _scroll.BeginAnimation(OpacityProperty, null);
        AnyVisibilityChanged?.Invoke(this, EventArgs.Empty);
        OnHidden();
    }

    /// <summary>The frosted glass, tinted to the current theme (applied on every opening so a theme change shows).</summary>
    private void Frost()
    {
        var tint = TryFindResource("PanelFrostTint") is Color color ? color : Color.FromArgb(0xB8, 0x1C, 0x1D, 0x22);
        _frosted = Services.GpuTransparency.Frost(this, tint);
        if (_frosted)
        {
            _sheet.Background = Brushes.Transparent;
            _sheet.BorderThickness = new Thickness(0);
            return;
        }
        _sheet.SetResourceReference(Border.BackgroundProperty, "WidgetPopupFill");
        _sheet.SetResourceReference(Border.BorderBrushProperty, "DockBorderMaterial");
        _sheet.BorderThickness = new Thickness(1);
    }

    private double ContentHeight()
    {
        var available = new Size(CardWidth + 2 * Pad, double.PositiveInfinity);
        Cards.Measure(available);
        var header = 0.0;
        if (Header.Visibility == Visibility.Visible)
        {
            Header.Measure(available);
            header = Header.DesiredSize.Height;
        }
        return Math.Min(_sheetMax, Math.Ceiling(header + Cards.DesiredSize.Height)); // DesiredSize includes the margins
    }

    // ---- Window movement (position and height follow the same easing as the tiles) ---------------

    private void Slide(double to, double milliseconds, IEasingFunction ease, Action? done = null)
    {
        _leftTween = new Tween(Left, to, _clock.Elapsed.TotalMilliseconds, milliseconds, ease, done);
        StartTicking();
    }

    /// <summary>
    /// Fits the glass to the tiles. Resizing a window is slow, so it never happens frame by frame:
    /// a taller glass is set at once before the tiles move, a shorter one after they have settled.
    /// </summary>
    private void Fit(double to)
    {
        _shrink.Stop();
        if (to > SheetHeight + 0.5) SetSheetHeight(to);
        else if (to < SheetHeight - 0.5) _shrink.Start();
    }

    private double SheetHeight => _dock is null ? ActualHeight : _contentExtent;

    /// <summary>Sets the glass's height; a dock-grown panel keeps its foot on the dock.</summary>
    private void SetSheetHeight(double height)
    {
        if (_dock is null)
        {
            Height = height;
            return;
        }
        PlaceSheet(height);
        DrawMorph(_morph);
    }

    /// <summary>Places a dock-grown panel around content of this height (and the card's width).</summary>
    private void PlaceSheet(double contentHeight)
    {
        if (_dock is not { } dock) return;
        var width = CardWidth + 2 * Pad;
        if (dock.Edge is Models.DockEdge.Left or Models.DockEdge.Right) PlaceOnDock(dock, contentHeight, width);
        else PlaceOnDock(dock, width, contentHeight);
        // The card grew or shrank: its glass is placed again for the new shape.
        if (ReferenceEquals(_onDock, this) && Services.Widgets.GlassLook.DockVeil is not null) PaintDockShape();
    }

    private void StartTicking()
    {
        if (_ticking) return;
        _ticking = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        if (_leftTween is { } left && Step(left, now, out var x))
        {
            Left = x;
            if (now - left.Start >= left.Duration && ReferenceEquals(_leftTween, left))
            {
                _leftTween = null;
                left.Done?.Invoke();
            }
        }
        if (_morphTween is { } morph)
        {
            var t = Math.Clamp((now - morph.Start) / morph.Duration, 0, 1);
            _morph = morph.From + (morph.To - morph.From) * morph.Ease.Ease(t);
            DrawMorph(_morph);
            if (t >= 1 && ReferenceEquals(_morphTween, morph))
            {
                _morphTween = null;
                morph.Done?.Invoke();
            }
        }
        if (_leftTween is null && _morphTween is null)
        {
            _ticking = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    private static bool Step(Tween tween, double now, out double value)
    {
        var t = Math.Clamp((now - tween.Start) / tween.Duration, 0, 1);
        value = Math.Round(tween.From + (tween.To - tween.From) * tween.Ease.Ease(t), 2);
        return true;
    }

    /// <summary>
    /// Rebuilds the tiles and moves each one from its old place to its new one while the glass
    /// grows or shrinks to fit. New tiles come out of <paramref name="origin"/> (a key of an old
    /// tile) or fade in where they land.
    /// </summary>
    protected void Rebuild(Func<string, Origin?>? origin = null)
    {
        if (!IsOpen)
        {
            Build();
        TrimLastGap();
            return;
        }
        var before = Positions();
        Build();
        TrimLastGap();
        Cards.UpdateLayout();
        var after = Positions();
        Fit(ContentHeight());
        // Every tile is put where it was (or where it comes from) right away; the movement starts
        // once that frame is on screen, so a slow first frame (new tiles, a resized glass) never skips.
        var starts = new List<Action>();
        var fresh = 0;
        foreach (UIElement child in Cards.Children)
        {
            if (child is not FrameworkElement { Tag: string key } element || !after.TryGetValue(key, out var now)) continue;
            var shift = Motion.Shift(element);
            if (before.TryGetValue(key, out var was))
            {
                if (Math.Abs(was - now) > 0.5) starts.Add(Move(shift, was - now, 0));
                continue;
            }
            var source = origin?.Invoke(key);
            double? start = source is { } o && before.TryGetValue(o.Key, out var at) ? at : null;
            var from = start is { } y ? y - now : -8;
            var delay = Math.Min(fresh++ * 30, 180);
            // Tiles coming out of one place: the first one drawn on top, the others appear as they leave it.
            if (start is not null) Panel.SetZIndex(element, 100 - fresh);
            starts.Add(Move(shift, from, source is { Continues: true } && start is not null ? 0 : delay));
            if (source is { Continues: true } && start is not null) continue;
            // Barely visible rather than invisible: WPF skips drawing invisible tiles, so their cached
            // picture would otherwise be made mid-movement, in one slow frame.
            element.Opacity = 0.01;
            var fadeDelay = delay + (start is null ? 0 : 60);
            starts.Add(() =>
            {
                element.Opacity = 1;
                element.BeginAnimation(OpacityProperty, Motion.FromTo(0.01, 1, start is null ? 300 : 240, Motion.Out, fadeDelay));
            });
        }
        Motion.AfterFirstFrame(() =>
        {
            foreach (var begin in starts) begin();
            Motion.Measure(GetType().Name + " rebuild");
        });
    }

    /// <summary>Holds the tile at <paramref name="from"/> now and returns what slides it home.</summary>
    private static Action Move(TranslateTransform shift, double from, double delay)
    {
        shift.BeginAnimation(TranslateTransform.YProperty, null);
        shift.Y = from;
        return () =>
        {
            shift.Y = 0;
            shift.BeginAnimation(TranslateTransform.YProperty, Motion.FromTo(from, 0, delay > 0 ? 460 : 420, Motion.Out, delay));
        };
    }

    /// <summary>The glass's own padding is the space under the last tile; its gap below would double it.</summary>
    private void TrimLastGap()
    {
        if (Cards.Children.Count > 0 && Cards.Children[^1] is FrameworkElement last)
            last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
    }

    private Dictionary<string, double> Positions()
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (UIElement child in Cards.Children)
            if (child is FrameworkElement { Tag: string key, IsVisible: true } element)
                map[key] = element.TranslatePoint(new Point(0, 0), Cards).Y; // where it shows now, mid-move included
        return map;
    }

    /// <summary>Moves one tile out to the right and fades it, then runs <paramref name="done"/>.</summary>
    protected static void SlideAway(UIElement card, Action done)
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = Motion.In };
        fade.Completed += (_, _) => done();
        Motion.Shift(card).BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(90, TimeSpan.FromMilliseconds(240)) { EasingFunction = Motion.In });
        card.IsHitTestVisible = false;
        card.BeginAnimation(OpacityProperty, fade);
    }

    private void SmoothWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var limit = _scroll.ScrollableHeight;
        if (limit <= 0) return;
        _scrollTarget = Math.Clamp(Math.Min(_scrollTarget, limit) - e.Delta * 0.9, 0, limit);
        BeginAnimation(ScrollOffsetProperty, new DoubleAnimation(_scroll.VerticalOffset, _scrollTarget, TimeSpan.FromMilliseconds(260)) { EasingFunction = Motion.Out });
    }

    protected static FrameworkElement Spaced(FrameworkElement element, double gap = 8)
    {
        element.Margin = new Thickness(0, 0, 0, gap);
        return element;
    }

    /// <summary>A light rounded tile on the glass (no shadow: the glass carries the depth).</summary>
    protected Border Glass(UIElement child, Thickness padding, double radius = 14, double? width = null)
    {
        var border = new Border
        {
            Width = width ?? CardWidth,
            Padding = padding,
            CornerRadius = new CornerRadius(radius),
            BorderThickness = new Thickness(1),
            Child = child
        };
        border.SetResourceReference(Border.BackgroundProperty, "PanelTile");
        border.SetResourceReference(Border.BorderBrushProperty, "PanelTileStroke");
        return border;
    }

    /// <summary>A small rounded button with text or an icon ("Daha az göster", "Temizle").</summary>
    protected static Border Capsule(UIElement content, Action click, string? tooltip = null)
    {
        var capsule = new Border
        {
            Height = 26,
            MinWidth = 26,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(13),
            Child = content,
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (content is FrameworkElement inner)
        {
            inner.VerticalAlignment = VerticalAlignment.Center;
            inner.HorizontalAlignment = HorizontalAlignment.Center;
        }
        capsule.SetResourceReference(Border.BackgroundProperty, "PanelTile");
        capsule.MouseEnter += (_, _) => capsule.SetResourceReference(Border.BackgroundProperty, "PanelTileHover");
        capsule.MouseLeave += (_, _) => capsule.SetResourceReference(Border.BackgroundProperty, "PanelTile");
        capsule.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return capsule;
    }

    private void WatchOutsideClick()
    {
        var down = AnyButtonDown();
        if (down && !_buttonWasDown && !IsMouseOver) Close(animated: true);
        _buttonWasDown = down;
    }

    private static bool AnyButtonDown() => (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is not HwndSource source) return;
        Services.GpuTransparency.Apply(this);
        var style = GetWindowLongPtr(source.Handle, -20).ToInt64();
        SetWindowLongPtr(source.Handle, -20, new nint((style | 0x00000080 | 0x08000000) & ~0x00040000));
        source.AddHook((nint _, int message, nint _, nint _, ref bool handled) =>
        {
            if (message != 0x0021 /* WM_MOUSEACTIVATE */ || _typing) return 0;
            handled = true;
            return 3; // MA_NOACTIVATE
        });
    }

    private bool _typing;

    /// <summary>
    /// Lets the panel take the keyboard (a password box) until it closes. Normally clicking it never takes the focus
    /// away from the window being worked in, so nothing could be typed into it.
    /// </summary>
    protected void AllowTyping()
    {
        if (_typing || PresentationSource.FromVisual(this) is not HwndSource source) return;
        _typing = true;
        var style = GetWindowLongPtr(source.Handle, -20).ToInt64();
        SetWindowLongPtr(source.Handle, -20, new nint(style & ~0x08000000L));
        Activate();
    }

    private void StopTyping()
    {
        if (!_typing || PresentationSource.FromVisual(this) is not HwndSource source) return;
        _typing = false;
        var style = GetWindowLongPtr(source.Handle, -20).ToInt64();
        SetWindowLongPtr(source.Handle, -20, new nint(style | 0x08000000L));
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}
