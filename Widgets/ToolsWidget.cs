using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using QiDock.Models;
using QiDock.Services;

namespace QiDock.Widgets;

/// <summary>One-click tools: screenshot, clipboard history, emoji, calculator, lock screen.</summary>
public sealed class ToolsWidget : DockWidget
{
    private readonly Border _tile;

    public ToolsWidget()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 4, 0) };
        row.Children.Add(Tool("IconCamera", "ToolScreenshot", () => Ui.Open("ms-screenclip:")));
        row.Children.Add(Tool("IconClipboard", "ToolClipboard", () => SendWinKey(0x56 /* V */)));
        row.Children.Add(Tool("IconSmile", "ToolEmoji", () => SendWinKey(0xBE /* . */)));
        row.Children.Add(Tool("IconCalculator", "ToolCalculator", () => Ui.Open("calc.exe")));
        row.Children.Add(Tool("IconLock", "ToolLock", () => LockWorkStation()));
        _tile = new Border { Child = row, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
        _tile.SetResourceReference(Border.BackgroundProperty, "WidgetTileFill");
        _tile.SetResourceReference(Border.BorderBrushProperty, "WidgetTileStroke");
        _tile.SetResourceReference(FrameworkElement.HeightProperty, "WidgetTileHeight");
    }

    public override string Id => WidgetIds.Tools;
    public override FrameworkElement View => _tile;

    private static Button Tool(string icon, string textKey, Action run)
    {
        var button = Ui.IconButton(Ui.Icon(icon, 16), (_, _) => run(), UiText.Get(textKey), 34, 34);
        button.Margin = new Thickness(1, 0, 1, 0);
        return button;
    }

    /// <summary>Windows key shortcuts (Win+V clipboard history, Win+. emoji panel).</summary>
    private static void SendWinKey(byte key)
    {
        keybd_event(0x5B, 0, 0, 0);
        keybd_event(key, 0, 0, 0);
        keybd_event(key, 0, 2, 0);
        keybd_event(0x5B, 0, 2, 0);
    }

    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, int flags, int extra);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool LockWorkStation();
}
