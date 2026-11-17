using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using QiDock.Models;
using QiDock.Services;
using QiDock.Services.Widgets;

namespace QiDock.Widgets;

/// <summary>
/// Claude and ChatGPT usage limits: a compact ring per provider in the dock. Clicking a ring opens that provider's
/// card (usage, this week's curve and pace); nothing shows on hover. Claude's card also lists this PC's recent
/// Claude Code sessions (what each is doing, or that it finished), read only while that card is open.
/// </summary>
public sealed class AiLimitsWidget : DockWidget
{
    private static readonly Brush WarningBrush = Ui.Solid("#FFFFB224");
    private static readonly Brush DangerBrush = Ui.Solid("#FFFF5F57");
    private static readonly Brush LineBrush = Ui.Solid("#FF32D74B");

    private readonly Border _tile;
    private string? _shown;
    private readonly ClaudeActivityWatcher _claudeWatch = new(Dispatcher.CurrentDispatcher);
    private readonly DispatcherTimer _sessionTick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
    private List<ClaudeActivity> _claudeSessions = [];
    private static readonly TimeSpan SessionWindow = TimeSpan.FromMinutes(30);
    private readonly StackPanel _cards;
    private readonly WidgetPopup _popup;
    private readonly DispatcherTimer _timer;
    private readonly AiUsageService _service = new();
    private readonly AiUsageHistory _history = new();
    private IReadOnlyList<ProviderUsage> _usage = [];
    private CancellationTokenSource? _load;
    private string _layoutSignature = "";

