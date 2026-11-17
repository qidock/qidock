using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QiDock.Services;
using QiDock.Services.Widgets;
using QiDock.Widgets;

namespace QiDock;

/// <summary>
/// The Wi-Fi network list of the control center: opened from the Wi-Fi row, it shows the networks in range, joins
/// one with a click and asks for the password of a network Windows has not joined before.
/// </summary>
public sealed partial class ControlCenter
{
    private const int MaxNetworks = 12;
    private readonly WifiNetworks _wifi = new();
    private bool _showNetworks;
    private bool _scanning;
    private bool _scannedOnce;
    private WifiNetworks.Scan _scanResult;
    private IReadOnlyList<WifiNetworks.Network> _networks = [];
    private string? _joining;
    private string? _passwordFor;
    private string? _joinMessage;
    private PasswordBox? _passwordBox;

    private void ToggleNetworks()
    {
        _showNetworks = !_showNetworks;
        _passwordFor = null;
        _joinMessage = null;
        Rebuild(key => key == "networks" ? new Origin("top") : null);
        if (_showNetworks) _ = ScanNetworksAsync();
    }

    protected override void OnHidden()
    {
        _showNetworks = false;
        _passwordFor = null;
        _joinMessage = null;
        _passwordBox = null;
    }

#if DEBUG
    /// <summary>For the UI snapshot sheet: the open network list with a password being asked.</summary>
    internal FrameworkElement RehearseNetworks()
    {
        _showNetworks = true;
        _scannedOnce = true;
        _networks =
        [
            new("Home", 4, true, true, null),
            new("Home-Guest", 4, true, false, null),
            new("Office-5G", 3, true, false, null),
            new("Cafe", 2, false, false, null),
            new("Neighbor-2.4GHz", 1, true, false, null)
        ];
        _passwordFor = "Home-Guest";
        var sheet = Rehearse();
        _showNetworks = false;
        _passwordFor = null;
        return sheet;
    }
#endif

    private async Task ScanNetworksAsync()
    {
        if (_scanning) return;
        _scanning = true;
        try { (_scanResult, _networks) = await _wifi.ScanAsync(); }
        finally
        {
            _scanning = false;
            _scannedOnce = true;
        }
        if (_showNetworks) Refresh();
    }

    private async Task JoinAsync(WifiNetworks.Network network, string? password)
    {
        if (_joining is not null) return;
        _joining = network.Ssid;
        _joinMessage = null;
        Rebuild();
        WifiNetworks.Join result;
        try { result = await _wifi.ConnectAsync(network, password); }
        finally { _joining = null; }
        _passwordFor = result is WifiNetworks.Join.NeedsPassword or WifiNetworks.Join.WrongPassword ? network.Ssid : null;
        _joinMessage = result switch
        {
            WifiNetworks.Join.WrongPassword => UiText.Get("WifiWrongPassword"),
            WifiNetworks.Join.NotInRange => UiText.Get("WifiNotInRange"),
            WifiNetworks.Join.Unsupported => UiText.Get("WifiUnsupported"),
            WifiNetworks.Join.Failed => UiText.Get("WifiFailed"),
            _ => null
        };
        if (!IsOpen) return;
        if (result == WifiNetworks.Join.Connected)
        {
            await ScanNetworksAsync(); // the tick moves to the joined network
            return;
        }
        Rebuild();
        if (_passwordFor is not null && _passwordBox is { } box)
        {
            AllowTyping();
            _ = Dispatcher.InvokeAsync(() => Keyboard.Focus(box), DispatcherPriority.Input);
        }
    }

    private FrameworkElement NetworksCard()
    {
        var column = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(4, 0, 0, 4) };
        var rescan = Ui.IconButton(Ui.Icon("IconRefresh", 13), (_, _) => _ = ScanNetworksAsync(), UiText.Get("WifiRescan"), 26, 24);
        rescan.IsEnabled = !_scanning;
        DockPanel.SetDock(rescan, Dock.Right);
        header.Children.Add(rescan);
        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(Ui.Text(UiText.Get("WifiNetworks"), 12.5, FontWeights.SemiBold));
        if (_scanning && _networks.Count > 0)
        {
            var busy = Ui.Text(UiText.Get("WifiScanning"), 11, brushKey: "TextSecondary");
            busy.Margin = new Thickness(8, 0, 0, 0);
            busy.VerticalAlignment = VerticalAlignment.Center;
            title.Children.Add(busy);
        }
        header.Children.Add(title);
        column.Children.Add(header);

        _passwordBox = null;
        if (_scanResult == WifiNetworks.Scan.Denied && !_scanning)
        {
            column.Children.Add(Note(UiText.Get("WifiDenied")));
        }
        else if (_networks.Count == 0)
        {
            column.Children.Add(Note(UiText.Get(_scanning || !_scannedOnce ? "WifiScanning" : _scanResult == WifiNetworks.Scan.NoAdapter ? "WifiNoAdapter" : "WifiNone")));
        }
        else
        {
            foreach (var network in _networks.Take(MaxNetworks))
            {
                column.Children.Add(NetworkRow(network));
                if (network.Ssid == _passwordFor) column.Children.Add(PasswordRow(network));
            }
        }
        if (_joinMessage is not null && _passwordFor is null) column.Children.Add(Note(_joinMessage, error: true));

