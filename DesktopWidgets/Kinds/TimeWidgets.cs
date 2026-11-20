using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using QiDock.Services.Widgets;
using QiDock.Widgets;
using static QiDock.DesktopWidgets.DeskUi;

namespace QiDock.DesktopWidgets.Kinds;

/// <summary>Digital clock: time, weekday and date; the medium size adds the weather.</summary>
internal sealed class DigitalClockWidget : DeskWidget
{
    private TextBlock? _time, _seconds, _day, _date, _temp;
    private ContentControl? _weatherIcon;

    public override TimeSpan? Interval => TimeSpan.FromSeconds(1);
    private bool ShowSeconds => Option("seconds") == "1" || Size == DeskSize.Strip;

    public override FrameworkElement Build()
    {
        _seconds = _temp = null;
        _weatherIcon = null;
        switch (Size)
        {
            case DeskSize.Mini:
            {
                _time = Digits("", 30);
                _day = T("", 12, FontWeights.SemiBold, Orange);
                _date = T("", 12, brush: Secondary);
                var grid = Grid(columns: "*,Auto");
                grid.Children.Add(_time);
                var right = Column(_day, _date);
                right.HorizontalAlignment = HorizontalAlignment.Right;
                grid.Children.Add(At(right, 0, 1));
                return grid;
            }
            case DeskSize.Strip:
            {
                _time = Digits("", 42);
                _time.HorizontalAlignment = HorizontalAlignment.Center;
                return _time;
            }
            case DeskSize.Medium:
            {
                _day = T("", 14, FontWeights.SemiBold, Orange);
                _time = Digits("", 60);
                _date = T("", 13, brush: Secondary);
                _weatherIcon = new ContentControl { Width = 44, Height = 44 };
                _temp = T("", 28, FontWeights.SemiBold);
                var right = Column(_weatherIcon, _temp);
                right.HorizontalAlignment = HorizontalAlignment.Right;
                _temp.HorizontalAlignment = HorizontalAlignment.Center;
                var grid = Grid(columns: "*,Auto");
                grid.Children.Add(Column(_day, _time, _date));
                grid.Children.Add(At(right, 0, 1));
                return grid;
            }
            case DeskSize.Tall:
            {
                // Upright: hours over minutes, large, the weather at the foot.
                _day = T("", 14, FontWeights.SemiBold, Orange);
                _time = Digits("", 78, FontWeights.SemiBold);
                _time.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                _time.LineHeight = 80;
                _time.VerticalAlignment = VerticalAlignment.Center;
                _date = T("", 12.5, brush: Secondary);
                _weatherIcon = new ContentControl { Width = 26, Height = 26 };
                _temp = T("", 17, FontWeights.SemiBold);
                var grid = Grid("Auto,*,Auto,Auto");
                grid.Children.Add(_day);
                grid.Children.Add(At(_time, 1));
                grid.Children.Add(At(_date, 2));
                grid.Children.Add(At(Margin(Row(_weatherIcon, Margin(_temp, 6)), 0, 8), 3));
                return grid;
            }
            default:
            {
                _day = T("", 14, FontWeights.SemiBold, Orange);
                _time = Digits("", 46);
                _date = T("", 12.5, brush: Secondary);
                var stack = Column(_day, Margin(_time, 0, 6, 0, 2), _date);
                if (ShowSeconds)
                {
                    _seconds = Digits("", 14, FontWeights.Medium, Secondary);
                    stack.Children.Add(Margin(_seconds, 0, 6));
                }
                stack.VerticalAlignment = VerticalAlignment.Center;
                return stack;
            }
        }
    }

    protected override void OnAttach()
    {
        if (Size is DeskSize.Medium or DeskSize.Tall) WeatherHub.Instance.Subscribe(UpdateWeather);
    }

    protected override void OnDetach() => WeatherHub.Instance.Unsubscribe(UpdateWeather);

