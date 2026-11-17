using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QiDock.Models;
using QiDock.Services.Widgets;
using FocusTimer = QiDock.Services.FocusTimer;
using static QiDock.DesktopWidgets.DeskUi;

namespace QiDock.DesktopWidgets.Kinds;

/// <summary>Transparent text box that fits the dark cards.</summary>
internal static class DeskInput
{
    public static TextBox Box(string hint, double size = 13, bool multiline = false)
    {
        var box = new TextBox
        {
            FontSize = size,
            Foreground = Text,
            CaretBrush = Text,
            SelectionBrush = Blue,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Tag = hint
        };
        if (multiline) box.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        return box;
    }

    /// <summary>A text box with a grey hint behind it while empty.</summary>
    public static Grid WithHint(TextBox box)
    {
        var hint = T(box.Tag as string ?? "", box.FontSize, brush: Tertiary);
        hint.IsHitTestVisible = false;
        hint.VerticalAlignment = box.AcceptsReturn ? VerticalAlignment.Top : VerticalAlignment.Center;
        hint.Margin = new Thickness(2, 0, 0, 0);
        box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var grid = new Grid();
        grid.Children.Add(hint);
        grid.Children.Add(box);
        return grid;
    }

    /// <summary>Clicking the box gives the widget window the keyboard (widgets never take focus otherwise).</summary>
    public static void Focusable(TextBox box, IDeskHost host) =>
        box.PreviewMouseLeftButtonDown += (_, _) => host.FocusInput(box);

    /// <summary>Rounded inset field (search boxes, the to-do input).</summary>
    public static Border Field(UIElement content, double height = 34) => new()
    {
        Height = height,
        CornerRadius = new CornerRadius(height / 2),
        Background = Inset,
        Padding = new Thickness(14, 0, 6, 0),
        Child = content
    };
}

/// <summary>To-do list shared with the bar's to-do widget.</summary>
internal sealed class TodoDeskWidget : DeskWidget
{
    private bool _adding;
    private bool _refocus;

    private static IReadOnlyList<TodoItem> Items => Desk.Settings().Todos ?? [];

    public override FrameworkElement Build()
    {
        var items = Items.Where(t => !t.Done).Concat(Items.Where(t => t.Done)).ToList();
        var capacity = Size switch { DeskSize.Strip => 3, DeskSize.Small => 4, DeskSize.Medium => 3, _ => 10 };
        var withHeader = Size is DeskSize.Small or DeskSize.Medium or DeskSize.Large;
        var withInput = Size is DeskSize.Medium or DeskSize.Large || _adding;
        if (_adding && Size is DeskSize.Strip or DeskSize.Small) capacity -= 1;

        var stack = new StackPanel();
        if (withHeader)
        {
            var open = Items.Count(t => !t.Done);
            var header = Grid(columns: "*,Auto");
            header.Children.Add(T(Desk.L("Yapılacaklar", "To-do"), 14, FontWeights.SemiBold, Blue));
            var count = Digits(open.ToString(CultureInfo.InvariantCulture), 14, FontWeights.SemiBold, Secondary);
            header.Children.Add(At(count, 0, 1));
            header.Margin = new Thickness(0, 0, 0, 6);
            stack.Children.Add(header);
        }
        if (withInput) stack.Children.Add(Input());
        foreach (var item in items.Take(capacity)) stack.Children.Add(Row(item));
        if (items.Count == 0 && !withInput)
            stack.Children.Add(T(Desk.L("Liste boş. Sağ tık → Görev ekle", "Empty. Right-click → Add task"), 12.5, brush: Secondary));
        else if (items.Count > capacity)
            stack.Children.Add(T(Desk.L($"+{items.Count - capacity} görev daha", $"+{items.Count - capacity} more"), 11.5, FontWeights.SemiBold, Secondary));
        stack.VerticalAlignment = Size == DeskSize.Strip ? VerticalAlignment.Center : VerticalAlignment.Top;
        return stack;
    }

