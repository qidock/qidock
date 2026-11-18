using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using QiDock.Services;
using QiDock.Widgets;

namespace QiDock;

/// <summary>
/// AppGrid, QuickSearch and Window Overview on global shortcuts, the Downloads stack on the dock,
/// hiding the dock and bar while a full-screen app is in front, and giving memory back while idle.
/// </summary>
public partial class MainWindow
{
    // Alt+Shift+Space: QuickSearch · Ctrl+Alt+↑: Window Overview · Ctrl+Alt+↓: AppGrid ·
    // Ctrl+Alt+←: notification center · Ctrl+Alt+→: control center.
    // (Alt+Space and Ctrl+Alt+Space are usually taken by other apps' quick launchers.)
    private const int HotkeyQuickSearch = 0xB001, HotkeyWindowOverview = 0xB002, HotkeyAppGrid = 0xB003, HotkeyNotifications = 0xB004, HotkeyControlCenter = 0xB005, HotkeyProfileFirst = 0xB011;
    private static readonly TimeSpan IdleBeforeTrim = TimeSpan.FromMinutes(2);

    private readonly DispatcherTimer _fullScreenWatch = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _memoryTrim = new(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMinutes(1) };
    private WidgetPopup? _downloadsPopup;
    private nint _extrasHandle;
    private bool _ownFullScreenHide;
    private uint _trimmedAtInput;

