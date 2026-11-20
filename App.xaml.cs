using System.Diagnostics;
using System.Threading;
using System.Windows;
using QiDock.Services;

namespace QiDock;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private Mutex? _instanceMutex;

    /// <summary>
    /// The circle Windows draws where a finger or pen taps a WPF window could stay behind on the screen
    /// when the window under it hid or closed at once (the dock hiding, an overlay closing after a tap).
    /// QiDock's windows answer every tap themselves, so that circle is turned off for all of them before
    /// the first one is made. Press and hold (a right click by touch) keeps working.
    /// </summary>
    private static void TurnOffTouchRings()
    {
        foreach (var property in new[] { System.Windows.Input.Stylus.IsTapFeedbackEnabledProperty, System.Windows.Input.Stylus.IsTouchFeedbackEnabledProperty, System.Windows.Input.Stylus.IsFlicksEnabledProperty })
        {
            try
            {
                property.OverrideMetadata(typeof(Window), new FrameworkPropertyMetadata(false));
            }
            catch (ArgumentException ex)
            {
                Trace.TraceWarning("Touch feedback stays on: {0}", ex.Message);
            }
        }
    }
    public ApplicationPreferences Preferences { get; } = new(new DockSettingsStore());

#if DEBUG
    /// <summary>Debug: draw the running dock with the widgets in each dock placement, then quit.</summary>
    public static string? DockSnapshotFolder { get; private set; }
    public static bool DockCycle { get; private set; }