    public AiLimitsWidget()
    {
        _cards = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        // A plain tile: each ring inside is its own button.
        _tile = new Border { Child = _cards, Padding = new Thickness(3, 0, 3, 0), Margin = new Thickness(3, 0, 3, 0), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        _tile.SetResourceReference(Border.BackgroundProperty, "WidgetTileFill");
        _tile.SetResourceReference(Border.BorderBrushProperty, "WidgetTileStroke");
        _tile.SetResourceReference(FrameworkElement.HeightProperty, "WidgetTileHeight");
        _tile.CornerRadius = new CornerRadius(11);
        _popup = new WidgetPopup(_tile);
        _popup.Closed += (_, _) => StopSessions();
        _claudeWatch.Changed += () => _ = LoadSessionsAsync();
        _sessionTick.Tick += (_, _) => _ = LoadSessionsAsync(); // "3 dk önce" and sessions going quiet
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += (_, _) => _ = RefreshAsync(false);
        UiText.Current.PropertyChanged += (_, _) => Render();
        Render();
    }

    public override string Id => WidgetIds.AiLimits;
    public override FrameworkElement View => _tile;
    public override bool IsPopupOpen => _popup.IsOpen;

    public override void Start()
    {
        _timer.Start();
        _ = RefreshAsync(false);
    }

    public override void Stop()
    {
        _timer.Stop();
        _load?.Cancel();
        _popup.Close();
        StopSessions();
    }

    public override void ClosePopup() => _popup.Close();

    private async Task RefreshAsync(bool force)
    {
        _load?.Cancel();
        var load = _load = new CancellationTokenSource();
        try
        {
            _usage = await _service.FetchAsync(force, load.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        foreach (var provider in _usage.Where(p => p.Error is null))
        foreach (var window in provider.Windows.Where(w => w.Key is not null))
            _history.Record($"{provider.Provider}:{window.Key}", window.UsedPercent, provider.UpdatedAt);

        Render();
        if (_popup.IsOpen) BuildPopup();
    }

    // ---- Dock tile ---------------------------------------------------------------------------

    private void Render()
    {
        _cards.Children.Clear();
        if (_usage.Count == 0)
        {
            _cards.Children.Add(Gauge(AiUsageService.ClaudeId, null));
        }

        // Compact: one ring per provider (the logo inside, the busier window's use around it) and its percent.
        foreach (var (provider, index) in _usage.Select((p, i) => (p, i)))
        {
            var used = provider.Error is null && provider.Windows.Count > 0 ? provider.Windows.Max(w => w.UsedPercent) : (double?)null;
            var gauge = Gauge(provider.Provider, used);
            gauge.Margin = new Thickness(index == 0 ? 0 : 2, 0, 0, 0);
            _cards.Children.Add(gauge);
        }
        var signature = string.Join("|", _usage.Select(p => $"{p.Provider}{p.Windows.Count}{p.Error is null}"));
        if (signature != _layoutSignature)
        {
            _layoutSignature = signature;
            RaiseLayoutChanged();
        }
    }

    private static CultureInfo Culture => CultureInfo.GetCultureInfo(UiText.Get("CultureName"));

    private static string Percent(double value) => UiText.Format("OpacityPercent", Math.Round(value).ToString("0", CultureInfo.InvariantCulture));

    /// <summary>"2sa 14dk" for today's resets, "19 Çar" for later ones.</summary>
    private static string ShortReset(DateTime at)
    {
        var span = at - DateTime.Now;
        if (span <= TimeSpan.Zero) return UiText.Get("AiSoon");
        if (span.TotalHours < 24)
            return span.TotalHours >= 1 ? UiText.Format("AiShortHM", (int)span.TotalHours, span.Minutes) : UiText.Format("AiShortM", Math.Max(1, span.Minutes));
        return at.ToString("d ddd", Culture);
    }

    internal static string Remaining(TimeSpan span)
    {
        if (span <= TimeSpan.Zero) return UiText.Get("AiSoon");
        if (span.TotalDays >= 1) return UiText.Format("AiDaysHours", (int)span.TotalDays, span.Hours);
        if (span.TotalHours >= 1) return UiText.Format("AiHoursMinutes", (int)span.TotalHours, span.Minutes);
        return UiText.Format("AiMinutes", Math.Max(1, span.Minutes));
    }

    private static string When(DateTime at) =>
        at.Date == DateTime.Today ? at.ToString("HH:mm", Culture) : at.ToString("ddd HH:mm", Culture);

    // ---- Pace --------------------------------------------------------------------------------

    /// <summary>At the current average rate since the window started: when 100% is reached, or the level at reset.</summary>
    private static (DateTime? ExhaustedAt, double AtReset)? Pace(UsageWindow window)
    {
        if (window.StartsAt is not { } start || window.ResetsAt is not { } reset) return null;
        var elapsed = DateTime.Now - start;
        if (elapsed < TimeSpan.FromMinutes(20) || window.UsedPercent <= 0) return null;
        var perHour = window.UsedPercent / elapsed.TotalHours;
        var atReset = window.UsedPercent + perHour * (reset - DateTime.Now).TotalHours;
        if (atReset < 100) return (null, atReset);
        var exhausted = DateTime.Now + TimeSpan.FromHours((100 - window.UsedPercent) / perHour);
        return (exhausted, 100);
    }

    // ---- Popup -------------------------------------------------------------------------------

    private void TogglePopup()
    {
        if (!_popup.IsOpen)
        {
            BuildPopup();
            _ = RefreshAsync(true);
        }
        _popup.Toggle();
    }

    /// <summary>A ring was clicked: its provider's card opens (another provider's card on show switches over).</summary>
    private void ToggleProvider(string provider)
    {
        if (_popup.IsOpen && _shown != provider)
        {
            _shown = provider;
            FollowSessions();
            BuildPopup();
            return;
        }
        _shown = provider;
        if (!_popup.IsOpen) FollowSessions();
        TogglePopup();
    }

    // ---- Claude Code sessions (only while Claude's card is open) ------------------------------

    private bool ShowSessions => _shown == AiUsageService.ClaudeId
        && (Application.Current as App)?.Preferences.Current.ClaudeActivity != false;

    private void FollowSessions()
    {
        if (!ShowSessions)
        {
            StopSessions();
            return;
        }
        _claudeWatch.Start();
        _sessionTick.Start();
        _ = LoadSessionsAsync();
    }

    private void StopSessions()
    {
        _claudeWatch.Stop();
        _sessionTick.Stop();
    }

    private async Task LoadSessionsAsync()
    {
        var sessions = await ClaudeActivityWatcher.RecentAsync(SessionWindow);
        _claudeSessions = sessions;
        if (_popup.IsOpen && ShowSessions) BuildPopup();
    }

    /// <summary>Claude Code on this PC: each recent session with what it is doing, or that it finished.</summary>
    private UIElement SessionsCard()
    {
        var card = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 8), Margin = new Thickness(0, 0, 0, 10) };
        card.SetResourceReference(Border.BackgroundProperty, "WidgetTileFill");
        var content = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        title.Children.Add(Logo(AiUsageService.ClaudeId, 22));
        var name = Ui.Text(UiText.Get("ClaudeSessionsTitle"), 13.5, FontWeights.SemiBold);
        name.Margin = new Thickness(8, 0, 0, 0);
        title.Children.Add(name);
        content.Children.Add(title);
        content.Children.Add(Caption(UiText.Get("ClaudeSessionsCaption")));
        if (_claudeSessions.Count == 0)
        {
            var none = Ui.Text(UiText.Get("ClaudeSessionsNone"), 11.5, brushKey: "TextSecondary");
            none.TextWrapping = TextWrapping.Wrap;
            none.TextTrimming = TextTrimming.None;
            none.Margin = new Thickness(0, 0, 0, 6);
            content.Children.Add(none);
        }
        foreach (var session in _claudeSessions) content.Children.Add(SessionRow(session));
        card.Child = content;
        return card;
    }

    private static UIElement SessionRow(ClaudeActivity session)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        // A green tick when finished, an orange dot while working, a grey one when it has gone quiet.
        FrameworkElement mark = session.Done
            ? Ui.Icon("IconCheck", 13, Ui.Solid("#FF30D158"), stroke: 2.6)
            : new Ellipse { Width = 8, Height = 8, Fill = session.Working ? Ui.Solid("#FFD97757") : Ui.Solid("#FF8E8E93") };
        var markHost = new Grid { Width = 16, Margin = new Thickness(0, 3, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        mark.VerticalAlignment = VerticalAlignment.Center;
        markHost.Children.Add(mark);
        DockPanel.SetDock(markHost, Dock.Left);
        row.Children.Add(markHost);

        var minutes = (int)Math.Floor((DateTime.UtcNow - session.UpdatedAt).TotalMinutes);
        var ago = Ui.Text(minutes < 1 ? UiText.Get("ClaudeAgoNow") : UiText.Format("ClaudeAgoMinutes", minutes), 10.5, brushKey: "TextSecondary");
        ago.VerticalAlignment = VerticalAlignment.Top;
        ago.Margin = new Thickness(8, 1, 0, 0);
        DockPanel.SetDock(ago, Dock.Right);
        row.Children.Add(ago);

        var text = new StackPanel();
        var project = Ui.Text(session.Project, 12.5, FontWeights.SemiBold);
        project.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(project);
        var detail = Ui.Text(session.Done || session.Working ? session.Detail : UiText.Get("ClaudeIdle"), 11, brushKey: "TextSecondary");
        detail.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(detail);
        row.Children.Add(text);
        return row;
    }

    private const double PopupWidth = 360;
    private const double ContentWidth = PopupWidth - 32 - 28;

    private void BuildPopup()
    {
        var root = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var close = Ui.IconButton(Ui.Icon("IconClose", 12, brushKey: "TextSecondary"), (_, _) => _popup.Close(), UiText.Get("Close"));
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(Ui.Text(_usage.FirstOrDefault(p => p.Provider == _shown)?.DisplayName ?? UiText.Get("AiLimits"), 14, FontWeights.SemiBold));
        root.Children.Add(header);

        if (_usage.Count == 0)
        {
            var help = Ui.Text(UiText.Get("AiNoDataHelp"), 11.5, brushKey: "TextSecondary");
            help.TextWrapping = TextWrapping.Wrap;
            help.TextTrimming = TextTrimming.None;
            root.Children.Add(help);
        }

        // Only the clicked provider (all of them if it is not among the data).
        foreach (var provider in _shown is null ? _usage : _usage.Where(p => p.Provider == _shown)) root.Children.Add(ProviderCard(provider));
        if (ShowSessions) root.Children.Add(SessionsCard());

        // Footer: where the numbers come from, and a refresh button.
        var footer = new DockPanel { Margin = new Thickness(2, 4, 0, 0) };
        var refresh = new Button { Content = UiText.Get("AiRefresh"), VerticalAlignment = VerticalAlignment.Center };
        refresh.SetResourceReference(FrameworkElement.StyleProperty, "WidgetPillButton");
        refresh.Click += (_, _) => _ = RefreshAsync(true);
        DockPanel.SetDock(refresh, Dock.Right);
        footer.Children.Add(refresh);
        var note = Ui.Text(UiText.Get("AiFooterNote"), 10, brushKey: "TextSecondary");
        note.TextWrapping = TextWrapping.Wrap;
        note.TextTrimming = TextTrimming.None;
        note.Margin = new Thickness(0, 0, 10, 0);
        footer.Children.Add(note);
        root.Children.Add(footer);

        _popup.SetContent(root, PopupWidth);
    }

    private UIElement ProviderCard(ProviderUsage provider)
    {
        var card = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 10) };
        card.SetResourceReference(Border.BackgroundProperty, "WidgetTileFill");
        var content = new StackPanel();

        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        title.Children.Add(Logo(provider.Provider, 22));
        var name = Ui.Text(provider.DisplayName, 13.5, FontWeights.SemiBold);
        name.Margin = new Thickness(8, 0, 8, 0);
        title.Children.Add(name);
        if (provider.Plan is { } plan)
        {
            var badge = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 1, 7, 2), VerticalAlignment = VerticalAlignment.Center };
            badge.SetResourceReference(Border.BackgroundProperty, "SurfaceHover");
            badge.Child = Ui.Text(plan, 10.5, FontWeights.SemiBold, "TextSecondary");
            title.Children.Add(badge);
        }
        content.Children.Add(title);

        if (provider.Windows.Count > 0) content.Children.Add(Caption(UiText.Get("AiUsageCaption")));
        foreach (var window in provider.Windows) content.Children.Add(UsageRow(window));

        if (provider.Error is { } error)
        {
            var errorText = Ui.Text(error, 11, brushKey: "DangerText");
            errorText.TextWrapping = TextWrapping.Wrap;
            errorText.TextTrimming = TextTrimming.None;
            errorText.Margin = new Thickness(0, 2, 0, 6);
            content.Children.Add(errorText);
        }

        // The longest window that has a start and end gets the curve (weekly for Claude, monthly/weekly for ChatGPT).
        var charted = provider.Windows.Where(w => w.StartsAt is not null && w.Length >= TimeSpan.FromDays(1))
            .OrderByDescending(w => w.UsedPercent).FirstOrDefault();
        if (charted is not null)
        {
            content.Children.Add(Caption(charted.Length <= TimeSpan.FromDays(7) ? UiText.Get("AiThisWeek") : UiText.Get("AiThisPeriod")));
            content.Children.Add(Chart(provider, charted));
        }

        var updated = Ui.Text(UiText.Format("AiUpdated", When(provider.UpdatedAt),
            provider.Provider == AiUsageService.CodexId ? UiText.Get("AiSourceCodex") : UiText.Get("AiSourceClaude")), 10, brushKey: "TextSecondary");
        updated.Opacity = 0.8;
        updated.Margin = new Thickness(0, 8, 0, 0);
        content.Children.Add(updated);
        card.Child = content;
        return card;
    }

    private static TextBlock Caption(string text)
    {
        var caption = Ui.Text(text.ToUpper(Culture), 9.5, FontWeights.SemiBold, "TextSecondary");
        caption.Margin = new Thickness(0, 2, 0, 6);
        caption.Opacity = 0.85;
        return caption;
    }

    private static UIElement UsageRow(UsageWindow window)
    {
        var block = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var top = new DockPanel();
        var percent = Ui.Text(Percent(window.UsedPercent), 12, FontWeights.SemiBold);
        percent.Width = 38;
        percent.TextAlignment = TextAlignment.Right;
        DockPanel.SetDock(percent, Dock.Right);
        top.Children.Add(percent);

        TextBlock status;
        if (Pace(window) is { ExhaustedAt: { } exhausted } && window.ResetsAt is { } reset && exhausted < reset)
            status = Ui.Text(UiText.Format("AiOutBy", When(exhausted)), 11, FontWeights.SemiBold, brush: WarningBrush);
        else if (window.ResetsAt is { } resetsAt)
            status = Ui.Text(UiText.Format("AiResetsShort", Remaining(resetsAt - DateTime.Now)), 11, brushKey: "TextSecondary");
        else
            status = Ui.Text(window.UsedPercent <= 0 ? UiText.Get("AiIdle") : UiText.Get("AiNoReset"), 11, brushKey: "TextSecondary");
        if (window.ResetsAt is { } r) status.ToolTip = r.ToString("f", Culture);
        DockPanel.SetDock(status, Dock.Right);
        top.Children.Add(status);
        top.Children.Add(Ui.Text(window.Label, 12, FontWeights.SemiBold));
        block.Children.Add(top);

        var bar = Ui.Bar(window.UsedPercent, ContentWidth, 5, Ui.UsageBrush(window.UsedPercent));
        bar.Margin = new Thickness(0, 5, 0, 0);
        block.Children.Add(bar);
        return block;
    }

    /// <summary>Usage since the window started (green), and where the current pace leads (dashed).</summary>
    private UIElement Chart(ProviderUsage provider, UsageWindow window)
    {
        const double height = 50;
        var width = ContentWidth;
        var start = window.StartsAt!.Value;
        var reset = window.ResetsAt!.Value;
        var span = (reset - start).TotalSeconds;
        double X(DateTime t) => Math.Clamp((t - start).TotalSeconds / span, 0, 1) * width;
        double Y(double p) => height - Math.Clamp(p, 0, 100) / 100 * (height - 4) - 1;

        var now = DateTime.Now;
        var points = new List<Point> { new(X(start), Y(0)) };
        foreach (var sample in _history.Since($"{provider.Provider}:{window.Key}", start).Where(s => s.At <= now))
            points.Add(new Point(X(sample.At), Y(sample.Percent)));
        points.Add(new Point(X(now), Y(window.UsedPercent)));

        var canvas = new Canvas { Width = width, Height = height, ClipToBounds = false };
        var panel = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(0, 6, 0, 0) };

        // Faint guides at 50% and 100%.
        foreach (var level in new[] { 50.0, 100.0 })
        {
            var guide = new Line { X1 = 0, X2 = width, Y1 = Y(level), Y2 = Y(level), StrokeThickness = 1, StrokeDashArray = [2, 3] };
            guide.SetResourceReference(Shape.StrokeProperty, "WidgetTrack");
            canvas.Children.Add(guide);
        }

        var area = new Polygon { Fill = new LinearGradientBrush(Color.FromArgb(0x66, 0x32, 0xD7, 0x4B), Color.FromArgb(0x08, 0x32, 0xD7, 0x4B), 90) };
        foreach (var point in points) area.Points.Add(point);
        area.Points.Add(new Point(points[^1].X, height));
        area.Points.Add(new Point(points[0].X, height));
        canvas.Children.Add(area);

        var line = new Polyline { Stroke = LineBrush, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
        foreach (var point in points) line.Points.Add(point);
        canvas.Children.Add(line);

        string? paceText = null;
        Brush paceBrush = Brushes.Gray;
        if (Pace(window) is { } pace)
        {
            var exhausts = pace.ExhaustedAt is { } at && at < reset;
            var end = exhausts ? new Point(X(pace.ExhaustedAt!.Value), Y(100)) : new Point(X(reset), Y(pace.AtReset));
            paceBrush = exhausts ? DangerBrush : Ui.Solid("#998E8E93");
            canvas.Children.Add(new Line
            {
                X1 = points[^1].X, Y1 = points[^1].Y, X2 = end.X, Y2 = end.Y,
                Stroke = paceBrush, StrokeThickness = 1.6, StrokeDashArray = [3, 2.5]
            });
            paceText = exhausts ? UiText.Format("AiOutBy", When(pace.ExhaustedAt!.Value))
                : UiText.Format("AiPaceOk", Math.Round(pace.AtReset).ToString("0", CultureInfo.InvariantCulture));
            if (exhausts) paceBrush = WarningBrush;
        }

        var dot = new Ellipse { Width = 7, Height = 7, Fill = LineBrush, Stroke = Brushes.White, StrokeThickness = 1.2 };
        Canvas.SetLeft(dot, points[^1].X - 3.5);
        Canvas.SetTop(dot, points[^1].Y - 3.5);
        canvas.Children.Add(dot);
        panel.Child = canvas;

        var stack = new StackPanel();
        stack.Children.Add(panel);
        var labels = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };
        if (paceText is not null)
        {
            var paceLabel = Ui.Text(paceText, 10, FontWeights.SemiBold, brush: paceBrush);
            DockPanel.SetDock(paceLabel, Dock.Right);
            labels.Children.Add(paceLabel);
        }
        labels.Children.Add(Ui.Text(start.ToString("ddd HH:mm", Culture), 10, brushKey: "TextSecondary"));
        stack.Children.Add(labels);
        return stack;
    }

    /// <summary>The provider's logo inside a thin usage ring, with the percent beside it ("—" without data); a button of its own.</summary>
    private FrameworkElement Gauge(string provider, double? used)
    {
        var ring = new QiDock.DesktopWidgets.RingGauge
        {
            Width = 32, Height = 32, Thickness = 3,
            TrackBrush = Ui.Solid("#47808080"),
            Stroke = used is { } u ? Ui.UsageBrush(u) : Ui.Solid("#55FFFFFF"),
            Value = used ?? 0
        };
        var logo = Logo(provider, 18);
        logo.HorizontalAlignment = HorizontalAlignment.Center;
        logo.VerticalAlignment = VerticalAlignment.Center;
        var face = new Grid { Width = 32, Height = 32 };
        face.Children.Add(ring);
        face.Children.Add(logo);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(face);
        var text = Ui.Text(used is { } v ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture) + "%" : "—", 11.5, FontWeights.SemiBold,
            brush: used is { } w ? Ui.UsageBrush(w) : null);
        text.Margin = new Thickness(5, 0, 0, 0);
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text);
        var button = new Button { Content = row, Width = double.NaN, Height = 38, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        button.SetResourceReference(FrameworkElement.StyleProperty, "WidgetIconButton");
        // The style fixes the width; the ring and its percent set it here, with room around them for the hover face.
        button.Width = double.NaN;
        row.Margin = new Thickness(4, 0, 6, 0);
        button.Click += (_, _) => ToggleProvider(provider);
        return button;
    }

    /// <summary>Simple brand-neutral marks: an orange burst for Claude, a ringed disc for ChatGPT.</summary>

    internal static FrameworkElement Logo(string provider, double size)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        if (provider == AiUsageService.CodexId)
        {
            canvas.Children.Add(new Rectangle { Width = 24, Height = 24, RadiusX = 6, RadiusY = 6, Fill = Ui.Solid("#FFF4F4F4") });
            var ring = new Ellipse { Width = 14, Height = 14, Stroke = Ui.Solid("#FF111111"), StrokeThickness = 2.2 };
            Canvas.SetLeft(ring, 5);
            Canvas.SetTop(ring, 5);
            canvas.Children.Add(ring);
            var dot = new Ellipse { Width = 4, Height = 4, Fill = Ui.Solid("#FF111111") };
            Canvas.SetLeft(dot, 10);
            Canvas.SetTop(dot, 10);
            canvas.Children.Add(dot);
        }
        else
        {
            canvas.Children.Add(new Rectangle { Width = 24, Height = 24, RadiusX = 6, RadiusY = 6, Fill = Ui.Solid("#FFD97757") });
            for (var i = 0; i < 6; i++)
            {
                var angle = i * Math.PI / 6;
                canvas.Children.Add(new Line
                {
                    X1 = 12 - Math.Cos(angle) * 7, Y1 = 12 - Math.Sin(angle) * 7,
                    X2 = 12 + Math.Cos(angle) * 7, Y2 = 12 + Math.Sin(angle) * 7,
                    Stroke = Brushes.White, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
                });
            }
        }
        return new Viewbox { Width = size, Height = size, Child = canvas, VerticalAlignment = VerticalAlignment.Center };
    }
}
