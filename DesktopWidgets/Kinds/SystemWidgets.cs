using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using static QiDock.DesktopWidgets.DeskUi;

namespace QiDock.DesktopWidgets.Kinds;

internal enum Metric { Cpu, Ram, Disk, Battery }

/// <summary>CPU, memory, disk or battery as a ring, a bar or (CPU) a live graph.</summary>
internal sealed class MetricWidget(Metric metric) : DeskWidget
{
    private RingGauge? _ring;
    private TextBlock? _value, _detail;
    private Sparkline? _graph;
    private Border? _barFill;
    private Grid? _bar;
    private ContentControl? _icon;
    private StackPanel? _top;
    private DateTime _lastTop;

    private Brush Color => metric switch
    {
        Metric.Cpu => Blue,
        Metric.Ram => Purple,
        Metric.Disk => Cyan,
        _ => Green
    };

    private string IconKey => metric switch
    {
        Metric.Cpu => "IconCpu",
        Metric.Ram => "IconMemory",
        Metric.Disk => "IconHardDrive",
        _ => "IconBattery"
    };

    private string Label => metric switch
    {
        Metric.Cpu => Desk.L("İşlemci", "CPU"),
        Metric.Ram => Desk.L("Bellek", "Memory"),
        Metric.Disk => Desk.L("Disk", "Disk"),
        _ => Desk.L("Pil", "Battery")
    };

