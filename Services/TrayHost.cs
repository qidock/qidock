using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace QiDock.Services;

/// <summary>An icon an app keeps in the notification area (the apps running in the background).</summary>
public sealed class TrayIcon
{
    internal TrayIcon(nint window, uint id, Guid? guid)
    {
        Window = window;
        Id = id;
        Guid = guid;
        GetWindowThreadProcessId(window, out var pid);
        ProcessId = (int)pid;
        (Path, Name) = Describe(ProcessId);
    }

    public nint Window { get; }
    public uint Id { get; }
    public Guid? Guid { get; }
    public int ProcessId { get; }
    public string? Path { get; }

    /// <summary>The app's own name ("Claude"), from its program file; the tooltip when there is none.</summary>
    public string Name { get; }

    public string Tip { get; internal set; } = "";
    public ImageSource? Image { get; internal set; }
    internal nint Handle { get; set; }
    public uint CallbackMessage { get; internal set; }
    public uint Version { get; internal set; }
    public bool Hidden { get; internal set; }

    // Windows' own helpers have plain program names ("Windows Gezgini"); their tooltip says what they are.
    public string Title => Name.Length > 0 && (CanClose || Tip.Length == 0) ? Name : FirstLine(Tip);
    public string Detail => Tip.Length > 0 && !string.Equals(FirstLine(Tip), Title, StringComparison.CurrentCultureIgnoreCase) ? Tip.Replace("\r", "").Replace('\n', ' ') : "";

    /// <summary>Windows' own helpers (explorer and the like) are shown but not closed from the dock.</summary>
    public bool CanClose => ProcessId > 0 && ProcessId != Environment.ProcessId && Path is { } path
        && !path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase);

    public bool IsAlive => IsWindow(Window);

    internal bool Matches(nint window, uint id, Guid? guid) => guid is { } g ? Guid == g : Guid is null && Window == window && Id == id;

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    private static (string? Path, string Name) Describe(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var path = process.MainModule?.FileName;
            if (path is null) return (null, process.ProcessName);
            var info = FileVersionInfo.GetVersionInfo(path);
            var name = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription.Trim()
                : !string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName.Trim()
                : System.IO.Path.GetFileNameWithoutExtension(path);
            return (path, name);
        }
        catch
        {
            // A process running as administrator cannot be read from here.
            try
            {
                using var process = Process.GetProcessById(pid);
                return (null, process.ProcessName);
            }
            catch { return (null, ""); }
        }
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hWnd);
}

/// <summary>
/// Stands in for the notification area while the Windows taskbar is hidden. Apps put their icons in the area by
/// sending them to the first window named "Shell_TrayWnd"; this host opens a hidden window of that name in front of
/// Explorer's, keeps a copy of every icon and passes each message on to Explorer, so Explorer's own area stays exactly
/// as it was. When the host closes, apps simply talk to Explorer again.
/// </summary>
public sealed class TrayHost
{
    public static TrayHost Shared { get; } = new();

    private const int WmCopyData = 0x004A, WmUser = 0x0400;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifState = 0x8, NifGuid = 0x20;
    private const uint NisHidden = 0x1;
    private const int Magic = 0x34753423;

    private readonly List<TrayIcon> _icons = [];
    private WndProc? _proc; // kept alive while the window lives
    private nint _tray, _notify;
    private uint _taskbarCreated;
    private DispatcherTimer? _keepInFront;

    /// <summary>The icon list changed (an app came, went or changed its picture).</summary>
    public event EventHandler? Changed;

    private DispatcherTimer? _notifyTimer;
    private bool _urgent;

    /// <summary>
    /// Tells the dock about a change, gathered: an app coming or going shows at once, a new picture or tooltip at
    /// most every two seconds (some apps swap their icon several times a second).
    /// </summary>
    private void Notify(bool urgent)
    {
        _urgent |= urgent;
        if (_notifyTimer is null)
        {
            _notifyTimer = new DispatcherTimer(DispatcherPriority.Background);
            _notifyTimer.Tick += (_, _) =>
            {
                _notifyTimer.Stop();
                _urgent = false;
                Changed?.Invoke(this, EventArgs.Empty);
            };
        }
        var wait = TimeSpan.FromMilliseconds(_urgent ? 150 : 2000);
        if (_notifyTimer.IsEnabled && _notifyTimer.Interval <= wait) return;
        _notifyTimer.Stop();
        _notifyTimer.Interval = wait;
        _notifyTimer.Start();
    }

    public bool IsRunning => _tray != 0;

