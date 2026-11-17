using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using QiDock.Models;
using QiDock.Services;
using QiDock.Widgets;

namespace QiDock;

/// <summary>
/// The dock's own right-click menu (customize widgets, weather city, position, auto-hide, theme),
/// widget edit mode, the Desktop button, and Start-menu toggling.
/// </summary>
public partial class MainWindow
{
    private static readonly string[] Provinces =
    [
        "Adana", "Adıyaman", "Afyonkarahisar", "Ağrı", "Aksaray", "Amasya", "Ankara", "Antalya", "Ardahan", "Artvin",
        "Aydın", "Balıkesir", "Bartın", "Batman", "Bayburt", "Bilecik", "Bingöl", "Bitlis", "Bolu", "Burdur", "Bursa",
        "Çanakkale", "Çankırı", "Çorum", "Denizli", "Diyarbakır", "Düzce", "Edirne", "Elazığ", "Erzincan", "Erzurum",
        "Eskişehir", "Gaziantep", "Giresun", "Gümüşhane", "Hakkari", "Hatay", "Iğdır", "Isparta", "İstanbul", "İzmir",
        "Kahramanmaraş", "Karabük", "Karaman", "Kars", "Kastamonu", "Kayseri", "Kilis", "Kırıkkale", "Kırklareli",
        "Kırşehir", "Kocaeli", "Konya", "Kütahya", "Malatya", "Manisa", "Mardin", "Mersin", "Muğla", "Muş", "Nevşehir",
        "Niğde", "Ordu", "Osmaniye", "Rize", "Sakarya", "Samsun", "Şanlıurfa", "Siirt", "Sinop", "Şırnak", "Sivas",
        "Tekirdağ", "Tokat", "Trabzon", "Tunceli", "Uşak", "Van", "Yalova", "Yozgat", "Zonguldak"
    ];

    private bool _startWasOpenAtPress;
    private readonly System.Windows.Threading.DispatcherTimer _menuWatch = new(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
    private bool _menuButtonWasDown;
    private bool _menuWatchHooked;
    private bool _desktopIconsHidden;

    // ---- Right-click menu -------------------------------------------------------------------

    /// <summary>
    /// Right-click anywhere on the dock except an app tile opens the dock menu. Opening it here
    /// (instead of through ContextMenuService on the dock surface) makes it work over widgets too.
    /// </summary>
    private void DockHost_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = ParentOf(node))
        {
            if (node is Button { DataContext: DockApp }) return; // app tiles keep their own menu
            if (ReferenceEquals(node, TrashButton)) return; // so does the Recycle Bin
        }

