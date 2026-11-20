using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using QiDock.Models;
using QiDock.Services;

namespace QiDock;

/// <summary>
/// Dock shortcuts the user adds (apps, files, folders, web links: from the menu or by dropping
/// them on the dock), the two desktop buttons, and the Recycle Bin tile.
/// </summary>
public partial class MainWindow
{
    private DispatcherTimer? _trashTimer;
    private bool? _trashFull;

    // ---- Desktop buttons ---------------------------------------------------------------------

    /// <summary>"Clean desktop": straight to the wallpaper and the widgets, the desktop icons hidden.</summary>
    private void DesktopButton_Click(object sender, RoutedEventArgs e)
    {
        _thumbnailPreview.Hide();
        AnimateActivationFeedback((Button)sender);
        GoToDesktop(files: false);
    }

    /// <summary>"Desktop with files": straight to the desktop with its folders and files showing.</summary>
    private void FilesDesktopButton_Click(object sender, RoutedEventArgs e)
    {
        _thumbnailPreview.Hide();
        AnimateActivationFeedback((Button)sender);
        GoToDesktop(files: true);
    }

    // True while the clean desktop button has the desktop showing (the files one is _desktopShown).
    private bool _cleanShown;
    // True while the open windows were stepped aside by one of the desktop buttons.
    private bool _windowsAside;

    /// <summary>
    /// Goes straight to the clean desktop or to the one with files, whichever was pressed and from wherever the
    /// screen is: open windows step aside (only when some are open, since on an empty desktop Win+D would bring
    /// them back instead) and the icons are hidden or shown. The same button pressed again while still there
    /// brings the windows back.
    /// </summary>
    private void GoToDesktop(bool files)
    {
        var here = files ? _desktopShown : _cleanShown;
        if (here && _windowsAside && ComputeDesktopClean())
        {
            if (_desktopToggled) ToggleDesktop();
            BringBackForcedAside(activate: !_desktopToggled);
            LeaveDesktop();
            return;
        }
        _desktopToggled = false;
        if (!ComputeDesktopClean())
        {
            ToggleDesktop();
            _windowsAside = true;
            _desktopToggled = true;
        }
        SweepStubbornWindows();
        _desktopShownAt = DateTime.UtcNow;
        _cleanShown = !files;
        SetDesktopFilesMode(files);
        if (!files) ApplyDesktopIcons(true);
    }

    /// <summary>Back from either desktop: the icons return to what the settings ask for.</summary>
    private void LeaveDesktop()
    {
        // Another window came forward: the stepped-aside ones come back behind it, as they were.
        BringBackForcedAside(activate: false);
        _windowsAside = false;
        _cleanShown = false;
        SetDesktopFilesMode(false);
        if (!_preferences.Current.HideDesktopIcons) ApplyDesktopIcons(false);
    }

    // Whether the desktop button used Win+D (so pressing it again uses Win+D to bring the windows back).
    private bool _desktopToggled;
    // Windows Win+D leaves standing, which QiDock moved off the screen itself, top one first, with where they were.
    private readonly List<(nint Window, DesktopNative.Rect Bounds)> _forcedAside = [];

    /// <summary>Whether QiDock moved this window off the screen to show the desktop.</summary>
    private bool IsForcedAside(nint window) => _forcedAside.Any(f => f.Window == window);

