using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QiDock.Services.Widgets;
using QiDock.Widgets;
using static QiDock.DesktopWidgets.DeskUi;

namespace QiDock.DesktopWidgets.Kinds;

/// <summary>Weather from the widget-settings city: now, the next hours and the coming days.</summary>
internal sealed class WeatherDeskWidget : DeskWidget
{
    private Border? _root;

    private bool Colored => Option("plain") != "1";

    public override FrameworkElement Build()
    {
        _root = new Border { Margin = Negative(Desk.Padding(Size)), Padding = Desk.Padding(Size) };
        Render();
        return _root;
    }

    private static Thickness Negative(Thickness t) => new(-t.Left, -t.Top, -t.Right, -t.Bottom);

    protected override void OnAttach()
    {
        WeatherHub.Instance.Subscribe(Render);
        Render(); // data may have arrived while this widget was covered
    }
    protected override void OnDetach() => WeatherHub.Instance.Unsubscribe(Render);

    private void Render()
    {
        if (_root is null) return;
        if (WeatherHub.Instance.Report is not { } r)
        {
            _root.Background = null;
            _root.Child = Empty("IconMapPin", WeatherHub.Instance.Error ?? Desk.L("Hava durumu yükleniyor…", "Loading weather…"));
            return;
        }
        _root.Background = Colored ? WeatherArt.Sky(r.Code, r.IsDay) : null;
        _root.Child = Size switch
        {
            DeskSize.Mini => Mini(r),
            DeskSize.Small => Small(r),
            DeskSize.Strip => Hours(r, r.Hours.Count),
            DeskSize.Medium => Medium(r),
            _ => Large(r)
        };
    }

    private static FrameworkElement Icon(WeatherReport r, double size) => WeatherArt.Create(r.Code, r.IsDay, size);

    private static FrameworkElement Mini(WeatherReport r)
    {
        var grid = Grid(columns: "Auto,*");
        grid.Children.Add(Icon(r, 38));
        var temp = T(WeatherHub.Temp(r.Temperature), 30, FontWeights.SemiBold);
        var city = T(r.City, 11.5, FontWeights.SemiBold, Secondary);
        grid.Children.Add(At(Margin(Column(temp, city), 10), 0, 1));
        return grid;
    }

    private static FrameworkElement Small(WeatherReport r)
    {
        var city = Row(T(r.City, 14, FontWeights.SemiBold), Margin(DeskUi.Icon("IconMapPin", 11, Text), 4));
        var temp = T(WeatherHub.Temp(r.Temperature), 46, FontWeights.Light);
        var icon = Icon(r, 30);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        var condition = T(WeatherService.Describe(r.Code), 12.5, FontWeights.SemiBold);
        var range = T(Desk.L("Y:", "H:") + WeatherHub.Temp(r.TodayMax) + "  " + Desk.L("D:", "L:") + WeatherHub.Temp(r.TodayMin), 12, FontWeights.SemiBold, Secondary);
        var grid = Grid("Auto,*,Auto,Auto,Auto");
        grid.Children.Add(city);
        grid.Children.Add(At(Margin(temp, -2, -6, 0, 0), 1));
        grid.Children.Add(At(icon, 2));
        grid.Children.Add(At(condition, 3));
        grid.Children.Add(At(range, 4));
        return grid;
    }

    private static FrameworkElement Hours(WeatherReport r, int count)
    {
        var grid = Grid(columns: string.Join(",", Enumerable.Repeat("*", Math.Max(1, Math.Min(count, r.Hours.Count)))));
        var i = 0;
        foreach (var hour in r.Hours.Take(count))
        {
            var label = T(i == 0 ? Desk.L("Şimdi", "Now") : hour.Time.ToString("HH", CultureInfo.InvariantCulture), 11.5, FontWeights.SemiBold, Secondary);
            var icon = WeatherArt.Create(hour.Code, hour.IsDay, 22);
            icon.Margin = new Thickness(0, 3, 0, 3);
            var temp = T(WeatherHub.Temp(hour.Temperature), 13, FontWeights.SemiBold);
            foreach (var e in new FrameworkElement[] { label, icon, temp }) e.HorizontalAlignment = HorizontalAlignment.Center;
            grid.Children.Add(At(Column(label, icon, temp), 0, i++));
        }
        return grid;
    }

    private static FrameworkElement Now(WeatherReport r, double tempSize)
    {
        var city = Row(T(r.City, 15, FontWeights.SemiBold), Margin(DeskUi.Icon("IconMapPin", 11, Text), 4));
        var temp = T(WeatherHub.Temp(r.Temperature), tempSize, FontWeights.Light);
        temp.Margin = new Thickness(-2, -4, 0, -4);
        var right = Column(Icon(r, 34), T(WeatherService.Describe(r.Code), 12.5, FontWeights.SemiBold),
            T(Desk.L("Y:", "H:") + WeatherHub.Temp(r.TodayMax) + "  " + Desk.L("D:", "L:") + WeatherHub.Temp(r.TodayMin), 12, FontWeights.SemiBold, Secondary));
        right.HorizontalAlignment = HorizontalAlignment.Right;
        foreach (UIElement child in right.Children)
            if (child is FrameworkElement fe) fe.HorizontalAlignment = HorizontalAlignment.Right;
        var grid = Grid(columns: "*,Auto");
        grid.Children.Add(Column(city, temp));
        grid.Children.Add(At(right, 0, 1));
        return grid;
    }

