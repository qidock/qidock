using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QiDock.Models;
using QiDock.Services;
using QiDock.Services.Widgets;

namespace QiDock.Widgets;

/// <summary>Wi-Fi, Bluetooth, volume slider and battery.</summary>
public sealed class QuickControlsWidget : DockWidget
{
    private static readonly Brush OnFill = Ui.Solid("#FF0A84FF");
    private static readonly Brush OnIcon = Ui.Solid("#FF5EB1FF");
    private static readonly Brush PillFill = Ui.Solid("#33000000");
    private static readonly Brush PillOnStroke = Ui.Solid("#8C2F8BFF");
    private static readonly Brush PillOffStroke = Ui.Solid("#33FFFFFF");
    private static readonly Brush BatteryFill = Ui.Vertical("#FF7CD3FF", "#FF2D8CF0");
    private static readonly Brush BatteryLowFill = Ui.Vertical("#FFFF8A80", "#FFE5372C");

    private readonly Border _tile;
    private readonly Border _wifiPill;
    private readonly Border _bluetoothPill;
    private readonly Button _wifiButton;
    private readonly Button _bluetoothButton;
    private readonly Border _wifiIconBack;
    private readonly Border _bluetoothIconBack;
    private readonly Button _muteButton;
    private readonly TextBlock _deviceName;
    private readonly TextBlock _volumeText;
    private readonly Grid _slider;
    private readonly Border _sliderFill;
    private readonly Border _battery;
    private readonly TextBlock _batteryText;
    private readonly ContentControl _batteryBolt;
    private readonly System.Windows.Shapes.Path _batteryLevel;
    private double _batteryPercent = 100;
    private readonly DispatcherTimer _timer;
    private readonly AudioEndpoint _audio = new();
    private readonly RadioControls _radios = new();
    private AudioEndpoint.State? _audioState;
    private bool _dragging;
    private bool _initialized;
    private int _tick;