    /// <summary>
    /// Win+D leaves windows without a minimize button where they are, so a program that fills the
    /// whole screen that way (a till or a kiosk program, often several such windows on top of each other)
    /// kept the desktop out of reach. Once Win+D has done its part, those still covering the screen are
    /// moved off the screen here. They are not minimized: such programs often close a screen that gets
    /// minimized and go back to their main menu, while a move leaves them exactly as they were.
    /// </summary>
    private void SweepStubbornWindows()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var primary = DesktopNative.MonitorFromPoint(default, 1 /* MONITOR_DEFAULTTOPRIMARY */);
            var info = new DesktopNative.MonitorInfo { Size = (uint)Marshal.SizeOf<DesktopNative.MonitorInfo>() };
            if (!DesktopNative.GetMonitorInfo(primary, ref info)) return;
            var screen = info.Bounds;
            var screenArea = (double)(screen.Right - screen.Left) * (screen.Bottom - screen.Top);
            var own = (uint)Environment.ProcessId;
            var found = new List<(nint, DesktopNative.Rect)>();
            DesktopNative.EnumWindows((window, _) =>
            {
                if (!DesktopNative.IsWindowVisible(window) || DesktopNative.IsIconic(window)) return true;
                var style = DesktopNative.GetWindowLongPtr(window, -16).ToInt64();
                var exStyle = DesktopNative.GetWindowLongPtr(window, -20).ToInt64();
                if ((style & 0x00020000 /* WS_MINIMIZEBOX */) != 0 || (exStyle & 0x00000080 /* WS_EX_TOOLWINDOW */) != 0) return true;
                if (DesktopNative.GetAncestor(window, 2 /* GA_ROOT */) != window) return true;
                DesktopNative.GetWindowThreadProcessId(window, out var process);
                if (process == own) return true;
                if (DesktopNative.DwmGetWindowAttribute(window, 14 /* DWMWA_CLOAKED */, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                var name = new StringBuilder(64);
                DesktopNative.GetClassName(window, name, name.Capacity);
                if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow") return true;
                if (DesktopNative.MonitorFromWindow(window, 2 /* MONITOR_DEFAULTTONEAREST */) != primary || !DesktopNative.GetWindowRect(window, out var rect)) return true;
                var width = Math.Min(rect.Right, screen.Right) - Math.Max(rect.Left, screen.Left);
                var height = Math.Min(rect.Bottom, screen.Bottom) - Math.Max(rect.Top, screen.Top);
                // Only windows that hide most of the desktop; small dialogs and floating tools stay.
                if (width > 0 && height > 0 && width * (double)height >= screenArea * 0.5) found.Add((window, rect));
                return true;
            }, 0);
            if (found.Count == 0) return;
            var foreground = DesktopNative.GetForegroundWindow();
            foreach (var (window, _) in found) DesktopNative.SetWindowPos(window, 0, OffScreen, OffScreen, 0, 0, MoveOnly);
            _forcedAside.AddRange(found);
            _windowsAside = true;
            // The keyboard goes to the desktop, as with Win+D, not to a window that is out of sight.
            if (found.Any(f => f.Item1 == foreground) && DesktopNative.FindWindow("Progman", null) is var desktop and not 0)
                DesktopNative.SetForegroundWindow(desktop);
        };
        timer.Start();
    }

    private const int OffScreen = -32000;
    private const uint MoveOnly = 0x0001 /* NOSIZE */ | 0x0004 /* NOZORDER */ | 0x0010 /* NOACTIVATE */ | 0x4000 /* ASYNCWINDOWPOS */;

    /// <summary>
    /// Puts the windows moved by <see cref="SweepStubbornWindows"/> back where they were, in the same
    /// order on top of each other; with <paramref name="activate"/> the top one takes the keyboard again.
    /// </summary>
    private void BringBackForcedAside(bool activate)
    {
        if (_forcedAside.Count == 0) return;
        foreach (var (window, bounds) in _forcedAside)
            if (DesktopNative.IsWindow(window)) DesktopNative.SetWindowPos(window, 0, bounds.Left, bounds.Top, 0, 0, MoveOnly);
        var top = _forcedAside[0].Window;
        _forcedAside.Clear();
        if (activate && DesktopNative.IsWindow(top)) NativeWindowTracker.Activate(top);
    }