#endif

    protected override void OnStartup(StartupEventArgs e)
    {
        TurnOffTouchRings();
        UiText.Current.SetLanguage(Preferences.Current.Language);
        ThemeManager.Apply(Preferences.Current.Theme, Preferences.Current.SurfaceOpacity, Preferences.Current.Style);
#if DEBUG
        if (e.Args is ["--render-desktop-widgets", var sheet])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            DesktopWidgets.DeskSheet.Render(sheet, () => Preferences.Current.WidgetOptions, () => Shutdown());
            return;
        }
        if (e.Args is ["--promo-shots", var promoFolder, var promoLanguage, .. var promoSteps])
        {
            // The pictures of the promotional videos, with made-up data (QIDOCK_PROMO=1); see PromoShots.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            UiText.Current.SetLanguage(promoLanguage == "en" ? Models.AppLanguage.English : Models.AppLanguage.Turkish);
            ThemeManager.Apply(Models.AppTheme.Dark, Preferences.Current.SurfaceOpacity, ThemeStyles.Glass);
            PromoShots.Render(promoFolder, promoSteps.FirstOrDefault(), () => Shutdown());
            return;
        }
        if (e.Args is ["--render-apps", var appsPicture])
        {
            // The app screen as the low-memory (software) drawing shows it, saved as a picture at the screen's scale.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            AppGridWindow.RenderTo(appsPicture, () => Shutdown());
            return;
        }
        if (e.Args is ["--snapshot-dock", var snapshots]) DockSnapshotFolder = snapshots;
        if (e.Args is ["--snapshot-cycle", var cycle]) { DockSnapshotFolder = cycle; DockCycle = true; }
        if (e.Args is ["--claude-activity", var report])
        {
            // Lists the Claude Code sessions of the last 30 minutes into the report file, as the Claude card shows them.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ClaudeActivityWatcher.RecentAsync(TimeSpan.FromMinutes(30)).ContinueWith(task =>
            {
                System.IO.File.WriteAllLines(report, task.Result.Select(a => $"{a.UpdatedAt.ToLocalTime():HH:mm:ss} {a.Project} | {a.Detail} | done={a.Done} | working={a.Working}"));
                Shutdown();
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }
        if (e.Args is ["--render-styles", var styles])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            UiSheet.RenderStyles(styles, Preferences.Current.WidgetOptions, Preferences.Current.SurfaceOpacity, () => Shutdown());
            return;
        }
        if (e.Args is ["--memory-probe", var probe, .. var probeMode])
        {
            if (probeMode is ["software"]) System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            // How much memory the panel windows hold: none, open, hidden but kept, and destroyed.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            void Note(string step)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                using var self = System.Diagnostics.Process.GetCurrentProcess();
                System.IO.File.AppendAllText(probe, $"{step,-34} özel {self.PrivateMemorySize64 >> 20,5} MB  çalışma {self.WorkingSet64 >> 20,5} MB{Environment.NewLine}");
            }
            var dock = new Window { Width = 1600, Height = 110, WindowStyle = WindowStyle.None, ShowInTaskbar = false, Left = 100, Top = 100, Content = new System.Windows.Controls.Border { Background = System.Windows.Media.Brushes.DimGray } };
            EdgePanel[] panels = [];
            var steps = new Queue<(string Name, Action Run)>(
            [
                ("başlangıç (pencere yok)", () => { }),
                ("bir pencere açık", () => dock.Show()),
                ("paneller açık", () =>
                {
                    panels = [NotificationCenter.Shared, ControlCenter.Shared, DockHud.Shared];
                    foreach (var panel in panels) panel.Toggle();
                }),
                ("paneller gizli (bekliyor)", () => { foreach (var panel in panels) panel.Close(animated: false); }),
                ("paneller tamamen kapatıldı", () => { foreach (var panel in panels) ((Window)panel).Close(); }),
                ("pencere de kapatıldı", () => dock.Close())
            ]);
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            (string Name, Action Run)? pending = null;
            timer.Tick += (_, _) =>
            {
                if (pending is { } done) Note(done.Name);
                if (steps.Count == 0)
                {
                    timer.Stop();
                    Shutdown();
                    return;
                }
                pending = steps.Dequeue();
                try { pending.Value.Run(); }
                catch (Exception ex) { System.IO.File.AppendAllText(probe, $"{pending.Value.Name}: {ex.Message}{Environment.NewLine}"); }
            };
            timer.Start();
            return;
        }
        if (e.Args is ["--frame-probe", var frames, .. var frameMode])
        {
            // How smoothly a dock-like see-through window slides, with and without its blurred shadow.
            if (frameMode is ["software"]) System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var surfaceMove = new System.Windows.Media.TranslateTransform();
            var surface = new System.Windows.Controls.Border
            {
                Background = new System.Windows.Media.LinearGradientBrush(System.Windows.Media.Color.FromArgb(0xD0, 0x30, 0x30, 0x38), System.Windows.Media.Color.FromArgb(0xE0, 0x20, 0x20, 0x26), 90),
                CornerRadius = new CornerRadius(22), Margin = new Thickness(16, 44, 16, 8), RenderTransform = surfaceMove,
                BorderBrush = System.Windows.Media.Brushes.Gray, BorderThickness = new Thickness(1)
            };
            var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
            for (var i = 0; i < 18; i++)
                row.Children.Add(new System.Windows.Controls.Border { Width = 50, Height = 50, Margin = new Thickness(4), CornerRadius = new CornerRadius(12), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x60, (byte)(40 * i % 255), 120, 200)) });
            surface.Child = row;
            var host = new Window { Width = 1200, Height = 120, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent, ShowInTaskbar = false, Topmost = true, Left = 200, Top = 300, Content = surface };
            host.Show();
            var cases = new Queue<(string Name, bool Shadow)>([("gölgeli", true), ("gölgesiz", false), ("gölgeli", true), ("gölgesiz", false)]);
            void RunNext()
            {
                if (cases.Count == 0) { host.Close(); Shutdown(); return; }
                var (name, shadow) = cases.Dequeue();
                surface.Effect = shadow ? new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 3, Direction = 270, Opacity = 0.38, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance } : null;
                var frameCount = 0;
                var gaps = new List<double>();
                var last = Stopwatch.GetTimestamp();
                void Count(object? s, EventArgs a)
                {
                    var now = Stopwatch.GetTimestamp();
                    gaps.Add((now - last) * 1000.0 / Stopwatch.Frequency);
                    last = now;
                    frameCount++;
                }
                using var self = Process.GetCurrentProcess();
                var cpuBefore = self.TotalProcessorTime;
                var clock = Stopwatch.StartNew();
                System.Windows.Media.CompositionTarget.Rendering += Count;
                var slide = new System.Windows.Media.Animation.DoubleAnimation(0, 80, TimeSpan.FromMilliseconds(150)) { AutoReverse = true, RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(10) };
                slide.Completed += (_, _) =>
                {
                    System.Windows.Media.CompositionTarget.Rendering -= Count;
                    clock.Stop();
                    using var me = Process.GetCurrentProcess();
                    var cpu = (me.TotalProcessorTime - cpuBefore).TotalMilliseconds / clock.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
                    gaps.Sort();
                    var worst = gaps.Count > 0 ? gaps[^1] : 0;
                    var slow = gaps.Count(g => g > 25);
                    System.IO.File.AppendAllText(frames, $"{(frameMode is ["software"] ? "işlemci" : "ekran kartı"),-12} {name,-9} saniyede {frameCount / clock.Elapsed.TotalSeconds,5:0} kare, 25 ms'yi aşan kare {slow,3}, en uzun {worst,5:0} ms, işlemci %{cpu:0.0}{Environment.NewLine}");
                    surfaceMove.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
                    Dispatcher.BeginInvoke(RunNext, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                };
                surfaceMove.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slide);
            }
            Dispatcher.BeginInvoke(RunNext, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }
