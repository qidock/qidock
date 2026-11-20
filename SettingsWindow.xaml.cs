using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using QiDock.Models;
using QiDock.Services;

namespace QiDock;

public partial class SettingsWindow : Window
{
    private readonly ApplicationPreferences _preferences;
    private readonly StartupRegistration _startup = new();
    private bool _startupEnabled;
    private bool _startupAvailable;
    private DockSettings _applied;
    private bool _closingApplication;
    private bool _initializing = true;
    private bool _sourceReady;
    private HwndSource? _source;
    private readonly List<string> _widgetOrder;
    private readonly HashSet<string> _hiddenWidgets;
    private readonly DockPreview _preview = new();

    public SettingsWindow(ApplicationPreferences preferences)
    {
        _preferences = preferences;
        _applied = preferences.Current;
        InitializeComponent();
        _preferences.Changed += Preferences_Changed;
        Closed += (_, _) =>
        {
            _preferences.Changed -= Preferences_Changed;
            if (_source is { IsDisposed: false }) _source.RemoveHook(WindowProc);
            ThemeManager.SetSurfaceOpacity(_preferences.Current.SurfaceOpacity);
        };
        _widgetOrder = [];
        _hiddenWidgets = [];
        BuildStylePicker();
        PreviewHost.Child = _preview;
        AddRowHelp();
        LoadControls(_applied);
        var startup = _startup.Read();
        _startupEnabled = startup.Enabled;
        _startupAvailable = startup.Error is null;
        StartupToggle.IsEnabled = _startupAvailable;
        if (startup.Error is not null)
        {
            StartupToggle.ToolTip = startup.Error;
            System.Windows.Controls.ToolTipService.SetShowOnDisabled(StartupToggle, true);
        }
        StartupToggle.IsChecked = _startupEnabled;
        var errors = new[] { preferences.LoadError, startup.Error }.OfType<string>();
        var message = string.Join(" ", errors.Select(UiText.LocalizeError));
        if (message.Length > 0) ShowResult(message, error: true);
        var workArea = SystemParameters.WorkArea;
        MinHeight = Math.Min(MinHeight, workArea.Height);
        MinWidth = Math.Min(MinWidth, workArea.Width);
        Height = Math.Min(Height, workArea.Height);
        Width = Math.Min(Width, workArea.Width);
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(PreferenceEdited));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(PreferenceEdited));
        _initializing = false;
        UpdateApplyState();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source?.AddHook(WindowProc);
        _sourceReady = true;
        UpdateBackdrop();
    }

    private nint WindowProc(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0024 || lParam == 0) return 0; // WM_GETMINMAXINFO
        var monitor = MonitorFromWindow(window, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
        {
            Trace.TraceWarning("Settings monitor work area unavailable: {0}", Marshal.GetLastWin32Error());
            return 0;
        }
        var sizing = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        sizing.MaxPosition = new NativePoint(info.Work.Left - info.Monitor.Left, info.Work.Top - info.Monitor.Top);
        sizing.MaxSize = new NativePoint(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        var dpi = GetDpiForWindow(window);
        var scale = dpi > 0 ? dpi / 96d : 1;
        sizing.MinTrackSize = new NativePoint((int)Math.Ceiling(MinWidth * scale), (int)Math.Ceiling(MinHeight * scale));
        Marshal.StructureToPtr(sizing, lParam, false);
        handled = true;
        return 0;
    }

    /// <summary>Puts the saved settings into the controls; what they show becomes the baseline for edits.</summary>
    private void LoadControls(DockSettings settings)
    {
        var wasInitializing = _initializing;
        _initializing = true;
        DarkTheme.IsChecked = settings.Theme == AppTheme.Dark;
        LightTheme.IsChecked = settings.Theme == AppTheme.Light;
        TurkishLanguage.IsChecked = settings.Language == AppLanguage.Turkish;
        EnglishLanguage.IsChecked = settings.Language == AppLanguage.English;
        LeftPosition.IsChecked = settings.Position == DockPosition.Left;
        CenterPosition.IsChecked = settings.Position == DockPosition.Center;
        RightPosition.IsChecked = settings.Position == DockPosition.Right;
        EdgeBottom.IsChecked = settings.Edge == DockEdge.Bottom;
        EdgeTop.IsChecked = settings.Edge == DockEdge.Top;
        EdgeLeft.IsChecked = settings.Edge == DockEdge.Left;
        EdgeRight.IsChecked = settings.Edge == DockEdge.Right;
        WidgetsTop.IsChecked = settings.WidgetPlacement == WidgetPlacement.TopBar;
        WidgetsDockLeft.IsChecked = settings.WidgetPlacement == WidgetPlacement.DockLeft;
        WidgetsDockRight.IsChecked = settings.WidgetPlacement == WidgetPlacement.DockRight;
        AnimationsToggle.IsChecked = settings.AnimationsEnabled;
        GenieToggle.IsChecked = settings.GenieEffectEnabled;
        PreviewsToggle.IsChecked = settings.PreviewsEnabled;
        NotchBrowserToggle.IsChecked = settings.NotchBrowser;
        DockGlassToggle.IsChecked = settings.DockGlass;
        BadgesToggle.IsChecked = settings.NotificationBadgesEnabled;
        DockHudToggle.IsChecked = settings.DockHud;
        ClaudeActivityToggle.IsChecked = settings.ClaudeActivity;
        LowMemoryToggle.IsChecked = settings.LowMemory;
        AutoHideDisabled.IsChecked = settings.AutoHide == AutoHideMode.Disabled;
        AutoHideSmart.IsChecked = settings.AutoHide == AutoHideMode.Smart;
        AutoHideAlways.IsChecked = settings.AutoHide == AutoHideMode.Always;
        OpacitySlider.Value = settings.SurfaceOpacity * 100;
        MagnifyToggle.IsChecked = settings.Magnify;
        MagnifySlider.Value = Math.Clamp(settings.MagnifyScale, 1.1, 2.2) * 100;
        var style = ThemeStyles.Find(settings.Style).Id;
        foreach (var choice in StylePicker.Children.OfType<System.Windows.Controls.RadioButton>())
            choice.IsChecked = (string)choice.Tag == style;
        var widgets = settings.WidgetOptions;
        _widgetOrder.Clear();
        _widgetOrder.AddRange(widgets.EffectiveOrder);
        _hiddenWidgets.Clear();
        _hiddenWidgets.UnionWith(widgets.Hidden ?? []);
        WeatherCityBox.Text = widgets.WeatherCity;
        CalendarFeedBox.Text = widgets.CalendarFeedUrl ?? "";
        SecondsToggle.IsChecked = widgets.ShowSeconds;
        BuildWidgetList();
        UpdateOpacityLabel();
        UpdateGenieAvailability();
        _initializing = wasInitializing;
        UpdatePreview();
    }

    /// <summary>The genie effect needs animations: without them its switch would be on but do nothing.</summary>
    private void UpdateGenieAvailability()
    {
        // In low-memory mode the genie stutters (it is drawn on the processor), so it stays off there.
        GenieToggle.IsEnabled = AnimationsToggle.IsChecked == true && LowMemoryToggle.IsChecked != true;
        // The magnification is an animation too; its size only matters while it is on.
        MagnifyToggle.IsEnabled = AnimationsToggle.IsChecked == true;
        MagnifyScalePanel.IsEnabled = MagnifyToggle.IsChecked == true && MagnifyToggle.IsEnabled;
        MagnifyScalePanel.Opacity = MagnifyScalePanel.IsEnabled ? 1 : 0.45;
        MagnifyValue.Text = UiText.Percent(MagnifySlider.Value);
        // A gentle note, not a dialog: what the magnification costs, and that it stutters while low-memory mode is on.
        var magnifyOn = MagnifyToggle.IsChecked == true && MagnifyToggle.IsEnabled;
        MagnifyNote.Visibility = magnifyOn ? Visibility.Visible : Visibility.Collapsed;
        MagnifyNoteText.Text = UiText.Get(LowMemoryToggle.IsChecked == true ? "MagnifyNoteLowMemory" : "MagnifyNote");
    }

    /// <summary>The dock picture follows the controls (not the saved settings) on every edit.</summary>
    private void UpdatePreview(bool showMagnify = false)
    {
        var settings = ReadSelection();
        _preview.Show(settings, settings.WidgetOptions.VisibleWidgets.Count(), showMagnify);
    }

    /// <summary>
    /// Each switch row shows what it does under its name (the same words screen readers get), so nothing has to be
    /// found out by trying.
    /// </summary>
    private void AddRowHelp()
    {
        foreach (var row in LogicalChildren<System.Windows.Controls.DockPanel>(this).ToList())
        {
            var toggle = row.Children.OfType<System.Windows.Controls.CheckBox>().FirstOrDefault();
            var label = row.Children.OfType<System.Windows.Controls.TextBlock>().FirstOrDefault();
            if (toggle is null || label is null) continue;
            var binding = System.Windows.Data.BindingOperations.GetBindingBase(toggle, System.Windows.Automation.AutomationProperties.HelpTextProperty);
            var text = System.Windows.Automation.AutomationProperties.GetHelpText(toggle);
            if (binding is null && string.IsNullOrWhiteSpace(text)) continue;
            var help = new System.Windows.Controls.TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 16, 0) };
            help.SetResourceReference(ForegroundProperty, "TextSecondary");
            if (binding is not null) help.SetBinding(System.Windows.Controls.TextBlock.TextProperty, binding);
            else help.Text = text;
            var index = row.Children.IndexOf(label);
            row.Children.Remove(label);
            var stack = new System.Windows.Controls.StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(label);
            stack.Children.Add(help);
            row.Children.Insert(index, stack);
        }
    }

    private static IEnumerable<T> LogicalChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in LogicalChildren<T>(child)) yield return nested;
        }
    }

    private void Preferences_Changed(object? sender, EventArgs e)
    {
        // Changed elsewhere (dock menu, edit mode, another profile): show it here too unless the user is
        // in the middle of editing; then only the fields they edit are written back on Apply.
        if (!HasEdits())
        {
            _applied = _preferences.Current;
            LoadControls(_applied);
            UpdateApplyState();
        }
        ThemeManager.SetSurfaceOpacity(Math.Round(OpacitySlider.Value) / 100);
        UpdateOpacityLabel();
        UpdateBackdrop();
    }

    private void UpdateBackdrop()
    {
        if (!_sourceReady) return;
        var glass = SettingsBackdrop.TryEnable(this, _preferences.Current.Theme);
        SettingsFrame.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, glass ? "SettingsGlassMaterial" : "SettingsBackground");
        if (glass)
        {
            WindowChrome.GetWindowChrome(this).GlassFrameThickness = new Thickness(-1);
            SettingsFrame.ClearValue(System.Windows.Controls.Border.CornerRadiusProperty);
        }
        else
        {
            WindowChrome.GetWindowChrome(this).GlassFrameThickness = new Thickness(0);
            SettingsFrame.CornerRadius = new CornerRadius(0);
        }
    }

    /// <summary>
    /// The saved settings with this window's edits on top: only fields the user changed here (compared with
    /// what the window showed) are taken from the controls, so changes made elsewhere meanwhile are kept.
    /// </summary>
    private DockSettings ReadSelection()
    {
        var now = _preferences.Current;
        var shown = _applied;
        static T Pick<T>(T control, T wasShown, T saved) => EqualityComparer<T>.Default.Equals(control, wasShown) ? saved : control;
        var widgets = now.WidgetOptions;
        var shownWidgets = shown.WidgetOptions;
        var city = string.IsNullOrWhiteSpace(WeatherCityBox.Text) ? "İstanbul" : WeatherCityBox.Text.Trim();
        var feed = string.IsNullOrWhiteSpace(CalendarFeedBox.Text) ? null : CalendarFeedBox.Text.Trim();
        return now with
        {
            Theme = Pick(LightTheme.IsChecked == true ? AppTheme.Light : AppTheme.Dark, shown.Theme, now.Theme),
            Language = Pick(EnglishLanguage.IsChecked == true ? AppLanguage.English : AppLanguage.Turkish, shown.Language, now.Language),
            Position = Pick(RightPosition.IsChecked == true ? DockPosition.Right : CenterPosition.IsChecked == true ? DockPosition.Center : DockPosition.Left, shown.Position, now.Position),
            Edge = Pick(EdgeTop.IsChecked == true ? DockEdge.Top : EdgeLeft.IsChecked == true ? DockEdge.Left : EdgeRight.IsChecked == true ? DockEdge.Right : DockEdge.Bottom, shown.Edge, now.Edge),
            WidgetPlacement = Pick(WidgetsDockLeft.IsChecked == true ? WidgetPlacement.DockLeft : WidgetsDockRight.IsChecked == true ? WidgetPlacement.DockRight : WidgetPlacement.TopBar, shown.WidgetPlacement, now.WidgetPlacement),
            AnimationsEnabled = Pick(AnimationsToggle.IsChecked == true, shown.AnimationsEnabled, now.AnimationsEnabled),
            GenieEffectEnabled = Pick(GenieToggle.IsChecked == true, shown.GenieEffectEnabled, now.GenieEffectEnabled),
            PreviewsEnabled = Pick(PreviewsToggle.IsChecked == true, shown.PreviewsEnabled, now.PreviewsEnabled),
            NotchBrowser = Pick(NotchBrowserToggle.IsChecked == true, shown.NotchBrowser, now.NotchBrowser),
            DockGlass = Pick(DockGlassToggle.IsChecked == true, shown.DockGlass, now.DockGlass),
            NotificationBadgesEnabled = Pick(BadgesToggle.IsChecked == true, shown.NotificationBadgesEnabled, now.NotificationBadgesEnabled),
            DockHud = Pick(DockHudToggle.IsChecked == true, shown.DockHud, now.DockHud),
            ClaudeActivity = Pick(ClaudeActivityToggle.IsChecked == true, shown.ClaudeActivity, now.ClaudeActivity),
            LowMemory = Pick(LowMemoryToggle.IsChecked == true, shown.LowMemory, now.LowMemory),
            AutoHide = Pick(AutoHideAlways.IsChecked == true ? AutoHideMode.Always : AutoHideSmart.IsChecked == true ? AutoHideMode.Smart : AutoHideMode.Disabled, shown.AutoHide, now.AutoHide),
            Style = Pick(SelectedStyle(), ThemeStyles.Find(shown.Style).Id, now.Style),
            SurfaceOpacity = Pick(Math.Round(OpacitySlider.Value) / 100, Math.Round(shown.SurfaceOpacity * 100) / 100, now.SurfaceOpacity),
            Magnify = Pick(MagnifyToggle.IsChecked == true, shown.Magnify, now.Magnify),
            MagnifyScale = Pick(Math.Round(MagnifySlider.Value) / 100, Math.Round(Math.Clamp(shown.MagnifyScale, 1.1, 2.2) * 100) / 100, now.MagnifyScale),
            // Keep the fields this window does not edit (profiles, to-dos, colors, shelf…).
            Widgets = KeepUnset(now.Widgets, widgets, widgets with
            {
                Order = _widgetOrder.SequenceEqual(shownWidgets.EffectiveOrder) ? widgets.Order : _widgetOrder.ToList(),
                Hidden = _hiddenWidgets.SetEquals(shownWidgets.Hidden ?? []) ? widgets.Hidden : _hiddenWidgets.OrderBy(id => id, StringComparer.Ordinal).ToList(),
                WeatherCity = Pick(city, shownWidgets.WeatherCity, widgets.WeatherCity),
                CalendarFeedUrl = Pick(feed, shownWidgets.CalendarFeedUrl, widgets.CalendarFeedUrl),
                ShowSeconds = Pick(SecondsToggle.IsChecked == true, shownWidgets.ShowSeconds, widgets.ShowSeconds)
            })
        };
    }

    /// <summary>Unset widget settings stay unset while nothing in them was edited.</summary>
    private static WidgetSettings? KeepUnset(WidgetSettings? saved, WidgetSettings defaults, WidgetSettings edited) =>
        saved is null && edited == defaults ? null : edited;

    private void BuildWidgetList()
    {
        WidgetList.Children.Clear();
        for (var index = 0; index < _widgetOrder.Count; index++)
        {
            var id = _widgetOrder[index];
            var position = index;
            var row = new System.Windows.Controls.Border();
            row.SetResourceReference(StyleProperty, "SettingsRow");
            var panel = new System.Windows.Controls.DockPanel();

            var actions = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Controls.DockPanel.SetDock(actions, System.Windows.Controls.Dock.Right);
            actions.Children.Add(OrderButton("M18 15l-6-6-6 6", UiText.Get("MoveUp"), position > 0, () => MoveWidget(position, -1)));
            actions.Children.Add(OrderButton("M6 9l6 6 6-6", UiText.Get("MoveDown"), position < _widgetOrder.Count - 1, () => MoveWidget(position, 1)));
            var toggle = new System.Windows.Controls.CheckBox { IsChecked = !_hiddenWidgets.Contains(id) };
            toggle.SetResourceReference(StyleProperty, "SettingsSwitch");
            System.Windows.Automation.AutomationProperties.SetName(toggle, UiText.Get("Widget_" + id));
            toggle.Checked += (_, _) => _hiddenWidgets.Remove(id);
            toggle.Unchecked += (_, _) => _hiddenWidgets.Add(id);
            actions.Children.Add(toggle);
            panel.Children.Add(actions);

            var text = new System.Windows.Controls.StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new System.Windows.Controls.TextBlock { Text = UiText.Get("Widget_" + id), FontWeight = FontWeights.SemiBold });
            var description = new System.Windows.Controls.TextBlock { Text = UiText.Get("WidgetDescription_" + id), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 12, 0) };
            description.SetResourceReference(ForegroundProperty, "TextSecondary");
            text.Children.Add(description);
            panel.Children.Add(text);
            row.Child = panel;
            WidgetList.Children.Add(row);
        }
    }

    private System.Windows.Controls.Button OrderButton(string geometry, string label, bool enabled, Action move)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = System.Windows.Media.Geometry.Parse(geometry),
            Width = 12, Height = 12, Stretch = System.Windows.Media.Stretch.Uniform, StrokeThickness = 2,
            StrokeStartLineCap = System.Windows.Media.PenLineCap.Round, StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeLineJoin = System.Windows.Media.PenLineJoin.Round
        };
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextPrimary");
        var button = new System.Windows.Controls.Button { Content = path, IsEnabled = enabled, ToolTip = label };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.SetResourceReference(StyleProperty, "SettingsOrderButton");
        button.Click += (_, _) => move();
        return button;
    }

    private void MoveWidget(int index, int delta)
    {
        var target = index + delta;
        if (target < 0 || target >= _widgetOrder.Count) return;
        (_widgetOrder[index], _widgetOrder[target]) = (_widgetOrder[target], _widgetOrder[index]);
        BuildWidgetList();
        PreferenceEdited(this, new RoutedEventArgs());
    }

    private void WidgetText_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => PreferenceEdited(sender, e);

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var startupChanged = _startupAvailable && StartupToggle.IsChecked != _startupEnabled;
        if (startupChanged && _startup.SetEnabled(StartupToggle.IsChecked == true) is { } startupError)
        {
            ShowResult(startupError, error: true);
            return;
        }
        var settings = ReadSelection();
        if (_preferences.Save(settings) is { } error)
        {
            error = UiText.LocalizeError(error);
            if (startupChanged && _startup.SetEnabled(_startupEnabled) is not null)
                error += " " + UiText.Get("StartupRestoreError");
            ShowResult(error, error: true);
            return;
        }
        _applied = settings;
        _startupEnabled = StartupToggle.IsChecked == true;
        ShowResult(UiText.Get("SettingsSaved"), error: false);
        UpdateApplyState();
        // The drawing mode is chosen at start: offer to restart when it no longer matches.
        if (Application.Current is App app && app.SoftwareRendering != settings.LowMemory
            && MessageBox.Show(this, UiText.Get("LowMemoryRestart"), "QiDock", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes)
            app.Restart();
    }

    private void ShowResult(string message, bool error)
    {
        SaveStatus.Text = UiText.LocalizeError(message);
        SaveStatus.Visibility = Visibility.Visible;
        SaveStatus.SetResourceReference(ForegroundProperty, error ? "DangerText" : "TextSecondary");
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closingApplication || Application.Current.Dispatcher.HasShutdownStarted) return;
        if (!HasEdits()) return;
        e.Cancel = MessageBox.Show(this, UiText.Get("DiscardChanges"), UiText.Get("DiscardTitle"),
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes;
    }

    /// <summary>One chip per look: three dots of its colours and its name.</summary>
    private void BuildStylePicker()
    {
        foreach (var look in ThemeStyles.All)
        {
            var dots = new System.Windows.Controls.Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4, 3, 4, 3),
                Margin = new Thickness(0, 0, 8, 0),
                Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                    look.Id == ThemeStyles.Glass ? "#33FFFFFF" : look.DockTop))
            };
            var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            foreach (var hex in look.Swatch)
                row.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 7, Height = 7, Margin = new Thickness(1, 0, 1, 0),
                    Fill = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex))
                });
            dots.Child = row;
            var content = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            content.Children.Add(dots);
            content.Children.Add(new System.Windows.Controls.TextBlock { Text = look.Name, VerticalAlignment = VerticalAlignment.Center });
            var choice = new System.Windows.Controls.RadioButton { GroupName = "LookStyle", Content = content, Tag = look.Id, Margin = new Thickness(3, 0, 3, 6), Padding = new Thickness(9, 7, 12, 7) };
            choice.SetResourceReference(StyleProperty, "SettingsChoice");
            System.Windows.Automation.AutomationProperties.SetName(choice, look.Name);
            StylePicker.Children.Add(choice);
        }
    }

    private string SelectedStyle() =>
        StylePicker.Children.OfType<System.Windows.Controls.RadioButton>().FirstOrDefault(c => c.IsChecked == true)?.Tag as string ?? ThemeStyles.Glass;

    private bool HasEdits()
    {
        // Unedited fields come from the saved settings, so anything changed elsewhere never counts as an edit here.
        // (Lists are reused when unedited, so record equality is exact.)
        return ReadSelection() != _preferences.Current
            || (_startupAvailable && StartupToggle.IsChecked != _startupEnabled);
    }

    private void PreferenceEdited(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        UpdateGenieAvailability();
        SaveStatus.Visibility = Visibility.Collapsed;
        UpdateApplyState();
        UpdatePreview(showMagnify: ReferenceEquals(e.OriginalSource, MagnifyToggle) && MagnifyToggle.IsChecked == true);
    }

    private void MagnifySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        UpdateGenieAvailability();
        SaveStatus.Visibility = Visibility.Collapsed;
        UpdateApplyState();
        UpdatePreview(showMagnify: true);
    }

    private void UpdateApplyState() => ApplyButton.IsEnabled = HasEdits() || _preferences.LoadError is not null;

    private void UpdateOpacityLabel() => OpacityValue.Text = UiText.Percent(OpacitySlider.Value);

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        UpdateOpacityLabel();
        ThemeManager.SetSurfaceOpacity(Math.Round(e.NewValue) / 100);
        PreferenceEdited(sender, e);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    internal void SelectWidgetsTab() => SettingsTabs.SelectedItem = WidgetsTab;

    internal void CloseForShutdown()
    {
        _closingApplication = true;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint(int x, int y) { public int X = x; public int Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);

}
