using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using QiDock.Models;
using QiDock.Services.Widgets;

namespace QiDock.Widgets;

/// <summary>A widget shown in the dock: a tile plus optional popup.</summary>
public abstract class DockWidget : IDisposable
{
    public abstract string Id { get; }
    public abstract FrameworkElement View { get; }
    public virtual bool IsPopupOpen => false;

    /// <summary>Raised when the tile's width or visibility changes so the dock can resize.</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>Raised when the widget wants the settings window (for example "change city").</summary>
    public event EventHandler? SettingsRequested;

    public virtual void Start() { }
    public virtual void Stop() { }
    public virtual void ApplySettings(WidgetSettings settings) { }
    public virtual void ClosePopup() { }

    /// <summary>Pauses (false) or resumes (true) per-second work while the widget is off screen.</summary>
    public virtual void SetLive(bool live) { }

    /// <summary>The widget settings last applied (kept by the bar before <see cref="ApplySettings"/>).</summary>
    public WidgetSettings Current { get; internal set; } = new();

    /// <summary>Raised with a change to the widget settings (to-dos, colors, shelf…); the bar saves it.</summary>
    public event EventHandler<Func<WidgetSettings, WidgetSettings>>? SettingsUpdateRequested;

    protected void UpdateSettings(Func<WidgetSettings, WidgetSettings> change) => SettingsUpdateRequested?.Invoke(this, change);
    public virtual void Dispose() => Stop();

    protected void RaiseLayoutChanged() => LayoutChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>The dock is being edited (or no longer).</summary>
    public virtual void SetEditing(bool editing) { }

    /// <summary>
    /// The tile opens out sideways for a moment (true) or has closed again (false). Meanwhile the dock keeps its
    /// width: what stands after the tile slides out of the dock's end and comes back when it closes.
    /// </summary>
    public event EventHandler<bool>? Expanding;

    protected void RaiseExpanding(bool expanding) => Expanding?.Invoke(this, expanding);
    protected void RequestSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Popup anchored centered above a dock tile. The dock window never takes activation, so WPF's
/// StaysOpen=false capture is unreliable here (it drops when content changes). Instead the popup
/// stays open and closes itself when a mouse button goes down outside the card and its tile.
/// </summary>
public sealed class WidgetPopup
{
    private readonly Popup _popup;
    private readonly FrameworkElement _target;
    private readonly Border _surface;
    private readonly System.Windows.Threading.DispatcherTimer _outsideClickWatch = new(System.Windows.Threading.DispatcherPriority.Input)
    {
        Interval = TimeSpan.FromMilliseconds(40)
    };
    private bool _buttonWasDown;
    private UIElement? _content;
    private double _width, _sheetWidth;
    private DockSheet? _sheet;
    public event EventHandler? Opened;

    /// <summary>The card closed (from its popup or from the dock sheet).</summary>
    public event EventHandler? Closed;

    /// <summary>Any widget card opened (the bar hides its profile tabs so they do not cover it).</summary>
    public static event EventHandler? AnyOpened;

    /// <summary>Any widget card or strip closed (a dock that hides itself may go now).</summary>
    public static event EventHandler? AnyClosed;

    internal static void RaiseAnyClosed() => AnyClosed?.Invoke(null, EventArgs.Empty);

    public WidgetPopup(FrameworkElement target)
    {
        _target = target;
        // Placement=Top aligns the popup window's bottom with the tile's top. The window must not overlap
        // the tile (it would swallow clicks meant for it), so the card's bottom margin is the gap.
        _popup = new Popup
        {
            AllowsTransparency = true,
            StaysOpen = true,
            Placement = PlacementMode.Top,
            PlacementTarget = target,
            PopupAnimation = PopupAnimation.Fade,
            VerticalOffset = 0
        };
        _surface = new Border { Style = (Style)Application.Current.FindResource("WidgetPopupSurface") };
        _surface.Margin = new Thickness(18, 18, 18, 10);
        _popup.Child = _surface;
        _popup.Opened += (_, _) =>
        {
            _buttonWasDown = AnyButtonDown();
            _outsideClickWatch.Start();
            Opened?.Invoke(this, EventArgs.Empty);
            AnyOpened?.Invoke(this, EventArgs.Empty);
        };
        _popup.Closed += (_, _) =>
        {
            _outsideClickWatch.Stop();
            Closed?.Invoke(this, EventArgs.Empty);
            AnyClosed?.Invoke(this, EventArgs.Empty);
        };
        // Keep the card centered above the tile when its size changes while open.
        _surface.SizeChanged += (_, _) =>
        {
            Center();
            if (!_popup.IsOpen) return;
            _popup.VerticalOffset = 0.01;
            _popup.VerticalOffset = 0;
        };
        _outsideClickWatch.Tick += (_, _) => WatchOutsideClick();
    }

