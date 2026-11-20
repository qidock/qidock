using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QiDock.Services;

namespace QiDock;

/// <summary>
/// Alt+Shift+D puts the keyboard on the dock: the arrow keys (or Tab) move along the icons, Enter or Space opens one,
/// the menu key or Shift+F10 opens its menu, Esc goes back to the window that was in front. Nothing runs until the
/// keys are pressed.
/// </summary>
public partial class MainWindow
{
    private bool _keyboardOnDock;
    private nint _foregroundBeforeKeys;
    private Button? _lastKeyButton;

    private void BeginDockKeyboard()
    {
        if (_keyboardOnDock)
        {
            EndDockKeyboard(restore: true);
            return;
        }
        var buttons = DockKeyButtons();
        if (buttons.Count == 0) return;
        _foregroundBeforeKeys = KeyNative.GetForegroundWindow();
        _keyboardOnDock = true;
        _autoHideController?.OnMouseEnterDock();
        // The dock never takes focus from a click; for the keyboard it has to.
        Activate();
        KeyNative.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        var start = _lastKeyButton is { IsVisible: true } last && buttons.Contains(last)
            ? last
            : buttons.FirstOrDefault(b => b.DataContext is Models.DockApp) ?? buttons[0];
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => FocusDockButton(start));
        Widgets.Ui.ShowHud("IconKeyboard", UiText.Get("DockKeysHint"));
    }

    private void EndDockKeyboard(bool restore)
    {
        if (!_keyboardOnDock) return;
        _keyboardOnDock = false;
        Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(this, null);
        _autoHideController?.OnMouseLeaveDock();
        if (restore && _foregroundBeforeKeys != 0) KeyNative.SetForegroundWindow(_foregroundBeforeKeys);
        _foregroundBeforeKeys = 0;
    }

    /// <summary>The dock's buttons in the order they stand (start, apps, downloads, bin…).</summary>
    private List<Button> DockKeyButtons()
    {
        var found = new List<Button>();
        var style = TryFindResource("DockButton");
        void Walk(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Button { IsVisible: true, IsEnabled: true } button && ReferenceEquals(button.Style, style))
                {
                    found.Add(button);
                    continue;
                }
                Walk(child);
            }
        }
        Walk(DockContents);
        return found;
    }

    private void FocusDockButton(Button button)
    {
        _lastKeyButton = button;
        button.Focusable = true;
        Keyboard.Focus(button);
        // A keyboard focus ring even though the last thing WPF saw may have been the mouse.
        if (!button.IsKeyboardFocused) button.Focus();
    }

    /// <summary>Called from the window's key handler; returns whether the key was the dock's.</summary>
    private bool OnDockKey(KeyEventArgs e)
    {
        if (!_keyboardOnDock) return false;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var buttons = DockKeyButtons();
        if (buttons.Count == 0) return false;
        var current = Keyboard.FocusedElement as Button;
        var at = current is null ? -1 : buttons.IndexOf(current);
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        int? next = key switch
        {
            Key.Left or Key.Up => at - 1,
            Key.Right or Key.Down => at + 1,
            Key.Tab => shift ? at - 1 : at + 1,
            Key.Home => 0,
            Key.End => buttons.Count - 1,
            _ => null
        };
        if (next is { } index)
        {
            // Round the ends, as a menu does.
            FocusDockButton(buttons[(index % buttons.Count + buttons.Count) % buttons.Count]);
            return true;
        }
        switch (key)
        {
            case Key.Enter or Key.Space when current is not null:
                var button = current;
                EndDockKeyboard(restore: false);
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                return true;
            case Key.Escape:
                EndDockKeyboard(restore: true);
                return true;
            default:
                // Shift+F10 and the menu key open the focused icon's menu the usual way.
                return false;
        }
    }

    private static class KeyNative
    {
        [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint window);
    }
}