    public override FrameworkElement Build()
    {
        _ring = null;
        _graph = null;
        _bar = null;
        _icon = new ContentControl { Content = DeskUi.Icon(IconKey, 14, Color) };
        _value = Digits("", 15, FontWeights.Bold);
        _detail = T("", 11.5, FontWeights.SemiBold, Secondary);
        switch (Size)
        {
            case DeskSize.Mini:
            {
                _ring = new RingGauge { Width = 50, Height = 50, Thickness = 5, Stroke = Color };
                var ringHost = new Grid();
                ringHost.Children.Add(_ring);
                ringHost.Children.Add(_icon);
                _value.FontSize = 20;
                var grid = Grid(columns: "Auto,*");
                grid.Children.Add(ringHost);
                grid.Children.Add(At(Margin(Column(_value, T(Label, 12, FontWeights.SemiBold, Secondary)), 12), 0, 1));
                return grid;
            }
            case DeskSize.Small:
            {
                _ring = new RingGauge { Width = 96, Height = 96, Thickness = 9, Stroke = Color, HorizontalAlignment = HorizontalAlignment.Center };
                var ringHost = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
                ringHost.Children.Add(_ring);
                _value.FontSize = 24;
                _value.HorizontalAlignment = HorizontalAlignment.Center;
                ringHost.Children.Add(_value);
                var title = Row(_icon, Margin(T(Label, 12.5, FontWeights.SemiBold), 6));
                _detail.HorizontalAlignment = HorizontalAlignment.Center;
                var grid = Grid("Auto,*,Auto");
                grid.Children.Add(title);
                grid.Children.Add(At(ringHost, 1));
                grid.Children.Add(At(_detail, 2));
                return grid;
            }
            case DeskSize.Tall:
            {
                // Upright: the load now and over the last minutes, then what is using it, program by program.
                var header = Grid(columns: "Auto,*,Auto");
                header.Children.Add(_icon);
                header.Children.Add(At(Margin(T(Label, 13, FontWeights.SemiBold), 6), 0, 1));
                _value.FontSize = 20;
                header.Children.Add(At(_value, 0, 2));
                // Scaled to its own peak so small changes still show; the number gives the real figure.
                _graph = new Sparkline { Stroke = Color, Height = 50, Margin = new Thickness(0, 10, 0, 0) };
                var heading = T(Desk.L("En çok kullananlar", "Using it most"), 11, FontWeights.SemiBold, Secondary);
                heading.Margin = new Thickness(0, 12, 0, 4);
                _top = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
                _top.Children.Add(T(Desk.L("Ölçülüyor…", "Measuring…"), 12, brush: Secondary));
                _lastTop = default;
                var footer = Grid(columns: "*,Auto");
                footer.Children.Add(T(Desk.L($"{Environment.ProcessorCount} çekirdek", $"{Environment.ProcessorCount} cores"), 10.5, FontWeights.SemiBold, Tertiary));
                _detail.FontSize = 10.5;
                _detail.Foreground = Tertiary;
                footer.Children.Add(At(_detail, 0, 1));
                var grid = Grid("Auto,Auto,Auto,*,Auto");
                grid.Children.Add(header);
                grid.Children.Add(At(_graph, 1));
                grid.Children.Add(At(heading, 2));
                grid.Children.Add(At(_top, 3));
                grid.Children.Add(At(footer, 4));
                return grid;
            }
            default:
            {
                var header = Grid(columns: "Auto,*,Auto");
                header.Children.Add(_icon);
                header.Children.Add(At(Margin(T(Label, 13, FontWeights.SemiBold), 6), 0, 1));
                header.Children.Add(At(_value, 0, 2));
                var grid = Grid("Auto,*,Auto");
                grid.Children.Add(header);
                if (metric == Metric.Cpu)
                {
                    _graph = new Sparkline { Stroke = Color, Maximum = 100, Margin = new Thickness(0, 6, 0, Size == DeskSize.Medium ? 6 : 0) };
                    grid.Children.Add(At(_graph, 1));
                }
                else
                {
                    _barFill = new Border { CornerRadius = new CornerRadius(4), Background = Color, HorizontalAlignment = HorizontalAlignment.Left };
                    _bar = new Grid { Height = 8, VerticalAlignment = VerticalAlignment.Center };
                    _bar.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = Track });
                    _bar.Children.Add(_barFill);
                    grid.Children.Add(At(_bar, 1));
                }
                if (Size != DeskSize.Strip || metric != Metric.Cpu) grid.Children.Add(At(_detail, 2));
                return grid;
            }
        }
    }

    /// <summary>The busiest programs, with their icons; the first reading only sets the baseline.</summary>
    private void ShowTop()
    {
        if (_top is null) return;
        _lastTop = DateTime.UtcNow;
        var all = QiDock.Services.Widgets.CpuTop.Sample(6);
        if (all.Count == 0) return;
        // Programs that round to nothing only fill the list; three rows always stay.
        var users = all.Where((u, i) => i < 3 || u.Percent >= 0.5).ToList();
        _top.Children.Clear();
        foreach (var user in users)
        {
            FrameworkElement icon = user.Path is { } path && QiDock.Services.ApplicationIconProvider.GetForFile(path) is { } picture
                ? new System.Windows.Controls.Image { Source = picture }
                : Letter(user.Name);
            icon.Width = icon.Height = 18;
            icon.VerticalAlignment = VerticalAlignment.Center;
            var name = T(user.Name, 12, FontWeights.SemiBold);
            name.Margin = new Thickness(8, 0, 6, 0);
            var share = Digits(Percent(user.Percent), 12, FontWeights.SemiBold, user.Percent >= 25 ? Orange : Secondary);
            var row = Grid(columns: "Auto,*,Auto");
            row.Height = 25;
            row.Children.Add(icon);
            row.Children.Add(At(name, 0, 1));
            row.Children.Add(At(share, 0, 2));
            row.ToolTip = $"{user.Name}: {Percent(user.Percent)}";
            _top.Children.Add(row);
        }
    }

    /// <summary>A small tile with the program's first letter, for programs without an icon of their own.</summary>
    private static FrameworkElement Letter(string name)
    {
        var letter = T(name.Length > 0 ? name[..1].ToUpperInvariant() : "?", 10.5, FontWeights.Bold, Text);
        letter.HorizontalAlignment = HorizontalAlignment.Center;
        return new Border { CornerRadius = new CornerRadius(5), Background = Inset, Child = letter };
    }

    public override void AddMenuItems(ItemsControl menu)
    {
        if (metric == Metric.Cpu)
            menu.Items.Add(MenuEntry(Desk.L("Görev Yöneticisini aç", "Open Task Manager"), () => QiDock.Widgets.Ui.Open("taskmgr.exe")));
    }

    protected override void OnAttach() => SystemHub.Instance.Subscribe(Tick);
    protected override void OnDetach() => SystemHub.Instance.Unsubscribe(Tick);

    public override void Tick()
    {
        var hub = SystemHub.Instance;
        double percent;
        string detail;
        switch (metric)
        {
            case Metric.Cpu:
                percent = hub.Cpu;
                var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
                detail = Size == DeskSize.Tall
                    ? Desk.L($"açık {(int)uptime.TotalDays} g {uptime.Hours} sa", $"up {(int)uptime.TotalDays} d {uptime.Hours} h")
                    : Size == DeskSize.Small
                    ? Desk.L($"{Environment.ProcessorCount} çekirdek", $"{Environment.ProcessorCount} cores")
                    : Desk.L($"{Environment.ProcessorCount} çekirdek · açık {(int)uptime.TotalDays} g {uptime.Hours} sa", $"{Environment.ProcessorCount} cores · up {(int)uptime.TotalDays} d {uptime.Hours} h");
                break;
            case Metric.Ram:
                percent = hub.RamPercent;
                detail = $"{SystemHub.Gb(hub.RamUsed)} / {SystemHub.Gb(hub.RamTotal)}";
                break;
            case Metric.Disk:
                percent = hub.DiskPercent;
                detail = Desk.L($"{SystemHub.Gb(hub.DiskFree)} boş", $"{SystemHub.Gb(hub.DiskFree)} free");
                break;
            default:
                if (hub.Battery is not { } battery)
                {
                    percent = 0;
                    detail = Desk.L("Pil yok", "No battery");
                    break;
                }
                percent = battery.Percent;
                detail = battery.Charging ? Desk.L("Şarj oluyor", "Charging") : battery.OnAc ? Desk.L("Prize takılı", "Plugged in")
                    : battery.Remaining is { } left ? Desk.L($"{(int)left.TotalHours} sa {left.Minutes} dk kaldı", $"{(int)left.TotalHours} h {left.Minutes} m left") : Desk.L("Pilde", "On battery");
                if (_ring is not null) _ring.Stroke = percent <= 20 && !battery.Charging ? Red : Green;
                if (_icon is not null) _icon.Content = DeskUi.Icon(battery.Charging ? "IconBolt" : IconKey, 14, Color);
                break;
        }
        if (_ring is not null) _ring.Value = percent;
        _value!.Text = Percent(percent);
        if (Size != DeskSize.Mini) _value.Foreground = Size == DeskSize.Small ? Text : QiDock.Widgets.Ui.UsageBrush(metric == Metric.Battery ? 100 - percent : percent);
        _detail!.Text = detail;
        _graph?.SetValues([.. hub.CpuHistory]);
        if (_top is not null && DateTime.UtcNow - _lastTop >= TimeSpan.FromSeconds(3)) ShowTop();
        if (_bar is not null && _barFill is not null)
            _barFill.Width = Math.Max(8, (_bar.ActualWidth > 0 ? _bar.ActualWidth : 312) * percent / 100);
    }
}

