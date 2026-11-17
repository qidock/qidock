using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QiDock.Models;
using QiDock.Services;

namespace QiDock.Widgets;

/// <summary>Quick note: the tile shows the note's first line; the card is a small text box that keeps what is typed.</summary>
public sealed class NoteWidget : DockWidget
{
    private readonly Button _tile;
    private readonly WidgetPopup _popup;
    private readonly TextBlock _first;
    private readonly TextBlock _caption;
    private readonly TextBox _box = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Height = 170,
        VerticalContentAlignment = VerticalAlignment.Top,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly StackPanel _card = new();
    // Saving waits until typing pauses, so every key press does not rewrite the settings file.
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(700) };

    public NoteWidget()
    {
        var icon = Ui.Icon("IconEdit", 14);
        icon.Margin = new Thickness(0, 0, 8, 0);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _first = Ui.Text("", 12, FontWeights.SemiBold);
        _first.MaxWidth = 140;
        _caption = Ui.Text("", 10, brushKey: "TextSecondary");
        text.Children.Add(_first);
        text.Children.Add(_caption);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(text);
        _tile = Ui.Tile(row, (_, _) => Toggle());
        _popup = new WidgetPopup(_tile);
        _popup.Closed += (_, _) => Flush();

        _box.SetResourceReference(FrameworkElement.StyleProperty, "WidgetTextBox");
        _box.TextChanged += (_, _) =>
        {
            _save.Stop();
            _save.Start();
            Render(_box.Text);
        };
        _box.KeyDown += (_, e) => { if (e.Key == Key.Escape) _popup.Close(); };
        _save.Tick += (_, _) => Flush();
        _card.Children.Add(Ui.Header(UiText.Get("NoteTitle"), () => _popup.Close()));
        _card.Children.Add(_box);
        var hint = Ui.Text(UiText.Get("NoteSavedHint"), 10.5, brushKey: "TextSecondary");
        hint.Margin = new Thickness(0, 6, 0, 0);
        _card.Children.Add(hint);
        UiText.Current.PropertyChanged += (_, _) => Render(Current.Note ?? "");
        Render("");
    }

    public override string Id => WidgetIds.Note;
    public override FrameworkElement View => _tile;
    public override bool IsPopupOpen => _popup.IsOpen;
    public override void ClosePopup() => _popup.Close();
    public override void Stop() => _popup.Close();

    public override void ApplySettings(WidgetSettings settings)
    {
        var note = settings.Note ?? "";
        // Never overwrite what is being typed with the saved copy that is a moment behind.
        if (!_box.IsKeyboardFocusWithin && !_save.IsEnabled && _box.Text != note) _box.Text = note;
        Render(_box.IsKeyboardFocusWithin ? _box.Text : note);
    }

    private void Flush()
    {
        _save.Stop();
        var text = _box.Text;
        if (text != (Current.Note ?? "")) UpdateSettings(s => s with { Note = text });
    }

    private void Render(string note)
    {
        var lines = note.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _first.Text = lines.Length > 0 ? lines[0] : UiText.Get("NoteTitle");
        _caption.Text = lines.Length > 1 ? UiText.Format("NoteMoreLines", lines.Length - 1) : UiText.Get(lines.Length > 0 ? "NoteTapToEdit" : "NoteEmpty");
        _tile.ToolTip = lines.Length > 0 ? note.Trim() : null;
        _box.Tag = UiText.Get("NotePlaceholder");
    }

    private void Toggle()
    {
        if (_popup.IsOpen)
        {
            _popup.Close();
            return;
        }
        _popup.SetContent(_card, 320);
        _popup.Toggle();
        _popup.FocusInput(_box);
        _box.CaretIndex = _box.Text.Length;
    }
}