    public bool IsOpen => _popup.IsOpen || _sheet?.IsOpen == true;

    /// <summary>Replaces the popup body; the styled surface stays in place.</summary>
    public void SetContent(UIElement content, double width)
    {
        _content = content;
        _width = width;
        _surface.Width = width;
        if (_sheet?.IsVisible == true) _sheet.Body = content;
        else
        {
            if (_sheet is not null) _sheet.Body = null;
            _surface.Child = content;
        }
        Center();
    }

    /// <summary>
    /// While the widget sits in the dock its card grows out of the dock like the bell's list. Returns false when
    /// the tile is not in the dock (the card then opens as a popup).
    /// </summary>
    private bool ToggleSheet()
    {
        if (_sheet?.IsOpen == true)
        {
            _sheet.Close(animated: true);
            return true;
        }
        if (_popup.IsOpen || _content is null || EdgePanel.DockAnchorFor(_target) is not { } anchor) return false;
        if (_sheet is null || Math.Abs(_sheetWidth - _width) > 0.5)
        {
            _sheet?.Close();
            var sheet = new DockSheet(_width);
            _sheet = sheet;
            _sheetWidth = _width;
            sheet.SheetOpened += (_, _) =>
            {
                Opened?.Invoke(this, EventArgs.Empty);
                AnyOpened?.Invoke(this, EventArgs.Empty);
            };
            sheet.SheetHidden += (_, _) =>
            {
                Closed?.Invoke(this, EventArgs.Empty);
                AnyClosed?.Invoke(this, EventArgs.Empty);
            };
        }
        _surface.Child = null;
        _sheet.Body = _content;
        _sheet.ToggleFromDock(anchor);
        return true;
    }

#if DEBUG
    /// <summary>Debug snapshot run: Toggle records the popup here instead of opening it.</summary>
    internal static List<WidgetPopup>? Rehearsal;

    /// <summary>Debug snapshot run: takes the card out of its popup so it can be drawn to a picture.</summary>
    internal Border DetachSurface()
    {
        _popup.Child = null;
        _surface.Margin = new Thickness(0);
        return _surface;
    }

    /// <summary>Debug snapshot run: the card in the sheet it grows out of the dock in (as <see cref="ToggleSheet"/> does).</summary>
    internal DockSheet RehearseSheet()
    {
        var sheet = new DockSheet(_width);
        _surface.Child = null;
        sheet.Body = _content;
        return sheet;
    }
#endif

    public void Toggle()
    {
#if DEBUG
        if (Rehearsal is not null)
        {
            Rehearsal.Add(this);
            return;
        }
#endif
        if (ToggleSheet()) return;
        if (!_popup.IsOpen)
        {
            if (_surface.Child is null && _content is not null)
            {
                if (_sheet is not null) _sheet.Body = null;
                _surface.Child = _content;
            }
            var below = Window.GetWindow(_target) is { } window && window.Top < SystemParameters.WorkArea.Height / 2;
            _popup.Placement = below ? PlacementMode.Bottom : PlacementMode.Top;
            _surface.Margin = below ? new Thickness(18, 10, 18, 18) : new Thickness(18, 18, 18, 10);
            Center();
        }
        _popup.IsOpen = !_popup.IsOpen;
    }

    public void Close()
    {
        _popup.IsOpen = false;
        if (_sheet?.IsOpen == true) _sheet.Close(animated: true);
    }