    private static FrameworkElement Medium(WeatherReport r)
    {
        var grid = Grid("Auto,*,Auto");
        grid.Children.Add(Now(r, 40));
        grid.Children.Add(At(new Border { Height = 1, Background = Track, Margin = new Thickness(0, 8, 0, 6), VerticalAlignment = VerticalAlignment.Bottom }, 1));
        grid.Children.Add(At(Hours(r, 6), 2));
        return grid;
    }

    private static FrameworkElement Large(WeatherReport r)
    {
        var grid = Grid("Auto,Auto,Auto,*");
        grid.Children.Add(Now(r, 48));
        grid.Children.Add(At(Margin(Hours(r, 6), 0, 10, 0, 8), 1));
        grid.Children.Add(At(new Border { Height = 1, Background = Track, Margin = new Thickness(0, 0, 0, 6) }, 2));
        var days = new StackPanel();
        var low = r.Days.Take(5).Select(d => d.Min).DefaultIfEmpty(0).Min();
        var high = r.Days.Take(5).Select(d => d.Max).DefaultIfEmpty(1).Max();
        var i = 0;
        foreach (var day in r.Days.Take(5))
        {
            var row = Grid(columns: "70,32,36,*,36");
            row.Margin = new Thickness(0, 2, 0, 2);
            row.Children.Add(T(i++ == 0 ? Desk.L("Bugün", "Today") : day.Date.ToString("ddd", Desk.Culture), 13, FontWeights.SemiBold));
            row.Children.Add(At(WeatherArt.Create(day.Code, true, 20), 0, 1));
            var min = T(WeatherHub.Temp(day.Min), 13, FontWeights.SemiBold, Secondary);
            min.HorizontalAlignment = HorizontalAlignment.Right;
            row.Children.Add(At(min, 0, 2));
            var bar = new Grid { Height = 5, Margin = new Thickness(10, 0, 10, 0) };
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(2.5), Background = Track });
            var span = Math.Max(1, high - low);
            var fill = new Border
            {
                CornerRadius = new CornerRadius(2.5),
                Background = new LinearGradientBrush(Color.FromRgb(0x64, 0xD2, 0xFF), Color.FromRgb(0xFF, 0x9F, 0x0A), 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            bar.Children.Add(fill);
            bar.SizeChanged += (_, e) =>
            {
                fill.Margin = new Thickness(e.NewSize.Width * (day.Min - low) / span, 0, 0, 0);
                fill.Width = Math.Max(5, e.NewSize.Width * (day.Max - day.Min) / span);
            };
            row.Children.Add(At(bar, 0, 3));
            var max = T(WeatherHub.Temp(day.Max), 13, FontWeights.SemiBold);
            max.HorizontalAlignment = HorizontalAlignment.Right;
            row.Children.Add(At(max, 0, 4));
            days.Children.Add(row);
        }
        grid.Children.Add(At(days, 3));
        return grid;
    }

    public override void SettingsChanged() => Render();

    public override void AddMenuItems(ItemsControl menu)
    {
        menu.Items.Add(MenuEntry(Desk.L("Gökyüzü renginde arka plan", "Sky-colored background"), () =>
        {
            SetOption("plain", Colored ? "1" : null);
            Render();
        }, Colored));
        menu.Items.Add(MenuEntry(Desk.L("Yenile", "Refresh"), () => _ = WeatherHub.Instance.RefreshAsync()));
    }
}

/// <summary>Sunrise and sunset with the sun's place on its arc.</summary>
internal sealed class SunWidget : DeskWidget
{
    private SunArc? _arc;
    private TextBlock? _rise, _set, _middle, _title, _big;

    public override TimeSpan? Interval => TimeSpan.FromMinutes(1);

    public override FrameworkElement Build()
    {
        _arc = new SunArc();
        _rise = Digits("", 13, FontWeights.SemiBold);
        _set = Digits("", 13, FontWeights.SemiBold);
        _middle = T("", 11.5, FontWeights.SemiBold, Secondary);
        if (Size == DeskSize.Small)
        {
            _title = T("", 12.5, FontWeights.SemiBold, Orange);
            _big = Digits("", 34, FontWeights.SemiBold);
            var grid = Grid("Auto,Auto,*,Auto");
            grid.Children.Add(_title);
            grid.Children.Add(At(_big, 1));
            grid.Children.Add(At(Margin(_arc, 0, 8, 0, 4), 2));
            grid.Children.Add(At(_middle, 3));
            return grid;
        }
        var footer = Grid(columns: "Auto,*,Auto");
        footer.Children.Add(Row(DeskUi.Icon("IconSunrise", 13, Orange), Margin(_rise, 5)));
        _middle.HorizontalAlignment = HorizontalAlignment.Center;
        footer.Children.Add(At(_middle, 0, 1));
        footer.Children.Add(At(Row(_set, Margin(DeskUi.Icon("IconSunset", 13, Orange), 5)), 0, 2));
        var layout = Grid("*,Auto");
        layout.Children.Add(Margin(_arc, 0, 0, 0, 4));
        layout.Children.Add(At(footer, 1));
        return layout;
    }

