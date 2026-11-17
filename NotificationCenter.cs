using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using QiDock.Models;
using QiDock.Services;
using QiDock.Services.Notifications;
using QiDock.Widgets;

namespace QiDock;

/// <summary>
/// The notification center: a narrow column of cards along the right edge, only as tall as what
/// is in it. Several notifications of one app sit in a single stack (the newest card with the
/// others peeking out below); clicking the stack fans it out, "Daha az göster" folds it back.
/// Clicking a single card opens its app; the × on hover slides it away. Every change moves the
/// cards smoothly from where they were to where they go.
/// </summary>
public sealed class NotificationCenter : EdgePanel
{
    private const int ExpandedLimit = 8;
    private static NotificationCenter? _shared;

    /// <summary>The panel (created on first use, on the UI thread).</summary>
    public static NotificationCenter Shared => _shared ??= new NotificationCenter();
    public static bool IsCreated => _shared is not null;

    /// <summary>Supplied by the main window: current settings and saving a change.</summary>
    public static Func<WidgetSettings> Settings { get; set; } = () => new WidgetSettings();
    public static Action<Func<WidgetSettings, WidgetSettings>> Update { get; set; } = _ => { };

    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Origin> _origins = new(StringComparer.Ordinal);
    private readonly HashSet<string> _showAll = new(StringComparer.OrdinalIgnoreCase);
    // Cards sliding out: rebuilt invisible (still taking their place) until they are really gone.
    private readonly HashSet<string> _leaving = new(StringComparer.Ordinal);
    private bool _quiet;
    private bool _showOptions;

