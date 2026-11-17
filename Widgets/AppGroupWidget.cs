using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using QiDock.Models;
using QiDock.Services;
using QiDock.Services.Notifications;

namespace QiDock.Widgets;

/// <summary>
/// Up to four favourite apps in one small square: a click on an icon opens that app; an empty slot (or a right
/// click) opens the card, where apps are picked from the Start menu's list.
/// </summary>
public sealed class AppGroupWidget : DockWidget
{
    private const int Slots = 4;
    private static readonly Dictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);

    private readonly Border _tile;
    private readonly UniformGrid _grid = new() { Rows = 2, Columns = 2 };
    private readonly WidgetPopup _popup;
    private readonly TextBox _search = new();
    private readonly StackPanel _chosen = new();
    private readonly StackPanel _results = new();
    private readonly StackPanel _card = new();
    private List<InstalledApp>? _catalog;
    private bool _loadingCatalog;
    private IReadOnlyList<AppLink> _apps = [];

    public AppGroupWidget()
    {
        _tile = new Border { Child = _grid, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), Padding = new Thickness(3), Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
        _tile.SetResourceReference(Border.BackgroundProperty, "WidgetTileFill");
        _tile.SetResourceReference(Border.BorderBrushProperty, "WidgetTileStroke");
        _tile.SetResourceReference(FrameworkElement.HeightProperty, "WidgetTileHeight");
        _tile.SetResourceReference(FrameworkElement.WidthProperty, "WidgetTileHeight");
        _tile.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            Toggle();
        };
        _popup = new WidgetPopup(_tile);

        _search.SetResourceReference(FrameworkElement.StyleProperty, "WidgetTextBox");
        _search.TextChanged += (_, _) => RenderResults();
        _search.KeyDown += (_, e) => { if (e.Key == Key.Escape) _popup.Close(); };
        _card.Children.Add(Ui.Header(UiText.Get("AppGroupTitle"), () => _popup.Close()));
        _card.Children.Add(_chosen);
        _search.Margin = new Thickness(0, 10, 0, 6);
        _card.Children.Add(_search);
        _card.Children.Add(Ui.FadingScroller(_results, 220));
        UiText.Current.PropertyChanged += (_, _) => Render();
        Render();
    }

    public override string Id => WidgetIds.AppGroup;
    public override FrameworkElement View => _tile;
    public override bool IsPopupOpen => _popup.IsOpen;
    public override void ClosePopup() => _popup.Close();

    public override void Stop()
    {
        _popup.Close();
        _catalog = null; // the list is read again when asked; it is not kept
    }

    public override void ApplySettings(WidgetSettings settings)
    {
        var apps = settings.AppGroup ?? [];
        if (apps.SequenceEqual(_apps)) return;
        _apps = apps;
        Render();
    }

    private static ImageSource? IconOf(string id, int size)
    {
        var key = id + "|" + size;
        if (!Icons.TryGetValue(key, out var icon)) Icons[key] = icon = NotificationApps.LoadIcon(id, size);
        return icon;
    }

    private void Render()
    {
        _grid.Children.Clear();
        var cell = Math.Max(14, ((Application.Current.TryFindResource("WidgetTileHeight") as double? ?? 40) - 10) / 2);
        for (var i = 0; i < Slots; i++)
        {
            if (i < _apps.Count)
            {
                var app = _apps[i];
                var image = new Image { Source = IconOf(app.Id, 32), Width = cell - 2, Height = cell - 2, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                var open = Ui.IconButton(image, (_, _) => new InstalledApp(app.Name, app.Id).Launch(), app.Name, cell, cell);
                open.Padding = new Thickness(0);
                _grid.Children.Add(open);
            }
            else
            {
                var plus = Ui.IconButton(Ui.Icon("IconPlus", 9, brushKey: "TextSecondary"), (_, _) => Toggle(), UiText.Get("AppGroupAdd"), cell, cell);
                plus.Padding = new Thickness(0);
                _grid.Children.Add(plus);
            }
        }
        _tile.ToolTip = _apps.Count > 0 ? UiText.Get("AppGroupHint") : null;
        if (_popup.IsOpen) RenderCard();
    }

    private void RenderCard()
    {
        _chosen.Children.Clear();
        if (_apps.Count == 0)
        {
            var none = Ui.Text(UiText.Get("AppGroupEmpty"), 11.5, brushKey: "TextSecondary");
            none.TextWrapping = TextWrapping.Wrap;
            _chosen.Children.Add(none);
        }
        foreach (var app in _apps)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var image = new Image { Source = IconOf(app.Id, 32), Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(image, Dock.Left);
            var remove = Ui.IconButton(Ui.Icon("IconClose", 10, brushKey: "TextSecondary"), (_, _) =>
                UpdateSettings(s => s with { AppGroup = (s.AppGroup ?? []).Where(a => a.Id != app.Id).ToList() }), UiText.Get("AppGroupRemove"));
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(image);
            row.Children.Add(remove);
            row.Children.Add(Ui.Text(app.Name, 12.5, FontWeights.SemiBold));
            _chosen.Children.Add(row);
        }
        _search.Tag = UiText.Get(_apps.Count >= Slots ? "AppGroupFull" : "AppGroupSearch");
        _search.IsEnabled = _apps.Count < Slots;
        RenderResults();
    }

    private void RenderResults()
    {
        _results.Children.Clear();
        if (_apps.Count >= Slots) return;
        if (_catalog is null)
        {
            _results.Children.Add(Ui.Text(UiText.Get("AppGroupLoading"), 11, brushKey: "TextSecondary"));
            _ = LoadCatalogAsync();
            return;
        }
        var query = _search.Text.Trim();
        var matches = _catalog
            .Where(app => _apps.All(a => a.Id != app.Id))
            .Where(app => query.Length == 0 || app.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Take(query.Length == 0 ? 8 : 20);
        foreach (var app in matches)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Image { Source = IconOf(app.Id, 32), Width = 20, Height = 20, Margin = new Thickness(0, 0, 8, 0) });
            row.Children.Add(Ui.Text(app.Name, 12));
            var pick = new Button { Content = row, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 4, 6, 4) };
            pick.SetResourceReference(FrameworkElement.StyleProperty, "WidgetIconButton");
            pick.Width = double.NaN;
            pick.Height = double.NaN;
            pick.HorizontalAlignment = HorizontalAlignment.Stretch;
            pick.Click += (_, _) =>
            {
                _search.Text = "";
                UpdateSettings(s => s with { AppGroup = (s.AppGroup ?? []).Append(new AppLink(app.Name, app.Id)).Take(Slots).ToList() });
            };
            _results.Children.Add(pick);
        }
    }

    private async Task LoadCatalogAsync()
    {
        if (_loadingCatalog) return;
        _loadingCatalog = true;
        try { _catalog = (await AppCatalog.LoadAsync()).OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToList(); }
        finally { _loadingCatalog = false; }
        if (_popup.IsOpen) RenderResults();
    }

    private void Toggle()
    {
        if (_popup.IsOpen)
        {
            _popup.Close();
            return;
        }
        _popup.SetContent(_card, 300);
        _popup.Toggle();
        RenderCard();
        if (_apps.Count < Slots) _popup.FocusInput(_search);
    }
}
