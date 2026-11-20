using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QiDock.Services;
using QiDock.Services.Notifications;
using QiDock.Services.Widgets;
using static QiDock.DesktopWidgets.DeskUi;

namespace QiDock.DesktopWidgets.Kinds;

/// <summary>An AI assistant on the desktop: its app (icon, name, colour) and, when known, its usage provider.</summary>
/// <param name="Plate">A light tile drawn behind an icon that is dark itself (Codex's black knot).</param>
/// <param name="Windows">How many limits the large tile lists (OpenCode Go has three: 5 hours, week, month).</param>
internal sealed record AiAssistant(string Name, string AppId, string? UsageProvider, Color From, Color To, bool Plate = false, int Windows = 2)
{
    public static readonly AiAssistant Claude = new("Claude", "Claude_pzs8sxrjxfjjc!Claude", AiUsageService.ClaudeId,
        Color.FromRgb(0xE0, 0x86, 0x5F), Color.FromRgb(0xC1, 0x5F, 0x3C));
    public static readonly AiAssistant Codex = new("Codex", "OpenAI.Codex_2p2nqsd0c76g0!App", AiUsageService.CodexId,
        Color.FromRgb(0x5B, 0x8C, 0xFF), Color.FromRgb(0x2F, 0x5B, 0xD8), Plate: true);
    public static readonly AiAssistant Gemini = new("Gemini", "com.google.gemini.windows", AiUsageService.GeminiId,
        Color.FromRgb(0x4F, 0x9B, 0xF5), Color.FromRgb(0xA4, 0x6B, 0xE8));
    public static readonly AiAssistant OpenCode = new("OpenCode", "ai.opencode.desktop", AiUsageService.OpenCodeId,
        Color.FromRgb(0x9A, 0xA3, 0xB5), Color.FromRgb(0x5E, 0x67, 0x7A), Windows: 3);

    public Brush Accent => new SolidColorBrush(From);

    public Brush Gradient => new LinearGradientBrush(From, To, new Point(0, 0), new Point(1, 1));

    private static readonly Dictionary<string, BitmapSource?> Icons = [];

    /// <summary>The app's own icon as Windows shows it in Start (null when the app is not installed).</summary>
    public BitmapSource? Icon()
    {
        if (!Icons.TryGetValue(AppId, out var icon)) Icons[AppId] = icon = NotificationApps.LoadIcon(AppId, 96);
        return icon;
    }

    public void Open()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + AppId) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Trace.TraceWarning("Could not open {0}: {1}", Name, ex.Message);
        }
    }
}

/// <summary>
/// Claude, Codex, Gemini and OpenCode usage shared by every AI widget on the desktop: read once, every two minutes, only while one of
/// them is visible.
/// </summary>
internal sealed class AiUsageHub
{
    public static AiUsageHub Instance { get; } = new();

    private readonly AiUsageService _service = new();
    private readonly List<Action> _listeners = [];
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(2) };
    private bool _loading;

    public IReadOnlyList<ProviderUsage> Usage { get; private set; } = [];
    public bool Loaded { get; private set; }

    private AiUsageHub()
    {
        Usage = _service.Cached;
        _timer.Tick += (_, _) => _ = RefreshAsync();
    }

    public ProviderUsage? For(string provider) => Usage.FirstOrDefault(u => u.Provider == provider);

    public void Subscribe(Action listener)
    {
        _listeners.Add(listener);
        if (_listeners.Count == 1)
        {
            _timer.Start();
            _ = RefreshAsync();
        }
        listener();
    }

    public void Unsubscribe(Action listener)
    {
        _listeners.Remove(listener);
        if (_listeners.Count == 0) _timer.Stop();
    }

    private async Task RefreshAsync()
    {
        if (_loading) return;
        _loading = true;
        try { Usage = await _service.FetchAsync(false, CancellationToken.None); }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.IO.IOException)
        {
            Trace.TraceWarning("AI usage refresh failed: {0}", ex.Message);
        }
        finally
        {
            _loading = false;
            Loaded = true;
        }
        foreach (var listener in _listeners.ToList()) listener();
    }
}

