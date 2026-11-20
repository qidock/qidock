using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace QiDock.Browser;

/// <summary>
/// Opens the notch browser when the pointer rests on the top edge of the main screen, in the middle (where a
/// MacBook has its notch). Only the pointer position is read, ten times a second; the browser itself is created on
/// first use and gives its memory back a few minutes after it is put away.
/// </summary>
internal static class NotchBrowser
{
    /// <summary>Half the width of the spot on the top edge that opens the browser (screen units).</summary>
    public const double ZoneHalfWidth = 110;

    private static readonly TimeSpan Dwell = TimeSpan.FromMilliseconds(220);
    private static readonly DispatcherTimer Watch = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
    private static NotchBrowserWindow? _window;
    private static DateTime _since;

    static NotchBrowser()
    {
        Watch.Tick += (_, _) => Poll();
    }

    public static bool Enabled { get; private set; }

    /// <summary>Follows the dock's "animations" setting: off, the browser simply appears and disappears.</summary>
    public static bool Animations { get; set; } = true;

    /// <summary>A full-screen app (game, video) is in front: the top edge belongs to it.</summary>
    public static bool Suppressed { get; set; }

    public static bool IsOpen => _window?.IsExpanded == true;

    public static void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        _since = default;
        if (enabled) Watch.Start();
        else
        {
            Watch.Stop();
            _window?.Collapse();
        }
    }

    public static void Open()
    {
        _window ??= new NotchBrowserWindow();
        _window.Expand();
    }

    public static void Toggle()
    {
        if (IsOpen) _window!.Collapse();
        else Open();
    }

    /// <summary>True when screen pixel x on the top edge is the browser's spot (the widget bar leaves it alone).</summary>
    public static bool InZone(int x)
    {
        if (!Enabled) return false;
        var width = GetSystemMetrics(0);
        return Math.Abs(x - width / 2.0) <= ZoneHalfWidth * Scale();
    }

    public static void Shutdown() => _window?.SaveState();

    private static double Scale()
    {
        var dpi = GetDpiForSystem();
        return dpi > 0 ? dpi / 96.0 : 1;
    }

    private static void Poll()
    {
        if (Suppressed || IsOpen || !GetCursorPos(out var cursor) || AnyMouseButtonDown())
        {
            _since = default;
            return;
        }
        // The very top row of the main screen only (browser tabs and title bars sit just below it).
        if (cursor.Y > 1 || cursor.Y < 0 || !InZone(cursor.X))
        {
            _since = default;
            return;
        }
        var now = DateTime.UtcNow;
        if (_since == default)
        {
            _since = now;
            return;
        }
        if (now - _since < Dwell) return;
        _since = default;
        Open();
    }

    private static bool AnyMouseButtonDown() =>
        (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