    private FrameworkElement Row(TodoItem item)
    {
        var size = Size == DeskSize.Strip ? 12.5 : 13.5;
        UIElement mark = item.Done
            ? new Border { Width = 15, Height = 15, CornerRadius = new CornerRadius(7.5), Background = Blue, Child = DeskUi.Icon("IconCheck", 9, Brushes.White, 2.4) }
            : new Border { Width = 15, Height = 15, CornerRadius = new CornerRadius(7.5), BorderBrush = Secondary, BorderThickness = new Thickness(1.5) };
        var toggle = new DeskButton(mark, () => Toggle(item), 22);
        var text = T(item.Text, size, FontWeights.Medium, item.Done ? Tertiary : Text);
        if (item.Done) text.TextDecorations = TextDecorations.Strikethrough;
        var remove = new DeskButton(DeskUi.Icon("IconClose", 9, Tertiary), () => Remove(item), 20) { Opacity = 0 };
        var grid = Grid(columns: "Auto,*,Auto");
        grid.Children.Add(toggle);
        grid.Children.Add(At(Margin(text, 6), 0, 1));
        grid.Children.Add(At(remove, 0, 2));
        grid.Background = Brushes.Transparent;
        grid.MouseEnter += (_, _) => remove.Opacity = 1;
        grid.MouseLeave += (_, _) => remove.Opacity = 0;
        grid.Margin = new Thickness(-3, Size == DeskSize.Strip ? 0 : 1, 0, Size == DeskSize.Strip ? 0 : 1);
        return grid;
    }

    private FrameworkElement Input()
    {
        var box = DeskInput.Box(Desk.L("Yeni görev…", "New task…"), 13);
        DeskInput.Focusable(box, Host);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _adding)
            {
                _adding = false;
                Host.Rebuild();
            }
            if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(box.Text)) return;
            var text = box.Text.Trim();
            Desk.UpdateSettings(s => s with { Todos = [.. s.Todos ?? [], new TodoItem(text)] }); // same order as the bar list
            _adding = false;
            _refocus = Size is DeskSize.Medium or DeskSize.Large;
            Host.Rebuild();
        };
        var field = DeskInput.Field(DeskInput.WithHint(box), Size == DeskSize.Strip ? 26 : 30);
        field.Margin = new Thickness(0, 0, 0, 6);
        if (_adding || _refocus)
        {
            _refocus = false;
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Host.FocusInput(box));
        }
        return field;
    }

    private void Toggle(TodoItem item)
    {
        Desk.UpdateSettings(s =>
        {
            var list = (s.Todos ?? []).ToList();
            var index = list.IndexOf(item);
            if (index >= 0) list[index] = item with { Done = !item.Done };
            return s with { Todos = list };
        });
        Host.Rebuild();
    }

    private void Remove(TodoItem item)
    {
        Desk.UpdateSettings(s => s with { Todos = (s.Todos ?? []).Where(t => !Equals(t, item)).ToList() });
        Host.Rebuild();
    }

    public override void SettingsChanged() => Host.Rebuild();

    public override void AddMenuItems(ItemsControl menu)
    {
        menu.Items.Add(MenuEntry(Desk.L("Görev ekle", "Add task"), () =>
        {
            _adding = true;
            Host.Rebuild();
        }));
        if (Items.Any(t => t.Done))
            menu.Items.Add(MenuEntry(Desk.L("Tamamlananları temizle", "Clear completed"), () =>
            {
                Desk.UpdateSettings(s => s with { Todos = (s.Todos ?? []).Where(t => !t.Done).ToList() });
                Host.Rebuild();
            }));
    }
}

