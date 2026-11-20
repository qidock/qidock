using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using QiDock.Widgets;

namespace QiDock.DesktopWidgets;

/// <summary>Colors and small building blocks shared by the desktop widgets (dark cards, like the reference).</summary>
internal static class DeskUi
{
    public static readonly Brush CardFill = Ui.Solid("#F21C1C1F");
    public static readonly Brush CardStroke = Ui.Solid("#1EFFFFFF");
    public static readonly Brush Text = Ui.Solid("#FFF5F5F7");
    public static readonly Brush Secondary = Ui.Solid("#99EBEBF5");
    public static readonly Brush Tertiary = Ui.Solid("#4DEBEBF5");
    public static readonly Brush Track = Ui.Solid("#24FFFFFF");
    public static readonly Brush Inset = Ui.Solid("#14FFFFFF");
    public static readonly Brush InsetHover = Ui.Solid("#24FFFFFF");
    public static readonly Brush Blue = Ui.Solid("#FF0A84FF");
    public static readonly Brush Green = Ui.Solid("#FF30D158");
    public static readonly Brush Orange = Ui.Solid("#FFFF9F0A");
    public static readonly Brush Red = Ui.Solid("#FFFF453A");
    public static readonly Brush Yellow = Ui.Solid("#FFFFD60A");
    public static readonly Brush Purple = Ui.Solid("#FFBF5AF2");
    public static readonly Brush Cyan = Ui.Solid("#FF64D2FF");
    public static readonly Brush Pink = Ui.Solid("#FFFF375F");

    public static TextBlock T(string text, double size, FontWeight? weight = null, Brush? brush = null)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            Foreground = brush ?? Text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        return block;
    }

    /// <summary>Numbers that do not jump around as digits change.</summary>
    public static TextBlock Digits(string text, double size, FontWeight? weight = null, Brush? brush = null)
    {
        var block = T(text, size, weight ?? FontWeights.SemiBold, brush);
        block.Typography.NumeralAlignment = FontNumeralAlignment.Tabular;
        block.TextTrimming = TextTrimming.None;
        return block;
    }

    public static Path Icon(string key, double size, Brush? brush = null, double stroke = 1.9) => Ui.Icon(key, size, brush ?? Text, stroke: stroke);
    public static Path Glyph(string key, double size, Brush? brush = null) => Ui.Glyph(key, size, brush ?? Text);

    public static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    public static StackPanel Column(params UIElement[] children)
    {
        var column = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var child in children) column.Children.Add(child);
        return column;
    }

    public static T2 At<T2>(T2 element, int row = 0, int column = 0, int rowSpan = 1, int columnSpan = 1) where T2 : UIElement
    {
        System.Windows.Controls.Grid.SetRow(element, row);
        System.Windows.Controls.Grid.SetColumn(element, column);
        if (rowSpan > 1) System.Windows.Controls.Grid.SetRowSpan(element, rowSpan);
        if (columnSpan > 1) System.Windows.Controls.Grid.SetColumnSpan(element, columnSpan);
        return element;
    }

    public static System.Windows.Controls.Grid Grid(string rows = "", string columns = "")
    {
        var grid = new System.Windows.Controls.Grid();
        foreach (var r in Split(rows)) grid.RowDefinitions.Add(new RowDefinition { Height = r });
        foreach (var c in Split(columns)) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c });
        return grid;
    }

    private static IEnumerable<GridLength> Split(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) yield break;
        foreach (var part in spec.Split(','))
        {
            var p = part.Trim();
            if (p == "Auto") yield return GridLength.Auto;
            else if (p.EndsWith('*')) yield return new GridLength(p.Length == 1 ? 1 : double.Parse(p[..^1], CultureInfo.InvariantCulture), GridUnitType.Star);
            else yield return new GridLength(double.Parse(p, CultureInfo.InvariantCulture));
        }
    }

    public static T2 Margin<T2>(T2 element, double left, double top = 0, double right = 0, double bottom = 0) where T2 : FrameworkElement
    {
        element.Margin = new Thickness(left, top, right, bottom);
        return element;
    }

    /// <summary>Placeholder shown when there is nothing to display yet.</summary>
    public static FrameworkElement Empty(string iconKey, string text)
    {
        var icon = Icon(iconKey, 22, Secondary);
        var label = T(text, 12, brush: Secondary);
        label.TextWrapping = TextWrapping.Wrap;
        label.TextAlignment = TextAlignment.Center;
        label.Margin = new Thickness(0, 8, 0, 0);
        var stack = Column(icon, label);
        stack.HorizontalAlignment = HorizontalAlignment.Center;
        return stack;
    }

    public static string Percent(double value) => Services.UiText.Percent(value);
}

