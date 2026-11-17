using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace QiDock.Services.Notifications;

/// <summary>
/// Keeps Windows' own notification pop-up (bottom right) off screen while QiDock shows the
/// notification at the top, so each notification appears once. Nothing in Windows' settings
/// changes: the pop-up window is only moved away when it appears (sound, the notification center
/// and "Do not disturb" keep working), and it is put back when this stops.
/// The window hooks run on a thread of their own, so the pop-up is moved within milliseconds even
/// while the app's interface thread is busy (starting up, drawing, loading icons).
/// </summary>
public sealed class WindowsToastHider : IDisposable
{
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint EventObjectUncloaked = 0x8018;
    private const uint OutOfContextSkipOwnProcess = 0x0002; // WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS
    private const uint WmQuit = 0x0012;
    private const int OffScreen = -32000;

    // The pop-up's window title in the languages QiDock ships with (the notification center has another title).
    private static readonly HashSet<string> Titles = new(StringComparer.OrdinalIgnoreCase) { "New notification", "Yeni bildirim" };
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase) { "ShellExperienceHost", "ShellHost" };
    private static readonly string LogFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QiDock", "toast-windows.log");

    // Everything below is used on the hook thread only.
    private readonly WinEventProc _callback;
    private readonly Dictionary<nint, NativeRect?> _moved = [];
    private readonly Dictionary<uint, string> _processNames = [];

    private Thread? _thread;
    private uint _threadId;

    public WindowsToastHider() => _callback = OnEvent;

    public void SetEnabled(bool enabled)
    {
        if (enabled == (_thread is not null)) return;
        if (enabled)
        {
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Windows toast hider" };
            _thread.Start();
            ready.Wait(2000);
        }
        else
        {
            PostThreadMessage(_threadId, WmQuit, 0, 0);
            _thread!.Join(2000);
            _thread = null;
        }
    }

    public void Dispose() => SetEnabled(false);

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        // The callbacks arrive through this thread's message queue.
        var hooks = new[]
        {
            SetWinEventHook(EventObjectShow, EventObjectShow, 0, _callback, 0, 0, OutOfContextSkipOwnProcess),
            SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange, 0, _callback, 0, 0, OutOfContextSkipOwnProcess),
            // The pop-up is a shell window that stays shown and is only uncloaked for each notification.
            SetWinEventHook(EventObjectUncloaked, EventObjectUncloaked, 0, _callback, 0, 0, OutOfContextSkipOwnProcess)
        };
        // A pop-up window may already exist (and may still sit off screen if an earlier run did not end cleanly).
        EnumWindows((window, _) =>
        {
            if (IsToast(window, "scan")) Hide(window, "scan");
            return true;
        }, 0);
        ready.Set();

        while (GetMessage(out var message, 0, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        foreach (var hook in hooks)
            if (hook != 0) UnhookWinEvent(hook);
        Restore();
    }

    private void OnEvent(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (objectId != 0 /* OBJID_WINDOW */ || childId != 0 || window == 0) return;
        if (_moved.ContainsKey(window))
        {
            // Windows places the reused pop-up again for each notification.
            if (GetWindowRect(window, out var rect) && rect.Left > OffScreen + 100)
            {
                MoveAway(window);
                if (kind != EventObjectLocationChange) Log($"moved again on {Name(kind)} from {rect.Left},{rect.Top}, {Lag(time)} ms after the event");
            }
            return;
        }
        if (GetAncestor(window, 2 /* GA_ROOT */) != window || !IsToast(window, kind == EventObjectUncloaked ? "trace" : null)) return;
        Hide(window, $"{Name(kind)}, {Lag(time)} ms after the event");
    }

    private void Hide(nint window, string reason)
    {
        // Remember the window even when it is already off screen, so later placements are caught at once.
        var onScreen = GetWindowRect(window, out var bounds) && bounds.Left > OffScreen + 100;
        _moved.TryAdd(window, onScreen ? bounds : null);
        if (onScreen) MoveAway(window);
        Log($"pop-up {window} {(onScreen ? $"moved from {bounds.Left},{bounds.Top}" : "already off screen")} ({reason})");
    }

    private bool IsToast(nint window, string? trace)
    {
        GetWindowThreadProcessId(window, out var pid);
        if (!_processNames.TryGetValue(pid, out var name))
        {
            try { name = Process.GetProcessById((int)pid).ProcessName; }
            catch (ArgumentException) { name = ""; }
            _processNames[pid] = name;
        }
        if (!Hosts.Contains(name)) return false;
        var title = Text(window, GetWindowText);
        if (title.Length == 0) return false;
        var match = Titles.Contains(title);
        // A short trace of shell windows, to recognise the notification pop-up on new Windows builds.
        if (!match && trace == "trace") Log($"{name} class={Text(window, GetClassName)} title='{title}' (not the pop-up)");
        return match;
    }

    private static void MoveAway(nint window) =>
        SetWindowPos(window, 0, OffScreen, OffScreen, 0, 0, 0x0001 /* NOSIZE */ | 0x0004 /* NOZORDER */ | 0x0010 /* NOACTIVATE */ | 0x4000 /* ASYNCWINDOWPOS */);

    private void Restore()
    {
        foreach (var (window, bounds) in _moved)
            if (bounds is { } rect && IsWindow(window)) SetWindowPos(window, 0, rect.Left, rect.Top, 0, 0, 0x0001 | 0x0004 | 0x0010 | 0x4000);
        _moved.Clear();
    }

    private static string Name(uint kind) => kind switch
    {
        EventObjectShow => "show",
        EventObjectUncloaked => "uncloak",
        EventObjectLocationChange => "move",
        _ => kind.ToString("X", System.Globalization.CultureInfo.InvariantCulture)
    };

    /// <summary>How long the event waited before it reached this thread.</summary>
    private static long Lag(uint time) => (uint)Environment.TickCount - time;

    private static void Log(string line)
    {
        try
        {
            if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 64 * 1024) File.Delete(LogFile);
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string Text(nint window, Func<nint, StringBuilder, int, int> read)
    {
        var buffer = new StringBuilder(128);
        read(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMessage { public nint Window; public uint Message; public nint WParam, LParam; public uint Time; public int X, Y; }
    private delegate void WinEventProc(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time);
    private delegate bool EnumWindowsProc(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern int GetMessage(out NativeMessage message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int capacity);
}
