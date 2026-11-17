using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QiDock.Models;
using QiDock.Services;

namespace QiDock.Widgets;

/// <summary>Up to three cities' local time with the difference to here; cities are picked in the card.</summary>
public sealed class WorldClockWidget : DockWidget
{
    private const int MaxCities = 3;

    // Key → (Turkish name, English name, Windows time zone id)
    internal static readonly (string Key, string Tr, string En, string Zone)[] Cities =
    [
        ("newyork", "New York", "New York", "Eastern Standard Time"),
        ("losangeles", "Los Angeles", "Los Angeles", "Pacific Standard Time"),
        ("chicago", "Chicago", "Chicago", "Central Standard Time"),
        ("saopaulo", "São Paulo", "São Paulo", "E. South America Standard Time"),
        ("london", "Londra", "London", "GMT Standard Time"),
        ("paris", "Paris", "Paris", "Romance Standard Time"),
        ("berlin", "Berlin", "Berlin", "W. Europe Standard Time"),
        ("istanbul", "İstanbul", "Istanbul", "Turkey Standard Time"),
        ("moscow", "Moskova", "Moscow", "Russian Standard Time"),
        ("dubai", "Dubai", "Dubai", "Arabian Standard Time"),
        ("baku", "Bakü", "Baku", "Azerbaijan Standard Time"),
        ("tashkent", "Taşkent", "Tashkent", "West Asia Standard Time"),
        ("delhi", "Delhi", "Delhi", "India Standard Time"),
        ("beijing", "Pekin", "Beijing", "China Standard Time"),
        ("tokyo", "Tokyo", "Tokyo", "Tokyo Standard Time"),
        ("sydney", "Sidney", "Sydney", "AUS Eastern Standard Time")
    ];

    internal static readonly string[] Defaults = ["newyork", "london", "tokyo"];
    private readonly Button _tile;
    private readonly WidgetPopup _popup;
    private readonly StackPanel _rows = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _timer;
    private IReadOnlyList<string> _keys = Defaults;

    public WorldClockWidget()
    {
        _tile = Ui.Tile(_rows, (_, _) => Toggle(), 10);
        _popup = new WidgetPopup(_tile);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += (_, _) => Render();
        UiText.Current.PropertyChanged += (_, _) => Render();
    }

    public override string Id => WidgetIds.WorldClock;
    public override FrameworkElement View => _tile;
    public override bool IsPopupOpen => _popup.IsOpen;
    public override void ClosePopup() => _popup.Close();

    public override void Start()
    {
        Render();
        _timer.Start();
    }

    public override void Stop()
    {
        _timer.Stop();
        _popup.Close();
    }

    public override void SetLive(bool live)
    {
        if (!live)
        {
            _timer.Stop();
            return;
        }
        Render();
        _timer.Start();
    }

    public override void ApplySettings(WidgetSettings settings)
    {
        var keys = settings.WorldClocks is { Count: > 0 } chosen ? chosen.Where(k => Cities.Any(c => c.Key == k)).ToList() : Defaults.ToList();
        if (keys.SequenceEqual(_keys)) return;
        _keys = keys;
        Render();
        if (_popup.IsOpen) Build();
    }

    private static string CityName((string Key, string Tr, string En, string Zone) city) =>
        UiText.Current.Language == AppLanguage.English ? city.En : city.Tr;

    private static DateTime? TimeIn(string zone)
    {
        try { return TimeZoneInfo.ConvertTime(DateTime.Now, TimeZoneInfo.FindSystemTimeZoneById(zone)); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }

    private static string Offset(DateTime there)
    {
        var hours = Math.Round((there - DateTime.Now).TotalHours * 2) / 2;
        return hours == 0 ? "±0" : (hours > 0 ? "+" : "−") + Math.Abs(hours).ToString("0.#", CultureInfo.InvariantCulture);
    }

    private void Render()
    {
        _rows.Children.Clear();
        foreach (var key in _keys.Take(MaxCities))
        {
            var city = Cities.First(c => c.Key == key);
            if (TimeIn(city.Zone) is not { } time) continue;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var name = Ui.Text(CityName(city), 10, brushKey: "TextSecondary");
            name.Width = 64;
            var clock = Ui.Text(time.ToString("HH:mm", CultureInfo.InvariantCulture), 10.5, FontWeights.SemiBold);
            clock.Width = 34;
            var diff = Ui.Text(Offset(time), 9.5, brushKey: "TextSecondary");
            row.Children.Add(name);
            row.Children.Add(clock);
            row.Children.Add(diff);
            _rows.Children.Add(row);
        }
    }

    private void Toggle()
    {
        if (!_popup.IsOpen) Build();
        _popup.Toggle();
    }

    private void Build()
    {
        var root = new StackPanel();
        root.Children.Add(Ui.Header(UiText.Get("WorldClockTitle"), () => _popup.Close()));
        var hint = Ui.Text(UiText.Format("WorldClockHint", MaxCities), 11, brushKey: "TextSecondary");
        hint.Margin = new Thickness(0, 0, 0, 8);
        root.Children.Add(hint);
        // Three tiles fill the card exactly: their outer edges line up with the title and the ×.
        var grid = new WrapPanel { Width = 340, Margin = new Thickness(-2, 0, -2, 0) };
        foreach (var city in Cities)
        {
            var chosen = _keys.Contains(city.Key);
            var time = TimeIn(city.Zone);
            var content = new StackPanel();
            content.Children.Add(Ui.Text(CityName(city), 11.5, FontWeights.SemiBold));
            content.Children.Add(Ui.Text(time is { } t ? $"{t:HH:mm} · {Offset(t)}" : "–", 10.5, brushKey: "TextSecondary"));
            var button = new Button { Content = content, Width = 340 / 3.0 - 4, Height = 46, Margin = new Thickness(2), HorizontalContentAlignment = HorizontalAlignment.Left };
            button.SetResourceReference(FrameworkElement.StyleProperty, "WidgetTileButton");
            button.Height = 46;
            if (chosen) button.SetResourceReference(Control.BorderBrushProperty, "WidgetAccent");
            button.Click += (_, _) =>
            {
                var keys = _keys.ToList();
                if (keys.Contains(city.Key)) keys.Remove(city.Key);
                else
                {
                    if (keys.Count >= MaxCities) keys.RemoveAt(0);
                    keys.Add(city.Key);
                }
                if (keys.Count == 0) return;
                UpdateSettings(s => s with { WorldClocks = keys });
            };
            grid.Children.Add(button);
        }
        root.Children.Add(grid);
        _popup.SetContent(root, 370);
    }
}
