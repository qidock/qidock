#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QiDock.Models;
using QiDock.Services;
using QiDock.Services.Notifications;
using QiDock.Widgets;

namespace QiDock;

/// <summary>
/// Debug builds only (<c>--render-ui &lt;folder&gt;</c>): draws every bar widget's tile and card,
/// the Notification Center, the Control Center and a top banner to PNG files at twice the size,
/// in the dark and the light theme, without showing a window. For reviewing layout and alignment.
/// </summary>
internal static class UiSheet
{
    private const double Scale = 2;

    public static void Render(string folder, WidgetSettings settings, double opacity, Action done)
    {
        Directory.CreateDirectory(folder);
        var themes = new Queue<AppTheme>([AppTheme.Dark, AppTheme.Light]);
        void Next()
        {
            if (themes.Count == 0)
            {
                ThemeManager.Apply(AppTheme.Dark, opacity);
                done();
                return;
            }
            var theme = themes.Dequeue();
            ThemeManager.Apply(theme, opacity);
            RenderTheme(folder, theme.ToString().ToLowerInvariant(), settings, Next);
        }
        Next();
    }

    private static void RenderTheme(string folder, string theme, WidgetSettings settings, Action next)
    {
        DockWidget[] widgets =
        [
            new MediaWidget(), new WeatherWidget(), new ClockWidget(), new BatteryWidget(), new AiLimitsWidget(), new SystemWidget(), new QuickControlsWidget(),
            new FolderWidget(), new SearchWidget(), new ColorsWidget(), new AppVolumeWidget(), new FocusWidget(), new WorldClockWidget(),
            new TodoWidget(), new ToolsWidget(), new NotificationsWidget(), new ControlCenterWidget(),
            new ProfileWidget(), new CurrencyWidget(), new StopwatchWidget(), new NoteWidget(), new AppGroupWidget(), new ProgressWidget(),
            new ReminderWidget(), new VoiceMemoWidget(), new TrayWidget()
        ];
        var tiles = new WrapPanel { Width = 1400 };
        foreach (var widget in widgets)
        {
            widget.Current = settings;
            widget.ApplySettings(settings);
            widget.Start();
            widget.SetLive(true);
            var cell = new StackPanel { Margin = new Thickness(10) };
            cell.Children.Add(Ui.Text(widget.Id, 10, brushKey: "TextSecondary"));
            cell.Children.Add(new Border { Height = 44, Child = widget.View, HorizontalAlignment = HorizontalAlignment.Left });
            tiles.Children.Add(cell);
        }
        Layout(tiles);

        // Let the widgets fetch their data (weather, media, feeds) before drawing them.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Layout(tiles);
            Save(Backdrop(tiles, null), Path.Combine(folder, $"{theme}-tiles.png"));

            foreach (var widget in widgets)
            {
                var popups = new List<WidgetPopup>();
                WidgetPopup.Rehearsal = popups;
                try
                {
                    // Only widgets with a card get their tile clicked: on the others the first button is a real
                    // action (screen snip, play, lock…) that must never run during a snapshot.
                    if (widget is MediaWidget media) media.RehearseCard();
                    else if (HasPopup(widget) && FirstButton(widget.View) is { } button) Click(button);
                }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(folder, $"{theme}-popup-{widget.Id}.error.txt"), ex.ToString());
                }
                finally
                {
                    WidgetPopup.Rehearsal = null;
                }
                foreach (var (popup, index) in popups.Distinct().Select((p, i) => (p, i)))
                {
                    var surface = popup.DetachSurface();
                    Save(Backdrop(surface, null), Path.Combine(folder, $"{theme}-popup-{widget.Id}{(index > 0 ? "-" + index : "")}.png"));
                }
            }

            var center = NotificationCenter.Shared.Rehearse();
            Save(Backdrop(center, "PanelFrostTint"), Path.Combine(folder, $"{theme}-notification-center.png"));
            Release(center);
            // The bell's panel growing out of the dock, frame by frame (1.04 = the overshoot).
            var bell = NotificationCenter.Shared.RehearseDock();
            foreach (var (step, index) in new[] { 0.06, 0.2, 0.45, 0.75, 1.04, 1.0 }.Select((p, i) => (p, i)))
            {
                NotificationCenter.Shared.RehearseMorph(step);
                Save(Backdrop(bell, null), Path.Combine(folder, $"{theme}-bell-{index}.png"));
                Release(bell);
            }
            var control = ControlCenter.Shared.Rehearse();
            Save(Backdrop(control, "PanelFrostTint"), Path.Combine(folder, $"{theme}-control-center.png"));
            Release(control);
            var networks = ControlCenter.Shared.RehearseNetworks();
            Save(Backdrop(networks, "PanelFrostTint"), Path.Combine(folder, $"{theme}-control-center-wifi.png"));
            Release(networks);

            var sample = new AppNotification(1, "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "Derleme tamamlandı",
                "QiDock sürümü hazır; taşınabilir dosya dist klasöründe. Uzun bir açıklama satırı da burada kırpılmalı ve üç satırı geçmemeli.",
                DateTime.UtcNow.AddMinutes(-2), null, false, null);
            var banner = new BannerCard(sample);
            Save(Backdrop(banner.Rehearse(), "PanelFrostTint"), Path.Combine(folder, $"{theme}-banner.png"));
            banner.Close();

            try
            {
                // The settings window, one picture per tab (built, never shown, never saved).
                var settingsWindow = new SettingsWindow(((App)Application.Current).Preferences);
                if (settingsWindow.Content is FrameworkElement page)
                {
                    settingsWindow.Content = null;
                    // The window's own styles (tabs, switches, lists) must come along, or the page draws unstyled.
                    page.Resources.MergedDictionaries.Add(settingsWindow.Resources);
                    page.Width = settingsWindow.Width;
                    page.Height = settingsWindow.Height;
                    var tabs = settingsWindow.SettingsTabs;
                    for (var i = 0; i < tabs.Items.Count; i++)
                    {
                        tabs.SelectedIndex = i;
                        Save(Backdrop(page, null), Path.Combine(folder, $"{theme}-settings-{i}.png"));
                        Release(page);
                    }
                }
                settingsWindow.Close();
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(folder, $"{theme}-settings.error.txt"), ex.ToString());
            }

            foreach (var widget in widgets) widget.Dispose();
            next();
        };
        timer.Start();
    }

    /// <summary>
    /// <c>--render-styles &lt;folder&gt;</c>: a dock-like strip (the dock material with a row of widget tiles) in
    /// every look, one picture each, after the widgets had time to load.
    /// </summary>
    public static void RenderStyles(string folder, WidgetSettings settings, double opacity, Action done)
    {
        Directory.CreateDirectory(folder);
        DockWidget[] widgets = [new MediaWidget(), new ProfileWidget(), new CurrencyWidget(), new StopwatchWidget(), new NoteWidget(), new AppGroupWidget(), new ProgressWidget(), new ReminderWidget(), new VoiceMemoWidget(), new ControlCenterWidget()];
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var widget in widgets)
        {
            widget.Current = settings;
            widget.ApplySettings(settings);
            widget.Start();
            row.Children.Add(new Border { Height = 44, Child = widget.View });
        }
        var dock = new Border { CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1), Padding = new Thickness(8, 7, 8, 7), Child = row };
        dock.SetResourceReference(Border.BackgroundProperty, "DockMaterial");
        dock.SetResourceReference(Border.BorderBrushProperty, "DockBorderMaterial");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            foreach (var look in ThemeStyles.All)
            {
                ThemeManager.Apply(AppTheme.Dark, opacity, look.Id);
                Save(Backdrop(dock, null), Path.Combine(folder, $"style-{look.Id}.png"));
                Release(dock);
                if (look.Id is ThemeStyles.Glass or "black" or "lofi")
                {
                    DockHud.Shared.RehearseLevel("IconVolume", UiText.Get("HudVolume"), 57, Ui.Solid("#FF30D158"));
                    var hud = DockHud.Shared.RehearseDock();
                    DockHud.Shared.RehearseMorph(1.0);
                    Save(Backdrop(hud, null), Path.Combine(folder, $"hud-{look.Id}.png"));
                    Release(hud);
                }
            }
            ThemeManager.Apply(AppTheme.Dark, opacity, ThemeStyles.Glass);
            foreach (var widget in widgets) widget.Dispose();
            done();
        };
        timer.Start();
    }

    /// <summary>Frees a shared panel's content from this run's backdrop so the next theme can draw it again.</summary>
    private static void Release(FrameworkElement element)
    {
        if (element.Parent is Border border) border.Child = null;
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(element.Width > 0 ? element.Width : double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
    }

    /// <summary>Puts the element on a desktop-like backdrop; <paramref name="frostKey"/> stands in for the frosted glass.</summary>
    private static FrameworkElement Backdrop(FrameworkElement element, string? frostKey)
    {
        FrameworkElement body = element;
        if (frostKey is not null)
        {
            var color = Application.Current.TryFindResource(frostKey) is Color c ? c : Color.FromArgb(0xC8, 0x19, 0x1A, 0x1F);
            body = new Border { Background = new SolidColorBrush(color), CornerRadius = new CornerRadius(14), Child = element };
        }
        var root = new Border
        {
            Background = Ui.Diagonal(Color.FromRgb(0x3E, 0x5F, 0x8E), Color.FromRgb(0x1D, 0x2B, 0x45)),
            Padding = new Thickness(24),
            Child = body
        };
        root.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "InterfaceFont");
        root.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextPrimary");
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Ideal);
        Layout(root);
        Layout(root); // twice: some cards size themselves after their first layout
        return root;
    }

    private static void Save(FrameworkElement element, string path)
    {
        var width = (int)Math.Ceiling(element.ActualWidth * Scale);
        var height = (int)Math.Ceiling(element.ActualHeight * Scale);
        if (width <= 0 || height <= 0) return;
        var bitmap = new RenderTargetBitmap(width, height, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static bool HasPopup(DockWidget widget) => widget.GetType()
        .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        .Any(field => field.FieldType == typeof(WidgetPopup));

    private static ButtonBase? FirstButton(DependencyObject node)
    {
        if (node is ButtonBase button) return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FirstButton(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
        return null;
    }

    private static void Click(ButtonBase button)
    {
        if (UIElementAutomationPeer.CreatePeerForElement(button)?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) invoke.Invoke();
        else button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        button.Dispatcher.Invoke(DispatcherPriority.Background, () => { });
    }
}
#endif