    /// <summary>
    /// The bar never takes focus by itself; a popup with a text box asks for it so typing works.
    /// Allowed because the user just clicked our window.
    /// </summary>
    public void FocusInput(IInputElement element)
    {
        Window? owner = _sheet?.IsOpen == true ? _sheet : Window.GetWindow(_target);
        if (owner is not null && PresentationSource.FromVisual(owner) is System.Windows.Interop.HwndSource source)
            SetForegroundWindow(source.Handle);
        _popup.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => Keyboard.Focus(element));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    private void Center()
    {
        _surface.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _popup.HorizontalOffset = (_target.ActualWidth - _surface.DesiredSize.Width) / 2;
    }

    private void WatchOutsideClick()
    {
        var down = AnyButtonDown();
        var pressed = down && !_buttonWasDown;
        _buttonWasDown = down;
        if (!pressed || !GetCursorPos(out var cursor)) return;
        // The tile handles its own click (toggle); clicks inside the card are the card's.
        if (Contains(_surface, cursor) || Contains(_target, cursor)) return;
        _popup.IsOpen = false;
    }

    private static bool Contains(FrameworkElement element, NativePoint cursor)
    {
        if (PresentationSource.FromVisual(element) is null || element.ActualWidth <= 0) return false;
        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return cursor.X >= topLeft.X && cursor.X <= bottomRight.X && cursor.Y >= topLeft.Y && cursor.Y <= bottomRight.Y;
    }

    private static bool AnyButtonDown() =>
        (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0 || (GetAsyncKeyState(0x04) & 0x8000) != 0;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
}

internal static class Ui
{
    public static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush Solid(byte a, byte r, byte g, byte b) => (SolidColorBrush)Freeze(new SolidColorBrush(Color.FromArgb(a, r, g, b)));
    public static SolidColorBrush Solid(string hex) => (SolidColorBrush)Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));

    public static LinearGradientBrush Vertical(string top, string bottom) => (LinearGradientBrush)Freeze(new LinearGradientBrush(
        (Color)ColorConverter.ConvertFromString(top), (Color)ColorConverter.ConvertFromString(bottom), 90));

    public static LinearGradientBrush Diagonal(Color from, Color to) => (LinearGradientBrush)Freeze(new LinearGradientBrush(from, to, new Point(0, 0), new Point(1, 1)));

    public static readonly Brush White = Brushes.White;
    public static readonly Brush WhiteSoft = Solid(0xD9, 0xFF, 0xFF, 0xFF);