    private void InitializeExtras(HwndSource source)
    {
        _extrasHandle = source.Handle;
        source.AddHook(ExtrasHook);
        RegisterHotKey(_extrasHandle, HotkeyQuickSearch, ModAlt | ModShift | ModNoRepeat, 0x20 /* Space */);
        RegisterHotKey(_extrasHandle, HotkeyWindowOverview, ModControl | ModAlt | ModNoRepeat, 0x26 /* Up */);
        RegisterHotKey(_extrasHandle, HotkeyAppGrid, ModControl | ModAlt | ModNoRepeat, 0x28 /* Down */);
        RegisterHotKey(_extrasHandle, HotkeyNotifications, ModControl | ModAlt | ModNoRepeat, 0x25 /* Left */);
        RegisterHotKey(_extrasHandle, HotkeyControlCenter, ModControl | ModAlt | ModNoRepeat, 0x27 /* Right */);
        // Ctrl+Alt+Shift+1…9: widget profiles (Ctrl+Alt alone is AltGr, which types # $ { [ on Turkish keyboards).
        for (var i = 0; i < 9; i++)
            RegisterHotKey(_extrasHandle, HotkeyProfileFirst + i, ModControl | ModAlt | ModShift | ModNoRepeat, (uint)(0x31 + i));
        ControlCenter.OpenAppSettings = () => OpenSettings_Click(this, new RoutedEventArgs());
        _downloadsPopup = new WidgetPopup(DownloadsButton);
        _downloadsPopup.Closed += (_, _) => _autoHideController?.EvaluateState();
        AppGridWindow.PinToDock = (target, name) => AddShortcuts([target], name);
        AppGridWindow.IsPinned = target => _pinnedApps.Any(a => string.Equals(a.ExecutablePath, target, StringComparison.OrdinalIgnoreCase));
        // The app grid's icons are read once the desktop has settled, so its first opening is instant.
        var prewarm = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(20) };
        prewarm.Tick += (_, _) =>
        {
            prewarm.Stop();
            AppGridWindow.Prewarm();
        };
        prewarm.Start();
        _fullScreenWatch.Tick += (_, _) => UpdateFullScreenAwareness(GetForegroundWindow());
        _fullScreenWatch.Start();
        _memoryTrim.Tick += (_, _) => TrimMemoryWhenIdle();
        _memoryTrim.Start();
        // Start-up work (icons, widgets, the first layout) leaves a lot of memory that is never touched again.
        var startupTrim = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(45) };
        startupTrim.Tick += (_, _) =>
        {
            startupTrim.Stop();
            TrimMemory();
        };
        startupTrim.Start();
        OverlayWindow.AnyClosed += TrimMemory;
    }

    private nint ExtrasHook(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0312 /* WM_HOTKEY */) return 0;
        switch ((int)wParam)
        {
            case HotkeyQuickSearch: QuickSearchWindow.Toggle(); break;
            case HotkeyWindowOverview: WindowOverviewWindow.Toggle(_windowTracker?.Snapshot ?? []); break;
            case HotkeyAppGrid: AppGridWindow.Toggle(); break;
            case HotkeyNotifications: NotificationCenter.Shared.Toggle(); break;
            case HotkeyControlCenter: ControlCenter.Shared.Toggle(); break;
            case >= HotkeyProfileFirst and < HotkeyProfileFirst + 9:
                var options = _preferences.Current.WidgetOptions;
                var index = (int)wParam - HotkeyProfileFirst;
                if (index >= options.EffectiveProfiles.Count) return 0;
                if (index != options.EffectiveActiveProfile) SaveWidgetOptions(options.SwitchProfile(index));
                Widgets.Ui.ShowHud("IconLayout", options.EffectiveProfiles[index].Name);
                break;
            default: return 0;
        }
        handled = true;
        return 0;
    }

    private void AppGridButton_Click(object sender, RoutedEventArgs e) => AppGridWindow.Toggle();

    private void DisposeExtras()
    {
        _fullScreenWatch.Stop();
        _memoryTrim.Stop();
        if (_extrasHandle != 0)
        {
            UnregisterHotKey(_extrasHandle, HotkeyQuickSearch);
            UnregisterHotKey(_extrasHandle, HotkeyWindowOverview);
            UnregisterHotKey(_extrasHandle, HotkeyAppGrid);
            UnregisterHotKey(_extrasHandle, HotkeyNotifications);
            UnregisterHotKey(_extrasHandle, HotkeyControlCenter);
            for (var i = 0; i < 9; i++) UnregisterHotKey(_extrasHandle, HotkeyProfileFirst + i);
        }
        // Never leave a window off the screen when QiDock closes.
        BringBackForcedAside(activate: false);
        _downloadsPopup?.Close();
        if (ControlCenter.IsCreated) ControlCenter.Shared.Close();
    }

    // ---- Full-screen apps ------------------------------------------------------------------

    /// <summary>
    /// Games, videos and presentations in full screen get the whole screen: the dock and the bar
    /// step aside until another window comes to the front. (The taskbar-replacement mode does
    /// this on its own.)
    /// </summary>
    private void UpdateFullScreenAwareness(nint foreground)
    {
        if (_replacementSession is not null || foreground == 0) return;
        GetWindowThreadProcessId(foreground, out var process);
        if (process == Environment.ProcessId) return;
        var fullScreen = IsFullScreenWindow(foreground);
        if (fullScreen == _ownFullScreenHide) return;
        _ownFullScreenHide = fullScreen;
        if (_extrasHandle != 0) ShowWindow(_extrasHandle, fullScreen ? 0 /* SW_HIDE */ : 4 /* SW_SHOWNOACTIVATE */);
        OnFullScreenChanged(fullScreen);
    }

    private static bool IsFullScreenWindow(nint window)
    {
        if (!IsWindowVisible(window) || IsIconic(window) || IsZoomed(window)) return false;
        var name = new StringBuilder(64);
        GetClassName(window, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow") return false;
        var monitor = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(MonitorFromWindow(window, 2), ref monitor) && GetWindowRect(window, out var rect)
            && rect.Left <= monitor.Bounds.Left && rect.Top <= monitor.Bounds.Top
            && rect.Right >= monitor.Bounds.Right && rect.Bottom >= monitor.Bounds.Bottom;
    }

    // ---- Memory ----------------------------------------------------------------------------

    /// <summary>After a couple of idle minutes, hands unused memory back to Windows (once per idle spell).</summary>
    private void TrimMemoryWhenIdle()
    {
        var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref input) || input.Time == _trimmedAtInput) return;
        if (TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - input.Time)) < IdleBeforeTrim) return;
        if (EdgePanel.AnyOpen || _thumbnailPreview.IsVisible) return;
        _trimmedAtInput = input.Time;
        TrimMemory();
    }

    private static void TrimMemory()
    {
        GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        SetProcessWorkingSetSize(self.Handle, -1, -1);
    }

    // ---- Downloads stack -------------------------------------------------------------------

    private void DownloadsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadsPopup is null) return;
        if (!_downloadsPopup.IsOpen) _downloadsPopup.SetContent(BuildDownloads(), 392);
        _downloadsPopup.Toggle();
    }

    private UIElement BuildDownloads()
    {
        var folder = DownloadsFolder();
        var root = new StackPanel();
        var open = Ui.Pill(UiText.Get("DownloadsOpenFolder"), (_, _) =>
        {
            _downloadsPopup?.Close();
            Ui.Open(folder);
        }, "IconFolderOpen");
        root.Children.Add(Ui.Header(UiText.Get("DownloadsTitle"), () => _downloadsPopup?.Close(), open));
        var files = Directory.Exists(folder)
            ? new DirectoryInfo(folder).EnumerateFileSystemInfos()
                .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0 && !IsPartial(f.Name))
                .OrderByDescending(f => f.CreationTimeUtc > f.LastWriteTimeUtc ? f.CreationTimeUtc : f.LastWriteTimeUtc)
                .Take(12).ToList()
            : [];
        if (files.Count == 0)
        {
            var empty = Ui.Text(UiText.Get("DownloadsEmpty"), 12, brushKey: "TextSecondary");
            empty.Margin = new Thickness(4, 10, 4, 10);
            root.Children.Add(empty);
            return root;
        }
        var grid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var file in files) grid.Children.Add(DownloadTile(file));
        root.Children.Add(grid);
        return root;
    }

    private FrameworkElement DownloadTile(FileSystemInfo file)
    {
        var icon = Ui.FileIcon(file.FullName, 44);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        var name = Ui.Text(file.Name, 11);
        name.TextAlignment = TextAlignment.Center;
        name.TextWrapping = TextWrapping.Wrap;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.MaxHeight = 11 * 1.4 * 2;
        name.Margin = new Thickness(0, 5, 0, 0);
        var when = Ui.Text(Ui.Ago(file.CreationTime > file.LastWriteTime ? file.CreationTime : file.LastWriteTime), 10, brushKey: "TextSecondary");
        when.HorizontalAlignment = HorizontalAlignment.Center;
        var column = new StackPanel();
        column.Children.Add(icon);
        column.Children.Add(name);
        column.Children.Add(when);
        var button = new Button { Content = column, Padding = new Thickness(4, 8, 4, 6), Margin = new Thickness(2), ToolTip = file.FullName, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        button.SetResourceReference(StyleProperty, "WidgetTileButton");
        button.Height = double.NaN;
        button.Click += (_, _) =>
        {
            _downloadsPopup?.Close();
            Ui.Open(file.FullName);
        };
        FolderWidget.AttachDragOut(button, file.FullName);
        return button;
    }

    private static bool IsPartial(string name) =>
        name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);

    private static string DownloadsFolder()
    {
        if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, 0, out var pointer) == 0 && pointer != 0)
        {
            try { return Marshal.PtrToStringUni(pointer) ?? ""; }
            finally { Marshal.FreeCoTaskMem(pointer); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    // ---- Native ----------------------------------------------------------------------------

    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModNoRepeat = 0x4000;

    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { public uint Size; public uint Time; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public NativeRect Bounds; public NativeRect Work; public uint Flags; }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(nint process, nint minimum, nint maximum);
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, nint token, out nint path);
}