/// <summary>Sticky note; the text is kept with the widget.</summary>
internal sealed class NotesWidget : DeskWidget
{
    private readonly DispatcherTimer _save = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(700) };
    private TextBox? _box;

    public NotesWidget()
    {
        _save.Tick += (_, _) =>
        {
            _save.Stop();
            if (_box is not null) SetOption("text", _box.Text);
        };
    }

    private Brush Accent => (Option("color") ?? "yellow") switch
    {
        "blue" => Blue,
        "green" => Green,
        "pink" => Pink,
        "purple" => Purple,
        _ => Yellow
    };

    public override FrameworkElement Build()
    {
        var header = Row(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = Accent }, Margin(T(Desk.L("Not", "Note"), 13, FontWeights.SemiBold, Accent), 8));
        header.Margin = new Thickness(0, 0, 0, 8);
        _box = DeskInput.Box(Desk.L("Bir şeyler yazın…", "Write something…"), Size == DeskSize.Small ? 13 : 14, multiline: true);
        _box.Text = Option("text") ?? "";
        _box.VerticalContentAlignment = VerticalAlignment.Top;
        DeskInput.Focusable(_box, Host);
        _box.TextChanged += (_, _) =>
        {
            _save.Stop();
            _save.Start();
        };
        var grid = Grid("Auto,*");
        grid.Children.Add(header);
        grid.Children.Add(At(DeskInput.WithHint(_box), 1));
        return grid;
    }

    public override void AddMenuItems(ItemsControl menu)
    {
        var colors = new MenuItem { Header = Desk.L("Renk", "Color") };
        foreach (var (key, tr, en) in new[] { ("yellow", "Sarı", "Yellow"), ("blue", "Mavi", "Blue"), ("green", "Yeşil", "Green"), ("pink", "Pembe", "Pink"), ("purple", "Mor", "Purple") })
            colors.Items.Add(MenuEntry(Desk.L(tr, en), () =>
            {
                SetOption("color", key == "yellow" ? null : key);
                Host.Rebuild();
            }, (Option("color") ?? "yellow") == key));
        menu.Items.Add(colors);
        menu.Items.Add(MenuEntry(Desk.L("Notu temizle", "Clear note"), () =>
        {
            SetOption("text", null);
            Host.Rebuild();
        }));
    }

    public override void Dispose()
    {
        if (_save.IsEnabled && _box is not null) Host.Item.Options["text"] = _box.Text;
        _save.Stop();
        base.Dispose();
    }
}

/// <summary>This week, today's date and events from the iCal feed in the widget settings.</summary>
internal sealed class CalendarDeskWidget : DeskWidget
{
    private Border? _root;
    private DateTime _builtFor;

    public override TimeSpan? Interval => TimeSpan.FromMinutes(1);

    public override FrameworkElement Build()
    {
        _root = new Border();
        Render();
        return _root;
    }

    protected override void OnAttach()
    {
        CalendarHub.Instance.Subscribe(Render);
        if (Size == DeskSize.Strip) WeatherHub.Instance.Subscribe(Render);
        Render();
    }

    protected override void OnDetach()
    {
        CalendarHub.Instance.Unsubscribe(Render);
        WeatherHub.Instance.Unsubscribe(Render);
    }

    public override void Tick()
    {
        if (_builtFor.Date != DateTime.Today || DateTime.Now.Minute % 5 == 0) Render();
    }

    private void Render()
    {
        if (_root is null) return;
        _builtFor = DateTime.Now;
        _root.Child = Size switch
        {
            DeskSize.Strip => Week(),
            DeskSize.Small => Today(3, compact: true),
            DeskSize.Medium => TodayWide(),
            _ => Month()
        };
    }

    private static IReadOnlyList<CalendarOccurrence> Upcoming(int count)
    {
        var now = DateTime.Now;
        return CalendarHub.Instance.Between(now.Date, now.Date.AddDays(7))
            .Where(o => o.End > now || o.AllDay && o.Start.Date >= now.Date)
            .OrderBy(o => o.Start)
            .Take(count)
            .ToList();
    }

    private static readonly Brush[] EventColors = [Blue, Purple, Orange, Green, Pink];

    private static FrameworkElement EventRow(CalendarOccurrence occurrence, int index, bool showDay)
    {
        var bar = new Border { Width = 3.5, CornerRadius = new CornerRadius(2), Background = EventColors[index % EventColors.Length], Margin = new Thickness(0, 1, 8, 1) };
        var title = T(occurrence.Title, 13, FontWeights.SemiBold);
        var culture = Desk.Culture;
        var day = occurrence.Start.Date == DateTime.Today ? "" : occurrence.Start.Date == DateTime.Today.AddDays(1) ? Desk.L("Yarın ", "Tomorrow ") : occurrence.Start.ToString("ddd ", culture);
        var when = occurrence.AllDay ? Desk.L("Tüm gün", "All day") : $"{occurrence.Start:HH:mm} – {occurrence.End:HH:mm}";
        var time = T((showDay ? day : "") + when, 11.5, FontWeights.SemiBold, Secondary);
        var grid = Grid(columns: "Auto,*");
        grid.Children.Add(bar);
        grid.Children.Add(At(Column(title, time), 0, 1));
        grid.Margin = new Thickness(0, 0, 0, 7);
        return grid;
    }

