using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using QiDock.Models;
using QiDock.Services;

namespace QiDock.Widgets;

/// <summary>
/// Focus timer (pomodoro): start, pause, reset; the length is picked in the card. It shows the shared
/// <see cref="FocusTimer"/>, the same countdown as the desktop focus widget. When time is up it chimes and
/// shows a message at the top of the screen, even while the bar is hidden.
/// </summary>
public sealed class FocusWidget : DockWidget
{
    private static readonly int[] Lengths = [15, 25, 45, 60];
    private static readonly System.Windows.Media.Brush Running = Ui.Solid("#FFFF6B5E");
    private static readonly System.Windows.Media.Brush Resting = Ui.Solid("#FF34C759");
    private readonly Button _tile;
    private readonly WidgetPopup _popup;
    private readonly TextBlock _time;
    private readonly TextBlock _label;
    private readonly ContentControl _playIcon = new() { Focusable = false };
    private readonly Button _reset;
    private readonly EventHandler _changed;

    public FocusWidget()
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        _label = Ui.Text(UiText.Get("FocusLabel"), 10, brushKey: "TextSecondary");
        _time = Ui.Text("25:00", 17, FontWeights.Bold);
        _time.FontFeatures();
        text.Children.Add(_label);
        text.Children.Add(_time);
        var play = Ui.IconButton(_playIcon, (_, e) =>
        {
            e.Handled = true;
            FocusTimer.Toggle();
        }, UiText.Get("FocusStartPause"), 28, 28);
        _reset = Ui.IconButton(Ui.Icon("IconRotate", 12), (_, e) =>
        {
            e.Handled = true;
            FocusTimer.Reset();
        }, UiText.Get("FocusReset"), 26, 28);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(text);
        row.Children.Add(play);
        row.Children.Add(_reset);
        _tile = Ui.Tile(row, (_, _) => Toggle(), 10);
        _popup = new WidgetPopup(_tile);
        _changed = (_, _) => Render();
        FocusTimer.Changed += _changed;
        UiText.Current.PropertyChanged += (_, _) => Render();
        Render();
    }

    public override string Id => WidgetIds.Focus;
    public override FrameworkElement View => _tile;
    public override bool IsPopupOpen => _popup.IsOpen;
    public override void ClosePopup() => _popup.Close();

    // The length setting is read by the shared timer itself; only the display needs a refresh.
    public override void ApplySettings(WidgetSettings settings) => Render();

    public override void Dispose()
    {
        FocusTimer.Changed -= _changed;
        base.Dispose();
    }

    private void Render()
    {
        var shown = FocusTimer.Remaining;
        _time.Text = shown.TotalHours >= 1
            ? shown.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : $"{(int)shown.TotalMinutes:00}:{shown.Seconds:00}";
        var accent = FocusTimer.OnBreak ? Resting : Running;
        if (FocusTimer.Running) _time.Foreground = accent;
        else _time.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        _label.Text = UiText.Get(FocusTimer.OnBreak ? "FocusOnBreak" : FocusTimer.Running ? "FocusRunning" : "FocusLabel");
        _playIcon.Content = Ui.Glyph(FocusTimer.Running ? "GlyphPause" : "GlyphPlay", 12, FocusTimer.Running ? accent : null);
        _reset.Visibility = FocusTimer.Started ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Toggle()
    {
        if (!_popup.IsOpen) Build();
        _popup.Toggle();
    }

    private void Build()
    {
        var root = new StackPanel();
        root.Children.Add(Ui.Header(UiText.Get("FocusTitle"), () => _popup.Close()));
        var hint = Ui.Text(UiText.Get("FocusLength"), 11, brushKey: "TextSecondary");
        hint.Margin = new Thickness(0, 0, 0, 6);
        root.Children.Add(hint);
        // The pills' outer edges line up with the title and the button below.
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = Lengths.Length, Margin = new Thickness(-2, 0, -2, 0) };
        foreach (var minutes in Lengths)
        {
            var selected = (int)FocusTimer.FocusLength.TotalMinutes == minutes;
            var button = Ui.Pill(UiText.Format("FocusMinutes", minutes), (_, _) =>
            {
                UpdateSettings(s => s with { FocusMinutes = minutes });
                FocusTimer.Reset();
                _popup.Close();
            });
            button.Margin = new Thickness(2);
            if (selected) button.SetResourceReference(Control.BackgroundProperty, "WidgetAccent");
            grid.Children.Add(button);
        }
        root.Children.Add(grid);
        var breakButton = Ui.Pill(UiText.Get("FocusBreak"), (_, _) =>
        {
            FocusTimer.StartBreak();
            _popup.Close();
        }, "IconLeaf");
        breakButton.Margin = new Thickness(0, 8, 0, 0);
        breakButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        root.Children.Add(breakButton);
        var stats = Ui.Text(UiText.Format("FocusToday", FocusTimer.CompletedToday), 11, brushKey: "TextSecondary");
        stats.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(stats);
        _popup.SetContent(root, 300);
    }
}

internal static class TextFeatures
{
    /// <summary>Tabular digits so the countdown does not wobble.</summary>
    public static void FontFeatures(this TextBlock text) => System.Windows.Documents.Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
}
