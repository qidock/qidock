using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QiDock.Models;
using QiDock.Services;

namespace QiDock.Widgets;

/// <summary>
/// Water / break / eye-rest reminder: every chosen number of minutes it chimes and shows a message at the top of
/// the screen. The tile shows what it reminds of and when; the card picks the kind and the interval (or off), and
/// for water counts today's glasses. One timer waits for the next reminder; nothing polls in between.
/// </summary>
public sealed class ReminderWidget : DockWidget
{
    private static readonly int[] Intervals = [20, 30, 45, 60, 90];
    private static readonly string[] Kinds = ["water", "break", "eyes"];

    private readonly Button _tile;
    private readonly WidgetPopup _popup;
    private readonly ContentControl _icon = new() { Focusable = false, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _title;
    private readonly TextBlock _next;
    private readonly DispatcherTimer _due = new(DispatcherPriority.Normal);
    private readonly DispatcherTimer _label = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
    private DateTime _nextAt;
    private int _glasses;
    private DateTime _glassesDay = DateTime.Today;
    private (string Kind, int Minutes) _applied;

    public ReminderWidget()
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _title = Ui.Text("", 12, FontWeights.SemiBold);
        _next = Ui.Text("", 10, brushKey: "TextSecondary");
        text.Children.Add(_title);
        text.Children.Add(_next);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_icon);
        row.Children.Add(text);
        _tile = Ui.Tile(row, (_, _) => Toggle());
        _popup = new WidgetPopup(_tile);
        _due.Tick += (_, _) => Remind();
        _label.Tick += (_, _) => Render();
        UiText.Current.PropertyChanged += (_, _) => Render();
        Render();
    }

    public override string Id => WidgetIds.Reminder;
    public override FrameworkElement View => _tile;
    public override bool IsPopupOpen => _popup.IsOpen;
    public override void ClosePopup() => _popup.Close();

    // Hidden widgets get their settings too; only a shown one reminds.
    private bool _started;

    public override void Start()
    {
        _started = true;
        _applied = default;
        ApplySettings(Current);
    }

    public override void Stop()
    {
        _started = false;
        _due.Stop();
        _label.Stop();
        _popup.Close();
        _applied = default;
    }

    public override void SetLive(bool live)
    {
        if (live && _due.IsEnabled) _label.Start();
        else _label.Stop();
        Render();
    }

    public override void ApplySettings(WidgetSettings settings)
    {
        var wanted = (settings.ReminderKind, settings.ReminderMinutes);
        if (_started && wanted != _applied)
        {
            _applied = wanted;
            Schedule();
        }
        Render();
    }

    private string Kind => Array.IndexOf(Kinds, Current.ReminderKind) >= 0 ? Current.ReminderKind : "water";

    private void Schedule()
    {
        _due.Stop();
        if (Current.ReminderMinutes <= 0)
        {
            _label.Stop();
            return;
        }
        _nextAt = DateTime.Now.AddMinutes(Current.ReminderMinutes);
        _due.Interval = TimeSpan.FromMinutes(Current.ReminderMinutes);
        _due.Start();
        _label.Start();
    }

    private void Remind()
    {
        _nextAt = DateTime.Now + _due.Interval;
        SystemSounds.Asterisk.Play();
        Ui.ShowHud(IconKey(Kind), UiText.Get("ReminderNow_" + Kind));
        Render();
    }

    private static string IconKey(string kind) => kind switch { "break" => "IconCoffee", "eyes" => "IconEye", _ => "IconDroplet" };

    private void Render()
    {
        if (_glassesDay != DateTime.Today)
        {
            _glassesDay = DateTime.Today;
            _glasses = 0;
        }
        var kind = Kind;
        var on = Current.ReminderMinutes > 0 && _due.IsEnabled;
        _icon.Content = Ui.Icon(IconKey(kind), 15, on ? Ui.Solid(kind == "water" ? "#FF64D2FF" : kind == "break" ? "#FFFF9F0A" : "#FF30D158") : null);
        _title.Text = UiText.Get("ReminderKind_" + kind);
        var minutes = Math.Max(0, (int)Math.Ceiling((_nextAt - DateTime.Now).TotalMinutes));
        _next.Text = !on ? UiText.Get("ReminderOff")
            : kind == "water" && _glasses > 0 ? UiText.Format("ReminderNextGlasses", minutes, _glasses)
            : UiText.Format("ReminderNext", minutes);
        _tile.ToolTip = on ? UiText.Format("ReminderEvery", Current.ReminderMinutes) : UiText.Get("ReminderOffHint");
    }

    private void Toggle()
    {
        if (_popup.IsOpen)
        {
            _popup.Close();
            return;
        }
        Build();
        _popup.Toggle();
    }

    private void Build()
    {
        var root = new StackPanel();
        root.Children.Add(Ui.Header(UiText.Get("ReminderTitle"), () => _popup.Close()));
        var kinds = new System.Windows.Controls.Primitives.UniformGrid { Columns = Kinds.Length, Margin = new Thickness(-2, 0, -2, 0) };
        foreach (var kind in Kinds)
        {
            var button = Ui.Pill(UiText.Get("ReminderShort_" + kind), (_, _) =>
            {
                UpdateSettings(s => s with { ReminderKind = kind, ReminderMinutes = s.ReminderMinutes > 0 ? s.ReminderMinutes : 30 });
                Build();
            }, IconKey(kind));
            button.ToolTip = UiText.Get("ReminderKind_" + kind);
            button.Margin = new Thickness(2);
            if (Kind == kind) button.SetResourceReference(Control.BackgroundProperty, "WidgetAccent");
            kinds.Children.Add(button);
        }
        root.Children.Add(kinds);
        var hint = Ui.Text(UiText.Get("ReminderInterval"), 11, brushKey: "TextSecondary");
        hint.Margin = new Thickness(0, 12, 0, 6);
        root.Children.Add(hint);
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(-2, 0, -2, 0) };
        foreach (var minutes in Intervals.Prepend(0))
        {
            var button = Ui.Pill(minutes == 0 ? UiText.Get("ReminderOffShort") : UiText.Format("FocusMinutes", minutes), (_, _) =>
            {
                UpdateSettings(s => s with { ReminderMinutes = minutes });
                _popup.Close();
            });
            button.Margin = new Thickness(2);
            if (Current.ReminderMinutes == minutes) button.SetResourceReference(Control.BackgroundProperty, "WidgetAccent");
            grid.Children.Add(button);
        }
        root.Children.Add(grid);
        if (Kind == "water")
        {
            var count = Ui.Text(UiText.Format("ReminderGlasses", _glasses), 12, FontWeights.SemiBold);
            var drink = Ui.Pill(UiText.Get("ReminderDrank"), (_, _) =>
            {
                _glasses++;
                // Drinking now restarts the wait, so the next reminder comes a full interval later.
                if (Current.ReminderMinutes > 0) Schedule();
                Render();
                Build();
            }, "IconPlus");
            var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(drink, Dock.Right);
            row.Children.Add(drink);
            row.Children.Add(count);
            root.Children.Add(row);
        }
        _popup.SetContent(root, 330);
    }
}