    /// <summary>Visible icons of apps that are still running, in the order they came.</summary>
    public IReadOnlyList<TrayIcon> Icons
    {
        get
        {
            if (_icons.RemoveAll(icon => !icon.IsAlive) > 0) Changed?.Invoke(this, EventArgs.Empty);
            return _icons.Where(icon => !icon.Hidden && icon.ProcessId != Environment.ProcessId).ToList();
        }
    }

    /// <summary>Explorer's taskbar, never this host's stand-in window.</summary>
    public static nint FindExplorerTaskbar()
    {
        var own = Shared._tray;
        nint found = 0;
        while ((found = FindWindowEx(0, found, "Shell_TrayWnd", null)) != 0)
        {
            if (found != own) return found;
        }
        return 0;
    }

    public void Start()
    {
        if (_tray != 0) return;
        try
        {
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            _proc = Procedure;
            var instance = GetModuleHandle(null);
            var cls = new WndClassEx { cbSize = (uint)Marshal.SizeOf<WndClassEx>(), lpfnWndProc = _proc, hInstance = instance, lpszClassName = "Shell_TrayWnd" };
            RegisterClassEx(ref cls);
            var notify = new WndClassEx { cbSize = (uint)Marshal.SizeOf<WndClassEx>(), lpfnWndProc = _proc, hInstance = instance, lpszClassName = "TrayNotifyWnd" };
            RegisterClassEx(ref notify);
            const uint popup = 0x80000000, clipChildren = 0x02000000, clipSiblings = 0x04000000, child = 0x40000000;
            const uint topmost = 0x8, toolWindow = 0x80;
            _tray = CreateWindowEx(topmost | toolWindow, "Shell_TrayWnd", "", popup | clipChildren | clipSiblings, 0, 0, 0, 0, 0, 0, instance, 0);
            if (_tray == 0)
            {
                AppLog.Write($"Arka plan simgeleri alınamadı (pencere açılmadı, hata {Marshal.GetLastWin32Error()})");
                return;
            }
            _notify = CreateWindowEx(0, "TrayNotifyWnd", null, child | clipChildren | clipSiblings, 0, 0, 0, 0, _tray, 0, instance, 0);
            KeepInFront();
            _keepInFront = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            _keepInFront.Tick += (_, _) => KeepInFront();
            _keepInFront.Start();
            AskAppsAgain();
            AppLog.Write("Arka plan simgeleri dock'a alındı");
            // Some apps (Electron ones such as Claude) lose their re-add while the dock is still busy starting, so
            // they are asked once more when things are calm; an app asked twice just puts its icon in again.
            var again = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(6) };
            again.Tick += (_, _) =>
            {
                again.Stop();
                if (_tray == 0) return;
                AskAppsAgain();
                var report = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                report.Tick += (_, _) =>
                {
                    report.Stop();
                    AppLog.Write($"Arka planda {Icons.Count} simge: {string.Join(", ", Icons.Select(i => i.Title))}");
                };
                report.Start();
            };
            again.Start();
        }
        catch (Exception ex)
        {
            AppLog.Write($"Arka plan simgeleri alınamadı: {ex.Message}");
            Stop();
        }
    }

    public void Stop()
    {
        _keepInFront?.Stop();
        _keepInFront = null;
        if (_tray != 0) DestroyWindow(_tray); // its child goes with it
        _tray = _notify = 0;
        _icons.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Explorer started again: stand in front of its new window and collect the icons once more.</summary>
    public void ExplorerRestarted()
    {
        if (_tray == 0) return;
        _icons.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            KeepInFront();
            AskAppsAgain();
        };
        timer.Start();
    }

    /// <summary>
    /// Every app with an icon puts it in again when it hears that the taskbar was created. Explorer is left out:
    /// hearing it, Explorer brings its own taskbar window to the front again and the icons would go back to it.
    /// </summary>
    private void AskAppsAgain()
    {
        var explorer = FindExplorerTaskbar();
        if (explorer == 0) return; // Explorer is not up yet; its own TaskbarCreated will ask everyone
        GetWindowThreadProcessId(explorer, out var shell);
        var own = (uint)Environment.ProcessId;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner != shell && owner != own) SendNotifyMessage(window, _taskbarCreated, 0, 0);
            return true;
        }, 0);
    }

    /// <summary>Apps send their icons to the first "Shell_TrayWnd" in the window order, so this one stays first.</summary>
    private void KeepInFront()
    {
        if (_tray == 0) return;
        const uint noSize = 0x1, noMove = 0x2, noActivate = 0x10;
        var explorer = FindExplorerTaskbar();
        if (explorer != 0 && GetWindowRect(explorer, out var rect))
            SetWindowPos(_tray, -1, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, noActivate);
        else
            SetWindowPos(_tray, -1, 0, 0, 0, 0, noSize | noMove | noActivate);
    }

    private nint Procedure(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (hwnd == _tray && message == WmCopyData && lParam != 0)
        {
            var data = Marshal.PtrToStructure<CopyData>(lParam);
            if (data.dwData == 1 && data.lpData != 0 && data.cbData >= 8 + 32)
            {
                var handled = false;
                try { handled = Take(data.lpData, data.cbData); }
                catch (Exception ex) { AppLog.Write($"Arka plan simgesi okunamadı: {ex.Message}"); }
                Forward(message, wParam, lParam);
                return handled ? 1 : 0;
            }
            // Desk bars (dwData 0) and icon places (3) are answered by Explorer itself.
            return Forward(message, wParam, lParam);
        }
        if (hwnd == _tray && message >= WmUser && message < 0x8000) return Forward(message, wParam, lParam);
        // Taskbar commands ("show desktop" / Win+D, minimize all, undo minimize all, cascade...) are sent to the first
        // Shell_TrayWnd found, which is now this one; Explorer's own taskbar is the one that carries them out.
        if (hwnd == _tray && message == 0x0111 /* WM_COMMAND */)
        {
            var explorer = FindExplorerTaskbar();
            if (explorer != 0) PostMessage(explorer, message, wParam, lParam);
            return 0;
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private static nint Forward(uint message, nint wParam, nint lParam)
    {
        var explorer = FindExplorerTaskbar();
        if (explorer == 0) return 0;
        const uint abortIfHung = 0x2;
        return SendMessageTimeout(explorer, message, wParam, lParam, abortIfHung, 2000, out var result) != 0 ? result : 0;
    }

    /// <summary>
    /// Reads one icon message. The layout is the 32-bit one Windows uses between processes: a magic number, the
    /// command, then NOTIFYICONDATA with every handle cut to 32 bits.
    /// </summary>
    private bool Take(nint data, int size)
    {
        const int n = 8; // NOTIFYICONDATA starts here
        if (size < n + 24 || Marshal.ReadInt32(data, 0) != Magic) return false;
        var command = (uint)Marshal.ReadInt32(data, 4);
        var window = (nint)(uint)Marshal.ReadInt32(data, n + 4);
        var id = (uint)Marshal.ReadInt32(data, n + 8);
        var flags = (uint)Marshal.ReadInt32(data, n + 12);
        Guid? guid = null;
        if ((flags & NifGuid) != 0 && size >= n + 952)
        {
            var bytes = new byte[16];
            Marshal.Copy(data + n + 936, bytes, 0, 16);
            guid = new Guid(bytes);
        }
        var icon = _icons.FirstOrDefault(i => i.Matches(window, id, guid));

        switch (command)
        {
            case NimDelete:
                if (icon is null) return false;
                _icons.Remove(icon);
                Notify(urgent: true);
                return true;
            case NimSetVersion:
                if (icon is null || size < n + 804) return false;
                icon.Version = (uint)Marshal.ReadInt32(data, n + 800);
                return true;
            case NimAdd:
            case NimModify:
                var urgent = false;
                if (icon is null)
                {
                    // A change to an icon added before this host opened counts as adding it, if it brings a picture.
                    if (command == NimModify && (flags & NifIcon) == 0) return false;
                    if (!IsWindow(window)) return false;
                    icon = new TrayIcon(window, id, guid);
                    _icons.Add(icon);
                    urgent = true;
                }
                else if (command == NimAdd) return false;
                var changed = urgent;
                if ((flags & NifMessage) != 0) icon.CallbackMessage = (uint)Marshal.ReadInt32(data, n + 16);
                if ((flags & NifIcon) != 0 && size >= n + 24)
                {
                    // Some apps send the same picture several times a second; only a new one is copied.
                    var handle = (nint)(uint)Marshal.ReadInt32(data, n + 20);
                    if (handle != icon.Handle || icon.Image is null)
                    {
                        icon.Handle = handle;
                        icon.Image = Picture(handle) ?? icon.Image;
                        changed = true;
                    }
                }
                if ((flags & NifTip) != 0 && size >= n + 24 + 256)
                {
                    var tip = (Marshal.PtrToStringUni(data + n + 24, 128) ?? "").Split('\0')[0];
                    changed |= tip != icon.Tip;
                    icon.Tip = tip;
                }
                if ((flags & NifState) != 0 && size >= n + 288)
                {
                    var state = (uint)Marshal.ReadInt32(data, n + 280);
                    var mask = (uint)Marshal.ReadInt32(data, n + 284);
                    var hidden = (mask & NisHidden) != 0 ? (state & NisHidden) != 0 : icon.Hidden;
                    if (hidden != icon.Hidden)
                    {
                        icon.Hidden = hidden;
                        changed = urgent = true;
                    }
                }
                if (changed) Notify(urgent);
                return true;
            default:
                return icon is not null;
        }
    }

    /// <summary>Copies the app's icon at once: the app may destroy its handle right after sending it.</summary>
    private static ImageSource? Picture(nint handle)
    {
        if (handle == 0) return null;
        var copy = CopyIcon(handle);
        if (copy == 0) return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(copy, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch { return null; }
        finally { DestroyIcon(copy); }
    }

    // ---- What a click on an icon tells its app ----

    private const uint WmContextMenu = 0x007B, WmLButtonDown = 0x0201, WmLButtonUp = 0x0202, WmLButtonDblClk = 0x0203;
    private const uint WmRButtonDown = 0x0204, WmRButtonUp = 0x0205, NinSelect = WmUser;

    /// <summary>A left click on the icon, as the taskbar would send it (most apps open their window).</summary>
    public static void Click(TrayIcon icon) => Send(icon, icon.Version >= 4 ? [WmLButtonDown, WmLButtonUp, NinSelect] : [WmLButtonDown, WmLButtonUp]);

    /// <summary>A double click (some older apps open only on this).</summary>
    public static void DoubleClick(TrayIcon icon) => Send(icon, [WmLButtonDblClk, WmLButtonUp]);

    /// <summary>A right click: the app opens its own menu (with its "Quit" item) at the pointer.</summary>
    public static void RightClick(TrayIcon icon) => Send(icon, icon.Version >= 4 ? [WmRButtonDown, WmRButtonUp, WmContextMenu] : [WmRButtonDown, WmRButtonUp]);

    private static void Send(TrayIcon icon, uint[] messages)
    {
        if (icon.CallbackMessage == 0 || !icon.IsAlive) return;
        // The app brings its window or menu to the front itself; it may, since the click came to us.
        AllowSetForegroundWindow(icon.ProcessId);
        GetCursorPos(out var point);
        foreach (var message in messages)
        {
            if (icon.Version >= 4)
                PostMessage(icon.Window, icon.CallbackMessage, (nint)((point.Y << 16) | (point.X & 0xFFFF)), (nint)(((int)icon.Id << 16) | (int)(message & 0xFFFF)));
            else
                PostMessage(icon.Window, icon.CallbackMessage, (nint)icon.Id, (nint)message);
        }
    }

    /// <summary>
    /// Asks the app to close: every window of the program is told to close. Returns false when it cannot be asked.
    /// Apps that only hide into the tray may keep running; <see cref="ForceClose"/> ends them.
    /// </summary>
    public static bool AskToClose(TrayIcon icon)
    {
        if (!icon.CanClose) return false;
        const uint wmClose = 0x0010;
        var pid = (uint)icon.ProcessId;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner == pid) PostMessage(window, wmClose, 0, 0);
            return true;
        }, 0);
        return true;
    }

    public static bool IsRunningProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch { return false; }
    }

    /// <summary>
    /// Ends the program: its own process and the other processes of the same program file (helpers of apps such as
    /// Electron ones). Programs it started (a launcher's apps, say) are left alone. Returns an error message, or null.
    /// </summary>
    public static string? ForceClose(TrayIcon icon)
    {
        if (!icon.CanClose) return UiText.Get("TrayCannotClose");
        try
        {
            using var process = Process.GetProcessById(icon.ProcessId);
            var name = process.ProcessName;
            var path = icon.Path;
            process.Kill();
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var other in Process.GetProcessesByName(name))
                {
                    using (other)
                    {
                        try
                        {
                            if (other.Id != Environment.ProcessId && string.Equals(other.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))
                                other.Kill();
                        }
                        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                    }
                }
            }
            return null;
        }
        catch (ArgumentException) { return null; } // already gone
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            AppLog.Write($"Arka plan uygulaması kapatılamadı ({icon.Name}): {ex.Message}");
            return UiText.Get("TrayCannotClose");
        }
    }

    // ---- Windows ----

    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);
    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyData
    {
        public nint dwData;
        public int cbData;
        public nint lpData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WndClassEx cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string? windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern nint DefWindowProc(nint hWnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string className, string? windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool SendNotifyMessage(nint hWnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeout(nint hWnd, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hWnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hWnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern nint CopyIcon(nint icon);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