/// <summary>
/// One AI assistant on the desktop with its own icon and colour. Claude and Codex show how much of their usage limits
/// is spent and when it resets (Gemini through Antigravity, OpenCode Go through its console). The icon opens the app.
/// </summary>
internal sealed class AiAssistantWidget(AiAssistant assistant) : DeskWidget
{
    private readonly List<(TextBlock Label, TextBlock Value, Border Fill, Grid Bar, TextBlock Reset)> _rows = [];
    private TextBlock? _headline, _status;
    private RingGauge? _ring;
    private bool _statusOnlyWhenEmpty;

    private bool HasUsage => assistant.UsageProvider is not null;

    public override FrameworkElement Build()
    {
        _rows.Clear();
        _headline = null;
        _status = null;
        _ring = null;
        _statusOnlyWhenEmpty = false;
        return HasUsage ? BuildUsage() : BuildLauncher();
    }

    private FrameworkElement AppIcon(double size)
    {
        FrameworkElement picture;
        if (assistant.Icon() is { } icon)
        {
            var image = new Image { Source = icon, Width = assistant.Plate ? size * 0.72 : size, Height = assistant.Plate ? size * 0.72 : size };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            picture = assistant.Plate
                ? new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.26), Background = Brushes.White, Child = image }
                : image;
        }
        else
        {
            var letter = T(assistant.Name[..1], size * 0.5, FontWeights.Bold, Brushes.White);
            letter.HorizontalAlignment = HorizontalAlignment.Center;
            letter.VerticalAlignment = VerticalAlignment.Center;
            picture = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.28), Background = assistant.Gradient, Child = letter };
        }
        var button = new DeskButton(picture, assistant.Open, double.NaN, null, size * 0.28) { ToolTip = Desk.L($"{assistant.Name} uygulamasını aç", $"Open {assistant.Name}") };
        button.VerticalAlignment = VerticalAlignment.Center;
        return button;
    }

    private TextBlock Name(double size) => T(assistant.Name, size, FontWeights.SemiBold);

    // ---- Claude and Codex: usage limits --------------------------------------------------------

    private FrameworkElement BuildUsage()
    {
        _status = T("", 11, FontWeights.SemiBold, Secondary);
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        switch (Size)
        {
            case DeskSize.Mini:
            {
                _ring = new RingGauge { Width = 50, Height = 50, Thickness = 5, Stroke = assistant.Accent };
                var host = new Grid();
                host.Children.Add(_ring);
                var icon = AppIcon(26);
                icon.HorizontalAlignment = HorizontalAlignment.Center;
                host.Children.Add(icon);
                _headline = Digits("", 20, FontWeights.Bold);
                // No room for a status line: the reason shows only while there is no number to show.
                _status.FontSize = 10.5;
                _statusOnlyWhenEmpty = true;
                var grid = Grid(columns: "Auto,*");
                grid.Children.Add(host);
                grid.Children.Add(At(Margin(Column(_headline, Name(12), _status), 12), 0, 1));
                return grid;
            }
            case DeskSize.Small:
            {
                var header = Grid(columns: "Auto,*");
                header.Children.Add(AppIcon(30));
                header.Children.Add(At(Margin(Column(Name(14), _status), 9), 0, 1));
                var grid = Grid("Auto,*");
                grid.Children.Add(header);
                var rows = Column(UsageRow(), Margin(UsageRow(), 0, 10));
                rows.VerticalAlignment = VerticalAlignment.Bottom;
                grid.Children.Add(At(rows, 1));
                return grid;
            }
            case DeskSize.Strip:
            {
                // A thin strip: the icon, then the two limits side by side.
                var grid = Grid(columns: "Auto,*,14,*");
                var icon = AppIcon(38);
                icon.ToolTip = assistant.Name;
                grid.Children.Add(icon);
                grid.Children.Add(At(Margin(UsageRow(), 14), 0, 1));
                grid.Children.Add(At(UsageRow(), 0, 3));
                return grid;
            }
            case DeskSize.Tall:
            {
                // Upright: icon and name, the first limit as a large ring, then every limit with its reset time.
                var header = Grid(columns: "Auto,*");
                header.Children.Add(AppIcon(30));
                header.Children.Add(At(Margin(Column(Name(14), _status), 9), 0, 1));
                _ring = new RingGauge { Width = 104, Height = 104, Thickness = 10, Stroke = assistant.Accent };
                _headline = Digits("", 26, FontWeights.Bold);
                _headline.HorizontalAlignment = HorizontalAlignment.Center;
                var ring = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                ring.Children.Add(_ring);
                ring.Children.Add(_headline);
                var rows = Column(UsageRow());
                for (var extra = 1; extra < assistant.Windows; extra++) rows.Children.Add(Margin(UsageRow(), 0, 9));
                var grid = Grid("Auto,*,Auto");
                grid.Children.Add(header);
                grid.Children.Add(At(ring, 1));
                grid.Children.Add(At(rows, 2));
                return grid;
            }
            default:
            {
                var grid = Grid("Auto,*");
                if (assistant.Windows > 2)
                {
                    // Three limits: a one-line header (icon, name, plan) leaves room for the third row.
                    var line = Grid(columns: "Auto,Auto,*");
                    line.Children.Add(AppIcon(26));
                    var name = Name(14);
                    name.VerticalAlignment = VerticalAlignment.Center;
                    line.Children.Add(At(Margin(name, 9), 0, 1));
                    _status.VerticalAlignment = VerticalAlignment.Center;
                    line.Children.Add(At(Margin(_status, 8), 0, 2));
                    grid.Children.Add(line);
                }
                else
                {
                    var header = Grid(columns: "Auto,*");
                    header.Children.Add(AppIcon(34));
                    header.Children.Add(At(Margin(Column(Name(15), _status), 10), 0, 1));
                    grid.Children.Add(header);
                }
                var rows = Column(UsageRow(), Margin(UsageRow(), 0, assistant.Windows > 2 ? 7 : 12));
                for (var extra = 2; extra < assistant.Windows; extra++) rows.Children.Add(Margin(UsageRow(), 0, 7));
                rows.VerticalAlignment = VerticalAlignment.Bottom;
                grid.Children.Add(At(rows, 1));
                return grid;
            }
        }
    }

    private FrameworkElement UsageRow()
    {
        var label = T("", 11.5, FontWeights.SemiBold, Secondary);
        var reset = T("", 10.5, brush: Tertiary);
        reset.TextTrimming = TextTrimming.CharacterEllipsis;
        reset.Margin = new Thickness(6, 0, 6, 0);
        reset.VerticalAlignment = VerticalAlignment.Center;
        var value = Digits("", 12.5, FontWeights.Bold);
        var fill = new Border { CornerRadius = new CornerRadius(3), Background = assistant.Gradient, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        var bar = new Grid { Height = 6, Margin = new Thickness(0, 5, 0, 0) };
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = Track });
        bar.Children.Add(fill);
        bar.SizeChanged += (_, _) => Tick();
        var top = Grid(columns: "Auto,*,Auto");
        top.Children.Add(label);
        top.Children.Add(At(reset, 0, 1));
        top.Children.Add(At(value, 0, 2));
        _rows.Add((label, value, fill, bar, reset));
        return Column(top, bar);
    }

    protected override void OnAttach()
    {
        if (HasUsage) AiUsageHub.Instance.Subscribe(Tick);
    }

    protected override void OnDetach()
    {
        if (HasUsage) AiUsageHub.Instance.Unsubscribe(Tick);
    }

    public override TimeSpan? Interval => HasUsage ? TimeSpan.FromMinutes(1) : null; // "2 sa 10 dk sonra" counts down

    public override void Tick()
    {
        if (!HasUsage) return;
        var hub = AiUsageHub.Instance;
        var usage = hub.For(assistant.UsageProvider!);
        var windows = usage?.Windows.Take(Math.Max(2, _rows.Count)).ToList() ?? [];
        var status = usage switch
        {
            null when !hub.Loaded => Desk.L("Okunuyor…", "Loading…"),
            null => Desk.L("Kullanım bilgisi bulunamadı", "No usage found"),
            { Error: { } error } => error,
            { Plan: { Length: > 0 } plan } => plan,
            _ => Desk.L("Kullanım sınırları", "Usage limits")
        };
        if (_status is not null)
        {
            _status.Text = status;
            _status.ToolTip = status;
            _status.Visibility = _statusOnlyWhenEmpty && windows.Count > 0 && usage?.Error is null ? Visibility.Collapsed : Visibility.Visible;
        }
        if (_headline is not null) _headline.Text = windows.Count > 0 ? Percent(windows[0].UsedPercent) : "—";
        if (_ring is not null) _ring.Value = windows.Count > 0 ? windows[0].UsedPercent : 0;
        for (var i = 0; i < _rows.Count; i++)
        {
            var (label, value, fill, bar, reset) = _rows[i];
            var window = i < windows.Count ? windows[i] : null;
            label.Text = window?.Label ?? (i == 0 ? UiText.Get("AiSession") : i == 1 ? UiText.Get("AiWeekly") : UiText.Get("AiMonthly"));
            value.Text = window is null ? "—" : Percent(window.UsedPercent);
            value.Foreground = window is { UsedPercent: >= 85 } ? Red : window is { UsedPercent: >= 70 } ? Orange : Text;
            var width = bar.ActualWidth > 0 ? bar.ActualWidth : 0;
            fill.Width = window is null || width <= 0 ? 0 : Math.Max(6, width * Math.Clamp(window.UsedPercent, 0, 100) / 100);
            fill.Background = window is { UsedPercent: >= 85 } ? Red : assistant.Gradient;
            reset.Text = window?.ResetsAt is { } at ? "↻ " + QiDock.Widgets.AiLimitsWidget.Remaining(at - DateTime.Now) : "";
            reset.ToolTip = window?.ResetsAt is { } when ? UiText.Format("AiResetsShort", QiDock.Widgets.AiLimitsWidget.Remaining(when - DateTime.Now)) : null;
        }
    }

    // ---- Gemini: the app ------------------------------------------------------------------------

    private FrameworkElement BuildLauncher()
    {
        var open = new DeskButton(Margin(T(Desk.L("Sohbet başlat", "Start a chat"), 12, FontWeights.SemiBold, Brushes.White), 14, 0, 14), assistant.Open, double.NaN, assistant.Gradient, 14)
        {
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        ((FrameworkElement)open.Child).VerticalAlignment = VerticalAlignment.Center;
        var subtitle = T(Desk.L("Google’ın yapay zekâsı", "Google’s AI assistant"), 11, FontWeights.SemiBold, Secondary);
        switch (Size)
        {
            case DeskSize.Mini:
            {
                var grid = Grid(columns: "Auto,*");
                grid.Children.Add(AppIcon(40));
                grid.Children.Add(At(Margin(Column(Name(15), T(Desk.L("Aç", "Open"), 11.5, FontWeights.SemiBold, Secondary)), 12), 0, 1));
                return grid;
            }
            case DeskSize.Small:
            {
                var grid = Grid("*,Auto");
                var top = Column(AppIcon(52), Margin(Name(15), 0, 8), subtitle);
                top.HorizontalAlignment = HorizontalAlignment.Left;
                top.VerticalAlignment = VerticalAlignment.Top;
                grid.Children.Add(top);
                grid.Children.Add(At(open, 1));
                return grid;
            }
            case DeskSize.Strip:
            {
                var grid = Grid(columns: "Auto,*,Auto");
                grid.Children.Add(AppIcon(42));
                grid.Children.Add(At(Margin(Column(Name(15), Margin(subtitle, 0, 1)), 12), 0, 1));
                open.VerticalAlignment = VerticalAlignment.Center;
                grid.Children.Add(At(open, 0, 2));
                return grid;
            }
            default:
            {
                var grid = Grid(columns: "Auto,*");
                grid.Children.Add(AppIcon(64));
                grid.Children.Add(At(Margin(Column(Name(16), Margin(subtitle, 0, 2), Margin(open, 0, 10)), 16), 0, 1));
                return grid;
            }
        }
    }
}
