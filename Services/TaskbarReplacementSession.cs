using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace QiDock.Services;

public sealed class TaskbarReplacementSession : IDisposable
{
    private const uint AbmNew = 0x00000000;
    private const uint AbmRemove = 0x00000001;
    private const uint AbmGetState = 0x00000004;
    private const uint AbmSetState = 0x0000000A;
    private const uint AbmQueryPos = 0x00000002;
    private const uint AbmSetPos = 0x00000003;
    private const uint AbeLeft = 0;
    private const uint AbeTop = 1;
    private const uint AbeRight = 2;
    private const uint AbeBottom = 3;
    private const uint AbsAutoHide = 0x00000001;
    private const uint AbsAlwaysOnTop = 0x00000002;
    private const uint SupportedTaskbarState = AbsAutoHide | AbsAlwaysOnTop;
    private const uint AbnStateChange = 0;
    private const uint AbnPosChanged = 1;
    private const uint AbnFullScreenApp = 2;
    private const uint WmDisplayChange = 0x007E;
    private const uint WmDpiChanged = 0x02E0;
    private const uint CallbackMessage = 0x8000 + 0x52;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private static readonly nint HwndTopmost = new(-1);

    private readonly Window _window;
    private readonly nint _handle;
    private bool _registered;
    private bool _active;
    private bool _stateChangeStarted;
    private uint _previousTaskbarState;
    private bool _disposed;
    private bool _fullScreenSuppressed;
    private double _nativeTaskbarHeight = 66;
    /// <summary>The dock's size (1 = normal, 1.3 = 30% larger), from the preferences.</summary>
    public static double Scale { get; set; } = 1;
    internal static double DockHeadroomDips => Math.Round(44 * Scale);
    /// <summary>Height of the dock strip (bar + gap), taller than Windows' own 48 DIP taskbar.</summary>
    public static double DockHeightDips => Math.Round(64 * Scale);
    private Rect _reservedRect;
    private bool _hasReservation;
    private bool _autoHideEnabled;
    private Size _lastLayout;
    private int _stopInProgress;

    public TaskbarReplacementSession(Window window, nint handle)
    {
        _window = window;
        _handle = handle;
        AppDomain.CurrentDomain.ProcessExit += Process_Exit;
    }

    public bool IsActive => _active || _stateChangeStarted;

    /// <summary>The screen edge the dock stands on. Away from the bottom, Windows' hidden taskbar is set to hide itself
    /// so it reserves nothing, and the dock reserves its own strip on its edge.</summary>
    public Models.DockEdge Edge { get; set; } = Models.DockEdge.Bottom;

    /// <summary>Moves the dock to another edge while running.</summary>
    public void SetEdge(Models.DockEdge edge)
    {
        if (Edge == edge) return;
        Edge = edge;
        _lastLayout = default;
        if (!_active || Volatile.Read(ref _stopInProgress) != 0) return;
        SetTaskbarState(ReplacementState, _handle);
        PositionDockWindow();
    }

    /// <summary>Windows' own (hidden) taskbar hides itself while the dock hides, or while the dock is on another edge.</summary>
    private uint ReplacementState => (_previousTaskbarState & AbsAlwaysOnTop) | (_autoHideEnabled || Edge != Models.DockEdge.Bottom ? AbsAutoHide : 0);

    public event Action<string>? OperationFailed;
    public event Action<Size>? LayoutChanged;
    public event Action<bool>? FullScreenChanged;

