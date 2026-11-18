using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using QiDock.Models;
using QiDock.Services;

namespace QiDock.Widgets;

/// <summary>
/// Hosts the enabled widgets next to the dock icons, in the user's order. In edit mode the widgets
/// wiggle and each gets ‹ × › controls (move left, remove, move right), plus "Add" and "Done" pills.
/// </summary>
public sealed class WidgetBar : StackPanel, IDisposable
{
    private readonly Dictionary<string, DockWidget> _widgets;
    private readonly HashSet<string> _started = [];
    private readonly List<Grid> _wrappers = [];
    private WidgetSettings _settings = new();
    private string _signature = "";

    public event EventHandler? LayoutInvalidated;
    public event EventHandler? SettingsRequested;

    /// <summary>Raised in edit mode with the new widget settings (order or hidden list changed).</summary>
    public event EventHandler<WidgetSettings>? SettingsEdited;

    /// <summary>Raised when the user asks for the "add widget" menu; the argument is the anchor element.</summary>
    public event EventHandler<FrameworkElement>? AddRequested;

    public event EventHandler? EditingChanged;

    /// <summary>Right-click on a profile tab: the argument is the profile index and the tab.</summary>
    public event EventHandler<(int Index, FrameworkElement Anchor)>? ProfileMenuRequested;