    private static FrameworkElement NoEvents()
    {
        var text = CalendarHub.Instance.HasFeed
            ? Desk.L("Yaklaşan etkinlik yok", "No upcoming events")
            : Desk.L("Etkinlikler için Ayarlar → Widget’lar bölümünden iCal adresi ekleyin", "Add an iCal address in Settings → Widgets for events");
        var block = T(text, 12, brush: Secondary);
        block.TextWrapping = TextWrapping.Wrap;
        return block;
    }

    private FrameworkElement Week()
    {
        var today = DateTime.Today;
        var culture = Desk.Culture;
        var header = Grid(columns: "*,Auto");
        header.Children.Add(T(today.ToString("MMMM", culture), 12.5, FontWeights.SemiBold, Secondary));
        if (WeatherHub.Instance.Report is { } r)
        {
            var weather = Row(QiDock.Widgets.WeatherArt.Create(r.Code, r.IsDay, 16), Margin(T(WeatherHub.Temp(r.Temperature), 12.5, FontWeights.SemiBold), 4));
            header.Children.Add(At(weather, 0, 1));
        }
        var days = Grid(columns: "*,*,*,*,*,*,*");
        for (var i = 0; i < 7; i++)
        {
            var day = today.AddDays(i);
            var isToday = i == 0;
            var name = T(day.ToString("ddd", culture), 10.5, FontWeights.SemiBold, isToday ? Brushes.White : Secondary);
            var number = Digits(day.Day.ToString(CultureInfo.InvariantCulture), 15, FontWeights.SemiBold, isToday ? Brushes.White : Text);
            name.HorizontalAlignment = number.HorizontalAlignment = HorizontalAlignment.Center;
            var cell = new Border
            {
                CornerRadius = new CornerRadius(9),
                Background = isToday ? Blue : Brushes.Transparent,
                Margin = new Thickness(2, 0, 2, 0),
                Padding = new Thickness(0, 1, 0, 2),
                Child = Column(name, number)
            };
            days.Children.Add(At(cell, 0, i));
        }
        var grid = Grid("Auto,*");
        grid.Children.Add(header);
        grid.Children.Add(At(Margin(days, -2, 3, -2, 0), 1));
        return grid;
    }

    private static FrameworkElement Today(int events, bool compact)
    {
        var today = DateTime.Today;
        var culture = Desk.Culture;
        var stack = new StackPanel();
        stack.Children.Add(T(today.ToString("dddd", culture).ToUpper(culture), 12, FontWeights.Bold, Red));
        stack.Children.Add(Margin(Digits(today.Day.ToString(CultureInfo.InvariantCulture), compact ? 38 : 52, FontWeights.Normal), 0, -4, 0, compact ? 2 : 0));
        if (!compact)
        {
            stack.Children.Add(T(today.ToString("MMMM yyyy", culture), 12.5, FontWeights.SemiBold, Secondary));
            return stack;
        }
        var upcoming = Upcoming(1);
        if (upcoming.Count == 0) stack.Children.Add(NoEvents());
        else stack.Children.Add(EventRow(upcoming[0], 0, true));
        return stack;
    }

    private static FrameworkElement TodayWide()
    {
        var grid = Grid(columns: "110,*");
        grid.Children.Add(Today(0, compact: false));
        var list = new StackPanel();
        var upcoming = Upcoming(3);
        if (upcoming.Count == 0) list.Children.Add(NoEvents());
        for (var i = 0; i < upcoming.Count; i++) list.Children.Add(EventRow(upcoming[i], i, true));
        grid.Children.Add(At(list, 0, 1));
        return grid;
    }