/// <summary>Clickable rounded area (round play buttons, toggles, list rows). Never takes focus.</summary>
internal sealed class DeskButton : Border
{
    public event EventHandler? Click;

    public DeskButton(UIElement content, Action? click = null, double size = double.NaN, Brush? background = null, double radius = -1)
    {
        Child = content;
        Width = size;
        Height = size;
        Background = background ?? Brushes.Transparent;
        CornerRadius = new CornerRadius(radius >= 0 ? radius : double.IsNaN(size) ? 10 : size / 2);
        Cursor = Cursors.Hand;
        if (click is not null) Click += (_, _) => click();
        MouseEnter += (_, _) => Opacity = 0.82;
        MouseLeave += (_, _) => Opacity = 1;
        MouseLeftButtonDown += (_, e) => e.Handled = true;
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (IsMouseOver) Click?.Invoke(this, EventArgs.Empty);
        };
    }

    public static DeskButton Round(string glyphKey, Action click, double size, Brush background, Brush? foreground = null, double glyph = 0, bool outline = false)
    {
        UIElement content = outline
            ? DeskUi.Icon(glyphKey, glyph > 0 ? glyph : size * 0.42, foreground)
            : DeskUi.Glyph(glyphKey, glyph > 0 ? glyph : size * 0.36, foreground);
        return new DeskButton(content, click, size, background);
    }
}

/// <summary>Progress ring (CPU, battery, timers).</summary>
internal sealed class RingGauge : FrameworkElement
{
    private double _value;
    public double Thickness { get; init; } = 6;
    public Brush Stroke { get; set; } = DeskUi.Blue;
    public Brush TrackBrush { get; init; } = DeskUi.Track;

    public double Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (Math.Abs(clamped - _value) < 0.05) return;
            _value = clamped;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = (size - Thickness) / 2;
        dc.DrawEllipse(null, new Pen(TrackBrush, Thickness), center, radius, radius);
        if (_value <= 0) return;
        var pen = new Pen(Stroke, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (_value >= 99.95)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
            return;
        }
        var angle = _value / 100 * 360;
        var start = new Point(center.X, center.Y - radius);
        var radians = (angle - 90) * Math.PI / 180;
        var end = new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, angle > 180, SweepDirection.Clockwise, true));
        dc.DrawGeometry(null, pen, new PathGeometry([figure]));
    }
}

/// <summary>Line graph with a soft fill (CPU, network, stocks-like curves).</summary>
internal sealed class Sparkline : FrameworkElement
{
    private IReadOnlyList<double> _values = [];
    private IReadOnlyList<double>? _second;
    public Brush Stroke { get; init; } = DeskUi.Blue;
    public Brush SecondStroke { get; init; } = DeskUi.Purple;
    public double? Maximum { get; init; }
    public double LineThickness { get; init; } = 2;

    public void SetValues(IReadOnlyList<double> values, IReadOnlyList<double>? second = null)
    {
        _values = values;
        _second = second;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var max = Maximum ?? Math.Max(1, Math.Max(_values.DefaultIfEmpty(0).Max(), _second?.DefaultIfEmpty(0).Max() ?? 0) * 1.15);
        if (_second is { Count: > 1 }) Draw(dc, _second, max, SecondStroke, false);
        if (_values.Count > 1) Draw(dc, _values, max, Stroke, true);
    }

    private void Draw(DrawingContext dc, IReadOnlyList<double> values, double max, Brush stroke, bool fill)
    {
        var w = ActualWidth;
        var h = ActualHeight - LineThickness;
        var step = w / (values.Count - 1);
        var points = values.Select((v, i) => new Point(i * step, LineThickness / 2 + h - Math.Clamp(v / max, 0, 1) * h)).ToList();
        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            for (var i = 1; i < points.Count; i++)
            {
                // Smooth with midpoints so the curve looks drawn, not jagged.
                var mid = new Point((points[i - 1].X + points[i].X) / 2, (points[i - 1].Y + points[i].Y) / 2);
                ctx.QuadraticBezierTo(points[i - 1], mid, true, true);
            }
            ctx.LineTo(points[^1], true, true);
        }
        line.Freeze();
        if (fill && stroke is SolidColorBrush solid)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(0, ActualHeight), true, true);
                ctx.LineTo(points[0], false, false);
                for (var i = 1; i < points.Count; i++)
                {
                    var mid = new Point((points[i - 1].X + points[i].X) / 2, (points[i - 1].Y + points[i].Y) / 2);
                    ctx.QuadraticBezierTo(points[i - 1], mid, false, false);
                }
                ctx.LineTo(points[^1], false, false);
                ctx.LineTo(new Point(w, ActualHeight), false, false);
            }
            area.Freeze();
            var color = solid.Color;
            var gradient = new LinearGradientBrush(Color.FromArgb(0x55, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B), 90);
            gradient.Freeze();
            dc.DrawGeometry(gradient, null, area);
        }
        dc.DrawGeometry(null, new Pen(stroke, LineThickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);
    }
}

