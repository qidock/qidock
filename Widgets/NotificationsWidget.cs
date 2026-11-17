using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QiDock.Models;
using QiDock.Services;
using QiDock.Services.Notifications;

namespace QiDock.Widgets;

/// <summary>
/// A square bell button with a red badge for unread notifications, at the far end of the row next to the battery;
/// the list grows out of it.
/// </summary>
public sealed class NotificationsWidget : DockWidget
{
    private readonly Button _tile;
    private readonly Border _badge;
    private readonly TextBlock _badgeText;

    public NotificationsWidget()
    {
        (var bell, _badge, _badgeText) = NotificationList.Bell(18, Ui.Icon("IconBell", 18));
        bell.HorizontalAlignment = HorizontalAlignment.Center;
        _tile = Ui.Tile(bell, (_, _) => Toggle(), horizontalPadding: 0);
        _tile.SetResourceReference(FrameworkElement.WidthProperty, "WidgetTileHeight");
        _tile.ToolTip = UiText.Get("NotificationsTitle");
    }

    public override string Id => WidgetIds.Notifications;
    public override FrameworkElement View => _tile;
    public override bool IsPopupOpen => NotificationCenter.IsCreated && NotificationCenter.Shared.IsOpen;
    public override void ClosePopup()
    {
        if (NotificationCenter.IsCreated) NotificationCenter.Shared.Close(animated: true);
    }

    public override void Start()
    {
        NotificationFeed.Shared.Changed += Render;
        Render();
    }

    public override void Stop()
    {
        NotificationFeed.Shared.Changed -= Render;
    }

    public override void ApplySettings(WidgetSettings settings) { }

    private void Render()
    {
        var feed = NotificationFeed.Shared;
        var unread = feed.UnreadCount;
        NotificationList.SetBadge(_badge, _badgeText, unread);
    }

    /// <summary>"3 yeni", "5 bildirim" or "Yeni bildirim yok": the same wording on every notifications tile.</summary>
    public static string StatusText()
    {
        var feed = NotificationFeed.Shared;
        var unread = feed.UnreadCount;
        return unread > 0 ? UiText.Format("NotificationsUnread", unread)
            : feed.Items.Count > 0 ? UiText.Format("NotificationsCount", feed.Items.Count)
            : UiText.Get("NotificationsNone");
    }

    /// <summary>The list grows out of the bell (out of the dock or the top bar, wherever the tile stands).</summary>
    private void Toggle() => NotificationCenter.Shared.ToggleFrom(_tile);
}

/// <summary>The bell with its red count badge, shared by the bar tile and the dock button.</summary>
public static class NotificationList
{
    /// <summary>An icon with a red count badge on its top-right corner.</summary>
    public static (Grid Host, Border Badge, TextBlock Count) Bell(double size, UIElement icon)
    {
        var count = new TextBlock { FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1, 0, 0) };
        var badge = new Border
        {
            MinWidth = 15,
            Height = 15,
            CornerRadius = new CornerRadius(7.5),
            Padding = new Thickness(4, 0, 4, 0),
            Background = Ui.Solid("#FFFF3B30"),
            BorderThickness = new Thickness(1.2),
            BorderBrush = Ui.Solid("#E6FFFFFF"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -6, -8, 0),
            Child = count,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        var host = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(icon);
        host.Children.Add(badge);
        return (host, badge, count);
    }

    public static void SetBadge(Border badge, TextBlock text, int unread)
    {
        badge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        text.Text = unread > 9 ? "9+" : unread.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