        var more = Ui.Pill(UiText.Get("WifiWindowsList"), (_, _) =>
        {
            Close(animated: true);
            Ui.Open("ms-availablenetworks:");
        });
        more.HorizontalAlignment = HorizontalAlignment.Left;
        more.Margin = new Thickness(4, 8, 0, 0);
        column.Children.Add(more);
        return Glass(column, new Thickness(10, 8, 10, 10), 16);
    }

    private static TextBlock Note(string text, bool error = false)
    {
        var note = Ui.Text(text, 11.5, brushKey: error ? null : "TextSecondary", brush: error ? Ui.Solid("#FFFF6961") : null);
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(4, 4, 4, 2);
        return note;
    }

    private FrameworkElement NetworkRow(WifiNetworks.Network network)
    {
        var signal = Ui.Icon("IconWifi", 14);
        signal.Opacity = network.Bars switch { >= 3 => 1, 2 => 0.75, _ => 0.5 };
        signal.VerticalAlignment = VerticalAlignment.Center;
        signal.Margin = new Thickness(0, 0, 10, 0);
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(signal, Dock.Left);
        row.Children.Add(signal);
        if (network.Connected)
        {
            var tick = Ui.Icon("IconCheck", 12, On, null, 2.2);
            tick.VerticalAlignment = VerticalAlignment.Center;
            tick.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(tick, Dock.Right);
            row.Children.Add(tick);
        }
        if (network.Secured)
        {
            var padlock = Ui.Icon("IconLock", 11, brushKey: "TextSecondary");
            padlock.VerticalAlignment = VerticalAlignment.Center;
            padlock.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(padlock, Dock.Right);
            row.Children.Add(padlock);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = Ui.Text(network.Ssid, 12.5, network.Connected ? FontWeights.SemiBold : FontWeights.Normal);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(name);
        if (network.Ssid == _joining)
            text.Children.Add(Ui.Text(UiText.Get("WifiJoining"), 10.5, brushKey: "TextSecondary"));
        row.Children.Add(text);

        var item = new Border
        {
            Child = row,
            MinHeight = 34,
            Padding = new Thickness(6, 3, 8, 3),
            CornerRadius = new CornerRadius(9),
            Background = Brushes.Transparent,
            Cursor = network.Connected ? null : Cursors.Hand,
            ToolTip = network.Connected ? UiText.Get("WifiConnectedHint") : null
        };
        item.MouseEnter += (_, _) => item.SetResourceReference(Border.BackgroundProperty, "SurfaceHover");
        item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
        item.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (network.Connected || _joining is not null) return;
            if (network.Ssid == _passwordFor) return;
            _passwordFor = null;
            _ = JoinAsync(network, null);
        };
        return item;
    }

    private FrameworkElement PasswordRow(WifiNetworks.Network network)
    {
        var box = new PasswordBox
        {
            Height = 30,
            FontSize = 12.5,
            Padding = new Thickness(8, 0, 8, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent
        };
        box.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        box.SetResourceReference(PasswordBox.CaretBrushProperty, "TextPrimary");
        var frame = new Border { Child = box, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BackgroundProperty, "WidgetTileFill");
        frame.SetResourceReference(Border.BorderBrushProperty, "WidgetAccent");
        _passwordBox = box;

        void Join()
        {
            if (box.Password.Length == 0) return;
            _ = JoinAsync(network, box.Password);
        }
        void Cancel()
        {
            _passwordFor = null;
            _joinMessage = null;
            Rebuild();
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Join();
            else if (e.Key == Key.Escape) Cancel();
            else return;
            e.Handled = true;
        };

        var column = new StackPanel { Margin = new Thickness(6, 2, 6, 8) };
        column.Children.Add(Note(_joinMessage ?? UiText.Format("WifiPasswordFor", network.Ssid), error: _joinMessage is not null));
        frame.Margin = new Thickness(0, 4, 0, 6);
        column.Children.Add(frame);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Ui.Pill(UiText.Get("WifiCancel"), (_, _) => Cancel());
        cancel.Margin = new Thickness(0, 0, 6, 0);
        buttons.Children.Add(cancel);
        var join = Ui.Pill(UiText.Get("WifiJoin"), (_, _) => Join());
        join.IsEnabled = _joining is null;
        buttons.Children.Add(join);
        column.Children.Add(buttons);
        // Clicking into the box lets the panel take the keyboard (it normally never takes the focus).
        box.PreviewMouseLeftButtonDown += (_, _) => AllowTyping();
        return column;
    }
}
