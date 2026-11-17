using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using QiDock.Models;
using QiDock.Services;
using QiDock.Services.Widgets;

namespace QiDock.Widgets;

/// <summary>Current weather tile; opens hourly and 7-day forecasts.</summary>
public sealed class WeatherWidget : DockWidget
{
    private readonly Button _tile;
    private readonly TextBlock _condition;
    private readonly TextBlock _city;
    private readonly TextBlock _temperature;
    private readonly ContentControl _icon;
    private readonly StackPanel _days;
    private readonly WidgetPopup _popup;
    private readonly DispatcherTimer _timer;
    private readonly WeatherService _service = new();
    private CancellationTokenSource? _load;
    private WeatherReport? _report;
    private string? _error;
    private string _city_ = "";

    public WeatherWidget()
    {
        // Left: the temperature, "Today" (or the city) under it. Right: the next three days (name, icon, high).
        _temperature = Ui.Text(size: 20, weight: FontWeights.SemiBold);
        _condition = Ui.Text(size: 10, brushKey: "TextSecondary");
        _condition.Margin = new Thickness(1, -3, 0, 0);
        _city = Ui.Text(size: 10, brushKey: "TextSecondary");
        _icon = new ContentControl { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Margin = new Thickness(5, 0, 0, 0) };
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(_temperature);
        top.Children.Add(_icon);
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(top);
        left.Children.Add(_condition);

        _days = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(left);
        row.Children.Add(_days);

        _tile = new Button { Content = row, Padding = new Thickness(12, 0, 10, 0) };
        _tile.SetResourceReference(FrameworkElement.StyleProperty, "WidgetTileButton");
        _tile.Click += (_, _) => TogglePopup();
        _popup = new WidgetPopup(_tile);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(15) };
        _timer.Tick += (_, _) => _ = RefreshAsync();
        UiText.Current.PropertyChanged += (_, _) => { Render(); _ = RefreshAsync(); };
        Render();
    }

    public override string Id => WidgetIds.Weather;
    public override FrameworkElement View => _tile;
    public override bool IsPopupOpen => _popup.IsOpen;

    public override void Start()
    {
        _timer.Start();
        _ = RefreshAsync();
    }

    public override void Stop()
    {
        _timer.Stop();
        _load?.Cancel();
        _popup.Close();
    }

    public override void ClosePopup() => _popup.Close();

    public override void ApplySettings(WidgetSettings settings)
    {
        var city = string.IsNullOrWhiteSpace(settings.WeatherCity) ? "İstanbul" : settings.WeatherCity.Trim();
        if (city == _city_) return;
        _city_ = city;
        _report = null;
        Render();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (string.IsNullOrEmpty(_city_)) return;
        _load?.Cancel();
        var load = _load = new CancellationTokenSource();
        try
        {
            _report = await _service.FetchAsync(_city_, load.Token);
            _error = null;
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
            return;
        }
        catch (InvalidOperationException ex)
        {
            _error = ex.Message == "Şehir bulunamadı." ? UiText.Get("WxCityNotFound") : UiText.Get("WxUnavailable");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or FormatException)
        {
            _error = UiText.Get("WxUnavailable");
        }

        Render();
        if (_popup.IsOpen) BuildPopup();
    }

    private static CultureInfo Culture => CultureInfo.GetCultureInfo(UiText.Get("CultureName"));

    private static string Degrees(double value) => double.IsFinite(value)
        ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture) + "°" : "–";

    private void Render()
    {
        _days.Children.Clear();
        if (_report is { } report)
        {
            _temperature.Text = Degrees(report.Temperature);
            _icon.Content = WeatherArt.Create(report.Code, report.IsDay, 16);
            _condition.Text = UiText.Get("WxToday");
            _city.Text = report.City;
            foreach (var day in report.Days.Where(d => d.Date.Date > DateTime.Today).Take(3))
                _days.Children.Add(DayColumn(day));
            _tile.ToolTip = $"{report.City} · {WeatherService.Describe(report.Code)} · {Degrees(report.Temperature)}";
        }
        else
        {
            _temperature.Text = "–";
            _icon.Content = WeatherArt.Create(2, true, 16);
            _condition.Text = _error ?? (string.IsNullOrEmpty(_city_) ? UiText.Get("Weather") : _city_);
            _tile.ToolTip = _error;
        }
    }

    /// <summary>"Per", the day's icon and its high, stacked.</summary>
    private static FrameworkElement DayColumn(WeatherDay day)
    {
        var name = Ui.Text(day.Date.ToString("ddd", Culture), 9.5, FontWeights.SemiBold, brushKey: "TextSecondary");
        name.HorizontalAlignment = HorizontalAlignment.Center;
        var icon = WeatherArt.Create(day.Code, true, 14);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.Margin = new Thickness(0, 1, 0, 1);
        var high = Ui.Text(Degrees(day.Max), 10, FontWeights.SemiBold);
        high.HorizontalAlignment = HorizontalAlignment.Center;
        var column = new StackPanel { Width = 32, VerticalAlignment = VerticalAlignment.Center };
        column.Children.Add(name);
        column.Children.Add(icon);
        column.Children.Add(high);
        return column;
    }

    private void TogglePopup()
    {
        if (!_popup.IsOpen)
        {
            BuildPopup();
            if (_report is null || DateTime.Now - _report.FetchedAt > TimeSpan.FromMinutes(10)) _ = RefreshAsync();
        }
        _popup.Toggle();
    }

    private void BuildPopup()
    {
        var culture = Culture;
        var root = new StackPanel();

        // Header: city + change link + close
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var close = Ui.IconButton(Ui.Icon("IconClose", 12), (_, _) => _popup.Close(), UiText.Get("Close"));
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(Ui.Text(_report?.City ?? _city_, 13, FontWeights.SemiBold));
        var change = new Button { Content = UiText.Get("WxChangeCity"), Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        change.SetResourceReference(FrameworkElement.StyleProperty, "WidgetLinkButton");
        change.Click += (_, _) => { _popup.Close(); RequestSettings(); };
        titleRow.Children.Add(change);
        header.Children.Add(titleRow);
        root.Children.Add(header);

        if (_report is not { } report)
        {
            root.Children.Add(Ui.Text(_error ?? UiText.Get("WxLoading"), 12, brushKey: _error is null ? "TextSecondary" : "DangerText"));
            _popup.SetContent(root, 360);
            return;
        }

        // Current conditions
        var now = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        now.Children.Add(WeatherArt.Create(report.Code, report.IsDay, 40));
        var nowTemp = Ui.Text(Degrees(report.Temperature), 34, FontWeights.Light);
        nowTemp.Margin = new Thickness(10, 0, 0, 0);
        now.Children.Add(nowTemp);
        root.Children.Add(now);
        root.Children.Add(Ui.Text($"{WeatherService.Describe(report.Code)} · {UiText.Get("WxHigh")} {Degrees(report.TodayMax)}  {UiText.Get("WxLow")} {Degrees(report.TodayMin)}",
            11.5, brushKey: "TextSecondary"));

        // Hourly strip
        var hours = new Grid();
        for (var i = 0; i < report.Hours.Count; i++)
        {
            hours.ColumnDefinitions.Add(new ColumnDefinition());
            var hour = report.Hours[i];
            var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var label = Ui.Text(i == 0 ? UiText.Get("WxNow") : hour.Time.ToString("HH:mm", culture), 10.5, i == 0 ? FontWeights.SemiBold : FontWeights.Normal, "TextSecondary");
            label.HorizontalAlignment = HorizontalAlignment.Center;
            cell.Children.Add(label);
            var icon = WeatherArt.Create(hour.Code, hour.IsDay, 22);
            icon.Margin = new Thickness(0, 4, 0, 4);
            cell.Children.Add(icon);
            var temp = Ui.Text(Degrees(hour.Temperature), 12, FontWeights.SemiBold);
            temp.HorizontalAlignment = HorizontalAlignment.Center;
            cell.Children.Add(temp);
            Grid.SetColumn(cell, i);
            hours.Children.Add(cell);
        }
        // Hourly strip sits on its own lighter panel, like the daily list's header.
        var hourPanel = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(4, 8, 4, 8), Margin = new Thickness(0, 12, 0, 10), Child = hours };
        hourPanel.SetResourceReference(Border.BackgroundProperty, "WidgetTileFill");
        root.Children.Add(hourPanel);

        // 7-day list with temperature range bars
        var weekMin = report.Days.Where(d => double.IsFinite(d.Min)).Select(d => d.Min).DefaultIfEmpty(0).Min();
        var weekMax = report.Days.Where(d => double.IsFinite(d.Max)).Select(d => d.Max).DefaultIfEmpty(1).Max();
        var span = Math.Max(1, weekMax - weekMin);
        foreach (var (day, index) in report.Days.Select((d, i) => (d, i)))
        {
            var row = new Grid { Height = 30 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });

            var name = index == 0 ? UiText.Get("CalToday") : culture.TextInfo.ToTitleCase(day.Date.ToString("ddd", culture));
            row.Children.Add(Ui.Text(name, 12.5, index == 0 ? FontWeights.SemiBold : FontWeights.Normal));
            var icon = WeatherArt.Create(day.Code, true, 20);
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            icon.ToolTip = WeatherService.Describe(day.Code);
            Grid.SetColumn(icon, 1);
            row.Children.Add(icon);
            var min = Ui.Text(Degrees(day.Min), 12, brushKey: "TextSecondary");
            min.HorizontalAlignment = HorizontalAlignment.Right;
            min.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(min, 2);
            row.Children.Add(min);

            var track = new Grid { Height = 5, VerticalAlignment = VerticalAlignment.Center };
            var trackBg = new Border { CornerRadius = new CornerRadius(2.5) };
            trackBg.SetResourceReference(Border.BackgroundProperty, "WidgetTrack");
            track.Children.Add(trackBg);
            var range = new Border
            {
                CornerRadius = new CornerRadius(2.5),
                Background = new LinearGradientBrush(Color.FromRgb(0x5A, 0xC8, 0xFA), Color.FromRgb(0xFF, 0xB3, 0x40), 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            track.Children.Add(range);
            var startRatio = double.IsFinite(day.Min) ? (day.Min - weekMin) / span : 0;
            var endRatio = double.IsFinite(day.Max) ? (day.Max - weekMin) / span : 1;
            track.SizeChanged += (_, e) =>
            {
                range.Margin = new Thickness(e.NewSize.Width * startRatio, 0, 0, 0);
                range.Width = Math.Max(5, e.NewSize.Width * Math.Max(0, endRatio - startRatio));
            };
            Grid.SetColumn(track, 3);
            row.Children.Add(track);

            var max = Ui.Text(Degrees(day.Max), 12, FontWeights.SemiBold);
            max.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(max, 4);
            row.Children.Add(max);
            root.Children.Add(row);
        }

        var footer = Ui.Text(UiText.Format("WxUpdated", report.FetchedAt.ToString("HH:mm", culture)), 10.5, brushKey: "TextSecondary");
        footer.Margin = new Thickness(0, 6, 0, 0);
        footer.Opacity = 0.8;
        root.Children.Add(footer);

        _popup.SetContent(root, 360);
    }
}
