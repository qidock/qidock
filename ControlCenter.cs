using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QiDock.Services;
using QiDock.Services.Notifications;
using QiDock.Services.Widgets;
using QiDock.Widgets;

namespace QiDock;

/// <summary>
/// Control center: Wi-Fi, Bluetooth, power mode, volume, brightness and battery in one panel on
/// the right edge, plus shortcuts (screen snip, lock, settings).
/// </summary>
public sealed partial class ControlCenter : EdgePanel
{
    private const double Half = 155;
    private static ControlCenter? _shared;
    private static readonly Brush On = Ui.Solid("#FF0A84FF");
    private static readonly Brush Off = Ui.Solid("#FF5A5A5E");

    public static ControlCenter Shared => _shared ??= new ControlCenter();
    public static bool IsCreated => _shared is not null;

    /// <summary>Opens QiDock's own settings (supplied by the main window).</summary>
    public static Action OpenAppSettings { get; set; } = () => { };

    private readonly AudioEndpoint _audio = new();
    private readonly RadioControls _radios = new();
    private readonly DispatcherTimer _brightnessDelay = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _radiosStarted;
    private int _pendingBrightness = -1;
    private Slider? _volumeSlider;
    private AudioEndpoint.State? _shownVolume;
    private bool _movingVolume;
    private string? _loggedVolumeProblem;

    private ControlCenter() : base(Half * 2 + 10, "QiDock Control Center")
    {
        _radios.Changed += (_, _) => Dispatcher.InvokeAsync(Refresh);
        _brightnessDelay.Tick += (_, _) =>
        {
            _brightnessDelay.Stop();
            if (_pendingBrightness >= 0) _ = Brightness.SetAsync(_pendingBrightness);
        };
    }

    /// <summary>
    /// Reads brightness (and the radios the first time) in the background and redraws once, after
    /// the panel has finished sliding in, and only if something changed.
    /// </summary>
    protected override async void OnOpened()
    {
        var shown = Brightness.Current;
        var work = new List<Task> { Brightness.RefreshAsync(), Task.Delay(480) };
        var firstRadios = !_radiosStarted;
        if (firstRadios)
        {
            _radiosStarted = true;
            work.Add(_radios.InitializeAsync());
        }
        try { await Task.WhenAll(work); }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Control center refresh failed: {0}", ex.Message);
            return;
        }
        // The volume is read again on every opening (off the window thread): what was shown may be out of date.
        AudioEndpoint.State? volume;
        try { volume = await AudioEndpoint.ReadInBackgroundAsync(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException) { volume = _shownVolume; }
        if (firstRadios || Brightness.Current != shown || volume != _shownVolume) Refresh();
    }

    /// <summary>The volume keys (or another app) changed the volume: the open panel's slider follows.</summary>
    public static void VolumeChanged(float level, bool muted)
    {
        if (_shared is not { IsOpen: true, _volumeSlider: { } slider } panel || slider.IsMouseCaptureWithin) return;
        if (panel._shownVolume is { } shown && shown.Muted != muted)
        {
            panel.Refresh(); // the mute icon changes too
            return;
        }
        panel._movingVolume = true;
        slider.Value = level * 100;
        panel._movingVolume = false;
        if (panel._shownVolume is { } was) panel._shownVolume = was with { Volume = level };
    }

    /// <summary>Redraws while open, but never under a slider being dragged (that would drop the drag).</summary>
    private void Refresh()
    {
        if (!IsOpen) return;
        if (_passwordBox is { IsKeyboardFocusWithin: true }) return; // a password being typed is never wiped away
        if (Mouse.Captured is DependencyObject held && Cards.IsAncestorOf(held))
        {
            Mouse.Captured.LostMouseCapture += Later;
            return;
        }
        Rebuild();

        void Later(object sender, MouseEventArgs e)
        {
            ((IInputElement)sender).LostMouseCapture -= Later;
            Dispatcher.BeginInvoke(Refresh, DispatcherPriority.Background);
        }
    }