#if DEBUG
        if (e.Args is ["--audio-probe", var audioReport])
        {
            string result;
            try
            {
                using var endpoint = new QiDock.Services.Widgets.AudioEndpoint();
                var watcher = new QiDock.Services.Widgets.VolumeWatcher(Dispatcher);
                watcher.Start();
                var first = endpoint.Read();
                var firstProblem = endpoint.LastProblem;
                var second = endpoint.Read();
                result = $"izleyiciden sonra birinci: {first} {firstProblem}\nikinci: {second} {endpoint.LastProblem}\n";
                watcher.Dispose();
            }
            catch (Exception ex) { result = ex.ToString(); }
            System.IO.File.WriteAllText(audioReport, result);
            Shutdown();
            return;
        }
        if (e.Args is ["--wifi-probe", var wifiReport])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.InvokeAsync(async () =>
            {
                string result;
                try
                {
                    var wifi = new QiDock.Services.Widgets.WifiNetworks();
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var (scan, networks) = await wifi.ScanAsync();
                    result = $"{scan} ({watch.ElapsedMilliseconds} ms), {networks.Count} ağ\n"
                        + string.Join("\n", networks.Select(n => $"{n.Ssid} çubuk={n.Bars} şifreli={n.Secured} bağlı={n.Connected}"));
                }
                catch (Exception ex) { result = ex.ToString(); }
                System.IO.File.WriteAllText(wifiReport, result);
                Shutdown();
            });
            return;
        }
        if (e.Args is ["--gallery-probe", var galleryReport, .. var galleryMode])
        {
            // How long the desktop widget gallery takes to open, and how smoothly it idles and scrolls.
            if (galleryMode is ["software"]) System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var label = galleryMode is ["software"] ? "işlemci" : "ekran kartı";
            var opening = Stopwatch.StartNew();
            var gallery = new QiDock.DesktopWidgets.DeskGallery(new QiDock.DesktopWidgets.DeskWidgetManager());
            gallery.ContentRendered += (_, _) =>
            {
                opening.Stop();
                System.IO.File.AppendAllText(galleryReport, $"{label}: açılış {opening.ElapsedMilliseconds} ms{Environment.NewLine}");
                var shot = new System.Windows.Media.Imaging.RenderTargetBitmap((int)gallery.ActualWidth, (int)gallery.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                shot.Render((System.Windows.Media.Visual)gallery.Content);
                var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(shot));
                using (var file = System.IO.File.Create(System.IO.Path.ChangeExtension(galleryReport, ".png"))) png.Save(file);
                Measure("boşta", 5, null, () =>
                {
                    var scroller = FindScroller(gallery);
                    var offset = 0.0;
                    Measure("kaydırırken", 4, () =>
                    {
                        offset += 9;
                        if (offset > scroller?.ScrollableHeight) offset = 0;
                        scroller?.ScrollToVerticalOffset(offset);
                    }, () => { gallery.Close(); Shutdown(); });
                });
            };
            void Measure(string name, double seconds, Action? eachFrame, Action next)
            {
                var gaps = new List<double>();
                var last = Stopwatch.GetTimestamp();
                using var self = Process.GetCurrentProcess();
                var cpuBefore = self.TotalProcessorTime;
                var clock = Stopwatch.StartNew();
                void Count(object? s, EventArgs a)
                {
                    var now = Stopwatch.GetTimestamp();
                    gaps.Add((now - last) * 1000.0 / Stopwatch.Frequency);
                    last = now;
                    eachFrame?.Invoke();
                    if (clock.Elapsed.TotalSeconds < seconds) return;
                    System.Windows.Media.CompositionTarget.Rendering -= Count;
                    using var me = Process.GetCurrentProcess();
                    var cpu = (me.TotalProcessorTime - cpuBefore).TotalMilliseconds / clock.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
                    gaps.Sort();
                    System.IO.File.AppendAllText(galleryReport, $"{label}: {name,-12} saniyede {gaps.Count / clock.Elapsed.TotalSeconds,5:0} kare, 50 ms'yi aşan {gaps.Count(g => g > 50),3}, en uzun {(gaps.Count > 0 ? gaps[^1] : 0),5:0} ms, işlemci %{cpu:0.0}{Environment.NewLine}");
                    Dispatcher.BeginInvoke(next, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }
                System.Windows.Media.CompositionTarget.Rendering += Count;
            }
            static System.Windows.Controls.ScrollViewer? FindScroller(DependencyObject node)
            {
                if (node is System.Windows.Controls.ScrollViewer found) return found;
                for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
                    if (FindScroller(System.Windows.Media.VisualTreeHelper.GetChild(node, i)) is { } inner) return inner;
                return null;
            }
            gallery.Show();
            return;
        }
#endif
        if (e.Args is ["--render-ui", var folder])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            UiSheet.Render(folder, Preferences.Current.WidgetOptions, Preferences.Current.SurfaceOpacity, () => Shutdown());
            return;
        }