    private static class DesktopNative
    {
        public delegate bool EnumWindowsProc(nint window, nint parameter);

        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct MonitorInfo { public uint Size; public Rect Bounds; public Rect Work; public uint Flags; }

        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsIconic(nint window);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint window, int index);
        [DllImport("user32.dll")] public static extern nint GetAncestor(nint window, uint flags);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint window, out uint process);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(nint window, StringBuilder name, int capacity);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(nint window, out Rect rect);
        [DllImport("user32.dll")] public static extern nint MonitorFromWindow(nint window, uint flags);
        [DllImport("user32.dll")] public static extern nint MonitorFromPoint(Point point, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(nint window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint FindWindow(string? className, string? title);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    }

    private void SetDesktopFilesMode(bool on)
    {
        _desktopShown = on;
        if (on) _desktopShownAt = DateTime.UtcNow;
        _topBar?.SetFilesMode(on);
        _deskWidgets?.SetHidden(on);
        if (on) ApplyDesktopIcons(false);
        else if (_preferences.Current.HideDesktopIcons) ApplyDesktopIcons(true);
    }

    // ---- Recycle Bin -------------------------------------------------------------------------

    private void InitializeTrash()
    {
        UpdateTrashIcon();
        _trashTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _trashTimer.Tick += (_, _) => UpdateTrashIcon();
        _trashTimer.Start();
    }

    private void UpdateTrashIcon()
    {
        var info = new RecycleBinInfo { Size = Marshal.SizeOf<RecycleBinInfo>() };
        var full = ShortcutNative.SHQueryRecycleBin(null, ref info) == 0 && info.Items > 0;
        if (_trashFull == full) return;
        _trashFull = full;
        TrashIcon.Source = ApplicationIconProvider.GetForResource(@"%SystemRoot%\System32\imageres.dll", full ? -54 : -55);
    }

    private void TrashButton_Click(object sender, RoutedEventArgs e)
    {
        _thumbnailPreview.Hide();
        AnimateActivationFeedback((Button)sender);
        Widgets.Ui.Open("shell:RecycleBinFolder");
    }

    private void TrashButton_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Files dropped on the bin go to the Recycle Bin (undoable), like dragging them there in Explorer.</summary>
    private void TrashButton_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        var operation = new ShortcutNative.FileOperation
        {
            Function = 3, // FO_DELETE
            From = string.Join('\0', paths) + "\0\0",
            Flags = 0x0040 // FOF_ALLOWUNDO: to the Recycle Bin, Windows asks if needed
        };
        ShortcutNative.SHFileOperation(ref operation);
        _trashFull = null;
        UpdateTrashIcon();
    }

    private void TrashButton_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = TrashButton, Placement = Services.DockGeometry.Inward };
        menu.Opened += DockContextMenu_Opened;
        menu.Closed += DockContextMenu_Closed;
        var open = Item(UiText.Get("RecycleBinOpen"), "IconOpen");
        open.Click += (_, _) => Widgets.Ui.Open("shell:RecycleBinFolder");
        menu.Items.Add(open);
        var empty = Item(UiText.Get("RecycleBinEmpty"), "IconTrash");
        empty.IsEnabled = _trashFull == true;
        // Windows shows its own "permanently delete?" confirmation.
        empty.Click += (_, _) =>
        {
            ShortcutNative.SHEmptyRecycleBin(_source?.Handle ?? 0, null, 0);
            _trashFull = null;
            UpdateTrashIcon();
        };
        menu.Items.Add(empty);
        menu.IsOpen = true;
    }

    // ---- Shortcuts ---------------------------------------------------------------------------

    /// <summary>Dock apps for the shortcuts saved in settings (added to the pinned list on load).</summary>
    private List<DockApp> LoadCustomShortcuts(IReadOnlyCollection<DockApp> imported)
    {
        var apps = new List<DockApp>();
        foreach (var shortcut in _preferences.Current.Shortcuts ?? [])
        {
            if (imported.Any(a => string.Equals(a.ExecutablePath, shortcut.Target, StringComparison.OrdinalIgnoreCase))) continue;
            apps.Add(new DockApp(shortcut.Name, shortcut.Target) { LaunchArguments = shortcut.Arguments, IsCustomShortcut = true });
        }
        return apps;
    }

    private static System.Windows.Media.Imaging.BitmapSource? ShortcutIcon(string target) =>
        IsWebAddress(target) ? (DefaultBrowser() is { } browser ? ApplicationIconProvider.GetForFile(browser) : null) : ApplicationIconProvider.GetForFile(target);

    private static bool IsWebAddress(string target) =>
        target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string? DefaultBrowser()
    {
        uint length = 1024;
        var path = new StringBuilder((int)length);
        return ShortcutNative.AssocQueryString(0, 2 /* ASSOCSTR_EXECUTABLE */, "https", "open", path, ref length) == 0 ? path.ToString() : null;
    }

    /// <summary>Opens a shortcut that is not an app (file, folder, web page) with its default handler.</summary>
    private bool TryOpenShortcut(DockApp app, FrameworkElement? anchor)
    {
        if (!app.IsCustomShortcut || app.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        Widgets.Ui.Open(app.ExecutablePath);
        return true;
    }

    private void AddShortcutFromFile_Click()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = UiText.Get("ShortcutPickFile"), Multiselect = true, DereferenceLinks = false };
        if (dialog.ShowDialog() == true) AddShortcuts(dialog.FileNames);
    }

    private void AddShortcutFromFolder_Click()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = UiText.Get("ShortcutPickFolder"), Multiselect = true };
        if (dialog.ShowDialog() == true) AddShortcuts(dialog.FolderNames);
    }

    /// <summary>Pins files or folders chosen in the Files window.</summary>
    internal void PinToDock(IEnumerable<string> paths) => AddShortcuts(paths);

    /// <param name="displayName">The name to show (an app pinned from the app screen keeps its Start menu name).</param>
    private void AddShortcuts(IEnumerable<string> paths, string? displayName = null)
    {
        var added = 0;
        foreach (var path in paths)
        {
            var (target, arguments, described) = Describe(path);
            var name = displayName ?? described;
            if (target is null) continue;
            if (_pinnedApps.Any(a => string.Equals(a.ExecutablePath, target, StringComparison.OrdinalIgnoreCase))) continue;
            var app = new DockApp(name, target) { LaunchArguments = arguments, IsCustomShortcut = true, Icon = ShortcutIcon(target) };
            var running = _runningApps.FirstOrDefault(r => string.Equals(r.ExecutablePath, target, StringComparison.OrdinalIgnoreCase));
            if (running is not null)
            {
                app.WindowCount = running.WindowCount;
                _runningApps.Remove(running);
            }
            _pinnedApps.Add(app);
            added++;
        }
        if (added == 0) return;
        SaveShortcuts();
        UpdateRunningAppsDivider();
        ResizeDockToContent();
        _replacementSession?.RefreshPosition();
        Widgets.Ui.ShowHud("IconPin", UiText.Format("ShortcutAdded", added));
    }

    private void SaveShortcuts()
    {
        var shortcuts = _pinnedApps.Where(a => a.IsCustomShortcut).Select(a => new DockShortcut(a.Name, a.ExecutablePath, a.LaunchArguments)).ToList();
        var order = _pinnedApps.Select(a => a.ExecutablePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var error = _preferences.Save(_preferences.Current with { Shortcuts = shortcuts, PinOrder = order });
        if (error is not null) ShowStatus(error);
    }

    private void RemoveShortcut(DockApp app)
    {
        AnimateDockAppRemoval(PinnedApps, _pinnedApps, app, () =>
        {
            SaveShortcuts();
            if (_windowTracker is not null) UpdateRunningApps(_windowTracker.Snapshot);
            ResizeDockToContent();
            _replacementSession?.RefreshPosition();
        });
    }

    /// <summary>What a dropped or picked item opens: .lnk files are followed to their app.</summary>
    private static (string? Target, string? Arguments, string Name) Describe(string path)
    {
        if (IsWebAddress(path))
        {
            var host = Uri.TryCreate(path, UriKind.Absolute, out var uri) ? uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase) : path;
            return (path, null, host);
        }
        if (Directory.Exists(path))
        {
            var name = Path.GetFileName(path.TrimEnd('\\'));
            return (path, null, string.IsNullOrEmpty(name) ? path : name);
        }
        if (!File.Exists(path)) return (null, null, "");
        var title = Path.GetFileNameWithoutExtension(path);
        if (string.Equals(Path.GetExtension(path), ".url", StringComparison.OrdinalIgnoreCase))
        {
            var url = File.ReadLines(path).FirstOrDefault(l => l.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))?[4..];
            return (url is not null && IsWebAddress(url) ? url : path, null, title);
        }
        if (string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase) && ReadLink(path) is { } link
            && link.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(link.Target))
        {
            return (link.Target, string.IsNullOrWhiteSpace(link.Arguments) ? null : link.Arguments, title);
        }
        return (path, null, title);
    }

    private static (string Target, string Arguments)? ReadLink(string path)
    {
        object? shell = null;
        object? link = null;
        try
        {
            if (Type.GetTypeFromProgID("WScript.Shell") is not { } type) return null;
            shell = Activator.CreateInstance(type);
            link = type.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [path]);
            if (link is null) return null;
            var linkType = link.GetType();
            var target = linkType.InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, link, null) as string ?? "";
            var arguments = linkType.InvokeMember("Arguments", System.Reflection.BindingFlags.GetProperty, null, link, null) as string ?? "";
            return (Environment.ExpandEnvironmentVariables(target), arguments);
        }
        catch (Exception ex) when (ex is COMException or System.Reflection.TargetInvocationException)
        {
            return null;
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static bool HasDroppable(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent("UniformResourceLocatorW") || data.GetDataPresent("UniformResourceLocator");

    private void DockSurface_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(DockApp))) return;
        e.Effects = HasDroppable(e.Data) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Drop files, folders, app shortcuts or a link from the browser to add them to the dock.</summary>
    private void DockSurface_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(DockApp))) return;
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            AddShortcuts(files);
            return;
        }
        var url = ReadUrl(e.Data);
        if (url is not null && IsWebAddress(url)) AddShortcuts([url]);
    }

    private static string? ReadUrl(IDataObject data)
    {
        try
        {
            if (data.GetData("UniformResourceLocatorW") is MemoryStream wide)
                return Encoding.Unicode.GetString(wide.ToArray()).TrimEnd('\0').Trim();
            if (data.GetData("UniformResourceLocator") is MemoryStream narrow)
                return Encoding.Default.GetString(narrow.ToArray()).TrimEnd('\0').Trim();
            return data.GetData(DataFormats.UnicodeText) as string;
        }
        catch (COMException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RecycleBinInfo
    {
        public int Size;
        public long Bytes;
        public long Items;
    }

    private static class ShortcutNative
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct FileOperation
        {
            public nint Window;
            public uint Function;
            [MarshalAs(UnmanagedType.LPWStr)] public string From;
            [MarshalAs(UnmanagedType.LPWStr)] public string? To;
            public ushort Flags;
            [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
            public nint NameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHFileOperation(ref FileOperation operation);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHQueryRecycleBin(string? root, ref RecycleBinInfo info);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHEmptyRecycleBin(nint window, string? root, uint flags);
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] public static extern int AssocQueryString(uint flags, int what, string association, string? extra, StringBuilder output, ref uint length);
    }
}