    public QuickControlsWidget()
    {
        // The Wi-Fi and Bluetooth capsules open the Control Center, where the radios are switched.
        (_wifiPill, _wifiButton, _wifiIconBack) = CreateRadioPill("IconWifi");
        (_bluetoothPill, _bluetoothButton, _bluetoothIconBack) = CreateRadioPill("IconBluetooth");

        // Volume: icon + device name + percent on top, slider below.
        _muteButton = Ui.IconButton(Ui.Icon("IconVolume", 13), (_, _) => ToggleMute(), UiText.Get("QcMute"), 22, 20);
        _deviceName = Ui.Text(UiText.Get("QcSound"), 10.5, FontWeights.SemiBold);
        _deviceName.Margin = new Thickness(4, 0, 0, 0);
        _volumeText = Ui.Text("", 10.5, FontWeights.SemiBold, "TextSecondary");
        _volumeText.Margin = new Thickness(6, 0, 2, 0);
        var top = new DockPanel();
        DockPanel.SetDock(_muteButton, Dock.Left);
        DockPanel.SetDock(_volumeText, Dock.Right);
        top.Children.Add(_muteButton);
        top.Children.Add(_volumeText);
        top.Children.Add(_deviceName);
        var deviceButton = (UIElement)top;

        _slider = new Grid { Height = 14, Margin = new Thickness(2, 1, 2, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        var track = new Border { Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
        track.SetResourceReference(Border.BackgroundProperty, "WidgetTrack");
        _sliderFill = new Border { Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Background = OnFill };
        _slider.Children.Add(track);
        _slider.Children.Add(_sliderFill);
        _slider.MouseLeftButtonDown += Slider_MouseDown;
        _slider.MouseMove += Slider_MouseMove;
        _slider.MouseLeftButtonUp += Slider_MouseUp;
        _slider.LostMouseCapture += (_, _) => _dragging = false;
        _slider.SizeChanged += (_, _) => UpdateSliderFill();

        var volume = new StackPanel { Width = 148, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) };
        volume.Children.Add(deviceButton);
        volume.Children.Add(_slider);
        volume.MouseWheel += Volume_MouseWheel;
        volume.Background = Brushes.Transparent;

        // Battery tile.
        _batteryText = Ui.Text("", 13, FontWeights.Bold, brush: Brushes.White);
        _batteryText.HorizontalAlignment = HorizontalAlignment.Center;
        _batteryBolt = new ContentControl { Content = Ui.Glyph("GlyphBolt", 9, Brushes.White), HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed, Margin = new Thickness(0, -2, 0, 0) };
        var batteryStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        batteryStack.Children.Add(_batteryText);
        batteryStack.Children.Add(_batteryBolt);
        // Battery: a glass square that fills up like liquid to the charge level, wavy surface on top.
        _batteryLevel = new System.Windows.Shapes.Path { Fill = BatteryFill };
        var batteryLayers = new Grid { ClipToBounds = true };
        batteryLayers.Children.Add(new Border { Background = Ui.Solid("#3364B5FF") });
        batteryLayers.Children.Add(_batteryLevel);
        batteryLayers.Children.Add(batteryStack);
        batteryLayers.SizeChanged += (_, e) =>
        {
            batteryLayers.Clip = new RectangleGeometry(new Rect(e.NewSize), 9, 9);
            UpdateBatteryLevel();
        };
        _battery = new Border
        {
            Width = 42,
            CornerRadius = new CornerRadius(9),
            Margin = new Thickness(4, 3, 0, 3),
            Child = batteryLayers,
            Visibility = Visibility.Collapsed,
            Cursor = Cursors.Hand
        };
        _battery.MouseLeftButtonUp += (_, _) => ControlCenter.Shared.Toggle();

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Stretch };
        row.Children.Add(_wifiPill);
        row.Children.Add(_bluetoothPill);
        row.Children.Add(volume);
        row.Children.Add(_battery);

        _tile = new Border
        {
            Child = row,
            CornerRadius = (CornerRadius)Application.Current.FindResource("WidgetRadius"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5, 0, 3, 0),
            Margin = new Thickness(3, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _tile.SetResourceReference(Border.BackgroundProperty, "WidgetTileFill");
        _tile.SetResourceReference(Border.BorderBrushProperty, "WidgetTileStroke");
        _tile.SetResourceReference(FrameworkElement.HeightProperty, "WidgetTileHeight");

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Poll();
        _radios.Changed += (_, _) => _tile.Dispatcher.InvokeAsync(UpdateRadios);
        UiText.Current.PropertyChanged += (_, _) => { UpdateRadios(); UpdateAudio(); UpdateBattery(); };
    }

    public override string Id => WidgetIds.QuickControls;
    public override FrameworkElement View => _tile;

    public override async void Start()
    {
        _timer.Start();
        Poll();
        if (_initialized) return;
        _initialized = true;
        await _radios.InitializeAsync();
        UpdateRadios();
    }

    public override void Stop()
    {
        _timer.Stop();
    }


    public override void SetLive(bool live)
    {
        if (!live)
        {
            _timer.Stop();
            return;
        }
        Poll();
        _timer.Start();
    }

    /// <summary>The tile is the quick view; its pills, chevrons and battery open the Control Center for the rest.</summary>
    private void ToggleConnections() => ControlCenter.Shared.ToggleFrom(View);

    public override void Dispose()
    {
        base.Dispose();
        _audio.Dispose();
    }

    private (Border Pill, Button Toggle, Border IconBack) CreateRadioPill(string icon)
    {
        // Outlined capsule: radio icon | divider | chevron. Either part opens the Control Center.
        var iconBack = new Border { Width = 22, Height = 22 };
        iconBack.Child = Ui.Icon(icon, 13);
        var toggleButton = new Button { Content = iconBack, Width = 30, Height = 26 };
        toggleButton.SetResourceReference(FrameworkElement.StyleProperty, "WidgetIconButton");
        toggleButton.Click += (_, _) => ToggleConnections();
        var divider = new Border { Width = 1, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        divider.SetResourceReference(Border.BackgroundProperty, "StrokeSubtle");
        var more = Ui.IconButton(Ui.Icon("IconChevronRight", 11, brushKey: "TextSecondary"), (_, _) => ToggleConnections(), UiText.Get("QcConnections"), 22, 26);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(toggleButton);
        row.Children.Add(divider);
        row.Children.Add(more);
        var pill = new Border
        {
            Child = row,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(2, 0, 2, 0),
            Margin = new Thickness(2, 0, 2, 0),
            Height = 30,
            VerticalAlignment = VerticalAlignment.Center,
            Background = PillFill,
            BorderBrush = PillOffStroke,
            BorderThickness = new Thickness(1)
        };
        return (pill, toggleButton, iconBack);
    }

    private void Poll()
    {
        _tick++;
        if (!_dragging) UpdateAudio();
        if (_tick % 5 == 1) UpdateRadios();
        if (_tick % 15 == 1) UpdateBattery();
    }

    private void UpdateRadios()
    {
        StyleRadio(_wifiPill, _wifiButton, _wifiIconBack, _radios.HasWifi, _radios.WifiOn, "IconWifi", "IconWifiOff",
            _radios.WifiOn ? (RadioControls.IsOnWifi() ? RadioControls.CurrentNetworkName() : null) ?? UiText.Get("QcWifiOn") : UiText.Get("QcWifiOffShort"));
        StyleRadio(_bluetoothPill, _bluetoothButton, _bluetoothIconBack, _radios.HasBluetooth, _radios.BluetoothOn, "IconBluetooth", "IconBluetoothOff",
            UiText.Get(_radios.BluetoothOn ? "QcBluetoothOnShort" : "QcBluetoothOffShort"));
    }

    private static void StyleRadio(Border pill, Button button, Border iconBack, bool available, bool on, string onIcon, string offIcon, string tooltip)
    {
        pill.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (!available) return;
        pill.BorderBrush = on ? PillOnStroke : PillOffStroke;
        iconBack.Child = on ? Ui.Icon(onIcon, 13, OnIcon) : Ui.Icon(offIcon, 13, brushKey: "TextSecondary");
        button.ToolTip = tooltip;
    }

    private void UpdateAudio()
    {
        _audioState = _audio.Read();
        if (_audioState is not { } state)
        {
            _volumeText.Text = "–";
            _sliderFill.Width = 0;
            return;
        }
        var name = string.IsNullOrWhiteSpace(state.DeviceName) ? UiText.Get("QcSound") : state.DeviceName;
        if (_deviceName.Text != name) _deviceName.Text = name;
        _deviceName.ToolTip = name;
        var percent = (int)Math.Round(state.Volume * 100);
        _volumeText.Text = state.Muted ? UiText.Get("QcMuted") : UiText.Format("OpacityPercent", percent.ToString(CultureInfo.InvariantCulture));
        _muteButton.Content = Ui.Icon(state.Muted || percent == 0 ? "IconVolumeMute" : "IconVolume", 13);
        UpdateSliderFill();
    }

    private void UpdateSliderFill()
    {
        if (_audioState is not { } state) return;
        _sliderFill.Background = state.Muted ? Ui.Solid("#FF8E8E93") : OnFill;
        _sliderFill.Width = Math.Max(state.Volume > 0 ? 6 : 0, _slider.ActualWidth * state.Volume);
    }

    private void SetVolumeFromPoint(Point point)
    {
        if (_slider.ActualWidth <= 0) return;
        var level = (float)Math.Clamp(point.X / _slider.ActualWidth, 0, 1);
        _audio.SetVolume(level);
        _audioState = (_audioState ?? new AudioEndpoint.State(level, false, "")) with { Volume = level, Muted = false };
        _volumeText.Text = UiText.Format("OpacityPercent", ((int)Math.Round(level * 100)).ToString(CultureInfo.InvariantCulture));
        UpdateSliderFill();
    }

    private void Slider_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _slider.CaptureMouse();
        SetVolumeFromPoint(e.GetPosition(_slider));
        e.Handled = true;
    }

    private void Slider_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging && e.LeftButton == MouseButtonState.Pressed) SetVolumeFromPoint(e.GetPosition(_slider));
    }

    private void Slider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        _slider.ReleaseMouseCapture();
        UpdateAudio();
    }

