using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using QiDock.Models;
using QiDock.Services;
using QiDock.Services.Widgets;

namespace QiDock.Widgets;

/// <summary>Clock tile; opens a month calendar with the selected day's events.</summary>
public sealed class ClockWidget : DockWidget
{
    private readonly Button _tile;
    private readonly TextBlock _time;
    private readonly TextBlock _seconds;
    private readonly TextBlock _date;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _feedTimer;
    private readonly WidgetPopup _popup;
    private readonly CalendarFeedService _feed = new();
    private CancellationTokenSource? _feedLoad;
    private bool _showSeconds;
    private string? _feedUrl;
    private DateTime _visibleMonth;
    private DateTime _selectedDay;
    private string _lastTime = "";

    public ClockWidget()
    {
        // Large hours:minutes, small seconds beside them and the date underneath, straight on the dock glass.
        _time = Ui.Text(size: 19, weight: FontWeights.SemiBold);
        _seconds = Ui.Text(size: 10.5, weight: FontWeights.SemiBold, brushKey: "TextSecondary");
        _seconds.VerticalAlignment = VerticalAlignment.Bottom;
        _seconds.Margin = new Thickness(1, 0, 0, 3);
        var timeRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        timeRow.Children.Add(_time);
        timeRow.Children.Add(_seconds);
        _date = Ui.Text(size: 10, brushKey: "TextSecondary");
        _date.Margin = new Thickness(0, -3, 0, 0);
        _date.HorizontalAlignment = HorizontalAlignment.Center;
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(timeRow);
        stack.Children.Add(_date);
        // A filled tile like the others: big time centred, the date under it.
        _tile = new Button { Content = stack, MinWidth = 88, Padding = new Thickness(12, 0, 12, 0) };
        _tile.SetResourceReference(FrameworkElement.StyleProperty, "WidgetTileButton");
        _tile.Click += (_, _) => TogglePopup();
        _popup = new WidgetPopup(_tile);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateClock();
        _feedTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(15) };
        _feedTimer.Tick += (_, _) => _ = RefreshFeedAsync();
        UiText.Current.PropertyChanged += (_, _) => { _lastTime = ""; UpdateClock(); };
    }

    public override string Id => WidgetIds.Clock;
    public override FrameworkElement View => _tile;
    public override bool IsPopupOpen => _popup.IsOpen;

    public override void Start()
    {
        UpdateClock();
        _timer.Start();
        _feedTimer.Start();
        _ = RefreshFeedAsync();
    }

    public override void Stop()
    {
        _timer.Stop();
        _feedTimer.Stop();
        _feedLoad?.Cancel();
        _popup.Close();
    }

    public override void ClosePopup() => _popup.Close();

    public override void SetLive(bool live)
    {
        if (!live)
        {
            _timer.Stop();
            return;
        }
        UpdateClock();
        _timer.Start();
    }

    public override void ApplySettings(WidgetSettings settings)
    {
        _showSeconds = settings.ShowSeconds;
        _lastTime = "";
        UpdateClock();
        if (!string.Equals(_feedUrl, settings.CalendarFeedUrl, StringComparison.Ordinal))
        {
            _feedUrl = settings.CalendarFeedUrl;
            _ = RefreshFeedAsync();
        }
    }

    private static CultureInfo Culture => CultureInfo.GetCultureInfo(UiText.Get("CultureName"));

    private void UpdateClock()
    {
        var now = DateTime.Now;
        var time = now.ToString("HH:mm:ss", Culture);
        if (time == _lastTime) return;
        _lastTime = time;
        _time.Text = now.ToString("HH:mm", Culture);
        _seconds.Text = now.ToString(":ss", Culture);
        _seconds.Visibility = _showSeconds ? Visibility.Visible : Visibility.Collapsed;
        _date.Text = now.ToString(UiText.Get("ClockDateFormat"), Culture);
        _tile.ToolTip = now.ToString("D", Culture);
    }

    private async Task RefreshFeedAsync()
    {
        _feedLoad?.Cancel();
        var load = _feedLoad = new CancellationTokenSource();
        try
        {
            await _feed.RefreshAsync(_feedUrl, load.Token);
            if (_popup.IsOpen) BuildPopup();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void TogglePopup()
    {
        if (!_popup.IsOpen)
        {
            _visibleMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            _selectedDay = DateTime.Today;
            BuildPopup();
            if (_feed.HasFeed && (_feed.LastUpdated is null || DateTime.Now - _feed.LastUpdated > TimeSpan.FromMinutes(5)))
                _ = RefreshFeedAsync();
        }
        _popup.Toggle();
    }

    private const double CellWidth = 44;
    private const double CellHeight = 46;

    private void BuildPopup()
    {
        var culture = Culture;
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CellWidth * 7 + 4) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(236) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // --- Header: "Kasım 2026" on the left; Calendars · Today · ‹ › · × on the right ---
        var header = new DockPanel { Margin = new Thickness(2, 0, 0, 4) };
        Grid.SetColumnSpan(header, 3);
        var nav = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(nav, Dock.Right);
        var calendars = new Button { Content = UiText.Get("CalCalendars"), Padding = new Thickness(11, 4, 11, 4), Margin = new Thickness(0, 0, 6, 0) };
        calendars.SetResourceReference(FrameworkElement.StyleProperty, "WidgetPillButton");
        calendars.Click += (_, _) => { _popup.Close(); RequestSettings(); };
        nav.Children.Add(calendars);
        var today = new Button { Content = UiText.Get("CalToday"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        today.SetResourceReference(FrameworkElement.StyleProperty, "WidgetLinkButton");
        today.Click += (_, _) => { _visibleMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); _selectedDay = DateTime.Today; BuildPopup(); };
        nav.Children.Add(today);
        nav.Children.Add(Ui.IconButton(Ui.Icon("IconChevronLeft", 14), (_, _) => ShiftMonth(-1), UiText.Get("CalPrevMonth")));
        nav.Children.Add(Ui.IconButton(Ui.Icon("IconChevronRight", 14), (_, _) => ShiftMonth(1), UiText.Get("CalNextMonth")));
        nav.Children.Add(Ui.IconButton(Ui.Icon("IconClose", 12, brushKey: "TextSecondary"), (_, _) => _popup.Close(), UiText.Get("Close")));
        header.Children.Add(nav);
        var title = Ui.Text(culture.TextInfo.ToTitleCase(_visibleMonth.ToString("MMMM, yyyy", culture)), 19, FontWeights.Bold);
        header.Children.Add(title);
        root.Children.Add(header);

        // --- Month view ---
        var month = new StackPanel();
        Grid.SetRow(month, 1);

        var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(3, 0, 0, 10) };
        legend.Children.Add(LegendDot(EventBrush, UiText.Get("CalEvents")));
        legend.Children.Add(LegendDot(AllDayBrush, UiText.Get("CalAllDay")));
        if (_feed.HasFeed) month.Children.Add(legend); // a key for colors that can't show yet is noise

        var weekdays = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 4) };
        var firstDay = culture.DateTimeFormat.FirstDayOfWeek;
        for (var i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)(((int)firstDay + i) % 7);
            var isTodayColumn = day == DateTime.Today.DayOfWeek;
            var chip = new Border { CornerRadius = new CornerRadius(8), Margin = new Thickness(2), Padding = new Thickness(0, 3, 0, 3) };
            chip.SetResourceReference(Border.BackgroundProperty, "WidgetTileFill");
            var label = Ui.Text(culture.DateTimeFormat.GetAbbreviatedDayName(day).ToUpper(culture), 9.5, FontWeights.SemiBold,
                isTodayColumn ? "TextPrimary" : "TextSecondary");
            label.HorizontalAlignment = HorizontalAlignment.Center;
            chip.Child = label;
            weekdays.Children.Add(chip);
        }
        month.Children.Add(weekdays);

        var gridStart = _visibleMonth.AddDays(-(((int)_visibleMonth.DayOfWeek - (int)firstDay + 7) % 7));
        var weeks = (int)Math.Ceiling(((_visibleMonth - gridStart).Days + DateTime.DaysInMonth(_visibleMonth.Year, _visibleMonth.Month)) / 7.0);
        var gridEnd = gridStart.AddDays(weeks * 7);
        var occurrences = _feed.GetOccurrences(gridStart, gridEnd);
        var perDay = new Dictionary<DateTime, (int Timed, int AllDay)>();
        foreach (var item in occurrences)
        {
            var lastDay = item.AllDay ? item.End.AddDays(-1).Date : (item.End > item.Start ? item.End.AddTicks(-1).Date : item.Start.Date);
            for (var d = item.Start.Date; d <= lastDay && d < gridEnd; d = d.AddDays(1))
            {
                perDay.TryGetValue(d, out var count);
                perDay[d] = item.AllDay ? (count.Timed, count.AllDay + 1) : (count.Timed + 1, count.AllDay);
            }
        }

        var days = new UniformGrid { Columns = 7, Rows = weeks };
        for (var i = 0; i < weeks * 7; i++)
        {
            var date = gridStart.AddDays(i);
            perDay.TryGetValue(date, out var count);
            days.Children.Add(date.Month == _visibleMonth.Month ? CreateDayCell(date, count.Timed, count.AllDay) : new Border());
        }
        month.Children.Add(days);
        root.Children.Add(month);

        // --- Agenda panel ---
        // Top and bottom edges line up with the weekday chips and the last row of day tiles.
        var panel = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 2, 0, 2) };
        panel.SetResourceReference(Border.BackgroundProperty, "WidgetTileFill");
        Grid.SetColumn(panel, 2);
        Grid.SetRow(panel, 1);
        var agendaLayout = new DockPanel();
        panel.Child = agendaLayout;

        // Footer: current time and a quick way to add an event.
        var footer = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 10, 0, 0) };
        footer.SetResourceReference(Border.BackgroundProperty, "WidgetTileFill");
        DockPanel.SetDock(footer, Dock.Bottom);
        var footerRow = new DockPanel();
        var now = Ui.Text(DateTime.Now.ToString("HH:mm", culture), 11.5, FontWeights.SemiBold);
        now.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(now, Dock.Left);
        footerRow.Children.Add(now);
        var newEvent = new Button { Content = UiText.Format("CalNewEventOn", _selectedDay == DateTime.Today ? UiText.Get("CalToday").ToLower(culture) : _selectedDay.ToString("d MMM", culture)), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        newEvent.SetResourceReference(FrameworkElement.StyleProperty, "WidgetLinkButton");
        newEvent.SetResourceReference(Control.ForegroundProperty, "TextSecondary");
        newEvent.Click += (_, _) => { _popup.Close(); Ui.Open(NewEventLink(_selectedDay)); };
        footerRow.Children.Add(newEvent);
        footer.Child = footerRow;
        agendaLayout.Children.Add(footer);

        var agenda = new StackPanel();
        var titleRow = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var dayItems = _feed.HasFeed ? _feed.GetOccurrences(_selectedDay, _selectedDay.AddDays(1)) : [];
        if (_feed.HasFeed)
        {
            var count = Ui.Text(UiText.Format("CalEventCount", dayItems.Count), 10.5, brushKey: "TextSecondary");
            DockPanel.SetDock(count, Dock.Right);
            titleRow.Children.Add(count);
        }
        var agendaTitle = _selectedDay == DateTime.Today ? UiText.Get("CalToday")
            : culture.TextInfo.ToTitleCase(_selectedDay.ToString("d MMMM dddd", culture));
        titleRow.Children.Add(Ui.Text(agendaTitle, 14, FontWeights.Bold));
        agenda.Children.Add(titleRow);

        var list = new StackPanel();
        var scroller = new ScrollViewer { Content = list, MaxHeight = 250, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        agenda.Children.Add(scroller);

        if (!_feed.HasFeed)
        {
            var hint = Ui.Text(UiText.Get("CalNoFeed"), 11.5, brushKey: "TextSecondary");
            hint.TextWrapping = TextWrapping.Wrap;
            hint.TextTrimming = TextTrimming.None;
            list.Children.Add(hint);
            var connect = new Button { Content = UiText.Get("CalConnect"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-6, 8, 0, 0) };
            connect.SetResourceReference(FrameworkElement.StyleProperty, "WidgetLinkButton");
            connect.Click += (_, _) => { _popup.Close(); RequestSettings(); };
            list.Children.Add(connect);
        }
        else if (dayItems.Count == 0)
        {
            list.Children.Add(Ui.Text(_feed.LastError ?? (_feed.LastUpdated is null ? UiText.Get("CalLoading") : UiText.Get("CalEmpty")),
                12, brushKey: _feed.LastError is null ? "TextSecondary" : "DangerText"));
        }
        foreach (var item in dayItems) list.Children.Add(CreateEventRow(item, culture));
        agendaLayout.Children.Add(agenda);
        root.Children.Add(panel);

        _popup.SetContent(root, CellWidth * 7 + 4 + 16 + 236 + 34);
    }

    private static readonly Brush EventBrush = Ui.Solid("#FF3B9BFF");
    private static readonly Brush AllDayBrush = Ui.Solid("#FF8E7CFF");

    private static UIElement LegendDot(Brush brush, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
        row.Children.Add(new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = brush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
        row.Children.Add(Ui.Text(text, 10.5, brushKey: "TextSecondary"));
        return row;
    }

    /// <summary>Opens a new-event page for the day: Google Calendar for Google feeds, Outlook on the web otherwise.</summary>
    private string NewEventLink(DateTime day)
    {
        var date = day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (_feedUrl is { } url && url.Contains("outlook", StringComparison.OrdinalIgnoreCase))
            return "https://outlook.live.com/calendar/0/deeplink/compose?startdt=" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T09:00:00";
        return $"https://calendar.google.com/calendar/r/eventedit?dates={date}/{day.AddDays(1).ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";
    }

    private UIElement CreateDayCell(DateTime date, int timed, int allDay)
    {
        var isToday = date == DateTime.Today;
        var isSelected = date == _selectedDay;
        var isWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

        // Same 2px inset as the weekday chips above, so the columns line up exactly.
        var tile = new Border { CornerRadius = new CornerRadius(10), Margin = new Thickness(2), Height = CellHeight - 4 };
        tile.SetResourceReference(Border.BackgroundProperty, "WidgetTileFill");
        if (isSelected)
        {
            tile.SetResourceReference(Border.BorderBrushProperty, "WidgetAccent");
            tile.BorderThickness = new Thickness(1.5);
        }

        // Day number in the middle of the tile (with its event chips under it); today sits in a blue circle.
        var grid = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var badge = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Center };
        if (isToday) badge.SetResourceReference(Border.BackgroundProperty, "WidgetAccent");
        var number = Ui.Text(date.Day.ToString(CultureInfo.InvariantCulture), 12, isToday ? FontWeights.Bold : FontWeights.SemiBold,
            brush: isToday ? Brushes.White : null, brushKey: isWeekend ? "TextSecondary" : "TextPrimary");
        number.HorizontalAlignment = HorizontalAlignment.Center;
        number.VerticalAlignment = VerticalAlignment.Center;
        badge.Child = number;
        grid.Children.Add(badge);

        // Event chips along the bottom: blue for timed events, violet for all-day ones.
        if (timed + allDay > 0)
        {
            var chips = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = 7, Margin = new Thickness(0, 2, 0, 0) };
            for (var i = 0; i < Math.Min(timed, 3); i++) chips.Children.Add(Chip(EventBrush));
            for (var i = 0; i < Math.Min(allDay, 3 - Math.Min(timed, 3)); i++) chips.Children.Add(Chip(AllDayBrush));
            var total = timed + allDay;
            if (total > 3) chips.Children.Add(Ui.Text("+" + (total - 3).ToString(CultureInfo.InvariantCulture), 8.5, FontWeights.SemiBold, "TextSecondary"));
            grid.Children.Add(chips);
        }
        tile.Child = grid;

        var button = new Button { Content = tile, ToolTip = date.ToString("D", Culture) };
        button.SetResourceReference(FrameworkElement.StyleProperty, "WidgetDayButton");
        button.Click += (_, _) =>
        {
            _selectedDay = date;
            BuildPopup();
        };
        return button;
    }

    private static UIElement Chip(Brush brush) => new Border
    {
        Width = 8, Height = 5, CornerRadius = new CornerRadius(2.5), Background = brush, Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center
    };

    private static UIElement CreateEventRow(CalendarOccurrence item, CultureInfo culture)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var time = Ui.Text(item.AllDay ? UiText.Get("CalAllDay") : item.Start.ToString("HH:mm", culture), 10.5, brushKey: "TextSecondary");
        time.Width = 46;
        time.VerticalAlignment = VerticalAlignment.Top;
        time.Margin = new Thickness(0, 2, 0, 0);
        DockPanel.SetDock(time, Dock.Left);
        row.Children.Add(time);

        var icon = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(7), Background = item.AllDay ? AllDayBrush : EventBrush, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 9, 0) };
        icon.Child = Ui.Icon("IconCalendar", 12, Brushes.White, stroke: 2.1);
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);

        var text = new StackPanel();
        var title = Ui.Text(item.Title, 12, FontWeights.SemiBold);
        title.ToolTip = item.Title;
        text.Children.Add(title);
        var when = item.AllDay ? UiText.Get("CalAllDay")
            : $"{item.Start.ToString("HH:mm", culture)} – {item.End.ToString("HH:mm", culture)}";
        if (!string.IsNullOrWhiteSpace(item.Location)) when += " · " + item.Location;
        var detail = Ui.Text(when, 10.5, brushKey: "TextSecondary");
        detail.ToolTip = when;
        text.Children.Add(detail);
        row.Children.Add(text);
        return row;
    }

    private void ShiftMonth(int delta)
    {
        _visibleMonth = _visibleMonth.AddMonths(delta);
        _selectedDay = _visibleMonth.Month == DateTime.Today.Month && _visibleMonth.Year == DateTime.Today.Year
            ? DateTime.Today : _visibleMonth;
        BuildPopup();
    }
}
