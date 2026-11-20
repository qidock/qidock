using System.Windows;
using System.Windows.Controls;
using QiDock.Models;
using QiDock.Services;

namespace QiDock.Widgets;

/// <summary>A single round button that opens the control center (Wi-Fi, Bluetooth, sound, brightness, battery).</summary>
public sealed class ControlCenterWidget : DockWidget
{
    private readonly Button _tile;

    public ControlCenterWidget()
    {
        var icon = Ui.Icon("IconMixer", 18);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        _tile = Ui.Tile(icon, (_, _) => ControlCenter.Shared.ToggleFrom(_tile!), horizontalPadding: 0);
        _tile.SetResourceReference(FrameworkElement.WidthProperty, "WidgetTileHeight");
        _tile.ToolTip = UiText.Get("ControlCenterTitle");
        _tile.MouseEnter += (_, _) => ControlCenter.Prepare(); // read ahead, so the panel opens complete
        System.Windows.Automation.AutomationProperties.SetName(_tile, UiText.Get("ControlCenterTitle"));
    }

    public override string Id => WidgetIds.ControlCenter;
    public override FrameworkElement View => _tile;
    public override bool IsPopupOpen => ControlCenter.IsCreated && ControlCenter.Shared.IsOpen;

    public override void ClosePopup()
    {
        if (ControlCenter.IsCreated) ControlCenter.Shared.Close(animated: true);
    }
}
