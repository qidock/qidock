using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QiDock.Services.Widgets;
using static QiDock.DesktopWidgets.DeskUi;

namespace QiDock.DesktopWidgets.Kinds;

/// <summary>
/// The battery of connected Bluetooth devices (mouse, keyboard, headphones), as Windows reports it. Read when the
/// widget shows and every two minutes while it stays visible.
/// </summary>
internal sealed class DeviceBatteryWidget : DeskWidget
{
    private Panel? _list;
    private IReadOnlyList<DeviceBattery> _devices = [];
    private bool _loaded, _reading;

    public override TimeSpan? Interval => TimeSpan.FromMinutes(2);

    private int Capacity => Size switch
    {
        DeskSize.Mini => 1,
        DeskSize.Strip => 2,
        DeskSize.Small => 3,
        _ => 4
    };

    public override FrameworkElement Build()
    {
        if (Size == DeskSize.Strip)
        {
            var grid = Grid(columns: "*,14,*");
            _list = grid;
        }
        else
        {
            _list = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        }
        var root = new System.Windows.Controls.Grid();
        root.Children.Add(_list);
        Draw();
        return root;
    }

    protected override void OnAttach() => Read();

    public override void Tick() => Read();

    private async void Read()
    {
        if (_reading) return;
        _reading = true;
        try { _devices = await BluetoothBatteries.ReadAsync(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceWarning("Bluetooth batteries: {0}", ex.Message);
        }
        finally
        {
            _reading = false;
            _loaded = true;
        }
        Draw();
    }

    private static string IconOf(string kind) => kind switch
    {
        "mouse" => "IconMouse",
        "keyboard" => "IconKeyboard",
        "headphones" => "IconHeadphones",
        _ => "IconBluetooth"
    };

    private static Brush LevelBrush(int percent) => percent <= 15 ? Red : percent <= 30 ? Orange : Green;

    private void Draw()
    {
        if (_list is null) return;
        _list.Children.Clear();
        if (_devices.Count == 0)
        {
            var empty = Size is DeskSize.Mini or DeskSize.Strip
                ? Row(Icon("IconBluetooth", 18, Secondary), Margin(T(_loaded ? Desk.L("Pil bildiren bağlı cihaz yok", "No connected device reports a battery") : Desk.L("Okunuyor…", "Reading…"), 12, brush: Secondary), 8))
                : Empty("IconBluetooth", _loaded ? Desk.L("Pil bildiren bağlı Bluetooth cihazı yok", "No connected Bluetooth device reports its battery") : Desk.L("Okunuyor…", "Reading…"));
            if (_list is System.Windows.Controls.Grid) System.Windows.Controls.Grid.SetColumnSpan(empty, 3);
            _list.Children.Add(empty);
            return;
        }

        if (Size == DeskSize.Mini)
        {
            var device = _devices[0];
            var grid = Grid(columns: "Auto,*");
            grid.Children.Add(Icon(IconOf(device.Kind), 26, LevelBrush(device.Percent)));
            grid.Children.Add(At(Margin(Column(Digits(Percent(device.Percent), 22, FontWeights.Bold), T(device.Name, 11.5, FontWeights.SemiBold, Secondary)), 12), 0, 1));
            _list.Children.Add(grid);
            return;
        }

        var index = 0;
        foreach (var device in _devices.Take(Capacity))
        {
            var row = Size == DeskSize.Tall ? DeviceRing(device) : DeviceRow(device);
            if (_list is System.Windows.Controls.Grid) At(row, 0, index * 2);
            else if (index > 0) row.Margin = new Thickness(0, 12, 0, 0);
            _list.Children.Add(row);
            index++;
        }
    }

    /// <summary>Upright: a ring per device with its icon inside, as a phone's batteries widget.</summary>
    private static FrameworkElement DeviceRing(DeviceBattery device)
    {
        var ring = new Grid { Width = 52, Height = 52 };
        ring.Children.Add(new RingGauge { Thickness = 5, Stroke = LevelBrush(device.Percent), Value = device.Percent });
        var icon = Icon(IconOf(device.Kind), 20, Text);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        ring.Children.Add(icon);
        var grid = Grid(columns: "Auto,*");
        grid.Children.Add(ring);
        grid.Children.Add(At(Margin(Column(Digits(Percent(device.Percent), 18, FontWeights.Bold, device.Percent <= 15 ? Red : Text), T(device.Name, 11.5, FontWeights.SemiBold, Secondary)), 10), 0, 1));
        grid.ToolTip = $"{device.Name}: {Percent(device.Percent)}";
        return grid;
    }

    private static FrameworkElement DeviceRow(DeviceBattery device)
    {
        var name = T(device.Name, 12.5, FontWeights.SemiBold);
        name.Margin = new Thickness(8, 0, 6, 0);
        var value = Digits(Percent(device.Percent), 12.5, FontWeights.Bold, device.Percent <= 15 ? Red : Text);
        var top = Grid(columns: "Auto,*,Auto");
        top.Children.Add(Icon(IconOf(device.Kind), 16, Secondary));
        top.Children.Add(At(name, 0, 1));
        top.Children.Add(At(value, 0, 2));
        var fill = new Border { CornerRadius = new CornerRadius(3), Background = LevelBrush(device.Percent), HorizontalAlignment = HorizontalAlignment.Left };
        var bar = new Grid { Height = 6, Margin = new Thickness(0, 6, 0, 0) };
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = Track });
        bar.Children.Add(fill);
        bar.SizeChanged += (_, e) => fill.Width = Math.Max(6, e.NewSize.Width * device.Percent / 100.0);
        var column = Column(top, bar);
        column.ToolTip = $"{device.Name}: {Percent(device.Percent)}";
        return column;
    }
}
