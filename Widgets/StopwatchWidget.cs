using System.Diagnostics;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QiDock.Models;
using QiDock.Services;

namespace QiDock.Widgets;

/// <summary>
/// Stopwatch and countdown in one tile: the tile shows whichever runs, with start/pause and reset; the card picks
/// stopwatch or a countdown length. A finished countdown chimes and shows a message at the top of the screen.
/// The display ticks only while something runs and the widget is on screen.
/// </summary>
public sealed class StopwatchWidget : DockWidget
{
    private static readonly int[] Countdowns = [1, 3, 5, 10, 15, 30, 45, 60];
    private static readonly System.Windows.Media.Brush Accent = Ui.Solid("#FF0A84FF");
    private static readonly System.Windows.Media.Brush CountAccent = Ui.Solid("#FFFF9F0A");

    private readonly Button _tile;
    private readonly WidgetPopup _popup;
    private readonly TextBlock _time;
    private readonly TextBlock _label;
    private readonly ContentControl _playIcon = new() { Focusable = false };
    private readonly Button _reset;
    private readonly DispatcherTimer _tick = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _alarm = new(DispatcherPriority.Normal);
    private readonly Stopwatch _watch = new();
    private TimeSpan? _countdown; // null: stopwatch mode
    private bool _live = true;

    public StopwatchWidget()
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), MinWidth = 64 };
        _label = Ui.Text("", 10, brushKey: "TextSecondary");
        _time = Ui.Text("0:00.0", 16, FontWeights.Bold);
        _time.FontFeatures();
        text.Children.Add(_label);
        text.Children.Add(_time);
        var play = Ui.IconButton(_playIcon, (_, e) =>
        {
            e.Handled = true;
            StartPause();
        }, UiText.Get("StopwatchStartPause"), 28, 28);
        _reset = Ui.IconButton(Ui.Icon("IconRotate", 12), (_, e) =>
        {
            e.Handled = true;
            Reset();
        }, UiText.Get("StopwatchReset"), 26, 28);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(text);
        row.Children.Add(play);
        row.Children.Add(_reset);
        _tile = Ui.Tile(row, (_, _) => Toggle(), 10);
        _popup = new WidgetPopup(_tile);
        _tick.Tick += (_, _) => Render();
        _alarm.Tick += (_, _) => Finished();
        UiText.Current.PropertyChanged += (_, _) => Render();
        Render();
    }

    public override string Id => WidgetIds.Stopwatch;
    public override FrameworkElement View => _tile;
    public override bool IsPopupOpen => _popup.IsOpen;
    public override void ClosePopup() => _popup.Close();
    public override void Stop() => _popup.Close();

    public override void SetLive(bool live)
    {
        _live = live;
        UpdateTicking();
        Render();
    }

    private TimeSpan Shown => _countdown is { } length ? (length - _watch.Elapsed) is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero : _watch.Elapsed;

    private void StartPause()
    {
        if (_watch.IsRunning) _watch.Stop();
        else
        {
            if (_countdown is not null && Shown <= TimeSpan.Zero) _watch.Reset();
            _watch.Start();
        }
        UpdateTicking();
        Render();
    }

    private void Reset()
    {
        _watch.Reset();
        UpdateTicking();
        Render();
    }

    private void UpdateTicking()
    {
        // The countdown's end is a single timer, so it fires while the dock is hidden; the display ticks only when seen.
        _alarm.Stop();
        if (_watch.IsRunning && _countdown is not null && Shown > TimeSpan.Zero)
        {
            _alarm.Interval = Shown;
            _alarm.Start();
        }
        _tick.Interval = TimeSpan.FromMilliseconds(_countdown is null ? 100 : 250);
        if (_watch.IsRunning && _live) _tick.Start();
        else _tick.Stop();
    }

    private void Finished()
    {
        _alarm.Stop();
        _watch.Stop();
        _watch.Reset();
        UpdateTicking();
        Render();
        SystemSounds.Exclamation.Play();
        Ui.ShowHud("IconTimer", UiText.Get("StopwatchDone"));
    }

    private void Render()
    {
        var shown = Shown;
        if (_countdown is null)
            _time.Text = shown.TotalHours >= 1 ? shown.ToString(@"h\:mm\:ss") : $"{(int)shown.TotalMinutes}:{shown.Seconds:00}.{shown.Milliseconds / 100}";
        else
        {
            // A countdown shows whole seconds, rounded up: it reads 0:01 until the very end.
            var seconds = (int)Math.Ceiling(shown.TotalSeconds - 0.0001);
            _time.Text = seconds >= 3600 ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}" : $"{seconds / 60}:{seconds % 60:00}";
        }
        _label.Text = _countdown is { } length ? UiText.Format("StopwatchCountdownLabel", (int)length.TotalMinutes) : UiText.Get("StopwatchLabel");
        var accent = _countdown is null ? Accent : CountAccent;
        if (_watch.IsRunning) _time.Foreground = accent;
        else _time.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        _playIcon.Content = Ui.Glyph(_watch.IsRunning ? "GlyphPause" : "GlyphPlay", 12, _watch.IsRunning ? accent : null);
        _reset.Visibility = _watch.Elapsed > TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Toggle()
    {
        if (_popup.IsOpen)
        {
            _popup.Close();
            return;
        }
        var root = new StackPanel();
        root.Children.Add(Ui.Header(UiText.Get("StopwatchTitle"), () => _popup.Close()));
        var stopwatch = Ui.Pill(UiText.Get("StopwatchMode"), (_, _) => Choose(null), "IconTimer");
        stopwatch.HorizontalAlignment = HorizontalAlignment.Stretch;
        if (_countdown is null) stopwatch.SetResourceReference(Control.BackgroundProperty, "WidgetAccent");
        root.Children.Add(stopwatch);
        var hint = Ui.Text(UiText.Get("StopwatchCountdownHint"), 11, brushKey: "TextSecondary");
        hint.Margin = new Thickness(0, 12, 0, 6);
        root.Children.Add(hint);
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, Margin = new Thickness(-2, 0, -2, 0) };
        foreach (var minutes in Countdowns)
        {
            var button = Ui.Pill(UiText.Format("FocusMinutes", minutes), (_, _) => Choose(TimeSpan.FromMinutes(minutes)));
            button.Margin = new Thickness(2);
            if (_countdown is { } length && (int)length.TotalMinutes == minutes) button.SetResourceReference(Control.BackgroundProperty, "WidgetAccent");
            grid.Children.Add(button);
        }
        root.Children.Add(grid);
        _popup.SetContent(root, 300);
        _popup.Toggle();
    }

    /// <summary>Switches to the stopwatch (null) or a countdown of the given length; a countdown starts at once.</summary>
    private void Choose(TimeSpan? countdown)
    {
        _countdown = countdown;
        _watch.Reset();
        if (countdown is not null) _watch.Start();
        UpdateTicking();
        Render();
        _popup.Close();
    }
}