    public override void Tick()
    {
        var now = DateTime.Now;
        var culture = Desk.Culture;
        if (_time is not null)
            _time.Text = Size == DeskSize.Tall
                ? now.ToString("HH", culture) + "\n" + now.ToString("mm", culture)
                : now.ToString(ShowSeconds && Size is DeskSize.Strip or DeskSize.Mini ? "HH:mm:ss" : "HH:mm", culture);
        if (Size == DeskSize.Mini && ShowSeconds && _time is not null) _time.FontSize = 26;
        if (_seconds is not null) _seconds.Text = now.ToString(":ss", culture);
        if (_day is not null) _day.Text = Size == DeskSize.Mini ? now.ToString("ddd", culture) : now.ToString("dddd", culture);
        if (_date is not null) _date.Text = Size == DeskSize.Mini ? now.ToString("d MMM", culture) : now.ToString("d MMMM yyyy", culture);
        UpdateWeather();
    }

    private void UpdateWeather()
    {
        if (_temp is null || _weatherIcon is null) return;
        if (WeatherHub.Instance.Report is not { } report) return;
        _temp.Text = WeatherHub.Temp(report.Temperature);
        if (_weatherIcon.Tag as string != $"{report.Code}{report.IsDay}")
        {
            _weatherIcon.Tag = $"{report.Code}{report.IsDay}";
            _weatherIcon.Content = WeatherArt.Create(report.Code, report.IsDay, _weatherIcon.Width);
        }
    }

    public override void AddMenuItems(ItemsControl menu)
    {
        if (Size is DeskSize.Small or DeskSize.Mini)
            menu.Items.Add(MenuEntry(Desk.L("Saniyeleri göster", "Show seconds"), () =>
            {
                SetOption("seconds", Option("seconds") == "1" ? null : "1");
                Host.Rebuild();
            }, Option("seconds") == "1"));
    }
}

/// <summary>Analog clock with a light or dark face.</summary>
internal sealed class AnalogClockWidget : DeskWidget
{
    private AnalogFace? _face;
    private TextBlock? _date;

    public override TimeSpan? Interval => TimeSpan.FromSeconds(1);

    public override FrameworkElement Build()
    {
        _face = new AnalogFace { Light = Option("dark") != "1" };
        if (Size == DeskSize.Small) return Margin(_face, -4, -4, -4, -4);
        _date = T("", 14, FontWeights.SemiBold, Secondary);
        _date.HorizontalAlignment = HorizontalAlignment.Center;
        var grid = Grid("*,Auto");
        grid.Children.Add(Margin(_face, 10, 4, 10, 8));
        grid.Children.Add(At(_date, 1));
        return grid;
    }

    public override void Tick()
    {
        var now = DateTime.Now;
        _face?.SetTime(now);
        if (_date is not null) _date.Text = now.ToString("dddd, d MMMM", Desk.Culture);
    }

    public override void AddMenuItems(ItemsControl menu) =>
        menu.Items.Add(MenuEntry(Desk.L("Koyu kadran", "Dark face"), () =>
        {
            SetOption("dark", Option("dark") == "1" ? null : "1");
            Host.Rebuild();
        }, Option("dark") == "1"));
}

/// <summary>Hours and minutes on four split-flap style tiles.</summary>
internal sealed class FlipClockWidget : DeskWidget
{
    private readonly TextBlock[] _digits = new TextBlock[4];
    private TextBlock? _date;

    public override TimeSpan? Interval => TimeSpan.FromSeconds(1);