    private void Volume_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var current = _audio.Read();
        if (current is null) return;
        var step = e.Delta > 0 ? 0.02f : -0.02f;
        _audio.SetVolume(current.Volume + step);
        UpdateAudio();
        e.Handled = true;
    }

    private void ToggleMute()
    {
        var current = _audio.Read();
        if (current is null) return;
        _audio.SetMute(!current.Muted);
        UpdateAudio();
    }

    private void UpdateBattery()
    {
        var state = BatteryStatus.Read();
        if (state is null)
        {
            if (_battery.Visibility != Visibility.Collapsed)
            {
                _battery.Visibility = Visibility.Collapsed;
                RaiseLayoutChanged();
            }
            return;
        }

        if (_battery.Visibility != Visibility.Visible)
        {
            _battery.Visibility = Visibility.Visible;
            RaiseLayoutChanged();
        }
        _batteryText.Text = state.Percent.ToString(CultureInfo.InvariantCulture);
        _batteryBolt.Visibility = state.Charging || state.OnAc ? Visibility.Visible : Visibility.Collapsed;
        _batteryLevel.Fill = state.Percent <= 20 && !state.OnAc ? BatteryLowFill : BatteryFill;
        _batteryPercent = state.Percent;
        UpdateBatteryLevel();
        var tooltip = UiText.Format("QcBattery", state.Percent);
        if (state.Charging) tooltip += " · " + UiText.Get("QcCharging");
        else if (state.OnAc) tooltip += " · " + UiText.Get("QcPluggedIn");
        else if (state.Remaining is { } remaining) tooltip += " · " + UiText.Format("QcRemaining", (int)remaining.TotalHours, remaining.Minutes);
        _battery.ToolTip = tooltip;
    }

    /// <summary>Liquid fill up to the charge level with a gentle wave on its surface.</summary>
    private void UpdateBatteryLevel()
    {
        if (_batteryLevel.Parent is not FrameworkElement host) return;
        var width = host.ActualWidth;
        var height = host.ActualHeight;
        if (width <= 0 || height <= 0) return;
        var surface = height * (1 - Math.Clamp(_batteryPercent, 0, 100) / 100);
        var wave = _batteryPercent >= 99 ? 0 : 3.2;
        var figure = new PathFigure { StartPoint = new Point(0, surface), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new BezierSegment(new Point(width * 0.25, surface - wave), new Point(width * 0.25, surface + wave), new Point(width * 0.5, surface), true));
        figure.Segments.Add(new BezierSegment(new Point(width * 0.75, surface - wave), new Point(width * 0.75, surface + wave), new Point(width, surface), true));
        figure.Segments.Add(new LineSegment(new Point(width, height), true));
        figure.Segments.Add(new LineSegment(new Point(0, height), true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        _batteryLevel.Data = geometry;
    }
}
