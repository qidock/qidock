using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using QiDock.Models;
using QiDock.Services;

namespace QiDock.Widgets;

/// <summary>
/// The apps running in the background (the icons the hidden taskbar would show by its clock): up to four of their
/// icons in one square. The card lists them all; a click opens the app, a right click opens the app's own menu and
/// the cross closes it (asking first, then ending it if it will not go).
/// </summary>
public sealed class TrayWidget : DockWidget
{
    private enum Closing { Asked, Stubborn }

    private readonly Button _tile;
    private readonly UniformGrid _grid = new() { Rows = 2, Columns = 2 };
    private readonly Path _empty = Ui.Icon("IconApps", 17, brushKey: "TextSecondary");
    private readonly Grid _face = new();
    private readonly WidgetPopup _popup;
    private readonly StackPanel _rows = new();
    private readonly StackPanel _card = new();
    private readonly Dictionary<int, Closing> _closing = [];
    private readonly Dictionary<int, string> _errors = [];
    private readonly DispatcherTimer _refresh = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
    private bool _started;

    public TrayWidget()
    {
        _face.Children.Add(_grid);
        _face.Children.Add(_empty);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _tile = Ui.Tile(_face, (_, _) => Toggle(), horizontalPadding: 0);
        _tile.SetResourceReference(FrameworkElement.WidthProperty, "WidgetTileHeight");
        _popup = new WidgetPopup(_tile);
        _popup.Opened += (_, _) => _refresh.Start();
        _popup.Closed += (_, _) => _refresh.Stop();
        _refresh.Tick += (_, _) => Render();

        _card.Children.Add(Ui.Header(UiText.Get("TrayTitle"), () => _popup.Close()));
        _card.Children.Add(Ui.FadingScroller(_rows, 330));
        var hint = Ui.Text(UiText.Get("TrayHint"), 10.5, brushKey: "TextSecondary");
        hint.TextWrapping = TextWrapping.Wrap;
        hint.Margin = new Thickness(2, 10, 2, 0);
        _card.Children.Add(hint);
        _popup.SetContent(_card, 310);
        UiText.Current.PropertyChanged += (_, _) => Render();
    }

    public override string Id => WidgetIds.Tray;
    public override FrameworkElement View => _tile;
    public override bool IsPopupOpen => _popup.IsOpen;
    public override void ClosePopup() => _popup.Close();

    public override void Start()
    {
        if (_started) return;
        _started = true;
        TrayHost.Shared.Changed += HostChanged;
        Render();
    }

    public override void Stop()
    {
        _started = false;
        TrayHost.Shared.Changed -= HostChanged;
        _popup.Close();
    }

    private void HostChanged(object? sender, EventArgs e) => Render();

    private void Toggle()
    {
        Render();
        _popup.Toggle();
    }

    private void Render()
    {
        var icons = TrayHost.Shared.Icons;
        RenderTile(icons);
        RenderCard(icons);
    }

    private void RenderTile(IReadOnlyList<TrayIcon> icons)
    {
        _grid.Children.Clear();
        _empty.Visibility = icons.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var cell = Math.Max(14, ((Application.Current.TryFindResource("WidgetTileHeight") as double? ?? 40) - 12) / 2);
        var shown = icons.Count > 4 ? 3 : icons.Count;
        for (var i = 0; i < shown; i++)
        {
            var image = new Image { Source = icons[i].Image, Width = cell - 3, Height = cell - 3, Stretch = Stretch.Uniform, Margin = new Thickness(1.5) };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            _grid.Children.Add(image);
        }
        if (icons.Count > 4)
        {
            var more = Ui.Text("+" + (icons.Count - 3), 9.5, FontWeights.Bold, brushKey: "TextSecondary");
            more.HorizontalAlignment = HorizontalAlignment.Center;
            more.VerticalAlignment = VerticalAlignment.Center;
            _grid.Children.Add(more);
        }
        _tile.ToolTip = icons.Count == 0 ? UiText.Get("TrayEmpty") : UiText.Format("TrayCount", icons.Count);
    }

    private void RenderCard(IReadOnlyList<TrayIcon> icons)
    {
        _rows.Children.Clear();
        foreach (var pid in _closing.Keys.Where(pid => icons.All(i => i.ProcessId != pid)).ToList()) _closing.Remove(pid);
        if (icons.Count == 0)
        {
            var none = Ui.Text(UiText.Get("TrayEmpty"), 11.5, brushKey: "TextSecondary");
            none.TextWrapping = TextWrapping.Wrap;
            none.Margin = new Thickness(2, 4, 2, 4);
            _rows.Children.Add(none);
            return;
        }
        foreach (var icon in icons) _rows.Children.Add(Row(icon));
    }