    public static TextBlock Text(string text = "", double size = 12, FontWeight? weight = null, string? brushKey = "TextPrimary", Brush? brush = null)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (brush is not null) block.Foreground = brush;
        else if (brushKey is not null) block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }

    /// <summary>Lucide-style outline icon.</summary>
    public static Path Icon(string geometryKey, double size, Brush? brush = null, string? brushKey = "TextPrimary", double stroke = 1.9)
    {
        var path = new Path
        {
            Data = (Geometry)Application.Current.FindResource(geometryKey),
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            StrokeThickness = stroke,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            SnapsToDevicePixels = true
        };
        if (brush is not null) path.Stroke = brush;
        else if (brushKey is not null) path.SetResourceReference(Shape.StrokeProperty, brushKey);
        return path;
    }

    /// <summary>Solid glyph (play, pause, bolt…).</summary>
    public static Path Glyph(string geometryKey, double size, Brush? brush = null, string? brushKey = "TextPrimary")
    {
        var path = new Path
        {
            Data = (Geometry)Application.Current.FindResource(geometryKey),
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        if (brush is not null) path.Fill = brush;
        else if (brushKey is not null) path.SetResourceReference(Shape.FillProperty, brushKey);
        return path;
    }

    public static Button IconButton(UIElement content, RoutedEventHandler click, string? tooltip = null, double width = 26, double height = 24)
    {
        var button = new Button { Content = content, Width = width, Height = height, ToolTip = tooltip };
        button.SetResourceReference(FrameworkElement.StyleProperty, "WidgetIconButton");
        button.Click += click;
        if (tooltip is not null) System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        return button;
    }

    /// <summary>Thin rounded progress bar.</summary>
    public static Grid Bar(double percent, double width, double height, Brush fill)
    {
        var grid = new Grid { Width = width, Height = height, VerticalAlignment = VerticalAlignment.Center };
        var track = new Border { CornerRadius = new CornerRadius(height / 2) };
        track.SetResourceReference(Border.BackgroundProperty, "WidgetTrack");
        grid.Children.Add(track);
        grid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(height / 2),
            Background = fill,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = Math.Max(percent > 0 ? height : 0, width * Math.Clamp(percent, 0, 100) / 100)
        });
        return grid;
    }

    private static readonly Brush UsageLow = Solid("#FF32D74B");
    private static readonly Brush UsageMedium = Solid("#FFFFD60A");
    private static readonly Brush UsageHigh = Solid("#FFFF9F0A");
    private static readonly Brush UsageCritical = Solid("#FFFF453A");

    /// <summary>Load colour: green when low, yellow in the middle, orange when high, red when nearly full.</summary>
    public static Brush UsageBrush(double percent) => percent switch
    {
        >= 85 => UsageCritical,
        >= 70 => UsageHigh,
        >= 50 => UsageMedium,
        _ => UsageLow
    };

    /// <summary>Short message pill at the top of the screen (set by the main window).</summary>
    public static Action<string, string>? HudHandler { get; set; }

    public static void ShowHud(string iconKey, string text) => HudHandler?.Invoke(iconKey, text);

    /// <summary>Shell icon of a file, folder or app as an image.</summary>
    public static FrameworkElement FileIcon(string path, double size)
    {
        var source = QiDock.Services.ApplicationIconProvider.GetForFile(path);
        if (source is null) return Icon(System.IO.Directory.Exists(path) ? "IconFolder" : "IconFile", size * 0.8);
        return new Image { Source = source, Width = size, Height = size, Stretch = Stretch.Uniform };
    }

    /// <summary>Popup title row: title on the left, extra buttons and a close button on the right.</summary>
    public static DockPanel Header(string title, Action close, params UIElement[] buttons)
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = true };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in buttons) right.Children.Add(button);
        right.Children.Add(IconButton(Icon("IconClose", 12), (_, _) => close(), QiDock.Services.UiText.Get("Close")));
        DockPanel.SetDock(right, Dock.Right);
        header.Children.Add(right);
        header.Children.Add(Text(title, 13, FontWeights.SemiBold));
        return header;
    }

    /// <summary>
    /// A scroll area without a scroll bar (the wheel still scrolls): while more content follows below,
    /// the bottom edge fades out instead of cutting the last row off.
    /// </summary>
    public static ScrollViewer FadingScroller(FrameworkElement content, double maxHeight)
    {
        var scroller = new ScrollViewer
        {
            Content = content,
            MaxHeight = maxHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var fade = (Brush)Freeze(new LinearGradientBrush(
            [new GradientStop(Colors.Black, 0), new GradientStop(Colors.Black, 1 - 28 / maxHeight), new GradientStop(Colors.Transparent, 1)],
            new Point(0, 0), new Point(0, 1)));
        void Update() => scroller.OpacityMask = scroller.VerticalOffset < scroller.ScrollableHeight - 1 ? fade : null;
        scroller.ScrollChanged += (_, _) => Update();
        scroller.Loaded += (_, _) => Update();
        return scroller;
    }

    public static Button Pill(string text, RoutedEventHandler click, string? iconKey = null)
    {
        UIElement content = Text(text, 11.5, FontWeights.SemiBold);
        if (iconKey is not null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = Icon(iconKey, 12);
            icon.Margin = new Thickness(0, 0, 6, 0);
            row.Children.Add(icon);
            row.Children.Add(content);
            content = row;
        }
        var button = new Button { Content = content, Height = 28 };
        button.SetResourceReference(FrameworkElement.StyleProperty, "WidgetPillButton");
        button.Click += click;
        return button;
    }

    public static Button Tile(UIElement content, RoutedEventHandler click, double horizontalPadding = 10)
    {
        var tile = new Button { Content = content, Padding = new Thickness(horizontalPadding, 0, horizontalPadding, 0) };
        tile.SetResourceReference(FrameworkElement.StyleProperty, "WidgetTileButton");
        tile.Click += click;
        return tile;
    }

    /// <summary>"3 dk önce", "2 gün önce"…</summary>
    public static string Ago(DateTime time)
    {
        var span = DateTime.Now - time;
        if (span.TotalMinutes < 1) return QiDock.Services.UiText.Get("AgoNow");
        if (span.TotalHours < 1) return QiDock.Services.UiText.Format("AgoMinutes", (int)span.TotalMinutes);
        if (span.TotalDays < 1) return QiDock.Services.UiText.Format("AgoHours", (int)span.TotalHours);
        if (span.TotalDays < 31) return QiDock.Services.UiText.Format("AgoDays", (int)span.TotalDays);
        if (span.TotalDays < 365) return QiDock.Services.UiText.Format("AgoMonths", (int)(span.TotalDays / 30));
        return QiDock.Services.UiText.Format("AgoYears", (int)(span.TotalDays / 365));
    }

    public static void Open(string target)
    {
#if DEBUG
        if (WidgetPopup.Rehearsal is not null) return; // a snapshot run never opens anything on the user's screen
#endif
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not open {0}: {1}", target, ex.Message);
        }
    }
}