    public override FrameworkElement Build()
    {
        var big = Size == DeskSize.Medium;
        var row = Grid(columns: "*,*,10,*,*");
        for (var i = 0; i < 4; i++)
        {
            _digits[i] = Digits("0", big ? 70 : 44, FontWeights.Bold);
            _digits[i].HorizontalAlignment = HorizontalAlignment.Center;
            var tile = new Grid { Margin = new Thickness(3, 0, 3, 0) };
            tile.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = QiDock.Widgets.Ui.Vertical("#FF3A3A3E", "#FF2A2A2E") });
            tile.Children.Add(_digits[i]);
            tile.Children.Add(new Border { Height = 1.5, Background = Ui.Solid("#CC1C1C1F"), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(At(tile, 0, i < 2 ? i : i + 1));
        }
        if (!big) return row;
        _date = T("", 13, FontWeights.SemiBold, Secondary);
        _date.HorizontalAlignment = HorizontalAlignment.Center;
        var grid = Grid("*,Auto");
        grid.Children.Add(row);
        grid.Children.Add(At(Margin(_date, 0, 8), 1));
        return grid;
    }

    public override void Tick()
    {
        var text = DateTime.Now.ToString("HHmm", CultureInfo.InvariantCulture);
        for (var i = 0; i < 4; i++) _digits[i].Text = text[i].ToString();
        if (_date is not null) _date.Text = DateTime.Now.ToString("dddd, d MMMM", Desk.Culture);
    }
}

/// <summary>Today's date in calendar-page style.</summary>
internal sealed class DateWidget : DeskWidget
{
    private TextBlock? _a, _b, _c, _temp;
    private ContentControl? _icon;

    public override TimeSpan? Interval => TimeSpan.FromSeconds(30);

    public override FrameworkElement Build()
    {
        _temp = null;
        _icon = null;
        switch (Size)
        {
            case DeskSize.Mini:
                _a = T("", 12.5, FontWeights.SemiBold, Red);
                _b = T("", 24, FontWeights.SemiBold);
                return Column(_a, _b);
            case DeskSize.Strip:
            {
                _a = T("", 20, FontWeights.SemiBold);
                _b = T("", 13, brush: Secondary);
                _icon = new ContentControl { Width = 30, Height = 30 };
                _temp = T("", 22, FontWeights.SemiBold);
                var grid = Grid(columns: "*,Auto,Auto");
                grid.Children.Add(Column(_a, _b));
                grid.Children.Add(At(_icon, 0, 1));
                grid.Children.Add(At(Margin(_temp, 6), 0, 2));
                return grid;
            }
            default:
            {
                _a = T("", 14, FontWeights.Bold, Red);
                _b = Digits("", 64, FontWeights.Light);
                _c = T("", 13, brush: Secondary);
                foreach (var t in new[] { _a, _b, _c }) t.HorizontalAlignment = HorizontalAlignment.Center;
                _b.Margin = new Thickness(0, -6, 0, -4);
                return Column(_a, _b, _c);
            }
        }
    }

    protected override void OnAttach()
    {
        if (Size == DeskSize.Strip) WeatherHub.Instance.Subscribe(Tick);
    }

    protected override void OnDetach() => WeatherHub.Instance.Unsubscribe(Tick);

    public override void Tick()
    {
        var now = DateTime.Now;
        var culture = Desk.Culture;
        switch (Size)
        {
            case DeskSize.Mini:
                _a!.Text = now.ToString("dddd", culture);
                _b!.Text = now.ToString("d MMMM", culture);
                break;
            case DeskSize.Strip:
                _a!.Text = now.ToString("d MMMM, dddd", culture);
                _b!.Text = now.ToString("HH:mm", culture) + (WeatherHub.Instance.Report is { } r ? "  ·  " + r.City : "");
                if (WeatherHub.Instance.Report is { } report)
                {
                    _temp!.Text = WeatherHub.Temp(report.Temperature);
                    if (_icon!.Tag as string != $"{report.Code}{report.IsDay}")
                    {
                        _icon.Tag = $"{report.Code}{report.IsDay}";
                        _icon.Content = WeatherArt.Create(report.Code, report.IsDay, 30);
                    }
                }
                break;
            default:
                _a!.Text = now.ToString("dddd", culture).ToUpper(culture);
                _b!.Text = now.Day.ToString(CultureInfo.InvariantCulture);
                _c!.Text = now.ToString("MMMM yyyy", culture);
                break;
        }
    }
}