    private FrameworkElement Row(TrayIcon icon)
    {
        var row = new DockPanel { LastChildFill = true };
        var image = new Image { Source = icon.Image, Width = 20, Height = 20, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        DockPanel.SetDock(image, Dock.Left);
        row.Children.Add(image);

        if (icon.CanClose)
        {
            FrameworkElement action;
            if (_closing.TryGetValue(icon.ProcessId, out var state) && state == Closing.Stubborn)
            {
                var force = Ui.Pill(UiText.Get("TrayForceClose"), (_, _) => Force(icon));
                force.Height = 24;
                force.Padding = new Thickness(9, 2, 9, 2);
                force.Foreground = Ui.Solid("#FFFF6B5E");
                action = force;
            }
            else
            {
                action = Ui.IconButton(Ui.Icon("IconClose", 10, brushKey: "TextSecondary"), (_, _) => Close(icon), UiText.Get("TrayClose"));
                action.IsEnabled = !_closing.ContainsKey(icon.ProcessId);
            }
            action.VerticalAlignment = VerticalAlignment.Center;
            action.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(action, Dock.Right);
            row.Children.Add(action);
        }

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = Ui.Text(icon.Title, 12.5, FontWeights.SemiBold);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        words.Children.Add(title);
        var detail = _errors.TryGetValue(icon.ProcessId, out var error) ? error
            : _closing.TryGetValue(icon.ProcessId, out var closing) ? UiText.Get(closing == Closing.Asked ? "TrayClosing" : "TrayStillRunning")
            : icon.Detail;
        if (detail.Length > 0)
        {
            var line = Ui.Text(detail, 10.5, brushKey: "TextSecondary");
            line.TextTrimming = TextTrimming.CharacterEllipsis;
            words.Children.Add(line);
        }
        row.Children.Add(words);

        var surface = new Border { Child = row, CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 6, 6, 6), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        surface.ToolTip = icon.Tip.Length > 0 ? icon.Tip : null;
        surface.MouseEnter += (_, _) => surface.SetResourceReference(Border.BackgroundProperty, "SurfaceHover");
        surface.MouseLeave += (_, _) => surface.Background = Brushes.Transparent;
        surface.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && IsInButton(source, surface)) return;
            e.Handled = true;
            _popup.Close();
            if (e.ClickCount >= 2) TrayHost.DoubleClick(icon);
            else TrayHost.Click(icon);
        };
        surface.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            _popup.Close();
            // The app's own menu opens at the pointer once the card is out of the way.
            Dispatcher.CurrentDispatcher.BeginInvoke(() => TrayHost.RightClick(icon), DispatcherPriority.Background);
        };
        return surface;
    }

    private static bool IsInButton(DependencyObject source, DependencyObject stop)
    {
        for (var node = source; node is not null && !ReferenceEquals(node, stop); node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
            if (node is ButtonBase) return true;
        return false;
    }

    /// <summary>Asks the app to close; if it is still running a moment later, offers to end it.</summary>
    private void Close(TrayIcon icon)
    {
        _errors.Remove(icon.ProcessId);
        if (!TrayHost.AskToClose(icon)) return;
        var pid = icon.ProcessId;
        _closing[pid] = Closing.Asked;
        Render();
        var wait = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        wait.Tick += (_, _) =>
        {
            wait.Stop();
            if (TrayHost.IsRunningProcess(pid) && icon.IsAlive) _closing[pid] = Closing.Stubborn;
            else _closing.Remove(pid);
            Render();
        };
        wait.Start();
    }

    private void Force(TrayIcon icon)
    {
        var error = TrayHost.ForceClose(icon);
        _closing.Remove(icon.ProcessId);
        if (error is not null) _errors[icon.ProcessId] = error;
        else
        {
            _errors.Remove(icon.ProcessId);
            Ui.ShowHud("IconCheck", UiText.Format("TrayClosed", icon.Title));
        }
        // Its icon leaves the list once its window is gone.
        var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        later.Tick += (_, _) => { later.Stop(); Render(); };
        later.Start();
        Render();
    }
}