    private NotificationCenter() : base(330, "QiDock Notification Center")
    {
        HeightShare = 0.62;
        NotificationFeed.Shared.Changed += () =>
        {
            if (IsOpen && !_quiet) Refresh();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) return;
            _showOptions = false;
            _expanded.Clear();
            _showAll.Clear();
            _leaving.Clear();
        };
    }

    /// <summary>Marks everything read; nothing here shows unread state, so the cards are not rebuilt for it.</summary>
    protected override void OnOpened()
    {
        _quiet = true;
        try { NotificationFeed.Shared.MarkAllRead(); }
        finally { _quiet = false; }
    }

    /// <summary>Rebuilds with movement; cards that appear come out of the place recorded in <see cref="_origins"/>.</summary>
    private void Refresh()
    {
        Rebuild(key => _origins.TryGetValue(key, out var source) ? source : null);
        _origins.Clear();
    }

    // ---- Content ---------------------------------------------------------------------------

    protected override void Build()
    {
        var feed = NotificationFeed.Shared;
        Cards.Children.Clear();
        Header.Content = TitleRow(feed);
        Header.Visibility = Visibility.Visible;
        if (_showOptions) Cards.Children.Add(Keyed("options", Options()));

        if (!feed.Available || feed.Items.Count == 0)
        {
            Cards.Children.Add(Keyed("empty", EmptyCard(!feed.Available ? UiText.Get("NotificationsUnavailable")
                : UiText.Get(Settings().NotificationBanners ? "NotificationsEmpty" : "NotificationsEmptyQuiet"))));
            return;
        }
        var groups = feed.Items.GroupBy(n => n.AppId, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Max(n => n.Arrived));
        foreach (var group in groups)
        {
            var items = group.OrderByDescending(n => n.Arrived).ToList();
            if (items.Count == 1)
                Cards.Children.Add(Keyed(NoteKey(items[0]), Spaced(Card(items[0]), 6)));
            else if (_expanded.Contains(group.Key))
            {
                Cards.Children.Add(Keyed("gh:" + group.Key, GroupHeader(group.Key, items)));
                var limit = _showAll.Contains(group.Key) ? 60 : ExpandedLimit;
                foreach (var n in items.Take(limit)) Cards.Children.Add(Keyed(NoteKey(n), Spaced(Card(n), 6)));
                if (items.Count > limit)
                    Cards.Children.Add(Keyed("rest:" + group.Key, Spaced(MoreLine(group.Key, items.Count - limit))));
            }
            else
                Cards.Children.Add(Keyed("g:" + group.Key, Stack(group.Key, items)));
        }
    }

    private static string NoteKey(AppNotification n) => "n:" + n.Id;

    private FrameworkElement Keyed(string key, FrameworkElement element)
    {
        element.Tag = key;
        if (_leaving.Contains(key))
        {
            element.Opacity = 0;
            element.IsHitTestVisible = false;
        }
        return element;
    }

    /// <summary>A slim capsule: the title, "Temizle" and the gear.</summary>
    private FrameworkElement TitleRow(NotificationFeed feed)
    {
        var title = Ui.Text(UiText.Get("NotificationsTitle"), 14, FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new Thickness(4, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (feed.Items.Count > 0)
        {
            var clear = Capsule(Ui.Text(UiText.Get("NotificationsClear"), 11.5, FontWeights.SemiBold, "TextSecondary"), ClearEverything);
            clear.Height = 24;
            clear.Margin = new Thickness(0, 0, 4, 0);
            buttons.Children.Add(clear);
        }
        var gear = Capsule(Ui.Icon("IconSettings", 12, brushKey: _showOptions ? "TextPrimary" : "TextSecondary"), () =>
        {
            _showOptions = !_showOptions;
            Refresh();
        }, UiText.Get("NotificationsOptions"));
        gear.Height = 24;
        gear.Padding = new Thickness(0);
        gear.Width = 24;
        buttons.Children.Add(gear);
        var row = new DockPanel { Width = CardWidth, Margin = new Thickness(0, 0, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(title);
        return row;
    }

    private FrameworkElement Options()
    {
        var settings = Settings();
        var list = new StackPanel();
        list.Children.Add(SwitchRow(UiText.Get("NotificationsBanners"), UiText.Get("NotificationsBannersHint"), settings.NotificationBanners, s => s with { NotificationBanners = !s.NotificationBanners }));
        list.Children.Add(SwitchRow(UiText.Get("NotificationsHideWindows"), UiText.Get("NotificationsHideWindowsHint"), settings.HideWindowsToasts, s => s with { HideWindowsToasts = !s.HideWindowsToasts }, settings.NotificationBanners));
        list.Children.Add(SwitchRow(UiText.Get("NotificationsSound"), UiText.Get("NotificationsSoundHint"), settings.NotificationSound, s => s with { NotificationSound = !s.NotificationSound }));
        var link = new Button { Content = UiText.Get("NotificationsWindowsSettings"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-4, 2, 0, 0) };
        link.SetResourceReference(FrameworkElement.StyleProperty, "WidgetLinkButton");
        link.Click += (_, _) =>
        {
            Close(animated: true);
            Ui.Open("ms-settings:notifications");
        };
        list.Children.Add(link);
        return Spaced(Glass(list, new Thickness(12, 8, 10, 6)), 6);
    }

    private FrameworkElement SwitchRow(string title, string hint, bool on, Func<WidgetSettings, WidgetSettings> change, bool enabled = true)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        text.Children.Add(Ui.Text(title, 12.5, FontWeights.SemiBold));
        var detail = Ui.Text(hint, 11, brushKey: "TextSecondary");
        detail.TextWrapping = TextWrapping.Wrap;
        text.Children.Add(detail);
        var toggle = Switch(on, () =>
        {
            Update(change);
            // Let the knob finish its slide before the card is redrawn.
            Dispatcher.BeginInvoke(() =>
            {
                if (IsOpen) Refresh();
            }, System.Windows.Threading.DispatcherPriority.Background);
        });
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 5), IsEnabled = enabled, Opacity = enabled ? 1 : 0.45 };
        DockPanel.SetDock(toggle, Dock.Right);
        row.Children.Add(toggle);
        row.Children.Add(text);
        return row;
    }

    /// <summary>A phone-style switch whose knob glides across and whose track tints green.</summary>
    private FrameworkElement Switch(bool on, Action toggle)
    {
        var shift = new TranslateTransform(on ? 16 : 0, 0);
        var knob = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2), RenderTransform = shift, Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.3, Direction = 270 } };
        var green = (Color)ColorConverter.ConvertFromString("#FF34C759");
        var gray = (Color)ColorConverter.ConvertFromString("#FF5A5A5E");
        var fill = new SolidColorBrush(on ? green : gray);
        var track = new Border { Width = 38, Height = 22, CornerRadius = new CornerRadius(11), Background = fill, Child = knob, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
        var busy = false;
        track.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (busy) return;
            busy = true;
            var slide = new DoubleAnimation(on ? 0 : 16, TimeSpan.FromMilliseconds(220)) { EasingFunction = Motion.Out };
            slide.Completed += (_, _) => toggle();
            fill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(on ? gray : green, TimeSpan.FromMilliseconds(200)));
            shift.BeginAnimation(TranslateTransform.XProperty, slide);
        };
        return track;
    }

    private FrameworkElement EmptyCard(string text)
    {
        var icon = Ui.Icon("IconBell", 18, brushKey: "TextSecondary");
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 10, 0);
        var label = Ui.Text(text, 12.5, brushKey: "TextSecondary");
        label.TextWrapping = TextWrapping.Wrap;
        label.VerticalAlignment = VerticalAlignment.Center;
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(label);
        return Spaced(Glass(row, new Thickness(14, 12, 14, 12)), 0);
    }

    /// <summary>One notification: icon, app and time, title, two lines of text. × on hover.</summary>
    private Border Card(AppNotification notification, int more = 0, IReadOnlyList<AppNotification>? stack = null)
    {
        var icon = NotificationBanner.IconFor(notification, 28);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 10, 0);
        var app = Ui.Text(NotificationApps.Name(notification.AppId), 10.5, FontWeights.SemiBold, "TextSecondary");
        app.TextTrimming = TextTrimming.CharacterEllipsis;
        var time = Ui.Text(Ui.Ago(notification.Arrived.ToLocalTime()), 10.5, brushKey: "TextSecondary");
        time.Margin = new Thickness(8, 0, 0, 0);
        var header = new DockPanel();
        DockPanel.SetDock(time, Dock.Right);
        header.Children.Add(time);
        if (more > 0)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(6, 0, 6, 1),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = Ui.Text(UiText.Format("NotificationsMore", more), 10, FontWeights.SemiBold, "TextSecondary")
            };
            chip.SetResourceReference(Border.BackgroundProperty, "SurfaceHover");
            DockPanel.SetDock(chip, Dock.Right);
            header.Children.Add(chip);
        }
        header.Children.Add(app);
        var text = new StackPanel();
        text.Children.Add(header);
        var title = Ui.Text(notification.Title, 12.5, FontWeights.SemiBold);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(title);
        if (notification.Body.Length > 0)
        {
            var body = Ui.Text(notification.Body, 12);
            body.TextWrapping = TextWrapping.Wrap;
            body.TextTrimming = TextTrimming.CharacterEllipsis;
            body.MaxHeight = 12 * 1.35 * 2;
            text.Children.Add(body);
        }
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);

        var layout = new Grid();
        layout.Children.Add(row);
        Border? card = null;
        var dismiss = CloseBadge(() =>
        {
            if (card is null) return;
            // The × of a stack clears the whole stack.
            var outer = Outer(card);
            var key = (outer as FrameworkElement)?.Tag as string;
            if (key is null || !_leaving.Add(key)) return;
            SlideAway(outer, () =>
            {
                NotificationFeed.Shared.RemoveMany(stack ?? [notification]);
                _leaving.Remove(key);
            });
        });
        layout.Children.Add(dismiss);
        card = Glass(layout, new Thickness(10, 8, 12, 9));
        card.Cursor = Cursors.Hand;
        card.MouseEnter += (_, _) => dismiss.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
        card.MouseLeave += (_, _) => dismiss.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)));
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            Close(animated: true);
            NotificationApps.Activate(notification);
            NotificationFeed.Shared.Remove(notification);
        };
        Pressable(card);
        return card;
    }

    /// <summary>The top-level element that holds <paramref name="element"/> (what the panel moves).</summary>
    private UIElement Outer(FrameworkElement element)
    {
        DependencyObject current = element;
        while (VisualTreeHelper.GetParent(current) is { } parent && !ReferenceEquals(parent, Cards)) current = parent;
        return (UIElement)current;
    }

    /// <summary>Shrinks a touch while pressed, like a key going down.</summary>
    private static void Pressable(FrameworkElement element)
    {
        var scale = new ScaleTransform(1, 1);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        // The panel moves cards through their translate; the press only scales.
        element.RenderTransform = new TransformGroup { Children = { scale, new TranslateTransform() } };
        void To(double value, double ms)
        {
            var animation = new DoubleAnimation(value, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Motion.Out };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }
        element.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && IsInBadge(source)) return; // the × doesn't press the card
            To(0.975, 90);
        };
        element.MouseLeave += (_, _) => To(1, 160);
        element.PreviewMouseLeftButtonUp += (_, _) => To(1, 160);
    }

    /// <summary>The round × that appears on a card's top-left corner on hover.</summary>
    private static Border CloseBadge(Action click)
    {
        var badge = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(-13, -11, 0, 0),
            Opacity = 0,
            Child = Ui.Icon("IconClose", 8),
            ToolTip = UiText.Get("NotificationDismiss"),
            Tag = "badge",
            Cursor = Cursors.Hand
        };
        badge.SetResourceReference(Border.BackgroundProperty, "WidgetPopupFill");
        badge.SetResourceReference(Border.BorderBrushProperty, "DockBorderMaterial");
        ((FrameworkElement)badge.Child).HorizontalAlignment = HorizontalAlignment.Center;
        ((FrameworkElement)badge.Child).VerticalAlignment = VerticalAlignment.Center;
        badge.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true; // no press shrink
        badge.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return badge;
    }

    /// <summary>Several notifications of one app: the newest card with one or two narrower lips peeking out below.</summary>
    private FrameworkElement Stack(string appId, List<AppNotification> items)
    {
        var top = Card(items[0], items.Count - 1, items);
        var host = new StackPanel { Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand };
        host.Children.Add(top);
        var lips = new List<TranslateTransform>();
        for (var layer = 1; layer <= Math.Min(2, items.Count - 1); layer++)
        {
            var shift = new TranslateTransform();
            var lip = new Border
            {
                Height = layer == 1 ? 7 : 6,
                Width = CardWidth - layer * 28,
                CornerRadius = new CornerRadius(0, 0, 9, 9),
                HorizontalAlignment = HorizontalAlignment.Center,
                Opacity = layer == 1 ? 0.75 : 0.45,
                RenderTransform = shift
            };
            lip.SetResourceReference(Border.BackgroundProperty, "PanelTile");
            host.Children.Add(lip);
            lips.Add(shift);
        }
        // Hovering spreads the lips a little, hinting that the stack opens.
        host.MouseEnter += (_, _) =>
        {
            for (var i = 0; i < lips.Count; i++) lips[i].BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(2 * (i + 1), TimeSpan.FromMilliseconds(200)) { EasingFunction = Motion.Out });
        };
        host.MouseLeave += (_, _) =>
        {
            foreach (var shift in lips) shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = Motion.Out });
        };
        // Clicking a stack fans it out instead of opening the newest notification.
        host.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && IsInBadge(source)) return;
            e.Handled = true;
            Expand(appId, items);
        };
        return host;
    }

    private void Expand(string appId, List<AppNotification> items)
    {
        _expanded.Add(appId);
        var stack = "g:" + appId;
        _origins["gh:" + appId] = new Origin(stack);
        foreach (var n in items.Take(ExpandedLimit)) _origins[NoteKey(n)] = new Origin(stack);
        _origins[NoteKey(items[0])] = new Origin(stack, Continues: true); // the stack's top card itself
        _origins["rest:" + appId] = new Origin(stack);
        Refresh();
    }

    /// <summary>Folds an opened group: its cards glide up into the first one, then the stack takes their place.</summary>
    private void Collapse(string appId, List<AppNotification> items)
    {
        if (!_expanded.Remove(appId)) return;
        var keys = items.Select(NoteKey).Append("rest:" + appId).Append("gh:" + appId).ToHashSet(StringComparer.Ordinal);
        var target = NoteKey(items[0]);
        FrameworkElement? first = null;
        foreach (UIElement child in Cards.Children)
            if (child is FrameworkElement { Tag: string key } element && key == target) first = element;
        var anchor = first?.TranslatePoint(new Point(0, 0), Cards).Y ?? 0;
        var any = false;
        foreach (UIElement child in Cards.Children)
        {
            if (child is not FrameworkElement { Tag: string key } element || !keys.Contains(key) || key == target) continue;
            var y = element.TranslatePoint(new Point(0, 0), Cards).Y;
            Motion.Shift(element).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(anchor - y, TimeSpan.FromMilliseconds(200)) { EasingFunction = Motion.In });
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(170)));
            element.IsHitTestVisible = false;
            any = true;
        }
        _origins["g:" + appId] = new Origin(target, Continues: true);
        if (!any)
        {
            Refresh();
            return;
        }
        var wait = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(170) };
        wait.Tick += (_, _) =>
        {
            wait.Stop();
            if (IsOpen) Refresh();
        };
        wait.Start();
    }

    private static bool IsInBadge(DependencyObject node)
    {
        for (var current = node; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is FrameworkElement { Tag: "badge" }) return true;
        return false;
    }

    /// <summary>The opened group's title row: app name on the left, "Daha az göster" and × capsules on the right.</summary>
    private FrameworkElement GroupHeader(string appId, List<AppNotification> items)
    {
        var name = Capsule(Ui.Text(NotificationApps.Name(appId), 12, FontWeights.SemiBold), () => Collapse(appId, items));
        name.HorizontalAlignment = HorizontalAlignment.Left;
        var less = Capsule(Ui.Text(UiText.Get("NotificationsShowLess"), 11.5, FontWeights.SemiBold, "TextSecondary"), () => Collapse(appId, items));
        less.Margin = new Thickness(0, 0, 4, 0);
        var clear = Capsule(Ui.Icon("IconClose", 9), () => DismissMany(items.Select(NoteKey).Append("gh:" + appId).Append("rest:" + appId), () => NotificationFeed.Shared.RemoveMany(items)), UiText.Get("NotificationsClear"));
        clear.Padding = new Thickness(0);
        clear.Width = 26;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(less);
        buttons.Children.Add(clear);
        var row = new DockPanel { Width = CardWidth, Margin = new Thickness(0, 2, 0, 6) };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(name);
        return row;
    }

    /// <summary>"+N daha" under an opened group: shows the rest of it.</summary>
    private FrameworkElement MoreLine(string appId, int count)
    {
        var capsule = Capsule(Ui.Text(UiText.Format("NotificationsMore", count), 11, FontWeights.SemiBold, "TextSecondary"), () =>
        {
            _showAll.Add(appId);
            Refresh();
        });
        capsule.Height = 24;
        capsule.HorizontalAlignment = HorizontalAlignment.Center;
        return capsule;
    }

    /// <summary>"Temizle": every card slides out, one after another, then the list is emptied.</summary>
    private void ClearEverything()
    {
        var keys = new List<string>();
        foreach (UIElement child in Cards.Children)
            if (child is FrameworkElement { Tag: string key } && key is not "options") keys.Add(key);
        DismissMany(keys, NotificationFeed.Shared.ClearAll);
    }

    private void DismissMany(IEnumerable<string> keys, Action done)
    {
        // Cards already on their way out are left alone (a second click changes nothing).
        var wanted = keys.Where(key => !_leaving.Contains(key)).ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0) return;
        _leaving.UnionWith(wanted);
        var delay = 0;
        var moving = 0;
        foreach (UIElement child in Cards.Children)
        {
            if (child is not FrameworkElement { Tag: string key } element || !wanted.Contains(key)) continue;
            var start = delay;
            var slide = Motion.FromTo(0, 110, 260, Motion.In, start);
            slide.FillBehavior = FillBehavior.HoldEnd;
            var fade = Motion.FromTo(1, 0, 220, Motion.In, start + 30);
            fade.FillBehavior = FillBehavior.HoldEnd;
            Motion.Shift(element).BeginAnimation(TranslateTransform.XProperty, slide);
            element.BeginAnimation(OpacityProperty, fade);
            element.IsHitTestVisible = false;
            delay = Math.Min(delay + 35, 210);
            moving++;
        }
        void Finish()
        {
            done();
            _leaving.ExceptWith(wanted);
        }
        if (moving == 0)
        {
            Finish();
            return;
        }
        var wait = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay + 230) };
        wait.Tick += (_, _) =>
        {
            wait.Stop();
            Finish();
        };
        wait.Start();
    }
}
