using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using QiDock.Services;
using QiDock.Widgets;

namespace QiDock;

/// <summary>
/// Volume, brightness and short messages: a card growing out of the middle of the dock while the dock is on screen
/// (and no other panel stands on it), otherwise the small pill at the top of the screen.
/// </summary>
public partial class MainWindow
{
    private static readonly System.Windows.Media.Brush VolumeAccent = Ui.Solid("#FF30D158");
    private static readonly System.Windows.Media.Brush BrightnessAccent = Ui.Solid("#FFFFD60A");

    // GUID_VIDEO_CURRENT_MONITOR_BRIGHTNESS: Windows reports the built-in display's brightness whenever it changes.
    private static Guid _brightnessSetting = new("8ffee2c6-2d01-46be-adb9-398addc5b4ff");
    private nint _brightnessNotification;
    private int _lastBrightness = -1;

    private void InitializeHud(HwndSource source)
    {
        Ui.HudHandler = ShowMessageHud;
        if (_volumeWatcher is not null) _volumeWatcher.Changed += ShowVolumeHud;
        if (_volumeWatcher is not null) _volumeWatcher.Changed += ControlCenter.VolumeChanged;
        source.AddHook(HudHook);
        _brightnessNotification = RegisterPowerSettingNotification(source.Handle, ref _brightnessSetting, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
        QuietBrightness(TimeSpan.FromSeconds(30));
    }

    private DateTime _brightnessQuietUntil;

    /// <summary>
    /// Right after Windows starts (or wakes), Windows and the display driver set the saved or automatic brightness
    /// on their own; that is not the user turning it, so the card stays away for a while and only learns the level.
    /// </summary>
    private void QuietBrightness(TimeSpan span) => _brightnessQuietUntil = DateTime.UtcNow + span;

    /// <summary>Widgets in a hidden dock stop their clocks and meters; they catch up the moment it comes back.</summary>
    private void AttachHudToAutoHide()
    {
        if (_autoHideController is null) return;
        _autoHideController.VisibilityChanged += visible =>
        {
            if (WidgetsInDock) _widgetBar.SetLive(visible);
        };
    }

    /// <summary>
    /// Where the dock card would stand, or null when the dock is off screen (full-screen app), busy with another panel
    /// or the card is turned off. A hidden dock is brought up for it when <paramref name="reveal"/> (it goes down again
    /// once the card has sunk back).
    /// </summary>
    private EdgePanel.DockAnchor? HudAnchor(bool reveal = true)
    {
        if (!_preferences.Current.DockHud || !IsVisible || EdgePanel.AnyOpen && !DockHud.Shared.IsOpen) return null;
        if (_autoHideController is { IsHidden: true } hide)
        {
            if (!reveal) return null;
            hide.AnimateReveal();
        }
        return DockAnchorOf(DockSurface) is { } anchor ? anchor with { TileWidth = 56 } : null;
    }

    private void ShowVolumeHud(float level, bool muted)
    {
        var percent = (int)Math.Round(Math.Clamp(level, 0f, 1f) * 100);
        if (HudAnchor() is { } anchor)
            DockHud.Shared.ShowLevel(anchor, muted || percent == 0 ? "IconVolumeMute" : "IconVolume", UiText.Get("HudVolume"), percent, VolumeAccent, dimmed: muted);
        else _volumeHud?.ShowLevel(level, muted);
    }

    private void ShowMessageHud(string iconKey, string text)
    {
        if (HudAnchor() is { } anchor) DockHud.Shared.ShowMessage(anchor, iconKey, text);
        else _volumeHud?.ShowMessage(iconKey, text);
    }

    private void ShowBrightnessHud(int percent)
    {
        // The first report is the current level (sent on registering); a change made in an open panel shows there.
        var first = _lastBrightness < 0;
        if (percent == _lastBrightness) return;
        _lastBrightness = percent;
        if (first || DateTime.UtcNow < _brightnessQuietUntil || EdgePanel.AnyOpen && !DockHud.Shared.IsOpen) return;
        if (HudAnchor() is { } anchor) DockHud.Shared.ShowLevel(anchor, "IconSun", UiText.Get("HudBrightness"), percent, BrightnessAccent);
        else _volumeHud?.ShowMessage("IconSun", UiText.Format("HudBrightnessPill", percent));
    }

    private nint HudHook(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0218 /* WM_POWERBROADCAST */) return 0;
        if (wParam is 0x7 /* PBT_APMRESUMESUSPEND */ or 0x12 /* PBT_APMRESUMEAUTOMATIC */)
        {
            QuietBrightness(TimeSpan.FromSeconds(20));
            return 0;
        }
        if (wParam != 0x8013 /* PBT_POWERSETTINGCHANGE */ || lParam == 0) return 0;
        var setting = Marshal.PtrToStructure<Guid>(lParam);
        if (setting != _brightnessSetting) return 0;
        var length = Marshal.ReadInt32(lParam, 16);
        if (length >= 4)
        {
            var percent = Marshal.ReadInt32(lParam, 20);
            Dispatcher.BeginInvoke(() => ShowBrightnessHud(Math.Clamp(percent, 0, 100)));
        }
        return 0;
    }

    private void DisposeHud()
    {
        if (_brightnessNotification != 0) UnregisterPowerSettingNotification(_brightnessNotification);
        _brightnessNotification = 0;
        if (_volumeWatcher is not null) _volumeWatcher.Changed -= ShowVolumeHud;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint RegisterPowerSettingNotification(nint recipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterPowerSettingNotification(nint handle);
}