/// <summary>How much of the year, month, week or day has gone by, as dots.</summary>
internal sealed class ProgressWidget : DeskWidget
{
    private DotGrid? _dots;
    private TextBlock? _label, _percent;
    private readonly List<(TextBlock Percent, Border Fill, Func<double> Value)> _rows = [];

    public override TimeSpan? Interval => TimeSpan.FromMinutes(1);
    private string Scope => Option("scope") ?? "year";

    public override FrameworkElement Build()
    {
        _rows.Clear();
        if (Size == DeskSize.Medium)
        {
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            AddRow(stack, Desk.L("Bugün", "Today"), () => DayFraction(DateTime.Now), Orange);
            AddRow(stack, Desk.L("Bu hafta", "This week"), () => WeekFraction(DateTime.Now), Purple);
            AddRow(stack, DateTime.Now.ToString("MMMM", Desk.Culture), () => MonthFraction(DateTime.Now), Blue);
            AddRow(stack, DateTime.Now.Year.ToString(CultureInfo.InvariantCulture), () => YearFraction(DateTime.Now), Green);
            return stack;
        }
        _dots = new DotGrid { FilledBrush = Text, CurrentBrush = Orange };
        _label = T("", 12.5, FontWeights.SemiBold, Secondary);
        _percent = Digits("", 12.5, FontWeights.Bold);
        var footer = Grid(columns: "*,Auto");
        footer.Children.Add(_label);
        footer.Children.Add(At(_percent, 0, 1));
        var grid = Grid("*,Auto");
        grid.Children.Add(Margin(_dots, 0, 0, 0, 6));
        grid.Children.Add(At(footer, 1));
        return grid;
    }

    private void AddRow(Panel stack, string label, Func<double> value, Brush color)
    {
        var name = T(label, 12, FontWeights.SemiBold, Secondary);
        var percent = Digits("", 12, FontWeights.Bold);
        var top = Grid(columns: "*,Auto");
        top.Children.Add(name);
        top.Children.Add(At(percent, 0, 1));
        var fill = new Border { CornerRadius = new CornerRadius(3), Background = color, HorizontalAlignment = HorizontalAlignment.Left };
        var bar = new Grid { Height = 6, Margin = new Thickness(0, 3, 0, 7) };
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = Track });
        bar.Children.Add(fill);
        stack.Children.Add(top);
        stack.Children.Add(bar);
        _rows.Add((percent, fill, value));
    }

    public override void Tick()
    {
        var now = DateTime.Now;
        foreach (var (percent, fill, value) in _rows)
        {
            var f = value();
            percent.Text = Percent(f * 100);
            if (fill.Parent is Grid bar) fill.Width = Math.Max(6, (bar.ActualWidth > 0 ? bar.ActualWidth : 312) * f);
        }
        if (_dots is null) return;
        var strip = Size == DeskSize.Strip;
        var culture = Desk.Culture;
        switch (Scope)
        {
            case "month":
            {
                var days = DateTime.DaysInMonth(now.Year, now.Month);
                _dots.Set(days, now.Day - 1, strip ? 16 : 7);
                _label!.Text = now.ToString("MMMM yyyy", culture);
                _percent!.Text = Percent(MonthFraction(now) * 100);
                break;
            }
            case "week":
                _dots.Set(7, ((int)now.DayOfWeek + 6) % 7, 7);
                _label!.Text = Desk.L("Bu hafta", "This week");
                _percent!.Text = Percent(WeekFraction(now) * 100);
                break;
            case "day":
                _dots.Set(24, now.Hour, strip ? 12 : 6);
                _label!.Text = Desk.L("Bugün", "Today");
                _percent!.Text = Percent(DayFraction(now) * 100);
                break;
            default:
            {
                var week = Math.Min(51, (now.DayOfYear - 1) / 7);
                _dots.Set(52, week, strip ? 26 : 8);
                _label!.Text = now.Year.ToString(CultureInfo.InvariantCulture);
                _percent!.Text = Percent(YearFraction(now) * 100);
                break;
            }
        }
    }

    private static double DayFraction(DateTime now) => now.TimeOfDay.TotalHours / 24;
    private static double WeekFraction(DateTime now) => (((int)now.DayOfWeek + 6) % 7 + DayFraction(now)) / 7;
    private static double MonthFraction(DateTime now) => (now.Day - 1 + DayFraction(now)) / DateTime.DaysInMonth(now.Year, now.Month);
    private static double YearFraction(DateTime now) => (now.DayOfYear - 1 + DayFraction(now)) / (DateTime.IsLeapYear(now.Year) ? 366 : 365);

    public override void AddMenuItems(ItemsControl menu)
    {
        if (Size == DeskSize.Medium) return;
        foreach (var (key, tr, en) in new[] { ("year", "Yıl", "Year"), ("month", "Ay", "Month"), ("week", "Hafta", "Week"), ("day", "Gün", "Day") })
            menu.Items.Add(MenuEntry(Desk.L(tr, en), () =>
            {
                SetOption("scope", key == "year" ? null : key);
                Tick();
            }, Scope == key));
    }
}