/// <summary>Grid of dots: days of the month or year gone by.</summary>
internal sealed class DotGrid : FrameworkElement
{
    public int Total { get; set; }
    public int Filled { get; set; }
    public int Columns { get; set; } = 10;
    public Brush FilledBrush { get; init; } = DeskUi.Text;
    public Brush EmptyBrush { get; init; } = DeskUi.Track;
    public Brush CurrentBrush { get; init; } = DeskUi.Orange;

    public void Set(int total, int filled, int columns)
    {
        Total = total;
        Filled = filled;
        Columns = columns;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Total <= 0 || Columns <= 0 || ActualWidth <= 0) return;
        var rows = (int)Math.Ceiling(Total / (double)Columns);
        var cellW = ActualWidth / Columns;
        var cellH = ActualHeight / rows;
        var radius = Math.Max(1, Math.Min(cellW, cellH) * 0.36);
        for (var i = 0; i < Total; i++)
        {
            var x = (i % Columns + 0.5) * cellW;
            var y = (i / Columns + 0.5) * cellH;
            var brush = i < Filled ? FilledBrush : i == Filled ? CurrentBrush : EmptyBrush;
            dc.DrawEllipse(brush, null, new Point(x, y), radius, radius);
        }
    }
}

/// <summary>Classic clock face with hour, minute and second hands.</summary>
internal sealed class AnalogFace : FrameworkElement
{
    private DateTime _time = DateTime.Now;
    public bool Light { get; init; } = true;
    public bool Numbers { get; init; } = true;

    public void SetTime(DateTime time)
    {
        _time = time;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var r = size / 2;
        var face = Light ? Ui.Solid("#FFF7F7F8") : Ui.Solid("#FF2C2C2E");
        var ink = Light ? Ui.Solid("#FF1C1C1E") : DeskUi.Text;
        var faint = Light ? Ui.Solid("#FF9A9AA0") : DeskUi.Secondary;
        dc.DrawEllipse(face, new Pen(Light ? Ui.Solid("#33000000") : DeskUi.CardStroke, 1), c, r - 0.5, r - 0.5);
        for (var i = 0; i < 60; i++)
        {
            var major = i % 5 == 0;
            var a = i * 6 * Math.PI / 180;
            var outer = r * 0.92;
            var inner = major ? r * 0.82 : r * 0.87;
            dc.DrawLine(new Pen(major ? ink : faint, major ? Math.Max(1.2, size / 70) : 0.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                new Point(c.X + Math.Sin(a) * inner, c.Y - Math.Cos(a) * inner),
                new Point(c.X + Math.Sin(a) * outer, c.Y - Math.Cos(a) * outer));
        }
        if (Numbers && size >= 110)
        {
            var typeface = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            for (var h = 1; h <= 12; h++)
            {
                var a = h * 30 * Math.PI / 180;
                var text = new FormattedText(h.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size * 0.1, ink, dpi);
                var p = new Point(c.X + Math.Sin(a) * r * 0.66 - text.Width / 2, c.Y - Math.Cos(a) * r * 0.66 - text.Height / 2);
                dc.DrawText(text, p);
            }
        }
        var t = _time;
        Hand(dc, c, (t.Hour % 12 + t.Minute / 60.0) * 30, r * 0.5, Math.Max(2.5, size / 34), ink);
        Hand(dc, c, (t.Minute + t.Second / 60.0) * 6, r * 0.74, Math.Max(2, size / 48), ink);
        Hand(dc, c, t.Second * 6, r * 0.8, Math.Max(1, size / 120), DeskUi.Orange, r * 0.16);
        dc.DrawEllipse(DeskUi.Orange, null, c, Math.Max(2, size / 40), Math.Max(2, size / 40));
    }

    private static void Hand(DrawingContext dc, Point c, double degrees, double length, double width, Brush brush, double tail = 0)
    {
        var a = degrees * Math.PI / 180;
        var tip = new Point(c.X + Math.Sin(a) * length, c.Y - Math.Cos(a) * length);
        var back = new Point(c.X - Math.Sin(a) * tail, c.Y + Math.Cos(a) * tail);
        dc.DrawLine(new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, back, tip);
    }
}

/// <summary>The sun's path between sunrise and sunset with its current position.</summary>
internal sealed class SunArc : FrameworkElement
{
    private double _progress = -1;

