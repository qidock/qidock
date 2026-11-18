using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using QiDock.Models;

namespace QiDock.Services;

public sealed class NativeWindowTracker : IDisposable
{
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectNameChange = 0x800C;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint EventSystemAlert = 0x0002;
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const int ObjectWindow = 0;
    private const int ChildIdSelf = 0;
    private const int WindowOwner = 4;
    private const int GwlExStyle = -20;
    private const int GwlStyle = -16;
    private const long WsPopup = 0x80000000;
    private const long WsMinimizeBox = 0x00020000;
    private const long WsExAppWindow = 0x00040000;
    private const long WsExToolWindow = 0x00000080;
    private const uint GaRootOwner = 3;
    private const uint GaRoot = 2;
    private const uint WmClose = 0x0010;
    private const int ShowRestore = 9;
    private const int ShowMinimize = 6;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private static readonly string WindowsSystemAppsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps") + Path.DirectorySeparatorChar;

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _refreshTimer;
    private readonly WinEventDelegate _eventCallback;
    private readonly EnumWindowsDelegate _enumCallback;
    private readonly HashSet<nint> _notificationWindows = [];
    private nint _windowHook;
    private nint _nameChangeHook;
    private nint _systemEventHook;
    private nint _excludedWindow;
    private int _refreshQueued;
    private int _urgentRefresh;
    private bool _disposed;

    public NativeWindowTracker(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Input, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        _refreshTimer.Tick += RefreshTimer_Tick;
        _eventCallback = OnWinEvent;
        _enumCallback = CollectWindow;
    }

    public event EventHandler<IReadOnlyList<TrackedWindow>>? WindowsChanged;

    public event Action<nint>? ForegroundWindowChanged;

    public IReadOnlyList<TrackedWindow> Snapshot { get; private set; } = [];

    public bool NotificationsAvailable => _systemEventHook != 0;