/// <summary>Colored vector weather icons drawn in a 24x24 space.</summary>
internal static class WeatherArt
{
    private static readonly Brush SunFill = Ui.Solid("#FFFFC83D");
    private static readonly Brush SunStroke = Ui.Solid("#FFFFB000");
    private static readonly Brush CloudFill = Ui.Vertical("#FFFFFFFF", "#FFDCE3EC");
    private static readonly Brush CloudDarkFill = Ui.Vertical("#FFC5CEDA", "#FF9AA6B5");
    private static readonly Brush RainStroke = Ui.Solid("#FF5AB8FF");
    private static readonly Brush SnowFill = Ui.Solid("#FFFFFFFF");
    private static readonly Brush MoonFill = Ui.Solid("#FFF4E9B8");
    private static readonly Brush FogStroke = Ui.Solid("#FFD4DAE2");
    private static readonly Brush BoltFill = Ui.Solid("#FFFFD60A");

    private static readonly Geometry CloudShape = Geometry.Parse(
        "M7 20 H17.5 A4.5 4.5 0 0 0 18.2 11.06 A6 6 0 0 0 6.6 11.6 A4.2 4.2 0 0 0 7 20 Z");
    private static readonly Geometry SmallCloudShape = Geometry.Parse(
        "M8 21 H17.5 A3.8 3.8 0 0 0 18 13.43 A5 5 0 0 0 8.4 14 A3.5 3.5 0 0 0 8 21 Z");
    private static readonly Geometry MoonShape = Geometry.Parse(
        "M14.5 2.5 A8 8 0 1 0 21.5 13.5 A6.3 6.3 0 0 1 14.5 2.5 Z");

