using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QiDock.Models;
using QiDock.Services;

namespace QiDock.Widgets;

/// <summary>Small to-do list: the tile shows the next open task; the card adds, ticks off and removes tasks.</summary>
public sealed class TodoWidget : DockWidget
{
    private readonly Button _tile;
    private readonly WidgetPopup _popup;
    private readonly TextBlock _next;
    private readonly TextBlock _count;
    private readonly TextBox _input = new();
    private readonly StackPanel _list = new();
    private readonly StackPanel _card = new();
    private IReadOnlyList<TodoItem> _items = [];

    public TodoWidget()
    {
        var icon = Ui.Icon("IconListCheck", 16);
        icon.Margin = new Thickness(0, 0, 9, 0);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _next = Ui.Text("", 12, FontWeights.SemiBold);
        _next.MaxWidth = 150;
        _count = Ui.Text("", 10.5, brushKey: "TextSecondary");
        text.Children.Add(_next);
        text.Children.Add(_count);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(text);
        _tile = Ui.Tile(row, (_, _) => Toggle());
        _popup = new WidgetPopup(_tile);

        _input.SetResourceReference(FrameworkElement.StyleProperty, "WidgetTextBox");
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _input.Text.Trim() is { Length: > 0 } text)
            {
                _input.Text = "";
                UpdateSettings(s => s with { Todos = (s.Todos ?? []).Append(new TodoItem(text)).ToList() });
            }
            else if (e.Key == Key.Escape) _popup.Close();
        };
        _card.Children.Add(Ui.Header(UiText.Get("TodoTitle"), () => _popup.Close()));
        _card.Children.Add(_input);
        _list.Margin = new Thickness(0, 8, 0, 0);
        _card.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        UiText.Current.PropertyChanged += (_, _) => Render();
        Render();
    }

    public override string Id => WidgetIds.Todo;
    public override FrameworkElement View => _tile;
    public override bool IsPopupOpen => _popup.IsOpen;
    public override void ClosePopup() => _popup.Close();
    public override void Stop() => _popup.Close();

    public override void ApplySettings(WidgetSettings settings)
    {
        var items = settings.Todos ?? [];
        if (items.SequenceEqual(_items)) return;
        _items = items;
        Render();
    }

    private void Render()
    {
        var open = _items.Where(i => !i.Done).ToList();
        _next.Text = open.Count > 0 ? open[0].Text : UiText.Get(_items.Count > 0 ? "TodoAllDone" : "TodoTitle");
        _count.Text = open.Count > 0 ? UiText.Format("TodoOpen", open.Count) : UiText.Get("TodoAddHint");
        _tile.ToolTip = string.Join(Environment.NewLine, open.Select(i => "• " + i.Text));
        if (_tile.ToolTip is "") _tile.ToolTip = null;

        _list.Children.Clear();
        for (var index = 0; index < _items.Count; index++) _list.Children.Add(Row(_items[index], index));
        if (_items.Any(i => i.Done))
        {
            var clear = Ui.Pill(UiText.Get("TodoClearDone"), (_, _) => UpdateSettings(s => s with { Todos = (s.Todos ?? []).Where(i => !i.Done).ToList() }));
            clear.HorizontalAlignment = HorizontalAlignment.Left;
            clear.Margin = new Thickness(0, 8, 0, 0);
            _list.Children.Add(clear);
        }
        _input.Tag = UiText.Get("TodoPlaceholder");
    }

    private FrameworkElement Row(TodoItem item, int index)
    {
        var check = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1.5),
            BorderBrush = item.Done ? Ui.Solid("#FF32D74B") : Ui.Solid("#80FFFFFF"),
            Background = item.Done ? Ui.Solid("#FF32D74B") : System.Windows.Media.Brushes.Transparent,
            Child = item.Done ? Ui.Icon("IconCheck", 10, System.Windows.Media.Brushes.White, stroke: 3) : null
        };
        var toggle = Ui.IconButton(check, (_, _) => Replace(index, item with { Done = !item.Done }), UiText.Get("TodoToggle"), 28, 28);
        var text = Ui.Text(item.Text, 12.5, item.Done ? FontWeights.Normal : FontWeights.SemiBold, item.Done ? "TextSecondary" : "TextPrimary");
        text.TextWrapping = TextWrapping.Wrap;
        text.TextTrimming = TextTrimming.None;
        if (item.Done) text.TextDecorations = TextDecorations.Strikethrough;
        text.Margin = new Thickness(6, 0, 6, 0);
        var remove = Ui.IconButton(Ui.Icon("IconClose", 10, brushKey: "TextSecondary"), (_, _) =>
            UpdateSettings(s => s with { Todos = (s.Todos ?? []).Where((_, i) => i != index).ToList() }), UiText.Get("TodoRemove"));
        var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
        DockPanel.SetDock(toggle, Dock.Left);
        DockPanel.SetDock(remove, Dock.Right);
        row.Children.Add(toggle);
        row.Children.Add(remove);
        row.Children.Add(text);
        return row;
    }

    private void Replace(int index, TodoItem item) =>
        UpdateSettings(s => s with { Todos = (s.Todos ?? []).Select((old, i) => i == index ? item : old).ToList() });

    private void Toggle()
    {
        if (_popup.IsOpen)
        {
            _popup.Close();
            return;
        }
        _popup.SetContent(_card, 330);
        _popup.Toggle();
        _popup.FocusInput(_input);
    }
}