    public void Start(nint excludedWindow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _excludedWindow = excludedWindow;
        _windowHook = SetWinEventHook(EventObjectCreate, EventObjectHide, 0, _eventCallback, 0, 0, WineventOutOfContext);
        if (_windowHook == 0)
        {
            throw new InvalidOperationException("Windows pencere bildirimleri dinlenemedi.");
        }

        _nameChangeHook = SetWinEventHook(EventObjectLocationChange, EventObjectNameChange, 0, _eventCallback, 0, 0, WineventOutOfContext);
        if (_nameChangeHook == 0)
        {
            UnhookWinEvent(_windowHook);
            _windowHook = 0;
            throw new InvalidOperationException("Pencere başlığı değişiklikleri dinlenemedi.");
        }

        _systemEventHook = SetWinEventHook(EventSystemAlert, EventSystemForeground, 0, _eventCallback, 0, 0, WineventOutOfContext);
        RefreshWindows();
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow != 0)
        {
            ForegroundWindowChanged?.Invoke(foregroundWindow);
        }
    }

    private void OnWinEvent(nint hook, uint eventType, nint windowHandle, int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (_disposed)
        {
            return;
        }

        if (eventType == EventObjectLocationChange)
        {
            if (objectId == ObjectWindow && childId == ChildIdSelf && windowHandle == GetForegroundWindow())
                QueueRefresh();
            return;
        }

        if (eventType == EventSystemAlert && windowHandle != 0)
        {
            _notificationWindows.Add(windowHandle);
            QueueRefresh();
            return;
        }

        if (eventType == EventSystemForeground && windowHandle != 0)
        {
            _notificationWindows.Remove(windowHandle);
            QueueRefresh(urgent: true);
            _lastReportedForeground = windowHandle;
            ForegroundWindowChanged?.Invoke(windowHandle);
            return;
        }

        if (objectId != ObjectWindow || childId != ChildIdSelf
            || eventType is not (EventObjectCreate or EventObjectDestroy or EventObjectShow or EventObjectHide or EventObjectNameChange))
        {
            return;
        }

        // Apps (Electron, browsers) create, show and rename child and helper windows constantly; only
        // top-level windows, or ones already in the dock, can change what the dock shows.
        var tracked = Snapshot.Any(window => window.Handle == windowHandle);
        if (!tracked && (eventType == EventObjectDestroy || GetAncestor(windowHandle, GaRoot) != windowHandle))
        {
            return;
        }

        if (!tracked && eventType is EventObjectNameChange or EventObjectHide or EventObjectCreate)
        {
            // An untracked top-level window that is renamed, hidden or created (not yet shown) is
            // picked up by its EVENT_OBJECT_SHOW; a low-priority refresh covers late titles.
            if (eventType != EventObjectNameChange || !IsWindowVisible(windowHandle)) return;
        }

        QueueRefresh(urgent: eventType is EventObjectShow or EventObjectDestroy);
    }

    private void QueueRefresh(bool urgent = false)
    {
        if (urgent) Interlocked.Exchange(ref _urgentRefresh, 1);
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0)
        {
            return;
        }

        _dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (!_disposed)
            {
                if (Interlocked.Exchange(ref _urgentRefresh, 0) != 0)
                {
                    FlushRefresh();
                }
                else if (!_refreshTimer.IsEnabled)
                {
                    // Bound the wait from the first event; later events must not postpone discovery.
                    _refreshTimer.Start();
                }
            }
        }, DispatcherPriority.Input);
    }

    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        FlushRefresh();
    }

    private void FlushRefresh()
    {
        _refreshTimer.Stop();
        var changed = RefreshWindows();
        // Only report real changes; every report repositions the dock window.
        var foreground = GetForegroundWindow();
        if (!changed && foreground == _lastReportedForeground) return;
        _lastReportedForeground = foreground;
        ForegroundWindowChanged?.Invoke(foreground);
    }

    private nint _lastReportedForeground;

    private bool RefreshWindows()
    {
        var windows = new List<TrackedWindow>();
        var previousWindows = Snapshot.ToDictionary(window => window.Handle);
        var state = GCHandle.Alloc(new WindowCollectionState(windows, previousWindows));
        try
        {
            EnumWindows(_enumCallback, GCHandle.ToIntPtr(state));
        }
        finally
        {
            state.Free();
        }

        _notificationWindows.RemoveWhere(windowHandle => !IsWindow(windowHandle));

        if (Snapshot.SequenceEqual(windows))
        {
            return false;
        }

        Snapshot = windows;
        WindowsChanged?.Invoke(this, windows);
        return true;
    }

    private bool CollectWindow(nint windowHandle, nint state)
    {
        if (windowHandle == _excludedWindow || !IsWindowVisible(windowHandle))
        {
            return true;
        }

        var extendedStyle = GetWindowLongPtr(windowHandle, GwlExStyle).ToInt64();
        if ((extendedStyle & WsExToolWindow) != 0 && (extendedStyle & WsExAppWindow) == 0)
        {
            return true;
        }

        // Owned windows are dialogs of another window, except when they ask for a taskbar button or their
        // owner is hidden: many business programs (WinForms, Delphi) keep a hidden main window that owns
        // every screen they show, and those screens are the program as far as the user can see.
        var owner = GetWindow(windowHandle, WindowOwner);
        if (owner != 0 && (extendedStyle & WsExAppWindow) == 0
            && (IsWindowVisible(owner) || (GetWindowLongPtr(windowHandle, GwlStyle).ToInt64() & WsPopup) != 0))
        {
            return true;
        }

        var titleLength = GetWindowTextLength(windowHandle);
        if (titleLength <= 0)
        {
            return true;
        }

        var title = new StringBuilder(Math.Min(titleLength + 1, 1024));
        GetWindowText(windowHandle, title, title.Capacity);
        var value = title.ToString().Trim();
        if (value.Length > 0)
        {
            if (GCHandle.FromIntPtr(state).Target is WindowCollectionState collection)
            {
                collection.Previous.TryGetValue(windowHandle, out var previous);
                if (IsCloaked(windowHandle))
                {
                    return true;
                }

                var executablePath = previous?.ExecutablePath ?? ResolveFrameHost(windowHandle, GetExecutablePath(windowHandle));
                if (executablePath == string.Empty || IsWindowsSystemApp(executablePath))
                {
                    return true;
                }

                if (IsExplorerShellWindow(executablePath, windowHandle))
                {
                    return true;
                }

                if (previous is not null)
                {
                    collection.Windows.Add(previous with
                    {
                        Title = value,
                        HasNotification = _notificationWindows.Contains(windowHandle)
                    });
                }
                else
                {
                    collection.Windows.Add(new TrackedWindow(windowHandle, value,
                        executablePath, _notificationWindows.Contains(windowHandle)));
                }
            }
        }

        return true;
    }

    public static bool Activate(nint windowHandle)
    {
        if (!IsWindow(windowHandle))
        {
            return false;
        }

        if (IsIconic(windowHandle))
        {
            ShowWindow(windowHandle, ShowRestore);
        }

        var targetThread = GetWindowThreadProcessId(windowHandle, out _);
        var currentThread = GetCurrentThreadId();
        var attached = targetThread != 0 && targetThread != currentThread
            && AttachThreadInput(currentThread, targetThread, true);
        try
        {
            BringWindowToTop(windowHandle);
            return SetForegroundWindow(windowHandle);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, targetThread, false);
            }
        }
    }

    public static nint GetForegroundApplicationWindow()
    {
        var foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out var processId);
        if (foreground == 0 || processId == Environment.ProcessId) return 0;
        var root = GetAncestor(foreground, GaRootOwner);
        return root != 0 ? root : foreground;
    }

    /// <summary>
    /// The window a dock click should minimize: the foreground window when it belongs to the clicked
    /// app, otherwise 0 (the click should bring the app forward instead).
    /// </summary>
    public static nint GetMinimizeTarget(nint windowHandle, string executablePath, nint? foregroundAtPress = null)
    {
        var foregroundWindow = foregroundAtPress ?? GetForegroundApplicationWindow();
        if (foregroundWindow == 0)
        {
            return 0;
        }

        var foregroundRoot = GetAncestor(foregroundWindow, GaRootOwner);
        if (foregroundRoot == 0 || !IsWindowVisible(foregroundRoot))
        {
            // A program whose hidden main window owns its screens: the screen itself, not the hidden window.
            foregroundRoot = GetAncestor(foregroundWindow, GaRoot) is var root and not 0 ? root : foregroundWindow;
        }

        // A window without a minimize button is not minimized from the dock either: programs made that way
        // (tills, kiosks) often close the screen when it is minimized.
        if ((GetWindowLongPtr(foregroundRoot, GwlStyle).ToInt64() & WsMinimizeBox) == 0)
        {
            return 0;
        }

        return IsWindow(foregroundRoot) && !IsIconic(foregroundRoot)
            && (foregroundRoot == windowHandle
                || string.Equals(GetExecutablePath(foregroundRoot), executablePath, StringComparison.OrdinalIgnoreCase))
            ? foregroundRoot
            : 0;
    }

    public static bool IsMinimized(nint windowHandle) => IsWindow(windowHandle) && IsIconic(windowHandle);

    internal static string? ExecutablePathOf(nint windowHandle) => GetExecutablePath(windowHandle);

    public static bool ToggleActivation(nint windowHandle, string executablePath, nint? foregroundAtPress = null)
    {
        if (!IsWindow(windowHandle))
        {
            return false;
        }

        var minimizeTarget = GetMinimizeTarget(windowHandle, executablePath, foregroundAtPress);
        return minimizeTarget != 0 ? ShowWindowAsync(minimizeTarget, ShowMinimize) : Activate(windowHandle);
    }

    public static bool RequestClose(nint windowHandle)
    {
        return IsWindow(windowHandle) && PostMessage(windowHandle, WmClose, 0, 0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= RefreshTimer_Tick;
        if (_windowHook != 0)
        {
            UnhookWinEvent(_windowHook);
            _windowHook = 0;
        }

        if (_nameChangeHook != 0)
        {
            UnhookWinEvent(_nameChangeHook);
            _nameChangeHook = 0;
        }

        if (_systemEventHook != 0)
        {
            UnhookWinEvent(_systemEventHook);
            _systemEventHook = 0;
        }
    }

    private delegate void WinEventDelegate(nint hook, uint eventType, nint windowHandle, int objectId, int childId, uint eventThread, uint eventTime);
    private delegate bool EnumWindowsDelegate(nint windowHandle, nint state);

    private sealed record WindowCollectionState(List<TrackedWindow> Windows, IReadOnlyDictionary<nint, TrackedWindow> Previous);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint eventHookModule, WinEventDelegate callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsDelegate callback, nint state);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint windowHandle, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint windowHandle, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static string? GetExecutablePath(nint windowHandle)
    {
        var threadId = GetWindowThreadProcessId(windowHandle, out var processId);
        if (threadId == 0 || processId == 0)
        {
            return null;
        }

        var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle == 0)
        {
            return null;
        }

        try
        {
            var path = new StringBuilder(32768);
            var length = (uint)path.Capacity;
            return QueryFullProcessImageName(processHandle, 0, path, ref length) ? path.ToString() : null;
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    /// <summary>Suspended or virtual-desktop windows are cloaked; Windows' own taskbar hides them too.</summary>
    private static bool IsCloaked(nint windowHandle) =>
        DwmGetWindowAttribute(windowHandle, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>
    /// Store apps (Settings, Calculator…) live inside ApplicationFrameHost.exe, which has no icon of its own.
    /// Use the hosted app's process instead, or an empty path (skipped) when the app is not attached yet.
    /// </summary>
    private static string? ResolveFrameHost(nint windowHandle, string? executablePath)
    {
        if (!string.Equals(Path.GetFileName(executablePath), "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            return executablePath;
        }

        GetWindowThreadProcessId(windowHandle, out var hostProcessId);
        nint child = 0;
        while ((child = FindWindowEx(windowHandle, child, "Windows.UI.Core.CoreWindow", null)) != 0)
        {
            GetWindowThreadProcessId(child, out var childProcessId);
            if (childProcessId != 0 && childProcessId != hostProcessId)
            {
                return GetExecutablePath(child) ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private const int DwmwaCloaked = 14;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint windowHandle, int attribute, out int value, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowName);

    private static bool IsWindowsSystemApp(string? executablePath)
    {
        return executablePath is not null
            && executablePath.StartsWith(WindowsSystemAppsDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExplorerShellWindow(string? executablePath, nint windowHandle)
    {
        if (!string.Equals(Path.GetFileName(executablePath), "explorer.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var className = new StringBuilder(256);
        var length = GetClassName(windowHandle, className, className.Capacity);
        return length <= 0 || className.ToString() is not ("CabinetWClass" or "ExploreWClass");
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint windowHandle, StringBuilder className, int maximumCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint windowHandle, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint threadIdAttach, uint threadIdAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint processHandle, uint flags, StringBuilder executablePath, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint windowHandle, int command);
}
