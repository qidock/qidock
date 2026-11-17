using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QiDock.Models;
using QiDock.Services;

namespace QiDock.Widgets;

/// <summary>CPU and memory load as two small bars that turn green → yellow → orange → red as load rises.</summary>
public sealed class SystemWidget : DockWidget
{
    private readonly Button _tile;
    private readonly WidgetPopup _popup;
    private readonly DispatcherTimer _timer;
    private readonly QiDock.DesktopWidgets.RingGauge _cpuBar;
    private readonly QiDock.DesktopWidgets.RingGauge _ramBar;
    private readonly TextBlock _cpuText;
    private readonly TextBlock _ramText;
    private ulong _lastIdle;
    private ulong _lastTotal;
    private double _cpu;
    private MemoryStatus _memory;

    public SystemWidget()
    {
        var rows = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        (_cpuBar, _cpuText) = AddRow(rows, "CPU");
        (_ramBar, _ramText) = AddRow(rows, "RAM");
        ((FrameworkElement)rows.Children[1]).Margin = new Thickness(6, 0, 0, 0);
        _tile = new Button { Content = rows, Padding = new Thickness(6, 0, 6, 0) };
        _tile.SetResourceReference(FrameworkElement.StyleProperty, "WidgetTileButton");
        _tile.Click += (_, _) => TogglePopup();
        _popup = new WidgetPopup(_tile);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Sample();
        UiText.Current.PropertyChanged += (_, _) => Render();
    }

    public override string Id => WidgetIds.System;
    public override FrameworkElement View => _tile;
    public override bool IsPopupOpen => _popup.IsOpen;

    public override void Start()
    {
        Sample();
        _timer.Start();
    }

    public override void Stop()
    {
        _timer.Stop();
        _popup.Close();
    }

    public override void ClosePopup() => _popup.Close();

    public override void SetLive(bool live)
    {
        if (!live)
        {
            _timer.Stop();
            return;
        }
        Sample();
        _timer.Start();
    }

    /// <summary>A small ring with the percent and the label ("CPU", "RAM") inside.</summary>
    private static (QiDock.DesktopWidgets.RingGauge Ring, TextBlock Percent) AddRow(Panel rows, string label)
    {
        var ring = new QiDock.DesktopWidgets.RingGauge { Width = 34, Height = 34, Thickness = 3, TrackBrush = Ui.Solid("#47808080") };
        var percent = Ui.Text("", 10.5, FontWeights.Bold);
        percent.HorizontalAlignment = HorizontalAlignment.Center;
        var name = Ui.Text(label, 6.5, FontWeights.SemiBold, brushKey: "TextSecondary");
        name.HorizontalAlignment = HorizontalAlignment.Center;
        name.Margin = new Thickness(0, -2, 0, 0);
        var inside = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        inside.Children.Add(percent);
        inside.Children.Add(name);
        var face = new Grid { Width = 34, Height = 34, VerticalAlignment = VerticalAlignment.Center };
        face.Children.Add(ring);
        face.Children.Add(inside);
        rows.Children.Add(face);
        return (ring, percent);
    }

    private void Sample()
    {
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var total = kernel + user; // kernel time includes idle time
            if (_lastTotal != 0 && total > _lastTotal)
            {
                var busy = 1 - (double)(idle - _lastIdle) / (total - _lastTotal);
                _cpu = Math.Clamp(busy * 100, 0, 100);
            }
            _lastIdle = idle;
            _lastTotal = total;
        }

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref memory)) _memory = memory;
        Render();
        if (_popup.IsOpen) BuildPopup();
    }

    private void Render()
    {
        SetBar(_cpuBar, _cpu);
        SetBar(_ramBar, _memory.MemoryLoad);
        _cpuText.Text = Math.Round(_cpu).ToString(CultureInfo.InvariantCulture);
        _ramText.Text = Math.Round((double)_memory.MemoryLoad).ToString(CultureInfo.InvariantCulture);
        _tile.ToolTip = $"CPU {Percent(_cpu)} · RAM {Gb(_memory.TotalPhys - _memory.AvailPhys)} / {Gb(_memory.TotalPhys)}";
    }

    private static void SetBar(QiDock.DesktopWidgets.RingGauge ring, double percent)
    {
        ring.Stroke = Ui.UsageBrush(percent);
        ring.Value = percent;
        ring.InvalidateVisual();
    }

    private void TogglePopup()
    {
        if (!_popup.IsOpen) BuildPopup();
        _popup.Toggle();
    }

    private void BuildPopup()
    {
        var root = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var close = Ui.IconButton(Ui.Icon("IconClose", 12), (_, _) => _popup.Close(), UiText.Get("Close"));
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(Ui.Text(UiText.Get("SysTitle"), 13, FontWeights.SemiBold));
        root.Children.Add(header);

        root.Children.Add(Card(UiText.Get("SysCpu"), Percent(_cpu), _cpu,
            string.Format(CultureInfo.CurrentCulture, UiText.Get("SysCores"), Environment.ProcessorCount)));
        var used = _memory.TotalPhys - _memory.AvailPhys;
        root.Children.Add(Card(UiText.Get("SysRam"), Percent(_memory.MemoryLoad), _memory.MemoryLoad,
            string.Format(CultureInfo.CurrentCulture, UiText.Get("SysRamDetail"), Gb(used), Gb(_memory.TotalPhys))));

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var footer = Ui.Text(string.Format(CultureInfo.CurrentCulture, UiText.Get("SysUptime"), (int)uptime.TotalDays, uptime.Hours, uptime.Minutes),
            11, brushKey: "TextSecondary");
        footer.Margin = new Thickness(2, 2, 0, 10);
        root.Children.Add(footer);

        var taskManager = new Button { Content = Ui.Text(UiText.Get("SysTaskManager"), 12, FontWeights.SemiBold), Height = 30, HorizontalAlignment = HorizontalAlignment.Stretch };
        taskManager.SetResourceReference(FrameworkElement.StyleProperty, "WidgetPillButton");
        taskManager.Click += (_, _) =>
        {
            _popup.Close();
            Ui.Open("taskmgr.exe");
        };
        root.Children.Add(taskManager);
        _popup.SetContent(root, 280);
    }

    private static FrameworkElement Card(string title, string value, double percent, string detail)
    {
        var top = new DockPanel();
        var valueText = Ui.Text(value, 15, FontWeights.Bold, brush: Ui.UsageBrush(percent));
        DockPanel.SetDock(valueText, Dock.Right);
        top.Children.Add(valueText);
        top.Children.Add(Ui.Text(title, 12.5, FontWeights.SemiBold));
        var stack = new StackPanel();
        stack.Children.Add(top);
        var bar = Ui.Bar(percent, 236, 6, Ui.UsageBrush(percent));
        bar.Margin = new Thickness(0, 6, 0, 5);
        bar.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(bar);
        stack.Children.Add(Ui.Text(detail, 11, brushKey: "TextSecondary"));
        var card = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 6), Child = stack };
        card.SetResourceReference(Border.BackgroundProperty, "WidgetTileFill");
        return card;
    }

    private static string Percent(double value) => "%" + Math.Round(value).ToString(CultureInfo.InvariantCulture);

    private static string Gb(ulong bytes) => (bytes / 1073741824d).ToString("0.0", CultureInfo.CurrentCulture) + " GB";

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}
