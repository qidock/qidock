using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using QiDock.Models;
using QiDock.Services;

namespace QiDock;

/// <summary>
/// A small picture of the desktop in the settings window: the dock as the choices in the window would make it
/// (edge, position, look, opacity, where the widgets go, auto-hide), redrawn on every change before anything is
/// applied. Pointing at its icons shows the magnification at the chosen size, and resting on one shows the window
/// preview after the same half second the real dock waits.
/// </summary>
public sealed class DockPreview : Border
{
    private const double W = 640, H = 196, Tile = 24, Gap = 5, Bar = 36, Margin8 = 8;
    private static readonly string[] AppColors = ["#FF0A84FF", "#FFFF9F0A", "#FF30D158", "#FFD97757", "#FFBF5AF2", "#FFFF375F", "#FF64D2FF"];

    private readonly Canvas _canvas = new() { Width = W, Height = H, ClipToBounds = true };
    private readonly TextBlock _caption = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 2, 0) };
    private readonly DispatcherTimer _previewDelay = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _demo = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private DockSettings? _settings;
    private int _visibleWidgets;
    private double? _pointer; // along the dock, in un-magnified dock coordinates
    private int _restingOn = -1;
    private bool _showWindowPreview;
    private DateTime _demoStarted;

    public DockPreview()
    {
        Padding = new Thickness(16, 12, 16, 10);
        var stack = new StackPanel();
        var frame = new Border { CornerRadius = new CornerRadius(12), ClipToBounds = true, Child = new Viewbox { Child = _canvas, Stretch = Stretch.Uniform, MaxHeight = 132 } };
        stack.Children.Add(frame);
        _caption.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        stack.Children.Add(_caption);
        Child = stack;

        _canvas.MouseMove += (_, e) => Point(e.GetPosition(_canvas));
        _canvas.MouseLeave += (_, _) =>
        {
            _pointer = null;
            _restingOn = -1;
            _showWindowPreview = false;
            _previewDelay.Stop();
            Draw();
        };
        _previewDelay.Tick += (_, _) =>
        {
            _previewDelay.Stop();
            _showWindowPreview = _settings?.PreviewsEnabled == true && _restingOn >= 0;
            Draw();
        };
        // After the magnification size changes, a sweep across the icons shows what it will look like.
        _demo.Tick += (_, _) =>
        {
            var t = (DateTime.Now - _demoStarted).TotalSeconds / 1.4;
            if (t >= 1)
            {
                _demo.Stop();
                _pointer = null;
            }
            else _pointer = AppStart + (0.15 + 0.7 * t) * AppCount * Cell;
            Draw();
        };
    }

    /// <summary>Draws the dock for <paramref name="settings"/>; <paramref name="showMagnify"/> plays the magnification once.</summary>
    public void Show(DockSettings settings, int visibleWidgets, bool showMagnify = false)
    {
        _settings = settings;
        _visibleWidgets = Math.Clamp(visibleWidgets, 0, 7);
        if (showMagnify && settings.Magnify && settings.AnimationsEnabled)
        {
            _demoStarted = DateTime.Now;
            _demo.Start();
        }
        Draw();
    }

    // ---- Geometry along the dock ---------------------------------------------------------------

    private const int AppCount = 7;
    private const double Cell = Tile + Gap;
    private static bool Vertical(DockSettings s) => s.Edge is DockEdge.Left or DockEdge.Right;
    private bool WidgetsInDock => _settings?.WidgetPlacement is WidgetPlacement.DockLeft or WidgetPlacement.DockRight;

    /// <summary>Where the first app tile starts along the dock (after Start and, on the left, the widgets).</summary>
    private double AppStart => 8 + Cell + (_settings?.WidgetPlacement == WidgetPlacement.DockLeft ? WidgetLength + 8 : 0);

    private double WidgetLength => _visibleWidgets == 0 ? 0 : Enumerable.Range(0, _visibleWidgets).Sum(WidgetSize) + (_visibleWidgets - 1) * 4;

    private double WidgetSize(int index) => _settings is not null && Vertical(_settings) ? 26 : index switch { 0 => 52, 1 => 42, 2 => 46, 3 => 36, _ => 26 };

    private void Point(Point at)
    {
        if (_settings is null || _demo.IsEnabled) return;
        var (origin, along, across) = DockFrame(_settings, DrawnLength());
        var main = Vertical(_settings) ? at.Y - origin.Y : at.X - origin.X;
        var cross = Vertical(_settings) ? at.X - origin.X : at.Y - origin.Y;
        var inside = cross >= -Tile * 1.2 && cross <= across + Tile * 0.4 && main >= 0 && main <= along + 40;
        if (!inside)
        {
            if (_pointer is null) return;
            _pointer = null;
            _restingOn = -1;
            _showWindowPreview = false;
            _previewDelay.Stop();
            Draw();
            return;
        }
        // Back to un-magnified coordinates: the dock grows around the pointer, so map through the drawn tiles.
        _pointer = Unmagnify(main);
        var over = (int)Math.Floor((_pointer.Value - AppStart) / Cell);
        var app = over is >= 0 and < AppCount ? over : -1;
        if (app != _restingOn)
        {
            _restingOn = app;
            _showWindowPreview = false;
            _previewDelay.Stop();
            if (app >= 0 && app < 4) _previewDelay.Start(); // the first four apps "have windows"
        }
        Draw();
    }

    private double Unmagnify(double drawn)
    {
        // Walk the tiles as drawn and find the base position under the pointer.
        var items = Items();
        double drawnPos = 8, basePos = 8;
        foreach (var item in items)
        {
            var size = item.Size * item.Scale + (item.Gap);
            if (drawn < drawnPos + size) return basePos + (drawn - drawnPos) / size * (item.Size + item.Gap);
            drawnPos += size;
            basePos += item.Size + item.Gap;
        }
        return basePos;
    }

    private sealed record Item(string Kind, double Size, double Gap, int Index)
    {
        public double Scale { get; set; } = 1;
    }

    /// <summary>The dock's contents along its length, each with its magnification for the pointer.</summary>
    private List<Item> Items()
    {
        var s = _settings!;
        var list = new List<Item>();
        void Widgets()
        {
            for (var i = 0; i < _visibleWidgets; i++) list.Add(new Item("widget", WidgetSize(i), 4, i));
            if (_visibleWidgets > 0) list[^1] = list[^1] with { Gap = 8 };
        }
        if (s.WidgetPlacement == WidgetPlacement.DockLeft) Widgets();
        list.Add(new Item("start", Tile, Gap, 0));
        for (var i = 0; i < AppCount; i++) list.Add(new Item("app", Tile, Gap, i));
        list.Add(new Item("divider", 1, Gap + 2, 0));
        list.Add(new Item("downloads", Tile, Gap, 0));
        list.Add(new Item("trash", Tile, s.WidgetPlacement == WidgetPlacement.DockRight ? 8 : 0, 0));
        if (s.WidgetPlacement == WidgetPlacement.DockRight) Widgets();

        var magnify = s.Magnify && s.AnimationsEnabled && _pointer is not null;
        var max = Math.Clamp(s.MagnifyScale, 1.1, 2.2);
        double pos = 8;
        foreach (var item in list)
        {
            if (magnify && item.Kind is "start" or "app" or "downloads" or "trash")
            {
                var d = Math.Abs(_pointer!.Value - (pos + item.Size / 2));
                var radius = Cell * 3.2;
                if (d < radius)
                {
                    var f = Math.Cos(d / radius * Math.PI / 2);
                    item.Scale = 1 + (max - 1) * f * f;
                }
            }
            pos += item.Size + item.Gap;
        }
        return list;
    }

    private double DrawnLength() => Items().Sum(i => i.Size * i.Scale + i.Gap) + 8;

    /// <summary>The dock's top-left corner, its length and thickness, for the chosen edge and position.</summary>
    private static (Point Origin, double Along, double Across) DockFrame(DockSettings s, double length)
    {
        var vertical = Vertical(s);
        var span = vertical ? H - 2 * Margin8 : W - 2 * Margin8;
        length = Math.Min(length, span);
        var start = s.Position switch
        {
            DockPosition.Left => Margin8 + 6,
            DockPosition.Right => Margin8 + span - length - 6,
            _ => Margin8 + (span - length) / 2
        };
        return s.Edge switch
        {
            DockEdge.Top => (new Point(start, Margin8), length, Bar),
            DockEdge.Left => (new Point(Margin8, start), length, Bar),
            DockEdge.Right => (new Point(W - Margin8 - Bar, start), length, Bar),
            _ => (new Point(start, H - Margin8 - Bar), length, Bar)
        };
    }

    // ---- Drawing -------------------------------------------------------------------------------

    private void Draw()
    {
        if (_settings is not { } s) return;
        _canvas.Children.Clear();
        var look = ThemeStyles.Find(s.Style);
        var glass = look.Id == ThemeStyles.Glass;
        var light = (glass ? s.Theme : look.Base) == AppTheme.Light;

        // Desktop: a wallpaper and a window or two.
        _canvas.Background = new LinearGradientBrush(Hex(light ? "#FFB9D3EE" : "#FF27405E").Color, Hex(light ? "#FFE9D8F0" : "#FF4A2F5E").Color, 35);
        AddRect(150, 34, 300, 112, 8, light ? Hex("#E6FFFFFF") : Hex("#CC2A2A30"), Hex("#22FFFFFF"));
        AddRect(150, 34, 300, 18, 8, light ? Hex("#FFF2F2F4") : Hex("#FF36363C"), null);

        var tileFill = glass ? (light ? Hex("#14000000") : Hex("#24FFFFFF")) : Hex(look.Tile);
        var text = glass ? (light ? Hex("#FF1C1C1E") : Hex("#FFF5F5F7")) : Hex(look.Text);

        // Widgets in their own strip at the top.
        if (s.WidgetPlacement == WidgetPlacement.TopBar && _visibleWidgets > 0)
        {
            var y = s.Edge == DockEdge.Top ? Margin8 + Bar + 8 : Margin8;
            var widths = Enumerable.Range(0, _visibleWidgets).Select(i => i switch { 0 => 52.0, 1 => 42, 2 => 46, 3 => 36, _ => 26 }).ToList();
            var total = widths.Sum() + (widths.Count - 1) * 4;
            var x = (W - total) / 2;
            AddRect(x - 6, y, total + 12, 32, 10, DockBrush(s, look, light), Edge(look, light));
            for (var i = 0; i < widths.Count; i++)
            {
                AddWidget(x, y + 4, widths[i], 24, i, tileFill, text);
                x += widths[i] + 4;
            }
        }

        var items = Items();
        var drawnLength = items.Sum(i => i.Size * i.Scale + i.Gap) + 8;
        var (origin, along, across) = DockFrame(s, drawnLength);
        var vertical = Vertical(s);
        var hidden = s.AutoHide == AutoHideMode.Always && _pointer is null && !_demo.IsEnabled;

        // The dock bar.
        var dock = new Rectangle
        {
            Width = vertical ? across : along,
            Height = vertical ? along : across,
            RadiusX = 12, RadiusY = 12,
            Fill = DockBrush(s, look, light),
            Stroke = Edge(look, light),
            StrokeThickness = 1,
            Opacity = hidden ? 0.28 : 1
        };
        if (hidden) dock.StrokeDashArray = [4, 3];
        Canvas.SetLeft(dock, origin.X);
        Canvas.SetTop(dock, origin.Y);
        _canvas.Children.Add(dock);

        // Its contents, grown away from the screen edge when magnified.
        double pos = 8;
        Point? previewAt = null;
        foreach (var item in items)
        {
            var size = item.Size * item.Scale;
            var crossSize = item.Kind == "widget" ? 26 : item.Kind == "divider" ? 20 : size;
            double inset = (across - (item.Kind is "widget" or "divider" ? crossSize : Tile)) / 2;
            // Magnified tiles keep their foot on the dock's inner edge and grow outward.
            double x, y;
            if (!vertical)
            {
                x = origin.X + pos;
                y = s.Edge == DockEdge.Top ? origin.Y + inset : origin.Y + across - inset - crossSize;
            }
            else
            {
                y = origin.Y + pos;
                x = s.Edge == DockEdge.Left ? origin.X + inset : origin.X + across - inset - crossSize;
            }
            var w = vertical ? crossSize : size;
            var h = vertical ? size : crossSize;
            if (item.Kind is not ("widget" or "divider")) { w = h = size; }
            var element = item.Kind switch
            {
                "widget" => Widget(w, h, item.Index, tileFill, text),
                "divider" => new Rectangle { Width = vertical ? 20 : 1, Height = vertical ? 1 : 20, Fill = Hex(light ? "#33000000" : "#40FFFFFF") },
                "start" => Icon(size, "#FF2A5BD7", "#FF7B4BE0"),
                "downloads" => Icon(size, "#FF5AB4FF", "#FF1A6BDB"),
                "trash" => Icon(size, "#FFB8BCC4", "#FF7D828C"),
                _ => Icon(size, AppColors[item.Index % AppColors.Length], null)
            };
            if (item.Kind == "divider")
            {
                if (!vertical) { x = origin.X + pos; y = origin.Y + (across - 20) / 2; }
                else { y = origin.Y + pos; x = origin.X + (across - 20) / 2; }
            }
            element.Opacity = hidden ? 0.28 : 1;
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
            _canvas.Children.Add(element);
            if (item.Kind == "app" && item.Index < 4 && !hidden)
            {
                // A dot under apps that have windows.
                var dot = new Ellipse { Width = 3, Height = 3, Fill = text, Opacity = 0.7 };
                if (!vertical) { Canvas.SetLeft(dot, x + size / 2 - 1.5); Canvas.SetTop(dot, s.Edge == DockEdge.Top ? origin.Y + 1.5 : origin.Y + across - 4.5); }
                else { Canvas.SetTop(dot, y + size / 2 - 1.5); Canvas.SetLeft(dot, s.Edge == DockEdge.Left ? origin.X + 1.5 : origin.X + across - 4.5); }
                _canvas.Children.Add(dot);
            }
            if (item.Kind == "app" && item.Index == _restingOn) previewAt = new Point(x + w / 2, y + h / 2);
            pos += size + item.Gap;
        }

        if (_showWindowPreview && previewAt is { } at && !hidden) DrawWindowPreview(s, at, look, light, text);
        if (hidden) DrawHiddenHint(s, text, light);
        _caption.Text = Caption(s);
    }

    private void DrawWindowPreview(DockSettings s, Point at, ThemeStyle look, bool light, Brush text)
    {
        const double pw = 112, ph = 74;
        var x = Math.Clamp(at.X - pw / 2, 4, W - pw - 4);
        var y = s.Edge == DockEdge.Top ? Margin8 + Bar + 14 : at.Y - ph - 34;
        if (s.Edge == DockEdge.Left) { x = Margin8 + Bar + 14; y = Math.Clamp(at.Y - ph / 2, 4, H - ph - 4); }
        if (s.Edge == DockEdge.Right) { x = W - Margin8 - Bar - 14 - pw; y = Math.Clamp(at.Y - ph / 2, 4, H - ph - 4); }
        AddRect(x, y, pw, ph, 10, look.Id == ThemeStyles.Glass ? Hex(light ? "#F2FFFFFF" : "#F22A2A30") : Hex(look.Popup), Edge(look, light));
        AddRect(x + 8, y + 18, pw - 16, ph - 26, 4, Hex(light ? "#FFE4E8F0" : "#FF3A3F4A"), null);
        var title = new TextBlock { Text = UiText.Get("PreviewWindowTitle"), FontSize = 8.5, Foreground = text };
        Canvas.SetLeft(title, x + 9);
        Canvas.SetTop(title, y + 4);
        _canvas.Children.Add(title);
    }

    private void DrawHiddenHint(DockSettings s, Brush text, bool light)
    {
        var hint = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 3, 8, 4),
            Background = Hex(light ? "#CCFFFFFF" : "#B3000000"),
            Child = new TextBlock { Text = UiText.Get("PreviewHiddenHint"), FontSize = 10, Foreground = text }
        };
        hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var (x, y) = s.Edge switch
        {
            DockEdge.Top => ((W - hint.DesiredSize.Width) / 2, Margin8 + Bar + 6),
            DockEdge.Left => (Margin8 + Bar + 6, (H - hint.DesiredSize.Height) / 2),
            DockEdge.Right => (W - Margin8 - Bar - 6 - hint.DesiredSize.Width, (H - hint.DesiredSize.Height) / 2),
            _ => ((W - hint.DesiredSize.Width) / 2, H - Margin8 - Bar - 6 - hint.DesiredSize.Height)
        };
        Canvas.SetLeft(hint, x);
        Canvas.SetTop(hint, y);
        _canvas.Children.Add(hint);
    }

    /// <summary>What the picture shows, in words.</summary>
    private string Caption(DockSettings s)
    {
        var parts = new List<string>
        {
            UiText.Get(s.Edge switch { DockEdge.Top => "PreviewEdgeTop", DockEdge.Left => "PreviewEdgeLeft", DockEdge.Right => "PreviewEdgeRight", _ => "PreviewEdgeBottom" }),
            UiText.Get(s.WidgetPlacement switch { WidgetPlacement.DockLeft => "PreviewWidgetsLeft", WidgetPlacement.DockRight => "PreviewWidgetsRight", _ => "PreviewWidgetsTop" }),
            s.Magnify && s.AnimationsEnabled
                ? UiText.Format("PreviewMagnifyOn", (int)Math.Round(Math.Clamp(s.MagnifyScale, 1.1, 2.2) * 100))
                : UiText.Get("PreviewMagnifyOff"),
            UiText.Get(s.AutoHide switch { AutoHideMode.Always => "PreviewHideAlways", AutoHideMode.Smart => "PreviewHideSmart", _ => "PreviewHideNever" })
        };
        return string.Join("  ·  ", parts) + "\n" + UiText.Get("PreviewTryHint");
    }

    // ---- Pieces --------------------------------------------------------------------------------

    private static Brush DockBrush(DockSettings s, ThemeStyle look, bool light)
    {
        var alpha = (byte)Math.Round(Math.Clamp(s.SurfaceOpacity, 0.3, 1) * 255);
        Color top, bottom;
        if (look.Id == ThemeStyles.Glass)
        {
            top = light ? Color.FromRgb(0xF6, 0xF6, 0xF8) : Color.FromRgb(0x2A, 0x2A, 0x30);
            bottom = light ? Color.FromRgb(0xE8, 0xE8, 0xEC) : Color.FromRgb(0x1C, 0x1C, 0x20);
        }
        else
        {
            top = Hex(look.DockTop).Color;
            bottom = Hex(look.DockBottom).Color;
        }
        top.A = bottom.A = alpha;
        return new LinearGradientBrush(top, bottom, 90);
    }

    private static Brush Edge(ThemeStyle look, bool light) =>
        look.Id == ThemeStyles.Glass ? Hex(light ? "#26000000" : "#33FFFFFF") : Hex(look.Edge);

    private static FrameworkElement Icon(double size, string from, string? to)
    {
        var fill = to is null ? (Brush)Hex(from) : new LinearGradientBrush(Hex(from).Color, Hex(to).Color, 90);
        var tile = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.24), Background = fill };
        tile.Child = new Ellipse { Width = size * 0.42, Height = size * 0.42, Fill = Hex("#D9FFFFFF"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return tile;
    }

    private static FrameworkElement Widget(double w, double h, int index, Brush fill, Brush text)
    {
        var tile = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(7), Background = fill };
        // A hint of what is in it: a ring, a line of text…
        if (index % 3 == 1 || w <= 26)
            tile.Child = new Ellipse { Width = Math.Min(w, h) * 0.55, Height = Math.Min(w, h) * 0.55, Stroke = text, StrokeThickness = 1.6, Opacity = 0.75, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        else
            tile.Child = new Rectangle { Width = w * 0.55, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = text, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return tile;
    }

    private void AddWidget(double x, double y, double w, double h, int index, Brush fill, Brush text)
    {
        var widget = Widget(w, h, index, fill, text);
        Canvas.SetLeft(widget, x);
        Canvas.SetTop(widget, y);
        _canvas.Children.Add(widget);
    }

    private void AddRect(double x, double y, double w, double h, double radius, Brush fill, Brush? stroke)
    {
        var rect = new Rectangle { Width = w, Height = h, RadiusX = radius, RadiusY = radius, Fill = fill };
        if (stroke is not null)
        {
            rect.Stroke = stroke;
            rect.StrokeThickness = 1;
        }
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        _canvas.Children.Add(rect);
    }

    private static SolidColorBrush Hex(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
