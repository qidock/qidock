using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using QiDock.Services;

namespace QiDock.Widgets;

/// <summary>
/// Two-finger scrolling on a touchpad over the song list. The card stands in a window that never takes the focus
/// (so the app in front keeps it), and a touchpad's scrolling then never reaches the card. While the list is open,
/// Windows' scroll steps are heard directly: a step with the pointer over the list scrolls the list (and goes
/// nowhere else); every other step passes on untouched. Nothing is listened to while the list is closed.
/// </summary>
public sealed partial class MediaWidget
{
    private const int MouseHook = 14; // WH_MOUSE_LL
    private const int WheelMessage = 0x020A;
    private nint _wheelHook;
    private HookProc? _wheelProc; // kept alive while Windows holds it

    private void WheelHook(bool on)
    {
        if (on == (_wheelHook != 0)) return;
        if (!on)
        {
            UnhookWindowsHookEx(_wheelHook);
            _wheelHook = 0;
            return;
        }
        _wheelProc ??= OnWheel;
        using var process = Process.GetCurrentProcess();
        _wheelHook = SetWindowsHookEx(MouseHook, _wheelProc, GetModuleHandle(process.MainModule?.ModuleName), 0);
        if (_wheelHook == 0) AppLog.Write($"Music card: wheel listener could not start ({Marshal.GetLastWin32Error()})");
    }

    private nint OnWheel(int code, nint message, nint data)
    {
        if (code >= 0 && message == WheelMessage && _backScroll is { IsVisible: true } scroll && PresentationSource.FromVisual(scroll) is not null)
        {
            var info = Marshal.PtrToStructure<LowLevelMouse>(data);
            var topLeft = scroll.PointToScreen(new Point(0, 0));
            var bottomRight = scroll.PointToScreen(new Point(scroll.ActualWidth, scroll.ActualHeight));
            if (info.X >= topLeft.X && info.X < bottomRight.X && info.Y >= topLeft.Y && info.Y < bottomRight.Y)
            {
                var delta = (short)((info.MouseData >> 16) & 0xFFFF);
                if (!_wheelSeen)
                {
                    _wheelSeen = true;
                    AppLog.Write($"Music card: scrolling heard (step {delta})");
                }
                scroll.ScrollToVerticalOffset(Math.Clamp(scroll.VerticalOffset - delta * 0.6, 0, scroll.ScrollableHeight));
                return 1; // handled here: the window under the list does not scroll as well
            }
        }
        return CallNextHookEx(0, code, message, data);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelMouse
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public nint ExtraInfo;
    }

    private delegate nint HookProc(int code, nint message, nint data);

    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