/// <summary>Countdown timer; tells you when time is up even while covered by windows.</summary>
internal sealed class TimerWidget : DeskWidget
{
    private static readonly int[] Presets = [1, 3, 5, 10, 15, 20, 30, 45, 60];
    private readonly DispatcherTimer _done = new(DispatcherPriority.Normal);
    private TimeSpan _remaining;
    private DateTime _endsAt;
    private bool _running;
    private RingGauge? _ring;
    private TextBlock? _text;
    private DeskButton? _play;

    public TimerWidget()
    {
        _done.Tick += (_, _) => Finish();
    }

    private TimeSpan Length => TimeSpan.FromMinutes(Math.Clamp(OptionInt("minutes", 5), 1, 180));
    public override TimeSpan? Interval => TimeSpan.FromMilliseconds(500);

    public override FrameworkElement Build()
    {
        if (_remaining == TimeSpan.Zero && !_running) _remaining = Length;
        _ring = new RingGauge { Stroke = Orange, Thickness = Size == DeskSize.Small ? 8 : 4 };
        _text = Digits("", Size switch { DeskSize.Small => 30, DeskSize.Mini => 28, _ => 36 });
        _play = DeskButton.Round("GlyphPlay", Toggle, Size == DeskSize.Small ? 40 : 36, Orange, Brushes.White);
        var reset = DeskButton.Round("IconClose", Reset, Size == DeskSize.Small ? 40 : 30, Inset, Secondary, outline: true, glyph: 11);
        switch (Size)
        {
            case DeskSize.Small:
            {
                var center = new Grid { Width = 92, Height = 92, HorizontalAlignment = HorizontalAlignment.Center };
                center.Children.Add(_ring);
                _text.HorizontalAlignment = HorizontalAlignment.Center;
                _text.FontSize = 24;
                center.Children.Add(_text);
                var buttons = Row(reset, Margin(_play, 12));
                buttons.HorizontalAlignment = HorizontalAlignment.Center;
                buttons.Margin = new Thickness(0, 8, 0, 0);
                return Column(center, buttons);
            }
            case DeskSize.Mini:
            {
                var grid = Grid(columns: "*,Auto");
                grid.Children.Add(_text);
                grid.Children.Add(At(_play, 0, 1));
                return grid;
            }
            default:
            {
                var grid = Grid(columns: "Auto,*,Auto,Auto");
                var ringHost = new Grid { Width = 40, Height = 40 };
                ringHost.Children.Add(_ring);
                ringHost.Children.Add(Icon("IconTimer", 15, Orange));
                grid.Children.Add(ringHost);
                grid.Children.Add(At(Margin(_text, 12), 0, 1));
                grid.Children.Add(At(Margin(reset, 0, 0, 10), 0, 2));
                grid.Children.Add(At(_play, 0, 3));
                return grid;
            }
        }
    }

