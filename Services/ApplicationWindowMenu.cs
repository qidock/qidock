using System.Runtime.InteropServices;
using System.Text;

namespace QiDock.Services;

public sealed record WindowMenuCommand(nint Window, nint Menu, uint Id, bool IsSystem);
public sealed record WindowMenuEntry(string Label, bool Enabled, bool Checked,
    WindowMenuCommand? Command, IReadOnlyList<WindowMenuEntry> Children, bool IsSeparator = false);

public static class ApplicationWindowMenu
{
    private const uint ByPosition = 0x400;

    public static IReadOnlyList<WindowMenuEntry> Read(nint window, bool system)
    {
        if (!IsWindow(window)) return [];
        var menu = system ? GetSystemMenu(window, false) : GetMenu(window);
        if (menu == 0) return [];
        // Let the owner refresh its own command state, without waiting on a hung process.
        if (SendMessageTimeout(window, 0x0116, menu, 0, 2, 80, out _) == 0) return [];
        var budget = 100;
        return ReadMenu(window, menu, system, 0, ref budget);
    }

    private static IReadOnlyList<WindowMenuEntry> ReadMenu(nint window, nint menu, bool system, int depth, ref int budget)
    {
        if (depth >= 5) return [];
        var entries = new List<WindowMenuEntry>();
        var count = Math.Min(GetMenuItemCount(menu), 100);
        for (uint index = 0; index < count && budget > 0; index++)
        {
            budget--;
            var state = GetMenuState(menu, index, ByPosition);
            if (state == uint.MaxValue) continue;
            if ((state & 0x800) != 0)
            {
                if (entries.Count > 0 && !entries[^1].IsSeparator)
                    entries.Add(new WindowMenuEntry("", false, false, null, [], true));
                continue;
            }
            var text = new StringBuilder(512);
            if (GetMenuString(menu, index, text, text.Capacity, ByPosition) <= 0) continue;
            var label = text.ToString().Split('\t')[0].Replace("&", "").Trim();
            if (label.Length == 0) continue;
            var submenu = GetSubMenu(menu, (int)index);
            var children = submenu == 0 ? [] : ReadMenu(window, submenu, system, depth + 1, ref budget);
            var id = GetMenuItemID(menu, (int)index);
            var command = submenu == 0 && id != uint.MaxValue ? new WindowMenuCommand(window, menu, id, system) : null;
            if (command is null && children.Count == 0) continue;
            entries.Add(new WindowMenuEntry(label, (state & 3) == 0, (state & 8) != 0, command, children));
        }
        if (entries.Count > 0 && entries[^1].IsSeparator) entries.RemoveAt(entries.Count - 1);
        return entries;
    }

    public static bool Invoke(WindowMenuCommand command)
    {
        if (!IsWindow(command.Window) || !IsMenu(command.Menu)) return false;
        var state = GetMenuState(command.Menu, command.Id, 0);
        if (state == uint.MaxValue || (state & 3) != 0) return false;
        if (!NativeWindowTracker.Activate(command.Window)) return false;
        return PostMessage(command.Window, command.IsSystem ? 0x0112u : 0x0111u, (nint)command.Id, 0);
    }

    [DllImport("user32.dll")] private static extern nint GetMenu(nint window);
    [DllImport("user32.dll")] private static extern nint GetSystemMenu(nint window, [MarshalAs(UnmanagedType.Bool)] bool revert);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(nint menu);
    [DllImport("user32.dll")] private static extern uint GetMenuItemID(nint menu, int position);
    [DllImport("user32.dll")] private static extern nint GetSubMenu(nint menu, int position);
    [DllImport("user32.dll")] private static extern uint GetMenuState(nint menu, uint item, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuString(nint menu, uint item, StringBuilder text, int count, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsMenu(nint menu);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam,
        uint flags, uint timeout, out nuint result);
}