/// <summary>Download and upload speed with a live graph.</summary>
internal sealed class NetworkWidget : DeskWidget
{
    private TextBlock? _down, _up;
    private Sparkline? _graph;

    public override FrameworkElement Build()
    {
        _down = Digits("", Size == DeskSize.Mini ? 13 : 14, FontWeights.Bold);
        _up = Digits("", Size == DeskSize.Mini ? 13 : 14, FontWeights.Bold);
        var down = Row(DeskUi.Icon("IconArrowDown", 13, Cyan), Margin(_down, 4));
        if (Size == DeskSize.Mini)
        {
            _graph = null;
            return Column(down, Margin(Row(DeskUi.Icon("IconArrowUp", 13, Purple), Margin(_up, 4)), 0, 6));
        }
        var up = Row(Margin(_up, 0, 0, 4), DeskUi.Icon("IconArrowUp", 13, Purple));
        _graph = new Sparkline { Stroke = Cyan, SecondStroke = Purple, LineThickness = 1.8 };
        var header = Grid(columns: "*,Auto");
        header.Children.Add(down);
        header.Children.Add(At(up, 0, 1));
        var grid = Grid("Auto,*");
        grid.Children.Add(header);
        grid.Children.Add(At(Margin(_graph, 0, 6), 1));
        return grid;
    }

    protected override void OnAttach() => SystemHub.Instance.Subscribe(Tick);
    protected override void OnDetach() => SystemHub.Instance.Unsubscribe(Tick);

    public override void Tick()
    {
        var hub = SystemHub.Instance;
        _down!.Text = SystemHub.Speed(hub.Down);
        _up!.Text = SystemHub.Speed(hub.Up);
        _graph?.SetValues([.. hub.DownHistory], [.. hub.UpHistory]);
    }
}
