using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using QiDock.DesktopWidgets;

namespace QiDock;

/// <summary>
/// Desktop widgets: cards placed freely on the desktop (clocks, weather, music, system, notes…),
/// each in Mini / Small / Strip / Medium / Large size. They pause while an app window covers them.
/// </summary>
public partial class MainWindow
{
    private DeskWidgetManager? _deskWidgets;

    private void InitializeDesktopWidgets()
    {
        Desk.Settings = () => _preferences.Current.WidgetOptions;
        Services.FocusTimer.Minutes = () => _preferences.Current.WidgetOptions.FocusMinutes;
        Desk.UpdateSettings = change => SaveWidgetOptions(change(_preferences.Current.WidgetOptions));
        Desk.DockApps = () => _pinnedApps
            .Select(app => app.ExecutablePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // After the dock is up, so the desktop widgets never delay it.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (_deskWidgets is not null) return;
            _deskWidgets = new DeskWidgetManager();
            _deskWidgets.Start();
            UpdateDeskCovers();
        });
    }

    /// <summary>Tells the desktop widgets which screen areas app windows cover (they pause underneath).</summary>
    private void UpdateDeskCovers()
    {
        if (_deskWidgets is null || _windowTracker?.Snapshot is not { } windows) return;
        var covers = new List<Rect>();
        foreach (var window in windows)
        {
            var handle = window.Handle;
            if (!TopNative.IsWindow(handle) || !TopNative.IsWindowVisible(handle) || TopNative.IsIconic(handle) || IsCloakedWindow(handle)) continue;
            if (GetWindowRect(handle, out var r)) covers.Add(new Rect(r.Left, r.Top, Math.Max(0, r.Right - r.Left), Math.Max(0, r.Bottom - r.Top)));
        }
        _deskWidgets.UpdateCovers(covers);
    }

    private void ShowDesktopWidgetGallery()
    {
        if (_deskWidgets is null)
        {
            _deskWidgets = new DeskWidgetManager();
            _deskWidgets.Start();
        }
        _deskWidgets.ShowGallery();
    }

    private void DisposeDesktopWidgets()
    {
        _deskWidgets?.Dispose();
        _deskWidgets = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);
}
