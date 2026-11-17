using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using QiDock.Models;
using QiDock.Services;

namespace QiDock.Widgets;

/// <summary>
/// How much of the month, year or day has gone, as dots (days, months or hours) and a percent. A click switches
/// between month, year and day. It redraws once a minute while on screen.
/// </summary>
public sealed class ProgressWidget : DockWidget
{
    private static readonly string[] Kinds = ["month", "year", "day"];
    private readonly Button _tile;
    private readonly UniformGrid _dots = new() { Rows = 3, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _name;
    private readonly TextBlock _percent;
    private readonly DispatcherTimer _minute = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
    private string _kind = "month";
    private string _drawn = "";

    public ProgressWidget()
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        _name = Ui.Text("", 10, brushKey: "TextSecondary");
        _percent = Ui.Text("", 13.5, FontWeights.Bold);
        _percent.FontFeatures();
        text.Children.Add(_name);
        text.Children.Add(_percent);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_dots);
        row.Children.Add(text);
        _tile = Ui.Tile(row, (_, _) => UpdateSettings(s => s with { ProgressKind = Kinds[(Array.IndexOf(Kinds, s.ProgressKind) + 1) % Kinds.Length] }), 9);
        _minute.Tick += (_, _) => Render();
        UiText.Current.PropertyChanged += (_, _) => { _drawn = ""; Render(); };
        Render();
    }

    public override string Id => WidgetIds.Progress;
    public override FrameworkElement View => _tile;

    public override void Start() => _minute.Start();
    public override void Stop() => _minute.Stop();

    public override void SetLive(bool live)
    {
        if (live)
        {
            Render();
            _minute.Start();
        }
        else _minute.Stop();
    }

    public override void ApplySettings(WidgetSettings settings)
    {
        _kind = Array.IndexOf(Kinds, settings.ProgressKind) >= 0 ? settings.ProgressKind : "month";
        Render();
    }

    private void Render()
    {
        var now = DateTime.Now;
        var culture = CultureInfo.CurrentUICulture;
        (int Total, int Done, double Fraction, string Name, int Rows) state = _kind switch
        {
            "year" => (12, now.Month - 1, (now.DayOfYear - 1 + now.TimeOfDay.TotalDays) / (DateTime.IsLeapYear(now.Year) ? 366 : 365), now.Year.ToString(culture), 2),
            "day" => (24, now.Hour, now.TimeOfDay.TotalDays, UiText.Get("ProgressToday"), 2),
            _ => (DateTime.DaysInMonth(now.Year, now.Month), now.Day - 1, (now.Day - 1 + now.TimeOfDay.TotalDays) / DateTime.DaysInMonth(now.Year, now.Month),
                culture.DateTimeFormat.GetMonthName(now.Month), 3)
        };
        _name.Text = state.Name;
        _percent.Text = UiText.Percent(Math.Floor(state.Fraction * 100));
        _tile.ToolTip = UiText.Format("ProgressHint", state.Name, Math.Floor(state.Fraction * 100));

        var signature = $"{_kind}|{state.Total}|{state.Done}";
        if (signature == _drawn) return;
        _drawn = signature;
        _dots.Children.Clear();
        _dots.Rows = state.Rows;
        _dots.Columns = (int)Math.Ceiling(state.Total / (double)state.Rows);
        var size = state.Rows == 3 ? 6.0 : 7.0;
        for (var i = 0; i < state.Total; i++)
        {
            var dot = new Ellipse { Width = size, Height = size, Margin = new Thickness(1.2) };
            // Gone: solid; the current one: the accent; still to come: faint.
            if (i < state.Done) dot.SetResourceReference(Shape.FillProperty, "TextPrimary");
            else if (i == state.Done) dot.Fill = Ui.Solid("#FF0A84FF");
            else dot.SetResourceReference(Shape.FillProperty, "WidgetTrack");
            _dots.Children.Add(dot);
        }
    }
}