    private static FrameworkElement Month()
    {
        var today = DateTime.Today;
        var culture = Desk.Culture;
        var stack = new StackPanel();
        var title = Grid(columns: "*,Auto");
        title.Children.Add(T(today.ToString("MMMM", culture), 15, FontWeights.SemiBold, Red));
        title.Children.Add(At(T(today.Year.ToString(CultureInfo.InvariantCulture), 15, FontWeights.SemiBold, Secondary), 0, 1));
        stack.Children.Add(title);
        var grid = Grid(columns: "*,*,*,*,*,*,*");
        grid.Margin = new Thickness(0, 6, 0, 8);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var first = culture.DateTimeFormat.FirstDayOfWeek;
        for (var i = 0; i < 7; i++)
        {
            var name = T(culture.DateTimeFormat.GetShortestDayName((DayOfWeek)(((int)first + i) % 7)), 10.5, FontWeights.SemiBold, Secondary);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            grid.Children.Add(At(name, 0, i));
        }
        var start = new DateTime(today.Year, today.Month, 1);
        var offset = ((int)start.DayOfWeek - (int)first + 7) % 7;
        var days = DateTime.DaysInMonth(today.Year, today.Month);
        var rows = (int)Math.Ceiling((offset + days) / 7.0);
        var busy = CalendarHub.Instance.Between(start, start.AddMonths(1)).Select(o => o.Start.Date).ToHashSet();
        for (var r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        for (var d = 1; d <= days; d++)
        {
            var index = offset + d - 1;
            var date = new DateTime(today.Year, today.Month, d);
            var isToday = date == today;
            var number = Digits(d.ToString(CultureInfo.InvariantCulture), 12.5, FontWeights.SemiBold, isToday ? Brushes.White : date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? Secondary : Text);
            number.HorizontalAlignment = HorizontalAlignment.Center;
            var cell = new Grid();
            if (isToday) cell.Children.Add(new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Red });
            cell.Children.Add(number);
            if (busy.Contains(date) && !isToday)
                cell.Children.Add(new Border { Width = 4, Height = 4, CornerRadius = new CornerRadius(2), Background = Blue, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 0) });
            grid.Children.Add(At(cell, index / 7 + 1, index % 7));
        }
        stack.Children.Add(grid);
        var upcoming = Upcoming(rows > 5 ? 1 : 2);
        if (upcoming.Count == 0) stack.Children.Add(NoEvents());
        for (var i = 0; i < upcoming.Count; i++) stack.Children.Add(EventRow(upcoming[i], i, true));
        return stack;
    }

    public override void SettingsChanged() => Render();
}

/// <summary>Repeating reminder (drink water, take a break…): fills up like a glass and tells you when due.</summary>
internal sealed class ReminderWidget : DeskWidget
{
    private static readonly int[] Intervals = [15, 20, 30, 45, 60, 90, 120];
    private static readonly (string Key, string Tr, string En, string Icon)[] Kinds =
    [
        ("water", "Su iç", "Drink water", "IconDroplet"),
        ("break", "Mola ver", "Take a break", "IconCoffee"),
        ("eyes", "Gözlerini dinlendir", "Rest your eyes", "IconEye"),
        ("stretch", "Esne, hareket et", "Stretch and move", "IconSmile"),
        ("posture", "Dik otur", "Sit up straight", "IconCheck")
    ];

    private readonly DispatcherTimer _due = new(DispatcherPriority.Normal);
    private WaveFill? _wave;
    private TextBlock? _label, _left;
    private bool _alerted;

    public ReminderWidget()
    {
        _due.Tick += (_, _) => Due();
    }

    private int Minutes => OptionInt("minutes", 30);
    private (string Key, string Tr, string En, string Icon) Kind => Kinds.FirstOrDefault(k => k.Key == (Option("kind") ?? "water")) is { Key: not null } k ? k : Kinds[0];

    private DateTime Since
    {
        get => long.TryParse(Option("since"), out var ticks) ? new DateTime(ticks) : DateTime.Now;
        set => SetOption("since", value.Ticks.ToString(CultureInfo.InvariantCulture));
    }