    public override void Tick()
    {
        var remaining = _running ? _endsAt - DateTime.Now : _remaining;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        if (_text is not null)
            _text.Text = remaining.TotalHours >= 1 ? remaining.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : $"{(int)remaining.TotalMinutes}:{remaining.Seconds:00}";
        if (_ring is not null) _ring.Value = remaining.TotalSeconds / Length.TotalSeconds * 100;
        if (_play is not null) _play.Child = Glyph(_running ? "GlyphPause" : "GlyphPlay", 13, Brushes.White);
    }

    private void Toggle()
    {
        if (_running)
        {
            _remaining = _endsAt - DateTime.Now;
            _running = false;
            _done.Stop();
        }
        else
        {
            if (_remaining <= TimeSpan.Zero) _remaining = Length;
            _endsAt = DateTime.Now + _remaining;
            _running = true;
            _done.Interval = _remaining;
            _done.Start();
        }
        Tick();
    }

    private void Reset()
    {
        _running = false;
        _done.Stop();
        _remaining = Length;
        Tick();
    }

    private void Finish()
    {
        _done.Stop();
        _running = false;
        _remaining = Length;
        Desk.Notify("IconTimer", Desk.L("Süre doldu", "Time is up"));
        if (Host.IsLive) Tick();
    }

    public override void AddMenuItems(ItemsControl menu)
    {
        var length = new MenuItem { Header = Desk.L("Süre", "Length") };
        foreach (var minutes in Presets)
            length.Items.Add(MenuEntry(Desk.L($"{minutes} dakika", $"{minutes} minutes"), () =>
            {
                SetOption("minutes", minutes.ToString(CultureInfo.InvariantCulture));
                Reset();
            }, Length.TotalMinutes == minutes));
        menu.Items.Add(length);
    }

    public override void Dispose()
    {
        _done.Stop();
        base.Dispose();
    }
}

/// <summary>Stopwatch with tenths of a second.</summary>
internal sealed class StopwatchWidget : DeskWidget
{
    private readonly System.Diagnostics.Stopwatch _watch = new();
    private TextBlock? _text;
    private DeskButton? _play;

    public override TimeSpan? Interval => TimeSpan.FromMilliseconds(100);

    public override FrameworkElement Build()
    {
        _text = Digits("", Size == DeskSize.Mini ? 26 : 36);
        _play = DeskButton.Round("GlyphPlay", Toggle, 36, Blue, Brushes.White);
        var reset = DeskButton.Round("IconRotate", Reset, 30, Inset, Secondary, outline: true, glyph: 13);
        var grid = Grid(columns: Size == DeskSize.Mini ? "*,Auto" : "*,Auto,Auto");
        grid.Children.Add(_text);
        if (Size == DeskSize.Mini)
        {
            grid.Children.Add(At(_play, 0, 1));
            return grid;
        }
        grid.Children.Add(At(Margin(reset, 0, 0, 10), 0, 1));
        grid.Children.Add(At(_play, 0, 2));
        return grid;
    }

    public override void Tick()
    {
        var t = _watch.Elapsed;
        if (_text is not null)
            _text.Text = t.TotalHours >= 1
                ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
        if (_play is not null) _play.Child = Glyph(_watch.IsRunning ? "GlyphPause" : "GlyphPlay", 13, Brushes.White);
    }

    private void Toggle()
    {
        if (_watch.IsRunning) _watch.Stop();
        else _watch.Start();
        Tick();
    }

    private void Reset()
    {
        _watch.Reset();
        Tick();
    }
}

/// <summary>Up to three cities' times (the same cities as the bar's world clock).</summary>
internal sealed class WorldClockDeskWidget : DeskWidget
{
    private readonly List<(string Zone, TextBlock Time, TextBlock? Offset, AnalogFace? Face)> _cells = [];