    protected override void Build()
    {
        Cards.Children.Clear();
        var top = new Grid { Width = CardWidth, Margin = new Thickness(0, 0, 0, 10), Tag = "top" };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Half) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Half) });
        // Centered in its tile, which is as tall as the two tiles beside it.
        var radios = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (_radios.HasWifi)
        {
            var network = _radios.WifiOn ? (RadioControls.IsOnWifi() ? RadioControls.CurrentNetworkName() : null) ?? UiText.Get("QcNotConnected") : UiText.Get("QcOff");
            if (!_radios.WifiOn) _showNetworks = false;
            radios.Children.Add(RadioRow(_radios.WifiOn ? "IconWifi" : "IconWifiOff", "Wi-Fi", network, _radios.WifiOn, () => _radios.ToggleWifiAsync(), _radios.WifiOn ? ToggleNetworks : null));
        }
        if (_radios.HasBluetooth)
            radios.Children.Add(RadioRow(_radios.BluetoothOn ? "IconBluetooth" : "IconBluetoothOff", "Bluetooth", UiText.Get(_radios.BluetoothOn ? "QcOn" : "QcOff"), _radios.BluetoothOn, () => _radios.ToggleBluetoothAsync()));
        if (radios.Children.Count == 0)
            radios.Children.Add(RadioRow("IconWifi", UiText.Get("CcNetwork"), RadioControls.CurrentNetworkName() ?? UiText.Get("QcNotConnected"), true, null, ToggleNetworks));
        var left = Glass(radios, new Thickness(12, 10, 10, 10), 16, Half);
        left.VerticalAlignment = VerticalAlignment.Stretch;
        top.Children.Add(left);

        var right = new StackPanel();
        Grid.SetColumn(right, 2);
        right.Children.Add(PowerTile());
        right.Children.Add(NotificationsTile());
        top.Children.Add(right);
        Cards.Children.Add(top);
        if (_showNetworks) Cards.Children.Add(Keyed("networks", NetworksCard()));

        Cards.Children.Add(Keyed("volume", VolumeCard()));
        if (Brightness.Current is { } level) Cards.Children.Add(Keyed("brightness", BrightnessCard(level)));
        if (BatteryStatus.Read() is { } battery) Cards.Children.Add(Keyed("battery", BatteryCard(battery)));
        Cards.Children.Add(Keyed("shortcuts", Shortcuts()));
    }

    private static FrameworkElement Keyed(string key, FrameworkElement card)
    {
        Spaced(card).Tag = key;
        return card;
    }

    // ---- Connectivity and tiles ------------------------------------------------------------

    /// <summary>
    /// A radio's row. With <paramref name="open"/> (Wi-Fi) the round button switches the radio and the rest of the row
    /// opens the network list; otherwise the whole row switches it.
    /// </summary>
    private FrameworkElement RadioRow(string iconKey, string title, string status, bool on, Func<Task<bool>>? toggle, Action? open = null)
    {
        var circle = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = on ? On : Off, Child = Centered(Ui.Icon(iconKey, 15, Ui.White, null)), Margin = new Thickness(0, 0, 10, 0) };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.Text(title, 12.5, FontWeights.SemiBold));
        var detail = Ui.Text(status, 11, brushKey: "TextSecondary");
        detail.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(detail);
        var row = new DockPanel { Margin = new Thickness(0, 5, 0, 5), Background = Brushes.Transparent, Cursor = toggle is null && open is null ? null : Cursors.Hand };
        DockPanel.SetDock(circle, Dock.Left);
        row.Children.Add(circle);
        if (open is not null)
        {
            var chevron = Ui.Icon("IconChevronRight", 10, brushKey: "TextSecondary");
            chevron.VerticalAlignment = VerticalAlignment.Center;
            chevron.Margin = new Thickness(6, 0, 0, 0);
            chevron.RenderTransformOrigin = new Point(0.5, 0.5);
            if (_showNetworks) chevron.RenderTransform = new RotateTransform(90);
            DockPanel.SetDock(chevron, Dock.Right);
            row.Children.Add(chevron);
            row.ToolTip = UiText.Get("WifiShowNetworks");
        }
        row.Children.Add(text);
        if (toggle is not null)
        {
            var target = open is null ? (UIElement)row : circle;
            circle.Cursor = Cursors.Hand;
            target.MouseLeftButtonUp += async (_, e) =>
            {
                e.Handled = true;
                if (!await toggle()) Ui.ShowHud(iconKey, UiText.Get("QcRadioDenied"));
                Rebuild();
            };
        }
        if (open is not null)
            row.MouseLeftButtonUp += (_, e) =>
            {
                if (e.Handled) return;
                e.Handled = true;
                open();
            };
        return row;
    }

    private FrameworkElement PowerTile()
    {
        var mode = PowerMode.Read();
        var name = mode switch
        {
            PowerMode.Mode.Efficiency => UiText.Get("CcModeEfficiency"),
            PowerMode.Mode.Performance => UiText.Get("CcModePerformance"),
            PowerMode.Mode.Balanced => UiText.Get("CcModeBalanced"),
            _ => "—"
        };
        return Tile("IconGauge", UiText.Get("CcPowerMode"), name, mode is not null, mode is null ? null : () =>
        {
            var next = mode switch { PowerMode.Mode.Efficiency => PowerMode.Mode.Balanced, PowerMode.Mode.Balanced => PowerMode.Mode.Performance, _ => PowerMode.Mode.Efficiency };
            if (!PowerMode.Set(next)) Ui.ShowHud("IconGauge", UiText.Get("QcPowerModeDenied"));
            Rebuild();
        });
    }

    private FrameworkElement NotificationsTile()
    {
        var unread = NotificationFeed.Shared.UnreadCount;
        var status = Widgets.NotificationsWidget.StatusText();
        var tile = Tile("IconBell", UiText.Get("NotificationsTitle"), status, unread > 0, () => NotificationCenter.Shared.Toggle());
        tile.Margin = new Thickness(0, 10, 0, 0);
        return tile;
    }

    private Border Tile(string iconKey, string title, string status, bool highlighted, Action? click)
    {
        var circle = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = highlighted ? On : Off, Child = Centered(Ui.Icon(iconKey, 15, Ui.White, null)), Margin = new Thickness(0, 0, 10, 0) };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = Ui.Text(title, 12.5, FontWeights.SemiBold);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(name);
        var detail = Ui.Text(status, 11, brushKey: "TextSecondary");
        detail.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(detail);
        var row = new DockPanel();
        DockPanel.SetDock(circle, Dock.Left);
        row.Children.Add(circle);
        row.Children.Add(text);
        var tile = Glass(row, new Thickness(12, 10, 10, 10), 16, Half);
        if (click is not null)
        {
            tile.Cursor = Cursors.Hand;
            tile.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                click();
            };
        }
        return tile;
    }

    // ---- Sliders ---------------------------------------------------------------------------

    private FrameworkElement VolumeCard()
    {
        var state = _audio.Read();
        _shownVolume = state;
        if (_audio.LastProblem is { } problem && problem != _loggedVolumeProblem)
        {
            _loggedVolumeProblem = problem;
            AppLog.Write($"Ses düzeyi okunamadı: {problem}");
        }
        var muted = state?.Muted == true;
        var mute = Ui.IconButton(Ui.Icon(muted ? "IconVolumeMute" : "IconVolume", 15), (_, _) =>
        {
            _audio.SetMute(!muted);
            Rebuild();
        }, UiText.Get("CcMute"), 30, 30);
        var slider = Slider(state is null ? 0 : state.Volume * 100, state is not null, value =>
        {
            if (_movingVolume) return;
            _audio.SetVolume((float)(value / 100));
            if (_shownVolume is { } was) _shownVolume = was with { Volume = (float)(value / 100), Muted = value > 0 ? false : was.Muted };
        });
        _volumeSlider = slider;
        return SliderCard(UiText.Get("CcVolume"), state?.DeviceName, mute, slider);
    }

    private FrameworkElement BrightnessCard(int level)
    {
        var icon = Centered(Ui.Icon("IconSun", 16));
        icon.Width = icon.Height = 30;
        var slider = Slider(level, true, value =>
        {
            // Applied every 120 ms while dragging (not only once the drag stops), newest value last.
            _pendingBrightness = (int)Math.Round(value);
            if (!_brightnessDelay.IsEnabled) _brightnessDelay.Start();
        });
        return SliderCard(UiText.Get("CcBrightness"), null, icon, slider);
    }

    private FrameworkElement SliderCard(string title, string? detail, FrameworkElement icon, Slider slider)
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        if (!string.IsNullOrWhiteSpace(detail))
        {
            var device = Ui.Text(detail, 11, brushKey: "TextSecondary");
            device.TextTrimming = TextTrimming.CharacterEllipsis;
            device.MaxWidth = 170;
            DockPanel.SetDock(device, Dock.Right);
            header.Children.Add(device);
        }
        header.Children.Add(Ui.Text(title, 12.5, FontWeights.SemiBold));
        var row = new DockPanel();
        icon.Margin = new Thickness(-4, 0, 6, 0);
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(slider);
        var column = new StackPanel();
        column.Children.Add(header);
        column.Children.Add(row);
        return Glass(column, new Thickness(16, 10, 16, 8), 16);
    }

    private static Slider Slider(double value, bool enabled, Action<double> changed)
    {
        var slider = new Slider { Value = value, IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Center, Height = 22 };
        slider.SetResourceReference(StyleProperty, "WidgetSlider");
        slider.ValueChanged += (_, e) => changed(e.NewValue);
        return slider;
    }

    // ---- Battery and shortcuts -------------------------------------------------------------

    private FrameworkElement BatteryCard(BatteryStatus.State battery)
    {
        var status = battery.Charging ? UiText.Get("CcCharging")
            : battery.OnAc ? UiText.Get("CcPlugged")
            : battery.Remaining is { } left ? UiText.Format("CcOnBattery", $"{(int)left.TotalHours} sa {left.Minutes} dk")
            : UiText.Get("CcBattery");
        var percent = Ui.Text($"%{battery.Percent}", 20, FontWeights.SemiBold);
        percent.VerticalAlignment = VerticalAlignment.Center;
        percent.Margin = new Thickness(0, 0, 14, 0);
        var fill = battery.Percent <= 20 && !battery.OnAc ? Ui.Solid("#FFFF453A") : battery.Charging ? Ui.Solid("#FF30D158") : On;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.Text(UiText.Get("CcBattery"), 12.5, FontWeights.SemiBold));
        text.Children.Add(Ui.Text(status, 11, brushKey: "TextSecondary"));
        var bar = Ui.Bar(battery.Percent, 70, 8, fill);
        bar.VerticalAlignment = VerticalAlignment.Center;
        var row = new DockPanel();
        DockPanel.SetDock(percent, Dock.Left);
        DockPanel.SetDock(bar, Dock.Right);
        row.Children.Add(percent);
        row.Children.Add(bar);
        row.Children.Add(text);
        return Glass(row, new Thickness(16, 10, 16, 10), 16);
    }

    private FrameworkElement Shortcuts()
    {
        var row = new UniformGrid { Columns = 4 };
        row.Children.Add(Shortcut("IconCamera", UiText.Get("CcScreenshot"), () => Ui.Open("ms-screenclip:")));
        row.Children.Add(Shortcut("IconLock", UiText.Get("CcLock"), () => LockWorkStation()));
        var windowsSettings = Shortcut("IconSettings", UiText.Get("CcWindowsSettings"), () => Ui.Open("ms-settings:"));
        windowsSettings.ToolTip = UiText.Get("CcWindowsSettingsTip");
        row.Children.Add(windowsSettings);
        row.Children.Add(Shortcut("IconDock", UiText.Get("CcDockSettings"), () => OpenAppSettings()));
        return Glass(row, new Thickness(6, 10, 6, 8), 16);
    }

    private FrameworkElement Shortcut(string iconKey, string label, Action run)
    {
        var circle = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Child = Centered(Ui.Icon(iconKey, 15)), HorizontalAlignment = HorizontalAlignment.Center };
        circle.SetResourceReference(Border.BackgroundProperty, "SurfaceHover");
        var text = Ui.Text(label, 10.5, brushKey: "TextSecondary");
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Margin = new Thickness(0, 5, 0, 0);
        var column = new StackPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand };
        column.Children.Add(circle);
        column.Children.Add(text);
        column.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Close(animated: true);
            run();
        };
        return column;
    }

    private static FrameworkElement Centered(FrameworkElement child)
    {
        child.HorizontalAlignment = HorizontalAlignment.Center;
        child.VerticalAlignment = VerticalAlignment.Center;
        var grid = new Grid();
        grid.Children.Add(child);
        return grid;
    }

    [DllImport("user32.dll")] private static extern bool LockWorkStation();
}