    protected override void OnAttach() => WeatherHub.Instance.Subscribe(Tick);
    protected override void OnDetach() => WeatherHub.Instance.Unsubscribe(Tick);

    public override void Tick()
    {
        if (WeatherHub.Instance.Report?.Extra is not { Sunrise: { } rise, Sunset: { } set } extra)
        {
            _middle?.SetCurrentValue(TextBlock.TextProperty, WeatherHub.Instance.Error ?? Desk.L("Yükleniyor…", "Loading…"));
            return;
        }
        var now = DateTime.Now;
        var length = set - rise;
        _arc?.SetProgress((now - rise).TotalMinutes / Math.Max(1, length.TotalMinutes));
        _rise!.Text = rise.ToString("HH:mm", CultureInfo.InvariantCulture);
        _set!.Text = set.ToString("HH:mm", CultureInfo.InvariantCulture);
        _middle!.Text = Desk.L($"Gün {(int)length.TotalHours} sa {length.Minutes} dk", $"Day {(int)length.TotalHours} h {length.Minutes} m");
        if (_title is null || _big is null) return;
        if (now < rise)
        {
            _title.Text = Desk.L("Gün doğumu", "Sunrise");
            _big.Text = rise.ToString("HH:mm", CultureInfo.InvariantCulture);
        }
        else if (now < set)
        {
            _title.Text = Desk.L("Gün batımı", "Sunset");
            _big.Text = set.ToString("HH:mm", CultureInfo.InvariantCulture);
        }
        else
        {
            _title.Text = Desk.L("Gün doğumu (yarın)", "Sunrise (tomorrow)");
            _big.Text = (extra.NextSunrise ?? rise.AddDays(1)).ToString("HH:mm", CultureInfo.InvariantCulture);
        }
    }
}

/// <summary>Wind speed, gusts and direction.</summary>
internal sealed class WindWidget : DeskWidget
{
    private Compass? _compass;
    private TextBlock? _speed, _detail;

    public override FrameworkElement Build()
    {
        _compass = new Compass();
        _speed = T("", Size == DeskSize.Small ? 22 : 26, FontWeights.SemiBold);
        _detail = T("", 11.5, FontWeights.SemiBold, Secondary);
        _detail.TextWrapping = TextWrapping.Wrap;
        if (Size == DeskSize.Small)
        {
            _compass.Width = _compass.Height = 58;
            _compass.VerticalAlignment = VerticalAlignment.Top;
            var top = Grid(columns: "*,Auto");
            var title = T(Desk.L("Rüzgâr", "Wind"), 12.5, FontWeights.SemiBold, Cyan);
            title.VerticalAlignment = VerticalAlignment.Top;
            top.Children.Add(title);
            top.Children.Add(At(_compass, 0, 1));
            var small = Grid("Auto,Auto,*");
            small.Children.Add(top);
            small.Children.Add(At(Margin(_speed, 0, 2), 1));
            _detail.VerticalAlignment = VerticalAlignment.Bottom;
            small.Children.Add(At(_detail, 2));
            return small;
        }
        _compass.Width = _compass.Height = 52;
        var grid = Grid(columns: "Auto,*");
        grid.Children.Add(_compass);
        grid.Children.Add(At(Margin(Column(_speed, _detail), 12), 0, 1));
        return grid;
    }

    protected override void OnAttach() => WeatherHub.Instance.Subscribe(Tick);
    protected override void OnDetach() => WeatherHub.Instance.Unsubscribe(Tick);

    public override void Tick()
    {
        if (WeatherHub.Instance.Report?.Extra is not { } extra || double.IsNaN(extra.WindSpeed))
        {
            if (_detail is not null) _detail.Text = WeatherHub.Instance.Error ?? Desk.L("Yükleniyor…", "Loading…");
            return;
        }
        _compass?.SetDirection(extra.WindDirection);
        _speed!.Text = Math.Round(extra.WindSpeed).ToString(CultureInfo.InvariantCulture) + Desk.L(" km/sa", " km/h");
        var direction = Direction(extra.WindDirection);
        var gust = double.IsNaN(extra.WindGusts) ? "" : Desk.L($"Hamle {Math.Round(extra.WindGusts)} km/sa", $"Gusts {Math.Round(extra.WindGusts)} km/h");
        _detail!.Text = Size == DeskSize.Small
            ? direction + (gust.Length > 0 ? Environment.NewLine + gust : "")
            : Desk.L($"{direction} rüzgârı", $"{direction} wind") + (gust.Length > 0 ? " · " + gust : "");
    }

    private static string Direction(double degrees)
    {
        string[] tr = ["K", "KD", "D", "GD", "G", "GB", "B", "KB"];
        string[] en = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
        var index = (int)Math.Round(((degrees % 360) + 360) % 360 / 45) % 8;
        return Desk.L(tr[index], en[index]);
    }
}