    /// <summary>0 = sunrise, 1 = sunset; outside that range the sun is below the horizon.</summary>
    public void SetProgress(double progress)
    {
        _progress = progress;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var w = ActualWidth;
        var h = ActualHeight;
        var horizon = h - 4;
        Point At(double p) => new(w * p, horizon - Math.Sin(Math.Clamp(p, 0, 1) * Math.PI) * (h - 12));
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(At(0), false, false);
            for (var i = 1; i <= 40; i++) ctx.LineTo(At(i / 40.0), true, true);
        }
        geometry.Freeze();
        var gradient = new LinearGradientBrush(Color.FromRgb(0x8E, 0x8E, 0x93), Color.FromRgb(0xFF, 0x9F, 0x0A), 0);
        gradient.Freeze();
        dc.DrawGeometry(null, new Pen(gradient, 2) { DashStyle = _progress is >= 0 and <= 1 ? DashStyles.Solid : DashStyles.Dot }, geometry);
        dc.DrawLine(new Pen(DeskUi.Track, 1), new Point(0, horizon), new Point(w, horizon));
        if (_progress is < 0 or > 1) return;
        var sun = At(_progress);
        dc.DrawEllipse(Ui.Solid("#55FFC83D"), null, sun, 9, 9);
        dc.DrawEllipse(DeskUi.Yellow, null, sun, 5.5, 5.5);
    }
}

/// <summary>Wind direction compass.</summary>
internal sealed class Compass : FrameworkElement
{
    private double _direction = double.NaN;

    public void SetDirection(double degrees)
    {
        _direction = degrees;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var r = size / 2 - 1;
        dc.DrawEllipse(DeskUi.Inset, new Pen(DeskUi.Track, 1), c, r, r);
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        string[] labels = [Desk.L("K", "N"), Desk.L("D", "E"), Desk.L("G", "S"), Desk.L("B", "W")];
        for (var i = 0; i < 4; i++)
        {
            var a = i * 90 * Math.PI / 180;
            var text = new FormattedText(labels[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, Math.Max(7, size * 0.13), i == 0 ? DeskUi.Text : DeskUi.Secondary, dpi);
            dc.DrawText(text, new Point(c.X + Math.Sin(a) * r * 0.74 - text.Width / 2, c.Y - Math.Cos(a) * r * 0.74 - text.Height / 2));
        }
        if (double.IsNaN(_direction)) return;
        // Arrow points where the wind blows to (meteorological direction + 180°).
        var to = (_direction + 180) * Math.PI / 180;
        var tip = new Point(c.X + Math.Sin(to) * r * 0.45, c.Y - Math.Cos(to) * r * 0.45);
        var left = new Point(c.X + Math.Sin(to + 2.5) * r * 0.28, c.Y - Math.Cos(to + 2.5) * r * 0.28);
        var right = new Point(c.X + Math.Sin(to - 2.5) * r * 0.28, c.Y - Math.Cos(to - 2.5) * r * 0.28);
        var arrow = new StreamGeometry();
        using (var ctx = arrow.Open())
        {
            ctx.BeginFigure(tip, true, true);
            ctx.LineTo(left, false, false);
            ctx.LineTo(c, false, false);
            ctx.LineTo(right, false, false);
        }
        arrow.Freeze();
        dc.DrawGeometry(DeskUi.Text, null, arrow);
    }
}

/// <summary>Water that rises as the reminder interval passes.</summary>
internal sealed class WaveFill : FrameworkElement
{
    private double _fraction;
    public Brush Water { get; init; } = Ui.Vertical("#FF3BA7F5", "#FF1565C0");

    public void SetFraction(double fraction)
    {
        var f = Math.Clamp(fraction, 0, 1);
        if (Math.Abs(f - _fraction) < 0.002) return;
        _fraction = f;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || _fraction <= 0) return;
        var w = ActualWidth;
        var h = ActualHeight;
        var level = h - h * _fraction;
        var wave = new StreamGeometry();
        using (var ctx = wave.Open())
        {
            ctx.BeginFigure(new Point(0, h), true, true);
            ctx.LineTo(new Point(0, level), false, false);
            const int steps = 24;
            for (var i = 1; i <= steps; i++)
            {
                var x = w * i / steps;
                ctx.LineTo(new Point(x, level + Math.Sin(i / (double)steps * Math.PI * 3) * 4), false, true);
            }
            ctx.LineTo(new Point(w, h), false, false);
        }
        wave.Freeze();
        dc.DrawGeometry(Water, null, wave);
    }
}