    /// <summary>Profile tabs ("Varsayılan", "İş", "+"), shown under the bar by its window.</summary>
    public StackPanel ProfileStrip { get; } = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };

    private void BuildProfileStrip()
    {
        ProfileStrip.Children.Clear();
        var profiles = _settings.EffectiveProfiles;
        var active = _settings.EffectiveActiveProfile;
        for (var index = 0; index < profiles.Count; index++)
        {
            var position = index;
            // The name and, for the first nine, its shortcut (Ctrl+Alt+Shift+1…9) in a small key cap.
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = profiles[index].Name, VerticalAlignment = VerticalAlignment.Center });
            if (index < 9)
            {
                var cap = new Border
                {
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(4, 0, 4, 1),
                    Margin = new Thickness(7, 0, -4, 0),
                    Background = Ui.Solid("#26808080"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = "⌃⌥⇧" + (index + 1), FontSize = 9.5, FontWeight = FontWeights.Normal, Opacity = 0.8 }
                };
                content.Children.Add(cap);
            }
            var tab = new System.Windows.Controls.Primitives.ToggleButton { Content = content, IsChecked = index == active, ToolTip = index < 9 ? UiText.Format("ProfileShortcut", index + 1) : null };
            tab.SetResourceReference(FrameworkElement.StyleProperty, "WidgetProfileTab");
            tab.Click += (_, _) =>
            {
                tab.IsChecked = position == _settings.EffectiveActiveProfile;
                if (position != _settings.EffectiveActiveProfile) SettingsEdited?.Invoke(this, _settings.SwitchProfile(position));
            };
            tab.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                ProfileMenuRequested?.Invoke(this, (position, tab));
            };
            ProfileStrip.Children.Add(tab);
        }
        var addContent = new StackPanel { Orientation = Orientation.Horizontal };
        var plus = Ui.Icon("IconPlus", 11, brushKey: null);
        plus.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextSecondary");
        plus.Margin = new Thickness(0, 0, 6, 0);
        plus.VerticalAlignment = VerticalAlignment.Center;
        addContent.Children.Add(plus);
        addContent.Children.Add(new TextBlock { Text = UiText.Get("ProfileCreate"), VerticalAlignment = VerticalAlignment.Center });
        var add = new System.Windows.Controls.Primitives.ToggleButton { Content = addContent, ToolTip = UiText.Get("ProfileAdd") };
        add.SetResourceReference(FrameworkElement.StyleProperty, "WidgetProfileTab");
        add.Click += (_, _) =>
        {
            add.IsChecked = false;
            SettingsEdited?.Invoke(this, _settings.AddProfile(UiText.Format("ProfileNew", _settings.EffectiveProfiles.Count + 1)));
        };
        ProfileStrip.Children.Add(add);
    }

    public WidgetBar()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;

        DockWidget[] all =
        [
            new MediaWidget(), new WeatherWidget(), new ClockWidget(), new BatteryWidget(), new AiLimitsWidget(), new SystemWidget(), new QuickControlsWidget(),
            new FolderWidget(), new SearchWidget(), new ColorsWidget(), new AppVolumeWidget(), new FocusWidget(), new WorldClockWidget(),
            new TodoWidget(), new ToolsWidget(), new ProfileWidget(), new CurrencyWidget(), new StopwatchWidget(), new NoteWidget(),
            new AppGroupWidget(), new ProgressWidget(), new ReminderWidget(), new VoiceMemoWidget(), new TrayWidget(),
            new NotificationsWidget(), new ControlCenterWidget()
        ];
        _widgets = all.ToDictionary(w => w.Id);
        foreach (var widget in all)
        {
            widget.LayoutChanged += (_, _) => LayoutInvalidated?.Invoke(this, EventArgs.Empty);
            widget.SettingsRequested += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
            widget.SettingsUpdateRequested += (_, change) => SettingsEdited?.Invoke(this, change(_settings));
            if (widget is ProfileWidget profile)
            {
                profile.IsEditing = () => IsEditing;
                profile.ToggleEditing = () => SetEditing(!IsEditing);
            }
        }
        SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5) LayoutInvalidated?.Invoke(this, EventArgs.Empty);
        };
    }

    public bool IsAnyPopupOpen => _widgets.Values.Any(w => w.IsPopupOpen);

    public bool HasVisibleWidgets => Children.Count > 0;

    private bool _live = true;

    /// <summary>
    /// Off while the bar is hidden: widgets drop their fast timers (clock, CPU/RAM, volume) and catch
    /// up the moment the bar is back on screen. Slow refreshes (weather, limits) keep running.
    /// </summary>
    public void SetLive(bool live)
    {
        if (_live == live) return;
        _live = live;
        foreach (var id in _started) _widgets[id].SetLive(live);
    }

    public bool IsEditing { get; private set; }

    public void SetEditing(bool editing)
    {
        if (IsEditing == editing) return;
        IsEditing = editing;
        ClosePopups();
        _signature = "";
        Apply(_settings);
        EditingChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Apply(WidgetSettings settings)
    {
        _settings = settings;
        var visible = settings.VisibleWidgets.ToList();
        var signature = string.Join(",", visible) + (IsEditing ? "|edit" : "");
        if (signature != _signature)
        {
            _signature = signature;
            Children.Clear();
            foreach (var wrapper in _wrappers) wrapper.Children.Clear();
            _wrappers.Clear();
            foreach (var widget in _widgets.Values.Where(w => !visible.Contains(w.Id)))
            {
                widget.Stop();
                _started.Remove(widget.Id);
            }
            for (var index = 0; index < visible.Count; index++)
            {
                var widget = _widgets[visible[index]];
                widget.View.IsHitTestVisible = !IsEditing;
                Children.Add(IsEditing ? Wrap(widget, index, visible.Count) : widget.View);
                if (_started.Add(widget.Id))
                {
                    widget.Start();
                    if (!_live) widget.SetLive(false);
                }
            }
            if (IsEditing)
            {
                var add = Pill(UiText.Get("EditAddWidget"), "IconPlus", accent: false);
                add.Click += (_, _) => AddRequested?.Invoke(this, add);
                add.IsEnabled = visible.Count < WidgetIds.All.Count;
                Children.Add(add);
                var done = Pill(UiText.Get("EditDone"), "IconCheck", accent: true);
                done.Click += (_, _) => SetEditing(false);
                Children.Add(done);
            }
            LayoutInvalidated?.Invoke(this, EventArgs.Empty);
        }

        BuildProfileStrip();
        foreach (var widget in _widgets.Values)
        {
            widget.Current = settings;
            widget.ApplySettings(settings);
        }
    }

    /// <summary>Shows a hidden widget again (from the add menu), keeping its place in the order.</summary>
    public void ShowWidget(string id) =>
        SettingsEdited?.Invoke(this, _settings with { Hidden = (_settings.Hidden ?? []).Where(h => h != id).ToList() });

    private Grid Wrap(DockWidget widget, int index, int count)
    {
        var host = new Grid { RenderTransformOrigin = new Point(0.5, 0.5), Margin = new Thickness(0, 0, 0, 0) };
        host.Children.Add(widget.View);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(RoundButton("IconChevronLeft", UiText.Get("EditMoveLeft"), index > 0, () => Move(widget.Id, -1)));
        controls.Children.Add(RoundButton("IconClose", UiText.Get("EditRemove"), true, () => Hide(widget.Id), danger: true));
        controls.Children.Add(RoundButton("IconChevronRight", UiText.Get("EditMoveRight"), index < count - 1, () => Move(widget.Id, 1)));
        var shade = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = Ui.Solid("#8C0B0D12"),
            BorderBrush = Ui.Solid("#59FFFFFF"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(2, 0, 2, 0),
            Child = controls
        };
        host.Children.Add(shade);

        // The tiles wiggle while editing.
        var rotate = new RotateTransform();
        host.RenderTransform = rotate;
        var wiggle = new DoubleAnimation(-0.9, 0.9, TimeSpan.FromMilliseconds(130 + index * 9))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromMilliseconds(index * 37)
        };
        rotate.BeginAnimation(RotateTransform.AngleProperty, wiggle);
        _wrappers.Add(host);
        return host;
    }

    private void Move(string id, int delta)
    {
        var order = _settings.EffectiveOrder.ToList();
        var visible = _settings.VisibleWidgets.ToList();
        var position = visible.IndexOf(id);
        var target = position + delta;
        if (position < 0 || target < 0 || target >= visible.Count) return;
        // Swap with the neighbouring visible widget inside the full order (hidden ones keep their slots).
        var a = order.IndexOf(id);
        var b = order.IndexOf(visible[target]);
        (order[a], order[b]) = (order[b], order[a]);
        SettingsEdited?.Invoke(this, _settings with { Order = order });
    }

    private void Hide(string id) =>
        SettingsEdited?.Invoke(this, _settings with { Hidden = (_settings.Hidden ?? []).Append(id).Distinct().ToList() });

    private static Button RoundButton(string icon, string tooltip, bool enabled, Action click, bool danger = false)
    {
        var face = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = danger ? Ui.Solid("#E6FF453A") : Ui.Solid("#E62C2F36"),
            BorderBrush = Ui.Solid("#66FFFFFF"),
            BorderThickness = new Thickness(1),
            Child = Ui.Icon(icon, 11, Brushes.White, stroke: 2.4)
        };
        var button = new Button { Content = face, Width = 28, Height = 28, Margin = new Thickness(1, 0, 1, 0), IsEnabled = enabled, ToolTip = tooltip, Cursor = Cursors.Hand };
        button.SetResourceReference(FrameworkElement.StyleProperty, "WidgetIconButton");
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return button;
    }

    /// <summary>Compact round edit-mode button (icon only, label in the tooltip) so the dock still fits.</summary>
    private static Button Pill(string text, string icon, bool accent)
    {
        var face = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Background = accent ? Ui.Vertical("#FF4DA3FF", "#FF1F78F0") : Ui.Solid("#40FFFFFF"),
            BorderBrush = Ui.Solid("#66FFFFFF"),
            BorderThickness = new Thickness(1),
            Child = Ui.Icon(icon, 13, Brushes.White, stroke: 2.4)
        };
        var button = new Button { Content = face, Width = 34, Height = 34, Margin = new Thickness(2, 0, 0, 0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, ToolTip = text };
        button.SetResourceReference(FrameworkElement.StyleProperty, "WidgetIconButton");
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        return button;
    }

    public void ClosePopups()
    {
        foreach (var widget in _widgets.Values) widget.ClosePopup();
    }

    public void Dispose()
    {
        foreach (var widget in _widgets.Values) widget.Dispose();
    }
}