#endif
        // Started by Restart(): the old copy is still closing; give it a moment to let go of the taskbar and the lock.
        if (e.Args is ["--after", var previous] && int.TryParse(previous, out var previousId))
        {
            try { using var old = Process.GetProcessById(previousId); old.WaitForExit(15000); }
            catch (ArgumentException) { } // already gone
        }
        string? filesRequest = e.Args is ["--files", .. var filesArgs] ? (filesArgs.Length > 0 ? filesArgs[0] : "") : null;
        _instanceMutex = new Mutex(initiallyOwned: true, name: "Local\\QiDock.Singleton", out var createdNew);
        if (!createdNew && filesRequest is not null)
        {
            // "QiDock.exe --files [folder]" while QiDock runs: the running copy opens its Files window.
            Files.FilesLauncher.Signal(filesRequest);
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }
        if (!createdNew)
        {
            MessageBox.Show(UiText.Get("AlreadyRunning"), "QiDock", MessageBoxButton.OK, MessageBoxImage.Information);
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            // One failing click or timer must not take the dock (and the hidden Windows taskbar) down with it.
            AppLog.Write("Arayüz hatası (uygulama açık kaldı): " + args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLog.Write("Ölümcül hata: " + args.ExceptionObject);
            // The process is about to end: the Windows taskbar comes back now, not only at the next start.
            if (args.IsTerminating) Services.TaskbarReplacementSession.RestoreAfterCrash();
        };
        AppLog.StartMemoryReports();
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Write("Arka plan görevi hatası: " + args.Exception);
            args.SetObserved();
        };
        // Low-memory mode: everything is drawn by the processor. The graphics driver then never sets up its drawing
        // device, which alone holds about 300 MB on integrated graphics; animations cost a little more processor time.
        if (Preferences.Current.LowMemory)
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            SoftwareRendering = true;
            // Blurred shadows are what the processor draws slowest: a sliding dock with one drops from about 120 to
            // about 34 frames a second. Every shadow goes as soon as its element appears.
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler(static (sender, _) =>
            {
                if (sender is UIElement { Effect: System.Windows.Media.Effects.DropShadowEffect } element) element.Effect = null;
            }));
            AppLog.Write("Az bellek modu açık (çizim işlemcide, gölgesiz)");
        }
        var firstWindow = MainWindow?.GetType().Name ?? "yok";
        var window = new MainWindow();
        // The app ends when THIS window closes. Without this, the first window created (possibly a helper made
        // while the dock was being built) would count as the main one, and closing it would quit everything.
        var assigned = MainWindow?.GetType().Name ?? "yok";
        MainWindow = window;
        AppLog.Write($"Başladı (önceki ana pencere: {firstWindow}, kendiliğinden atanan: {assigned})");
        window.Show();
        Files.FilesLauncher.Listen(Dispatcher);
        if (filesRequest is not null)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => Files.FilesWindow.Open(filesRequest.Length > 0 ? filesRequest : null));
    }

    /// <summary>Whether this run draws on the processor (low-memory mode was on at start).</summary>
    public bool SoftwareRendering { get; private set; }

    /// <summary>Whether blurred shadows are drawn (not in low-memory mode, where they make animations stutter).</summary>
    public static bool Shadows => Current is not App { SoftwareRendering: true };

    /// <summary>Starts a fresh copy (which waits for this one to close) and closes this one.</summary>
    public void Restart()
    {
        if (Environment.ProcessPath is not { } path) return;
        try
        {
            Process.Start(new ProcessStartInfo(path, $"--after {Environment.ProcessId}") { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Write("Yeniden başlatılamadı: " + ex.Message);
            return;
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Write($"Kapandı (kod {e.ApplicationExitCode})");
        TrayHost.Shared.Stop();
        var error = (MainWindow as QiDock.MainWindow)?.RestoreWindowsTaskbar();
        if (error is not null)
        {
            Trace.TraceError(error);
            e.ApplicationExitCode = 1;
        }
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        _instanceMutex = null;
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        TrayHost.Shared.Stop();
        var error = (MainWindow as QiDock.MainWindow)?.RestoreWindowsTaskbar();
        if (error is not null) Trace.TraceError(error);
        base.OnSessionEnding(e);
    }
}