    public static FrameworkElement Create(int code, bool isDay, double size)
    {
        var kind = WeatherService.Classify(code);
        var canvas = new Canvas { Width = 24, Height = 24 };
        switch (kind)
        {
            case WeatherKind.Clear:
                if (isDay) AddSun(canvas, 12, 12, 5.2);
                else AddMoon(canvas, 0, 0, 1);
                break;
            case WeatherKind.PartlyCloudy:
                if (isDay) AddSun(canvas, 15.5, 8.5, 4.2);
                else AddMoon(canvas, 5, -1, 0.7);
                canvas.Children.Add(new Path { Data = SmallCloudShape, Fill = CloudFill, RenderTransform = new TranslateTransform(-3, 0) });
                break;
            case WeatherKind.Cloudy:
                canvas.Children.Add(new Path { Data = SmallCloudShape, Fill = CloudDarkFill, RenderTransform = new TranslateTransform(1, -6) });
                canvas.Children.Add(new Path { Data = CloudShape, Fill = CloudFill, RenderTransform = new TranslateTransform(-1, 0) });
                break;
            case WeatherKind.Fog:
                canvas.Children.Add(new Path { Data = CloudShape, Fill = CloudDarkFill, RenderTransform = new TranslateTransform(0, -4) });
                for (var i = 0; i < 2; i++)
                    canvas.Children.Add(new Line { X1 = 4 + i * 2, X2 = 20 - i * 2, Y1 = 19 + i * 3, Y2 = 19 + i * 3, Stroke = FogStroke, StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                break;
            case WeatherKind.Drizzle:
            case WeatherKind.Rain:
                canvas.Children.Add(new Path { Data = CloudShape, Fill = CloudFill, RenderTransform = new TranslateTransform(0, -5) });
                var drops = kind == WeatherKind.Rain ? 3 : 2;
                for (var i = 0; i < drops; i++)
                {
                    var x = drops == 3 ? 8 + i * 4 : 10 + i * 4;
                    canvas.Children.Add(new Line { X1 = x, X2 = x - 1.5, Y1 = 17, Y2 = 22, Stroke = RainStroke, StrokeThickness = 1.9, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                }
                break;
            case WeatherKind.Snow:
                canvas.Children.Add(new Path { Data = CloudShape, Fill = CloudFill, RenderTransform = new TranslateTransform(0, -5) });
                foreach (var (x, y) in new[] { (8.0, 18.5), (12.0, 21.0), (16.0, 18.5) })
                {
                    var flake = new Ellipse { Width = 2.8, Height = 2.8, Fill = SnowFill, Stroke = RainStroke, StrokeThickness = 0.6 };
                    Canvas.SetLeft(flake, x - 1.4);
                    Canvas.SetTop(flake, y - 1.4);
                    canvas.Children.Add(flake);
                }
                break;
            case WeatherKind.Thunder:
                canvas.Children.Add(new Path { Data = CloudShape, Fill = CloudDarkFill, RenderTransform = new TranslateTransform(0, -5) });
                canvas.Children.Add(new Path { Data = Geometry.Parse("M13 13 L9 19 H12 L11 24 L16 17 H13 Z"), Fill = BoltFill });
                break;
        }

        return new Viewbox { Width = size, Height = size, Child = canvas, Stretch = Stretch.Uniform };
    }

    private static void AddSun(Canvas canvas, double cx, double cy, double radius)
    {
        for (var i = 0; i < 8; i++)
        {
            var angle = i * Math.PI / 4;
            canvas.Children.Add(new Line
            {
                X1 = cx + Math.Cos(angle) * (radius + 1.6),
                Y1 = cy + Math.Sin(angle) * (radius + 1.6),
                X2 = cx + Math.Cos(angle) * (radius + 3.4),
                Y2 = cy + Math.Sin(angle) * (radius + 3.4),
                Stroke = SunStroke,
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }
        var sun = new Ellipse { Width = radius * 2, Height = radius * 2, Fill = SunFill };
        Canvas.SetLeft(sun, cx - radius);
        Canvas.SetTop(sun, cy - radius);
        canvas.Children.Add(sun);
    }

    private static void AddMoon(Canvas canvas, double x, double y, double scale)
    {
        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform(scale, scale));
        group.Children.Add(new TranslateTransform(x, y));
        canvas.Children.Add(new Path { Data = MoonShape, Fill = MoonFill, RenderTransform = group });
    }

    /// <summary>Tile background that follows the sky.</summary>
    public static Brush Sky(int code, bool isDay)
    {
        var kind = WeatherService.Classify(code);
        if (!isDay) return Ui.Vertical("#FF27346B", "#FF141B3D");
        return kind switch
        {
            WeatherKind.Clear or WeatherKind.PartlyCloudy => Ui.Vertical("#FF5AA9F5", "#FF2F72DA"),
            WeatherKind.Cloudy or WeatherKind.Fog => Ui.Vertical("#FF8697AD", "#FF5E6E85"),
            WeatherKind.Thunder => Ui.Vertical("#FF55607A", "#FF2F3648"),
            _ => Ui.Vertical("#FF6683A6", "#FF3E5778")
        };
    }
}