/// <summary>
/// Built-in display brightness through WMI (laptops and tablets; external monitors are not covered).
/// WMI is called on a worker thread so the panel never waits for it.
/// </summary>
internal static class Brightness
{
    private static bool _unsupported;

    /// <summary>The last level read, or null when there is no adjustable display.</summary>
    public static int? Current { get; private set; }

    public static Task RefreshAsync() => _unsupported ? Task.CompletedTask : Task.Run(() =>
    {
        try
        {
            foreach (dynamic item in Query("SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE"))
            {
                Current = Convert.ToInt32(item.Properties_.Item("CurrentBrightness").Value);
                return;
            }
            _unsupported = true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or FormatException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            System.Diagnostics.Trace.TraceWarning("Brightness unavailable: {0}", ex.Message);
        }
        Current = null;
    });

    private static readonly object Gate = new();
    private static int _wanted = -1;

    /// <summary>
    /// Sets the level. Calls run one at a time and only the newest level is applied, so a fast
    /// drag never ends on an older value that finished last.
    /// </summary>
    public static Task SetAsync(int level)
    {
        Interlocked.Exchange(ref _wanted, Math.Clamp(level, 0, 100));
        return Task.Run(() =>
        {
            lock (Gate)
            {
                var target = Interlocked.Exchange(ref _wanted, -1);
                if (target < 0) return; // a newer call already applied it
                try
                {
                    // Through ExecMethod_ with fresh parameters: calling WmiSetBrightness directly on
                    // the object works once and then fails with "unspecified error" (stuck dimmed).
                    foreach (dynamic item in Query("SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE"))
                    {
                        dynamic parameters = item.Methods_.Item("WmiSetBrightness").InParameters.SpawnInstance_();
                        parameters.Properties_.Item("Timeout").Value = 1;
                        parameters.Properties_.Item("Brightness").Value = (byte)target;
                        item.ExecMethod_("WmiSetBrightness", parameters);
                    }
                    Current = target;
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                {
                    System.Diagnostics.Trace.TraceWarning("Could not set brightness: {0}", ex.Message);
                }
            }
        });
    }

    private static dynamic Query(string wql)
    {
        var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator") ?? throw new COMException("WMI is not available.");
        dynamic locator = Activator.CreateInstance(type)!;
        dynamic service = locator.ConnectServer(".", @"root\wmi");
        service.Security_.ImpersonationLevel = 3; // impersonate
        return service.ExecQuery(wql);
    }
}