    public void UpdateForeground(nint windowHandle)
    {
        if (!_active || Volatile.Read(ref _stopInProgress) != 0) return;
        GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId == Environment.ProcessId) return;
        SetFullScreenSuppression(IsFullScreen(windowHandle));
    }

    private static bool HasPendingRecovery(out string? error)
    {
        error = null;
        if (!File.Exists(JournalPath))
        {
            return false;
        }

        try
        {
            _ = ReadJournal();
            return true;
        }
        catch (InvalidDataException)
        {
            error = "Görev çubuğu kurtarma kaydı bozuk. Windows ayarlarını elle denetleyin.";
        }
        catch (IOException)
        {
            error = "Görev çubuğu kurtarma kaydı okunamadı.";
        }
        catch (UnauthorizedAccessException)
        {
            error = "Görev çubuğu kurtarma kaydına erişim izni yok.";
        }

        return false;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_active)
        {
            return;
        }

        var pending = HasPendingRecovery(out var recoveryError);
        if (recoveryError is not null)
        {
            throw new InvalidOperationException(recoveryError);
        }

        _previousTaskbarState = pending ? ReadJournal() : GetTaskbarState();
        var nativeTaskbar = TrayHost.FindExplorerTaskbar();
        if (nativeTaskbar != 0 && GetWindowRect(nativeTaskbar, out var taskbarRect))
        {
            var nativeDpi = GetDpiForWindow(nativeTaskbar);
            if (nativeDpi > 0 && taskbarRect.Bottom > taskbarRect.Top
                && taskbarRect.Right - taskbarRect.Left > taskbarRect.Bottom - taskbarRect.Top)
                _nativeTaskbarHeight = (taskbarRect.Bottom - taskbarRect.Top) * 96d / nativeDpi;
        }
        try
        {
            if (!pending)
            {
                WriteJournal(_previousTaskbarState);
            }
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("Görev çubuğu ayarı değiştirilmedi; kurtarma kaydı yazılamadı.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException("Görev çubuğu ayarı değiştirilmedi; kurtarma kaydına yazma izni yok.", ex);
        }

        try
        {
            _stateChangeStarted = true;
            var replacementState = ReplacementState;
            if (!SetTaskbarState(replacementState, _handle))
            {
                throw new InvalidOperationException("Windows görev çubuğunun kenar tetiklemesi kapatılamadı.");
            }

            if (!HideNativeTaskbar())
            {
                throw new InvalidOperationException("Windows görev çubuğu gizlenemedi.");
            }

            var data = CreateData();
            data.uCallbackMessage = CallbackMessage;
            if (SHAppBarMessage(AbmNew, ref data) == 0)
            {
                throw new InvalidOperationException("QiDock Windows'a AppBar olarak kaydedilemedi.");
            }

            _registered = true;
            _active = true;
            PositionDockWindow();
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void HandleTaskbarCreated()
    {
        if (!_active)
        {
            return;
        }

        _registered = false;
        _hasReservation = false;
        var nativeTaskbar = TrayHost.FindExplorerTaskbar();
        if (nativeTaskbar != 0 && GetWindowRect(nativeTaskbar, out var taskbarRect))
        {
            var nativeDpi = GetDpiForWindow(nativeTaskbar);
            if (nativeDpi > 0 && taskbarRect.Bottom > taskbarRect.Top
                && taskbarRect.Right - taskbarRect.Left > taskbarRect.Bottom - taskbarRect.Top)
            {
                _nativeTaskbarHeight = (taskbarRect.Bottom - taskbarRect.Top) * 96d / nativeDpi;
            }
        }

        var replacementState = ReplacementState;
        SetTaskbarState(replacementState, _handle);
        HideNativeTaskbar();

        var data = CreateData();
        data.uCallbackMessage = CallbackMessage;
        if (SHAppBarMessage(AbmNew, ref data) != 0)
        {
            _registered = true;
        }

        PositionDockWindow();
    }

    public bool HandleMessage(uint message, nint wParam, nint lParam)
    {
        if (!_active || Volatile.Read(ref _stopInProgress) != 0)
        {
            return false;
        }

        if (message == CallbackMessage)
        {
            switch (unchecked((uint)wParam.ToInt64()))
            {
                case AbnPosChanged:
                    EnforceNativeTaskbarHidden();
                    PositionDockWindow();
                    return true;
                case AbnFullScreenApp:
                    if (lParam != 0)
                    {
                        UpdateForeground(GetForegroundWindow());
                    }
                    else
                    {
                        SetFullScreenSuppression(false);
                        PositionDockWindow();
                    }
                    return true;
                case AbnStateChange:
                    EnforceNativeTaskbarHidden();
                    return true;
            }
        }

        if (message is WmDisplayChange or WmDpiChanged)
        {
            PositionDockWindow();
        }

        return false;
    }

    public string? Stop()
    {
        if (Interlocked.CompareExchange(ref _stopInProgress, 1, 0) != 0)
        {
            return "Windows görev çubuğu geri yükleme işlemi devam ediyor.";
        }

        try
        {
            if (!_active && !_registered && !_stateChangeStarted) return null;

            // Stop enforcing hidden state before Shell calls can reenter our AppBar callback.
            _active = false;
            if (_registered)
            {
                var data = CreateData();
                SHAppBarMessage(AbmRemove, ref data);
                _registered = false;
                _hasReservation = false;
            }

            var stateRestored = SetTaskbarState(_previousTaskbarState, _handle);
            var taskbarShown = ShowNativeTaskbar();
            if (!stateRestored || !taskbarShown)
                return "Windows görev çubuğu geri yüklenemedi. Kapatmayı yeniden deneyin veya Windows görev çubuğu ayarlarını denetleyin.";

            _stateChangeStarted = false;
            return DeleteJournal();
        }
        finally
        {
            Volatile.Write(ref _stopInProgress, 0);
        }
    }

    public void RefreshPosition()
    {
        PositionDockWindow();
    }

    public void UpdateAutoHide(bool autoHide)
    {
        _autoHideEnabled = autoHide;
        if (!_active || Volatile.Read(ref _stopInProgress) != 0) return;
        SetTaskbarState(ReplacementState, _handle);
        PositionDockWindow();
    }

    private void PositionDockWindow()
    {
        if ((!_active && !_registered) || Volatile.Read(ref _stopInProgress) != 0)
        {
            return;
        }

        var monitor = MonitorFromWindow(_handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            throw new InvalidOperationException("Ekran boyutları alınamadı.");
        }

        var dpi = GetDpiForWindow(_handle);
        if (dpi == 0) return;
        if (UpdateReservation(monitorInfo.rcMonitor, dpi))
        {
            GetMonitorInfo(monitor, ref monitorInfo);
        }
        var mon = monitorInfo.rcMonitor;
        var work = monitorInfo.rcWork;
        // Thickness of the strip the dock stands in, then the headroom where magnified tiles grow into.
        var reserved = Edge switch
        {
            Models.DockEdge.Top => work.Top - mon.Top,
            Models.DockEdge.Left => work.Left - mon.Left,
            Models.DockEdge.Right => mon.Right - work.Right,
            _ => mon.Bottom - work.Bottom
        };
        var preferredPixels = (int)Math.Round((Edge == Models.DockEdge.Bottom ? Math.Max(_nativeTaskbarHeight, DockHeightDips) : DockHeightDips) * dpi / 96d);
        var thickness = reserved > 0 ? Math.Max(reserved, preferredPixels) : Math.Max(1, preferredPixels);
        var headroom = (int)Math.Round(DockHeadroomDips * dpi / 96d);
        var total = thickness + headroom;
        var vertical = Edge is Models.DockEdge.Left or Models.DockEdge.Right;
        var alongPixels = vertical ? mon.Bottom - mon.Top : mon.Right - mon.Left;
        // Along the dock and across it (DIPs), as the dock lays itself out.
        var layout = new Size(alongPixels * 96d / dpi, thickness * 96d / dpi);
        var totalDips = total * 96d / dpi;
        if (_lastLayout != layout)
        {
            _lastLayout = layout;
            _window.Width = vertical ? totalDips : layout.Width;
            _window.Height = vertical ? layout.Width : totalDips;
            LayoutChanged?.Invoke(layout);
        }
        UpdateForeground(GetForegroundWindow());
        // Explorer already reserves the hidden native taskbar's area; another appbar position would reserve a second strip.
        var (left, top, width, height) = Edge switch
        {
            Models.DockEdge.Top => (mon.Left, mon.Top, mon.Right - mon.Left, total),
            Models.DockEdge.Left => (mon.Left, mon.Top, total, mon.Bottom - mon.Top),
            Models.DockEdge.Right => (mon.Right - total, mon.Top, total, mon.Bottom - mon.Top),
            _ => (mon.Left, mon.Bottom - total, mon.Right - mon.Left, total)
        };
        if (!SetWindowPos(_handle, _fullScreenSuppressed ? new nint(1) : HwndTopmost, left, top,
                width, height,
                SwpNoActivate | SwpNoOwnerZOrder))
        {
            throw new InvalidOperationException("QiDock ekranın kenarına yerleştirilemedi.");
        }
    }

    private bool IsFullScreen(nint windowHandle)
    {
        if (windowHandle == 0 || !IsWindowVisible(windowHandle) || IsIconic(windowHandle)
            || MonitorFromWindow(windowHandle, MonitorDefaultToNearest) != MonitorFromWindow(_handle, MonitorDefaultToNearest))
            return false;
        var className = new StringBuilder(128);
        GetClassName(windowHandle, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        // A maximized window fills the whole monitor once auto-hide frees the work area; that is an
        // ordinary window, not a full-screen game or video, so the dock must stay available.
        if (IsZoomed(windowHandle)) return false;
        var monitor = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(MonitorFromWindow(windowHandle, MonitorDefaultToNearest), ref monitor)
            && GetWindowRect(windowHandle, out var rect)
            && rect.Left <= monitor.rcMonitor.Left && rect.Top <= monitor.rcMonitor.Top
            && rect.Right >= monitor.rcMonitor.Right && rect.Bottom >= monitor.rcMonitor.Bottom;
    }

    private void SetFullScreenSuppression(bool suppressed)
    {
        if (_fullScreenSuppressed == suppressed) return;
        _fullScreenSuppressed = suppressed;
        ShowWindow(_handle, suppressed ? SwHide : SwShowNoActivate);
        FullScreenChanged?.Invoke(suppressed);
    }

    private static uint GetTaskbarState()
    {
        var data = new AppBarData { cbSize = (uint)Marshal.SizeOf<AppBarData>() };
        return unchecked((uint)SHAppBarMessage(AbmGetState, ref data));
    }

    private static bool HideNativeTaskbar()
    {
        var taskbarHandle = TrayHost.FindExplorerTaskbar();
        if (taskbarHandle == 0)
        {
            return false;
        }

        if (IsWindowVisible(taskbarHandle))
        {
            ShowWindow(taskbarHandle, SwHide);
        }

        return !IsWindowVisible(taskbarHandle);
    }

    /// <summary>
    /// Called while the process dies of a fatal error: Windows' taskbar is shown again, in the state saved in the
    /// recovery journal when there is one, so the user is not left without a taskbar until the dock starts again.
    /// </summary>
    public static void RestoreAfterCrash()
    {
        try
        {
            if (File.Exists(JournalPath) && SetTaskbarState(ReadJournal(), 0)) DeleteJournal();
            ShowNativeTaskbar();
        }
        catch (Exception)
        {
            // Nothing more can be done in a dying process; the journal brings the taskbar back at the next start.
        }
    }

    private static bool ShowNativeTaskbar()
    {
        var taskbarHandle = TrayHost.FindExplorerTaskbar();
        if (taskbarHandle == 0)
        {
            return false;
        }

        ShowWindow(taskbarHandle, SwShowNoActivate);
        return IsWindowVisible(taskbarHandle);
    }

    private void EnforceNativeTaskbarHidden()
    {
        if (!_active || Volatile.Read(ref _stopInProgress) != 0) return;
        // In auto-hide mode Explorer's (hidden) taskbar must be auto-hide too, otherwise it keeps
        // reserving its strip and maximized windows stop short of the screen bottom.
        var replacementState = ReplacementState;
        if (!SetTaskbarState(replacementState, _handle))
        {
            OperationFailed?.Invoke("Windows görev çubuğunun otomatik açılması yeniden etkinleşti.");
            return;
        }

        if (!HideNativeTaskbar())
        {
            OperationFailed?.Invoke("Windows görev çubuğu yeniden gizlenemedi.");
        }
    }

    private static bool SetTaskbarState(uint state, nint windowHandle)
    {
        var data = new AppBarData
        {
            cbSize = (uint)Marshal.SizeOf<AppBarData>(),
            hWnd = windowHandle,
            lParam = unchecked((nint)state)
        };
        SHAppBarMessage(AbmSetState, ref data);
        return (GetTaskbarState() & SupportedTaskbarState) == (state & SupportedTaskbarState);
    }

    /// <summary>
    /// Explorer keeps reserving the hidden taskbar's strip; the dock is taller, so reserve the
    /// difference as our own appbar right above it. Maximized windows then end above the dock.
    /// Returns true when the reservation changed (the work area moved).
    /// </summary>
    private bool UpdateReservation(Rect monitor, uint dpi)
    {
        var dockPixels = (int)Math.Round(DockHeightDips * dpi / 96d);
        // On the bottom edge Explorer's hidden taskbar still reserves its own strip; elsewhere it reserves nothing.
        var extra = _autoHideEnabled ? 0
            : Edge == Models.DockEdge.Bottom ? dockPixels - (int)Math.Round(_nativeTaskbarHeight * dpi / 96d)
            : dockPixels;
        var data = CreateData();
        data.uEdge = Edge switch
        {
            Models.DockEdge.Top => AbeTop,
            Models.DockEdge.Left => AbeLeft,
            Models.DockEdge.Right => AbeRight,
            _ => AbeBottom
        };
        if (extra <= 0)
        {
            if (!_hasReservation) return false;
            data.rc = new Rect(monitor.Left, monitor.Bottom, monitor.Right, monitor.Bottom);
            SHAppBarMessage(AbmSetPos, ref data);
            _hasReservation = false;
            return true;
        }

        data.rc = Edge switch
        {
            Models.DockEdge.Top => new Rect(monitor.Left, monitor.Top, monitor.Right, monitor.Top + extra),
            Models.DockEdge.Left => new Rect(monitor.Left, monitor.Top, monitor.Left + extra, monitor.Bottom),
            Models.DockEdge.Right => new Rect(monitor.Right - extra, monitor.Top, monitor.Right, monitor.Bottom),
            _ => new Rect(monitor.Left, monitor.Bottom - extra, monitor.Right, monitor.Bottom)
        };
        SHAppBarMessage(AbmQueryPos, ref data);
        switch (Edge)
        {
            case Models.DockEdge.Top: data.rc.Bottom = data.rc.Top + extra; break;
            case Models.DockEdge.Left: data.rc.Right = data.rc.Left + extra; break;
            case Models.DockEdge.Right: data.rc.Left = data.rc.Right - extra; break;
            default: data.rc.Top = data.rc.Bottom - extra; break;
        }
        if (_hasReservation && _reservedRect.Left == data.rc.Left && _reservedRect.Top == data.rc.Top
            && _reservedRect.Right == data.rc.Right && _reservedRect.Bottom == data.rc.Bottom)
            return false;
        SHAppBarMessage(AbmSetPos, ref data);
        _reservedRect = data.rc;
        _hasReservation = true;
        return true;
    }

    private AppBarData CreateData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<AppBarData>(),
        hWnd = _handle
    };

    private static string JournalPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QiDock",
        "taskbar-state.txt");

    private static void WriteJournal(uint state)
    {
        var path = JournalPath;
        var directory = Path.GetDirectoryName(path);
        if (directory is null)
        {
            throw new IOException("Görev çubuğu kurtarma kaydı için yol oluşturulamadı.");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, state.ToString(CultureInfo.InvariantCulture));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static uint ReadJournal()
    {
        var text = File.ReadAllText(JournalPath).Trim();
        if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var state)
            || (state & ~SupportedTaskbarState) != 0)
        {
            throw new InvalidDataException("Görev çubuğu kurtarma kaydı geçersiz.");
        }

        return state;
    }

    private static string? DeleteJournal()
    {
        try
        {
            if (File.Exists(JournalPath))
            {
                File.Delete(JournalPath);
            }

            return null;
        }
        catch (IOException)
        {
            return "Görev çubuğu ayarı geri yüklendi ancak kurtarma kaydı silinemedi.";
        }
        catch (UnauthorizedAccessException)
        {
            return "Görev çubuğu ayarı geri yüklendi ancak kurtarma kaydını silme izni yok.";
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        var error = Stop();
        if (error is not null) Trace.TraceError(error);
        if (IsActive) return;
        _disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= Process_Exit;
    }

    private void Process_Exit(object? sender, EventArgs e)
    {
        var error = Stop();
        if (error is not null) Trace.TraceError(error);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public Rect rc;
        public nint lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [DllImport("shell32.dll")]
    private static extern nuint SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
    private static extern nint FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint windowHandle, out Rect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint windowHandle, StringBuilder name, int length);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);
}