    public override TimeSpan? Interval => TimeSpan.FromSeconds(15);

    public override FrameworkElement Build()
    {
        if (Option("since") is null) Since = DateTime.Now;
        Schedule();
        _wave = new WaveFill { Margin = new Thickness(-16) };
        _label = T(Desk.L(Kind.Tr, Kind.En), Size == DeskSize.Small ? 15 : 16, FontWeights.SemiBold, Brushes.White);
        _left = T("", 12, FontWeights.SemiBold, Ui("#D9FFFFFF"));
        var every = T(Desk.L($"Her {Minutes} dk", $"Every {Minutes} min"), 11.5, FontWeights.SemiBold, Ui("#A6FFFFFF"));
        var done = new DeskButton(Row(DeskUi.Icon("IconCheck", 12, Brushes.White, 2.4), Margin(T(Desk.L("Yaptım", "Done"), 12, FontWeights.SemiBold, Brushes.White), 5)), Reset, background: Ui("#33FFFFFF"), radius: 13)
        {
            Height = 26,
            Padding = new Thickness(10, 0, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var grid = new Grid { Background = Ui("#FF16324F"), Margin = new Thickness(-Desk.Padding(Size).Left, -Desk.Padding(Size).Top, -Desk.Padding(Size).Right, -Desk.Padding(Size).Bottom) };
        _wave.Margin = new Thickness(0);
        grid.Children.Add(_wave);
        var content = Size == DeskSize.Small
            ? (FrameworkElement)Column(DeskUi.Icon(Kind.Icon, 20, Brushes.White), Margin(_label, 0, 8), _left, every, Margin(done, 0, 10))
            : new Func<FrameworkElement>(() =>
            {
                var row = Grid(columns: "Auto,*,Auto");
                row.Children.Add(DeskUi.Icon(Kind.Icon, 22, Brushes.White));
                row.Children.Add(At(Margin(Column(_label, Row(_left, Margin(T("·", 12, brush: Ui("#A6FFFFFF")), 5, 0, 5), every)), 12), 0, 1));
                row.Children.Add(At(done, 0, 2));
                return row;
            })();
        content.Margin = Desk.Padding(Size);
        grid.Children.Add(content);
        return grid;
    }

    private static Brush Ui(string hex) => QiDock.Widgets.Ui.Solid(hex);

    private void Schedule()
    {
        _due.Stop();
        var remaining = Since.AddMinutes(Minutes) - DateTime.Now;
        if (remaining <= TimeSpan.Zero) return;
        _alerted = false;
        _due.Interval = remaining;
        _due.Start();
    }

    private void Due()
    {
        _due.Stop();
        if (_alerted) return;
        _alerted = true;
        Desk.Notify(Kind.Icon, Desk.L(Kind.Tr, Kind.En));
        if (Host.IsLive) Tick();
    }

    private void Reset()
    {
        Since = DateTime.Now;
        Schedule();
        Tick();
    }

    public override void Tick()
    {
        var elapsed = DateTime.Now - Since;
        var fraction = elapsed.TotalMinutes / Minutes;
        _wave?.SetFraction(Math.Max(0.06, fraction));
        if (_left is null) return;
        var left = TimeSpan.FromMinutes(Minutes) - elapsed;
        _left.Text = left <= TimeSpan.Zero ? Desk.L("Zamanı geldi!", "Time now!") : Desk.L($"{Math.Ceiling(left.TotalMinutes)} dk sonra", $"in {Math.Ceiling(left.TotalMinutes)} min");
    }

    public override void AddMenuItems(ItemsControl menu)
    {
        var kinds = new MenuItem { Header = Desk.L("Hatırlatma", "Reminder") };
        foreach (var kind in Kinds)
            kinds.Items.Add(MenuEntry(Desk.L(kind.Tr, kind.En), () =>
            {
                SetOption("kind", kind.Key == "water" ? null : kind.Key);
                Host.Rebuild();
            }, Kind.Key == kind.Key));
        menu.Items.Add(kinds);
        var every = new MenuItem { Header = Desk.L("Sıklık", "Every") };
        foreach (var minutes in Intervals)
            every.Items.Add(MenuEntry(Desk.L($"{minutes} dakikada bir", $"Every {minutes} minutes"), () =>
            {
                SetOption("minutes", minutes.ToString(CultureInfo.InvariantCulture));
                Host.Rebuild();
            }, Minutes == minutes));
        menu.Items.Add(every);
    }

    public override void Dispose()
    {
        _due.Stop();
        base.Dispose();
    }
}

/// <summary>Pomodoro: focus and break rounds; tells you when each ends.</summary>
internal sealed class FocusDeskWidget : DeskWidget
{
    // Shows the shared focus timer, so the bar's focus tile and this widget run one countdown.
    private Border? _frame;
    private TextBlock? _label, _time;
    private DeskButton? _play;
    private RingGauge? _ring;

    public override TimeSpan? Interval => TimeSpan.FromSeconds(1);

    public override FrameworkElement Build()
    {
        _label = T("", 12, FontWeights.SemiBold, Secondary);
        _time = Digits("", Size == DeskSize.Small ? 30 : 30);
        _play = DeskButton.Round("GlyphPlay", () => { FocusTimer.Toggle(); Tick(); }, Size == DeskSize.Small ? 32 : 38, Red, Brushes.White);
        var skip = DeskButton.Round("GlyphNext", () => { FocusTimer.Skip(); Tick(); }, Size == DeskSize.Small ? 28 : 32, Inset, Secondary, 12);
        _frame = new Border
        {
            Margin = new Thickness(-Desk.Padding(Size).Left + 3, -Desk.Padding(Size).Top + 3, -Desk.Padding(Size).Right + 3, -Desk.Padding(Size).Bottom + 3),
            CornerRadius = new CornerRadius(21),
            BorderThickness = new Thickness(2),
            Padding = new Thickness(Desk.Padding(Size).Left - 5, Desk.Padding(Size).Top - 5, Desk.Padding(Size).Right - 5, Desk.Padding(Size).Bottom - 5)
        };
        if (Size == DeskSize.Small)
        {
            _ring = new RingGauge { Width = 66, Height = 66, Thickness = 6, Stroke = Red, HorizontalAlignment = HorizontalAlignment.Center };
            var center = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
            center.Children.Add(_ring);
            _time.FontSize = 17;
            _time.HorizontalAlignment = HorizontalAlignment.Center;
            center.Children.Add(_time);
            var buttons = Row(_play, Margin(skip, 10));
            buttons.HorizontalAlignment = HorizontalAlignment.Center;
            _label.HorizontalAlignment = HorizontalAlignment.Center;
            _frame.Child = Column(_label, Margin(center, 0, 4, 0, 6), buttons);
        }
        else
        {
            _ring = null;
            var grid = Grid(columns: "*,Auto,Auto");
            grid.Children.Add(Column(_label, _time));
            grid.Children.Add(At(Margin(skip, 0, 0, 10), 0, 1));
            grid.Children.Add(At(_play, 0, 2));
            _frame.Child = grid;
        }
        return _frame;
    }

    public override void Tick()
    {
        var remaining = FocusTimer.Remaining;
        var round = Math.Min(FocusTimer.Round, FocusTimer.Rounds);
        var color = FocusTimer.OnBreak ? Green : Red;
        _label!.Text = FocusTimer.OnBreak ? Desk.L("Mola", "Break") : Desk.L($"Odak {round}/{FocusTimer.Rounds}", $"Focus {round}/{FocusTimer.Rounds}");
        _label.Foreground = color;
        _time!.Text = $"{(int)remaining.TotalMinutes}:{remaining.Seconds:00}";
        _frame!.BorderBrush = FocusTimer.Running ? color : Brushes.Transparent;
        _play!.Background = color;
        _play.Child = Glyph(FocusTimer.Running ? "GlyphPause" : "GlyphPlay", 13, Brushes.White);
        if (_ring is not null)
        {
            _ring.Stroke = color;
            _ring.Value = remaining.TotalSeconds / FocusTimer.PhaseLength.TotalSeconds * 100;
        }
    }
}