        e.Handled = true;
        ShowDockMenu(PlacementMode.MousePoint, null);
    }

    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private void ShowDockMenu(PlacementMode placement, UIElement? anchor)
    {
        _widgetBar.ClosePopups();
        var current = _preferences.Current;
        var options = current.WidgetOptions;
        var menu = new ContextMenu { PlacementTarget = anchor ?? DockSurface, Placement = placement };
        menu.Opened += DockContextMenu_Opened;
        menu.Closed += DockContextMenu_Closed;

        // The layout first: where the widgets live and which edge the dock stands on.
        var widgetPlace = Item(UiText.Get("WidgetPlacement"), "IconAlignCenter");
        foreach (var (value, key) in new[] { (WidgetPlacement.TopBar, "WidgetPlacementTop"), (WidgetPlacement.DockLeft, "WidgetPlacementDockLeft"), (WidgetPlacement.DockRight, "WidgetPlacementDockRight") })
        {
            var item = Item(UiText.Get(key), null, current.WidgetPlacement == value);
            item.Click += (_, _) => SavePreferences(_preferences.Current with { WidgetPlacement = value });
            widgetPlace.Items.Add(item);
        }
        widgetPlace.IsEnabled = !Services.DockGeometry.Vertical; // a side dock keeps the widgets in the top bar
        menu.Items.Add(widgetPlace);
        var edgeMenu = Item(UiText.Get("DockEdge"), "IconDock");
        foreach (var (value, key) in new[] { (DockEdge.Bottom, "DockEdgeBottom"), (DockEdge.Top, "DockEdgeTop"), (DockEdge.Left, "DockEdgeLeft"), (DockEdge.Right, "DockEdgeRight") })
        {
            var item = Item(UiText.Get(key), null, current.Edge == value);
            item.Click += (_, _) => SavePreferences(_preferences.Current with { Edge = value });
            edgeMenu.Items.Add(item);
        }
        menu.Items.Add(edgeMenu);
        menu.Items.Add(new Separator());

        // Widgets: tick to show / untick to hide.
        var widgets = Item(UiText.Get("MenuWidgets"), "IconLayout");
        foreach (var id in options.EffectiveOrder)
        {
            var visible = options.IsVisible(id);
            var item = Item(UiText.Get("Widget_" + id), null, visible);
            item.Click += (_, _) => SaveWidgetOptions(visible
                ? options with { Hidden = (options.Hidden ?? []).Append(id).Distinct().ToList() }
                : options with { Hidden = (options.Hidden ?? []).Where(h => h != id).ToList() });
            widgets.Items.Add(item);
        }
        menu.Items.Add(widgets);


        var edit = Item(UiText.Get(_widgetBar.IsEditing ? "MenuEditDone" : "MenuEditDock"), "IconEdit");
        edit.Click += (_, _) => _widgetBar.SetEditing(!_widgetBar.IsEditing);
        menu.Items.Add(edit);

        // Dock shortcuts: apps, files, folders (web links: drag them from the browser onto the dock).
        var shortcuts = Item(UiText.Get("ShortcutAdd"), "IconPlus");
        var file = Item(UiText.Get("ShortcutAddFile"), "IconFile");
        file.Click += (_, _) => AddShortcutFromFile_Click();
        shortcuts.Items.Add(file);
        var folder = Item(UiText.Get("ShortcutAddFolder"), "IconFolder");
        folder.Click += (_, _) => AddShortcutFromFolder_Click();
        shortcuts.Items.Add(folder);
        var hint = Item(UiText.Get("ShortcutDropHint"), "IconLink");
        hint.IsEnabled = false;
        shortcuts.Items.Add(hint);
        menu.Items.Add(shortcuts);

        var desktopWidgets = Item(DesktopWidgets.Desk.L("Masaüstüne widget ekle…", "Add desktop widgets…"), "IconApps");
        desktopWidgets.Click += (_, _) => ShowDesktopWidgetGallery();
        menu.Items.Add(desktopWidgets);

        // Weather city: all 81 provinces, plus "other" for districts or foreign cities.
        var city = Item(string.Format(UiText.Get("MenuCity"), options.WeatherCity), "IconMapPin");
        foreach (var province in Provinces)
        {
            var item = Item(province, null, string.Equals(province, options.WeatherCity, StringComparison.CurrentCultureIgnoreCase));
            item.Click += (_, _) => SaveWidgetOptions(options with { WeatherCity = province });
            city.Items.Add(item);
        }
        city.Items.Add(new Separator());
        var other = Item(UiText.Get("MenuCityOther"), "IconSettings");
        other.Click += (_, _) => WidgetBar_SettingsRequested(this, EventArgs.Empty);
        city.Items.Add(other);
        menu.Items.Add(city);
        menu.Items.Add(new Separator());


        var position = Item(UiText.Get("MenuPosition"), "IconAlignCenter");
        foreach (var (value, key) in new[] { (DockPosition.Left, "Left"), (DockPosition.Center, "Center"), (DockPosition.Right, "Right") })
        {
            var item = Item(UiText.Get(key), null, current.Position == value);
            item.Click += (_, _) => SavePreferences(_preferences.Current with { Position = value });
            position.Items.Add(item);
        }
        menu.Items.Add(position);

        var autoHide = Item(UiText.Get("MenuAutoHide"), "IconDock");
        foreach (var (value, key) in new[] { (AutoHideMode.Disabled, "MenuAutoHideOff"), (AutoHideMode.Smart, "MenuAutoHideSmart"), (AutoHideMode.Always, "MenuAutoHideAlways") })
        {
            var item = Item(UiText.Get(key), null, current.AutoHide == value);
            item.Click += (_, _) => SavePreferences(_preferences.Current with { AutoHide = value });
            autoHide.Items.Add(item);
        }
        menu.Items.Add(autoHide);

        var theme = Item(UiText.Get("MenuTheme"), "IconMoon");
        foreach (var (value, key) in new[] { (AppTheme.Dark, "Dark"), (AppTheme.Light, "Light") })
        {
            var item = Item(UiText.Get(key), null, current.Theme == value);
            item.Click += (_, _) => SavePreferences(_preferences.Current with { Theme = value });
            theme.Items.Add(item);
        }
        // Looks: Glass follows the dark/light choice above, the others bring their own colours.
        theme.Items.Add(new Separator());
        foreach (var look in ThemeStyles.All)
        {
            var item = Item(look.Name, null, ThemeStyles.Find(current.Style).Id == look.Id);
            item.Click += (_, _) => SavePreferences(_preferences.Current with { Style = look.Id });
            theme.Items.Add(item);
        }
        menu.Items.Add(theme);

        var cleanDesktop = Item(UiText.Get("MenuHideDesktopIcons"), "IconMonitor", current.HideDesktopIcons);
        cleanDesktop.Click += (_, _) => SavePreferences(_preferences.Current with { HideDesktopIcons = !_preferences.Current.HideDesktopIcons });
        menu.Items.Add(cleanDesktop);

        var peek = Item(UiText.Get("MenuTopBarPeek"), "IconLayout", current.TopBarPeek);
        peek.Click += (_, _) => SavePreferences(_preferences.Current with { TopBarPeek = !_preferences.Current.TopBarPeek });
        peek.IsEnabled = current.WidgetPlacement == WidgetPlacement.TopBar;
        menu.Items.Add(peek);

        var genie = Item(UiText.Get("GenieEffect"), "IconSparkle", current.GenieEffectEnabled);
        genie.Click += (_, _) => SavePreferences(_preferences.Current with { GenieEffectEnabled = !_preferences.Current.GenieEffectEnabled });
        genie.IsEnabled = current.AnimationsEnabled; // the effect needs animations; on without them it would do nothing
        menu.Items.Add(genie);
        menu.Items.Add(new Separator());

        var settings = Item(UiText.Get("Settings"), "IconSettings");
        settings.Click += OpenSettings_Click;
        menu.Items.Add(settings);
        menu.Items.Add(new Separator());
        var exit = Item(UiText.Get("Exit"), "IconExit");
        exit.Click += ExitQiDock_Click;
        menu.Items.Add(exit);

        menu.IsOpen = true;
    }

    /// <summary>
    /// The dock never takes activation, so as soon as a menu activates itself Windows moves mouse capture
    /// and a StaysOpen=false menu closes again within a frame. Menus therefore stay open on their own and
    /// close here: on a click outside them (another app, the desktop or the dock) or after an item runs.
    /// </summary>
    private void BeginMenuWatch(ContextMenu menu)
    {
        menu.StaysOpen = true;
        if (!_menuWatchHooked)
        {
            _menuWatchHooked = true;
            _menuWatch.Tick += (_, _) => WatchMenu();
        }
        if (menu.Tag as string != "watched")
        {
            menu.Tag = "watched";
            menu.AddHandler(MenuItem.ClickEvent, new RoutedEventHandler((_, e) =>
            {
                if (e.OriginalSource is MenuItem { HasItems: false, StaysOpenOnClick: false }) menu.IsOpen = false;
            }));
            menu.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) menu.IsOpen = false;
            };
        }
        _menuButtonWasDown = AnyMouseButtonDown();
        _menuWatch.Start();
    }

    private void WatchMenu()
    {
        if (_openContextMenu is not { IsOpen: true } menu)
        {
            _menuWatch.Stop();
            return;
        }
        var down = AnyMouseButtonDown();
        var pressed = down && !_menuButtonWasDown;
        _menuButtonWasDown = down;
        if (!pressed || !DeskNative.GetCursorPos(out var cursor)) return;
        var root = DeskNative.GetAncestor(DeskNative.WindowFromPoint(cursor), 2);
        DeskNative.GetWindowThreadProcessId(root, out var processId);
        // Inside the menu or one of its submenus (our own popup windows): leave it to the menu.
        if (processId == Environment.ProcessId && root != (_source?.Handle ?? 0) && root != (_topBar?.Handle ?? 0)) return;
        menu.IsOpen = false;
    }

    private static bool AnyMouseButtonDown() =>
        (DeskNative.GetAsyncKeyState(0x01) & 0x8000) != 0 || (DeskNative.GetAsyncKeyState(0x02) & 0x8000) != 0 || (DeskNative.GetAsyncKeyState(0x04) & 0x8000) != 0;

    private static MenuItem Item(string header, string? icon, bool? isChecked = null)
    {
        var item = new MenuItem { Header = header.Replace("_", "__") };
        if (icon is not null && Application.Current.TryFindResource(icon) is Geometry geometry) item.Icon = geometry;
        if (isChecked is { } value) item.IsChecked = value;
        return item;
    }

    private void SavePreferences(DockSettings settings)
    {
        if (_preferences.Save(settings) is { } error) ShowStatus(error);
    }

    private void SaveWidgetOptions(WidgetSettings options) => SavePreferences(_preferences.Current with { Widgets = options });

    private void WidgetBar_SettingsEdited(object? sender, WidgetSettings options) => SaveWidgetOptions(options);

    /// <summary>"Add widget" pill in edit mode: lists the hidden widgets.</summary>
    private void WidgetBar_AddRequested(object? sender, FrameworkElement anchor)
    {
        var options = _preferences.Current.WidgetOptions;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = Services.DockGeometry.Inward };
        menu.Opened += DockContextMenu_Opened;
        menu.Closed += DockContextMenu_Closed;
        foreach (var id in options.EffectiveOrder.Where(id => !options.IsVisible(id)))
        {
            var item = Item(UiText.Get("Widget_" + id), "IconPlus");
            item.Click += (_, _) => _widgetBar.ShowWidget(id);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = UiText.Get("EditAllShown"), IsEnabled = false });
        menu.IsOpen = true;
    }

    private void WidgetBar_EditingChanged(object? sender, EventArgs e)
    {
        // Editing happens in the top bar, which stays on screen until "Done" even over open windows.
        UpdateTopBarContent();
    }

    // ---- Start button -----------------------------------------------------------------------

    // Windows dismisses Start itself on the mouse-down outside it, before our click arrives, so a
    // second click would reopen it. Track when Start was last open and treat a click right after
    // it closed as "close", like the Windows taskbar.
    private bool _startOpen;
    private DateTime _startClosedAt;

    private void TrackStartMenu(nint foreground)
    {
        var open = IsStartMenuWindow(foreground);
        if (_startOpen && !open) _startClosedAt = DateTime.UtcNow;
        _startOpen = open;
    }

    private void StartButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _startWasOpenAtPress = IsStartMenuOpen() || _startOpen || DateTime.UtcNow - _startClosedAt < TimeSpan.FromMilliseconds(600);

    /// <summary>The Start menu (or search) is the foreground window.</summary>
    private static bool IsStartMenuOpen() => IsStartMenuWindow(DeskNative.GetForegroundWindow());

    private static bool IsStartMenuWindow(nint foreground)
    {
        if (foreground == 0) return false;
        DeskNative.GetWindowThreadProcessId(foreground, out var processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName is "StartMenuExperienceHost" or "SearchHost" or "SearchApp" or "ShellExperienceHost";
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Second click on Start closes the menu, like the Windows taskbar.</summary>
    private bool CloseStartMenuIfOpen()
    {
        if (!_startWasOpenAtPress) return false;
        _startWasOpenAtPress = false;
        _startClosedAt = default;
        if (IsStartMenuOpen())
        {
            const byte vkEscape = 0x1B;
            DeskNative.keybd_event(vkEscape, 0, 0, 0);
            DeskNative.keybd_event(vkEscape, 0, 2, 0);
        }
        return true;
    }

    // ---- Desktop ----------------------------------------------------------------------------

    // True while the Desktop button has the desktop showing (windows minimized, icons visible).
    private bool _desktopShown;
    private DateTime _desktopShownAt;

    private void ToggleDesktop()
    {
        // Straight to Explorer's taskbar (what Win+D does): asking the shell to do it would find the dock's own
        // hidden tray window, which holds the background app icons, first.
        if (TrayHost.FindExplorerTaskbar() is var taskbar and not 0 && PostMessage(taskbar, 0x0111 /* WM_COMMAND */, 407 /* toggle desktop */, 0))
            return;
        object? shell = null;
        try
        {
            if (Type.GetTypeFromProgID("Shell.Application") is not { } type) return;
            shell = Activator.CreateInstance(type);
            type.InvokeMember("ToggleDesktop", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
        }
        catch (Exception ex) when (ex is COMException or System.Reflection.TargetInvocationException or InvalidCastException)
        {
            Trace.TraceWarning("Show desktop failed: {0}", ex.Message);
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
        }
    }

    /// <summary>Keeps the desktop clean whenever another window comes forward.</summary>
    private void KeepDesktopClean(nint foreground)
    {
        if (_desktopShown || _cleanShown)
        {
            // Either desktop ends only when a real app window comes forward. While Windows moves the
            // windows aside, focus passes through the taskbar and passing shell windows: those must not
            // hide the icons again (that made both desktop buttons look the same).
            if (DateTime.UtcNow - _desktopShownAt < TimeSpan.FromMilliseconds(900) || !IsAppWindow(foreground)) return;
            LeaveDesktop();
        }
        if (!_preferences.Current.HideDesktopIcons) return;
        ApplyDesktopIcons(true);
    }

    private static bool IsDesktopWindow(nint window)
    {
        if (window == 0) return false;
        var name = new System.Text.StringBuilder(32);
        DeskNative.GetClassName(window, name, name.Capacity);
        return name.ToString() is "Progman" or "WorkerW";
    }

    /// <summary>A window the user works in: shown, not minimized, not a tool window, not part of the shell.</summary>
    private static bool IsAppWindow(nint window)
    {
        if (window == 0 || !DeskNative.IsWindowVisible(window) || DeskNative.IsIconic(window)) return false;
        if ((DeskNative.GetWindowLongPtr(window, -20).ToInt64() & 0x00000080) != 0) return false; // WS_EX_TOOLWINDOW
        var name = new System.Text.StringBuilder(64);
        DeskNative.GetClassName(window, name, name.Capacity);
        return name.ToString() is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "ForegroundStaging" or "XamlExplorerHostIslandWindow" or "ApplicationManager_DesktopShellWindow"
            or "MultitaskingViewFrame" or "TaskListThumbnailWnd" or "NotifyIconOverflowWindow"
            or "TopLevelWindowForOverflowXamlIsland" or "Windows.UI.Core.CoreWindow");
    }

    /// <summary>
    /// Right after Windows starts, the dock can be up before Explorer has made the desktop (or Explorer makes it
    /// again a moment later). For the first two minutes the clean desktop is checked every few seconds: the icons
    /// are hidden once there, and the desktop widgets are tied to the desktop once it exists.
    /// </summary>
    private void WatchDesktopWhileStarting()
    {
        var started = DateTime.UtcNow;
        var hadDesktop = DeskNative.FindWindow("Progman", null) != 0;
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow - started > TimeSpan.FromMinutes(2)) timer.Stop();
            if (!hadDesktop && DeskNative.FindWindow("Progman", null) != 0)
            {
                hadDesktop = true;
                _deskWidgets?.Reattach();
            }
            if (_preferences.Current.HideDesktopIcons && !_desktopShown && FindDesktopListView() is var view and not 0 && DeskNative.IsWindowVisible(view))
                ApplyDesktopIcons(true);
        };
        timer.Start();
    }

    /// <summary>Hides (or shows again) the icons on the Windows desktop so only the wallpaper remains.</summary>
    private void ApplyDesktopIcons(bool hide)
    {
        var view = FindDesktopListView();
        if (view == 0) return;
        var visible = DeskNative.IsWindowVisible(view);
        if (hide)
        {
            if (visible) DeskNative.ShowWindow(view, 0);
            _desktopIconsHidden = true;
            MarkDesktopIconsHidden(true);
            return;
        }
        // Also when an earlier run hid them and never came back (crash, killed): the mark on disk says so.
        if (!visible && (_desktopIconsHidden || File.Exists(DesktopIconsMark))) DeskNative.ShowWindow(view, 5);
        _desktopIconsHidden = false;
        MarkDesktopIconsHidden(false);
    }

    private static readonly string DesktopIconsMark = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QiDock", "desktop-icons-hidden");

    private static void MarkDesktopIconsHidden(bool hidden)
    {
        try
        {
            if (hidden)
            {
                if (File.Exists(DesktopIconsMark)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(DesktopIconsMark)!);
                File.WriteAllText(DesktopIconsMark, "");
            }
            else if (File.Exists(DesktopIconsMark)) File.Delete(DesktopIconsMark);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static nint FindDesktopListView()
    {
        var defView = DeskNative.FindWindowEx(DeskNative.FindWindow("Progman", null), 0, "SHELLDLL_DefView", null);
        if (defView == 0)
        {
            // After a wallpaper slideshow or Win+Tab, Explorer re-parents the view under a WorkerW.
            nint worker = 0;
            while ((worker = DeskNative.FindWindowEx(0, worker, "WorkerW", null)) != 0)
            {
                defView = DeskNative.FindWindowEx(worker, 0, "SHELLDLL_DefView", null);
                if (defView != 0) break;
            }
        }
        return defView == 0 ? 0 : DeskNative.FindWindowEx(defView, 0, "SysListView32", null);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    private static class DeskNative
    {
        [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsIconic(nint window);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint window, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(nint window, System.Text.StringBuilder name, int count);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetCursorPos(out NativePoint point);
        [DllImport("user32.dll")] public static extern nint WindowFromPoint(NativePoint point);
        [DllImport("user32.dll")] public static extern nint GetAncestor(nint window, uint flags);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint window, out uint processId);
        [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, nint extra);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint FindWindow(string? className, string? title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint FindWindowEx(nint parent, nint after, string? className, string? title);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ShowWindow(nint window, int command);
    }
}
