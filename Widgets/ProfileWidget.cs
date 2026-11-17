using System.Windows;
using System.Windows.Controls;
using QiDock.Models;
using QiDock.Services;

namespace QiDock.Widgets;

/// <summary>
/// The active widget profile ("İş", "Kod"…) as a tile; a click opens the profile menu (switch, new, rename, remove).
/// The menu itself is built by the dock window, so it keeps the dock up while it is open.
/// </summary>
public sealed class ProfileWidget : DockWidget
{
    private readonly Button _tile;
    private readonly TextBlock _name;
    private readonly TextBlock _caption;

    /// <summary>Raised on a click; the argument is the tile to anchor the menu to.</summary>
    public event EventHandler<FrameworkElement>? MenuRequested;

    public ProfileWidget()
    {
        var icon = Ui.Icon("IconLayout", 15);
        icon.Margin = new Thickness(0, 0, 8, 0);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _caption = Ui.Text("", 10, brushKey: "TextSecondary");
        _name = Ui.Text("", 12.5, FontWeights.SemiBold);
        _name.MaxWidth = 110;
        text.Children.Add(_caption);
        text.Children.Add(_name);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(text);
        _tile = Ui.Tile(row, (sender, _) => MenuRequested?.Invoke(this, (FrameworkElement)sender));
        UiText.Current.PropertyChanged += (_, _) => Render();
        Render();
    }

    public override string Id => WidgetIds.Profile;
    public override FrameworkElement View => _tile;

    public override void ApplySettings(WidgetSettings settings) => Render();

    private void Render()
    {
        var profiles = Current.EffectiveProfiles;
        _caption.Text = UiText.Get("ProfileCaption");
        _name.Text = profiles[Current.EffectiveActiveProfile].Name;
        _tile.ToolTip = UiText.Get("ProfileTileHint");
    }
}
