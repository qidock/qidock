using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using QiDock.Services.Notifications;
using QiDock.Widgets;

namespace QiDock;

/// <summary>
/// Notifications: new Windows notifications slide in at the top of the screen, the dock's bell
/// shows how many are unread, and the notification center (also opened from the widget bar) lists them.
/// </summary>
public partial class MainWindow
{
    private NotificationBanner? _notificationBanner;
    private readonly WindowsToastHider _toastHider = new();

    private void InitializeNotifications()
    {
        var feed = NotificationFeed.Shared;
        _notificationBanner = new NotificationBanner();
        _notificationBanner.Opened += feed.Remove;
        feed.Arrived += ShowNotifications;
        feed.Changed += UpdateNotificationsBadge;
        NotificationCenter.Settings = () => _preferences.Current.WidgetOptions;
        NotificationCenter.Update = change => SaveWidgetOptions(change(_preferences.Current.WidgetOptions));
        EdgePanel.TopInset = VolumeHudTop;
        EdgePanel.DockSeam = seam =>
        {
            if (seam is null)
            {
                CutDockSeam(null);
                _topBar?.CutSeam(null);
            }
            // The top bar turns solid with no edge line while a card stands on it, so nothing needs cutting
            // there; cutting would leave a row of pixels between the two windows.
            else if (!_panelOnTopBar) CutDockSeam(seam);
        };
        EdgePanel.AnyVisibilityChanged += (_, _) =>
        {
            _autoHideController?.EvaluateState();
            // While a panel stands on the dock the two are one solid piece: no edge line on the dock and no shadow
            // (the dock's shadow would darken the panel's foot, its edge line would stick out beside the curve).
            var standing = EdgePanel.StandsOnDock ? EdgePanel.SolidDockMaterial() : null;
            _topBar?.SetJoined(standing is not null && _panelOnTopBar ? standing : null);
            _topBar?.Update(); // the bar stays down while a card stands on it
            if (standing is { } solid && !_panelOnTopBar)
            {
                DockSurface.Background = solid;
                DockSurface.BorderBrush = solid;
                _dockShadow ??= DockSurface.Effect;
                DockSurface.Effect = null;
            }
            else
            {
                DockSurface.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "DockMaterial");
                DockSurface.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "DockBorderMaterial");
                if (_dockShadow is not null && App.Shadows) DockSurface.Effect = _dockShadow;
            }
        };
        EdgePanel.DockAnchorFor = element =>
        {
            // A tile of the top bar (the classic layout): the card grows down out of the bar.
            if (_topBar is not null && ReferenceEquals(Window.GetWindow(element), _topBar))
            {
                var fromBar = _topBar.AnchorOf(element);
                if (fromBar is not null) _panelOnTopBar = true;
                return fromBar;
            }
            if (!ReferenceEquals(Window.GetWindow(element), this)) return null;
            EdgePanel.DockAnchor? anchor = null;
            ResetMagnification(() => anchor = DockAnchorOf(element));
            return anchor;
        };
        feed.Start();
        ApplyToastHiding();
    }

    /// <summary>Windows' own pop-up stays away while the top banners show each notification.</summary>
    private void ApplyToastHiding()
    {
        var options = _preferences.Current.WidgetOptions;
        _toastHider.SetEnabled(options.NotificationBanners && options.HideWindowsToasts);
    }

    private void ShowNotifications(IReadOnlyList<AppNotification> added)
    {
        var options = _preferences.Current.WidgetOptions;
        if (options.NotificationBanners) _notificationBanner?.Present(added);
        if (options.NotificationSound) PlaySound("Notification.Default", 0, 0x00010000 /* SND_ALIAS */ | 0x0001 /* SND_ASYNC */ | 0x0002 /* SND_NODEFAULT */);
    }

    private void UpdateNotificationsBadge()
    {
        NotificationList.SetBadge(NotificationsBadge, NotificationsBadgeText, NotificationFeed.Shared.UnreadCount);
    }

    /// <summary>The bell's list grows out of the bell itself (Ctrl+Alt+← and the bar tile keep the edge sheet).</summary>
    private void NotificationsButton_Click(object sender, RoutedEventArgs e)
    {
        // The tiles settle to their resting size and the panel grows out of where the bell comes to rest.
        EdgePanel.DockAnchor? anchor = null;
        ResetMagnification(() => anchor = DockAnchorOf(NotificationsButton));
        if (anchor is { } resting) NotificationCenter.Shared.ToggleFromDock(resting);
        else NotificationCenter.Shared.Toggle();
    }

    private static bool NotificationCenterOpen => EdgePanel.AnyOpen;

    private void DisposeNotifications()
    {
        NotificationFeed.Shared.Dispose();
        _toastHider.Dispose();
        if (NotificationCenter.IsCreated) NotificationCenter.Shared.Close();
        _notificationBanner?.Close();
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string sound, nint module, uint flags);
}