    public override TimeSpan? Interval => TimeSpan.FromSeconds(Size is DeskSize.Medium or DeskSize.Tall ? 1 : 20);

    private static IEnumerable<(string Name, string Zone)> Cities()
    {
        var keys = Desk.Settings().WorldClocks is { Count: > 0 } chosen ? chosen : WorldClockWidget.Defaults;
        foreach (var key in keys.Take(3))
        {
            var city = WorldClockWidget.Cities.FirstOrDefault(c => c.Key == key);
            if (city.Key is null) continue;
            yield return (Desk.L(city.Tr, city.En), city.Zone);
        }
    }

    public override FrameworkElement Build()
    {
        _cells.Clear();
        var cities = Cities().ToList();
        if (Size == DeskSize.Tall)
        {
            // Upright: one city per row, its clock face beside the time.
            var column = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            foreach (var (name, zone) in cities)
            {
                var face = new AnalogFace { Width = 50, Height = 50, Light = true, Numbers = false };
                var time = Digits("", 20);
                var offset = T("", 10.5, brush: Secondary);
                var row = Grid(columns: "Auto,*");
                row.Children.Add(face);
                row.Children.Add(At(Margin(Column(T(name, 12.5, FontWeights.SemiBold), time, offset), 10), 0, 1));
                row.Margin = new Thickness(0, 7, 0, 7);
                column.Children.Add(row);
                _cells.Add((zone, time, offset, face));
            }
            return column;
        }
        if (Size == DeskSize.Small)
        {
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            foreach (var (name, zone) in cities)
            {
                var time = Digits("", 20);
                var offset = T("", 11, brush: Secondary);
                var row = Grid(columns: "*,Auto");
                row.Children.Add(Column(T(name, 12.5, FontWeights.SemiBold), offset));
                row.Children.Add(At(time, 0, 1));
                row.Margin = new Thickness(0, 3, 0, 3);
                stack.Children.Add(row);
                _cells.Add((zone, time, offset, null));
            }
            return stack;
        }
        var grid = Grid(columns: string.Join(",", cities.Select(_ => "*")));
        var i = 0;
        foreach (var (name, zone) in cities)
        {
            AnalogFace? face = null;
            var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            if (Size == DeskSize.Medium)
            {
                face = new AnalogFace { Width = 64, Height = 64, Light = true, Numbers = false, Margin = new Thickness(0, 0, 0, 6) };
                cell.Children.Add(face);
            }
            var label = T(name, 12, FontWeights.SemiBold, Secondary);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var time = Digits("", Size == DeskSize.Medium ? 17 : 20);
            time.HorizontalAlignment = HorizontalAlignment.Center;
            cell.Children.Add(label);
            cell.Children.Add(time);
            grid.Children.Add(At(cell, 0, i++));
            _cells.Add((zone, time, null, face));
        }
        return grid;
    }

    public override void Tick()
    {
        foreach (var (zone, time, offset, face) in _cells)
        {
            DateTime local;
            try { local = TimeZoneInfo.ConvertTime(DateTime.Now, TimeZoneInfo.FindSystemTimeZoneById(zone)); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { continue; }
            time.Text = local.ToString("HH:mm", CultureInfo.InvariantCulture);
            face?.SetTime(local);
            if (offset is not null)
            {
                var hours = (local - DateTime.Now).TotalHours;
                hours = Math.Round(hours * 2) / 2;
                var day = local.Date == DateTime.Today ? Desk.L("Bugün", "Today") : local.Date > DateTime.Today ? Desk.L("Yarın", "Tomorrow") : Desk.L("Dün", "Yesterday");
                offset.Text = day + ", " + (hours == 0 ? "±0" : (hours > 0 ? "+" : "−") + Math.Abs(hours).ToString("0.#", CultureInfo.InvariantCulture)) + Desk.L(" sa", " h");
            }
        }
    }

    public override void SettingsChanged() => Host.Rebuild();
}
