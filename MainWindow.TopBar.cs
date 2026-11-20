using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using QiDock.Services.Widgets;

namespace QiDock;

/// <summary>
/// The widget bar lives in its own strip at the top of the screen (the dock keeps only apps). It is
/// on screen while the desktop is clean and steps aside when app windows are open. Also hosts the
/// short volume HUD shown when the volume keys are pressed.
/// </summary>
public partial class MainWindow
{
    private TopBarWindow? _topBar;
    private VolumeHud? _volumeHud;
    private VolumeWatcher? _volumeWatcher;
    private DispatcherTimer? _desktopCheck;
    private nint _minimizeHook;
    private TopNative.WinEventProc? _minimizeProc;

    private void InitializeTopBar()
    {
        MigrateWidgetProfiles();
        HideDuplicateTiles();
        PreferControlCenterTile();
        BellToCorner();
        SecondBatchHidden();
        ProfileToMenu();
        ApplyDockBell();
        _topBar = new TopBarWindow { Tabs = _widgetBar.ProfileStrip };
        _widgetBar.ProfileMenuRequested += (_, target) => ShowProfileMenu(target.Index, target.Anchor);
        _topBar.SetLock(() => _widgetBar.IsAnyPopupOpen || _widgetBar.IsEditing || _openContextMenu?.IsOpen == true || EdgePanel.StandsOnDock || _holdDockUp);
        _topBar.LiveChanged += (_, live) => _widgetBar.SetLive(live || WidgetsInDock && DockOnScreen);
        _topBar.MenuRequested += (_, _) => ShowDockMenu(PlacementMode.MousePoint, null);
        _topBar.SetPeekEnabled(_preferences.Current.TopBarPeek);
        ApplyDockGlass();
        _widgetBar.SetLive(false);
        ApplyWidgetPlacement();
        UpdateTopBarContent();

        // Minimize/restore do not always change the foreground window; listen for them directly.
        _desktopCheck = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _desktopCheck.Tick += (_, _) =>
        {
            _desktopCheck.Stop();
            RefreshDesktopClean();
        };
        _minimizeProc = (_, _, _, objectId, _, _, _) =>
        {
            if (objectId == 0) ScheduleDesktopCheck();
        };
        _minimizeHook = TopNative.SetWinEventHook(0x0016, 0x0017, 0, _minimizeProc, 0, 0, 0);
        RefreshDesktopClean();
        InitializeTrash();

        _volumeHud = new VolumeHud { TopOffset = VolumeHudTop };
        _volumeWatcher = new VolumeWatcher(Dispatcher);
        if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource hudSource) InitializeHud(hudSource);
        _volumeWatcher.Start();
    }

    private Models.WidgetPlacement? _placedWidgets;
    private Border? _widgetDivider;

    private Models.WidgetPlacement? _placementOverride;
    private double _dockWidgetScale = 1;

    /// <summary>Widgets in the dock shrink with the icons when the row is too wide for the screen.</summary>
    private void SetDockWidgetScale(double scale)
    {
        scale = Math.Round(scale, 3);
        if (Math.Abs(scale - _dockWidgetScale) < 0.001) return;
        _dockWidgetScale = scale;
        ApplyWidgetHostTransform();
        AlignDockWidgets();
    }

    /// <summary>
    /// The widgets' place for the dock's edge: a side dock is too narrow for widget tiles (they keep the top strip),
    /// and a dock on the top edge takes the strip's place (the widgets then join it).
    /// </summary>
    private Models.WidgetPlacement CurrentPlacement
    {
        get
        {
            var placement = _placementOverride ?? _preferences.Current.WidgetPlacement;
            if (Services.DockGeometry.Vertical) return Models.WidgetPlacement.TopBar;
            if (Edge == Models.DockEdge.Top && placement == Models.WidgetPlacement.TopBar) return Models.WidgetPlacement.DockRight;
            return placement;
        }
    }

    private bool WidgetsInDock => CurrentPlacement != Models.WidgetPlacement.TopBar;

    /// <summary>The dock is on screen (not slid away by auto-hide): widgets in it only tick while it is.</summary>
    private bool DockOnScreen => _autoHideController is not { IsHidden: true };

    /// <summary>
    /// Puts the widget tiles where the user wants them: in the top strip (the dock keeps only apps), or inside the
    /// dock, left of the apps or right of them (before the bin), with a thin divider like the other dock groups.
    /// </summary>
    private void ApplyWidgetPlacement()
    {
        var placement = CurrentPlacement;
        if (_topBar is null || _placedWidgets == placement) return;
        _placedWidgets = placement;
        _widgetBar.ClosePopups();
        _topBar.Bar = null;
        WidgetHost.Content = null;
        _widgetDivider ??= CreateWidgetDivider();
        if (VisualTreeHelper.GetParent(WidgetHost) is Panel hostParent) hostParent.Children.Remove(WidgetHost);
        if (VisualTreeHelper.GetParent(_widgetDivider) is Panel dividerParent) dividerParent.Children.Remove(_widgetDivider);

        if (placement == Models.WidgetPlacement.TopBar)
        {
            // Parked in the dock row, empty: the dock measures it as zero width.
            DockRow.Children.Add(WidgetHost);
            WidgetHost.Visibility = Visibility.Collapsed;
            SetDockWidgetScale(1);
            _topBar.Bar = _widgetBar;
            _widgetBar.SetLive(_topBar.IsShown);
        }
        else
        {
            WidgetHost.Margin = placement == Models.WidgetPlacement.DockLeft ? new Thickness(2, 0, 0, 0) : new Thickness(0, 0, 2, 0);
            WidgetHost.VerticalAlignment = VerticalAlignment.Center;
            if (placement == Models.WidgetPlacement.DockLeft)
            {
                DockContents.Children.Insert(0, WidgetHost);
                DockContents.Children.Insert(1, _widgetDivider);
            }
            else
            {
                DockContents.Children.Add(_widgetDivider);
                DockContents.Children.Add(WidgetHost);
            }
            WidgetHost.Content = _widgetBar;
            WidgetHost.Visibility = Visibility.Visible;
            ApplyWidgetHostTransform();
            _widgetBar.SetLive(DockOnScreen);
        }
        AlignDockWidgets();
        UpdateTopBarContent();
        ResizeDockToContent();
        _replacementSession?.RefreshPosition();
    }

#if DEBUG
    /// <summary>Draws the dock with the widgets left and right of the apps (no clicks, nothing saved), then quits.</summary>
    private void SnapshotDockPlacements(string folder)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            System.IO.Directory.CreateDirectory(folder);
            foreach (var placement in new[] { Models.WidgetPlacement.TopBar, Models.WidgetPlacement.DockRight, Models.WidgetPlacement.DockLeft })
            {
                _placementOverride = placement;
                ApplyWidgetPlacement();
                UpdateLayout();
                var width = DockSurface.ActualWidth + 40;
                var height = DockSurface.ActualHeight + 40;
                var visual = new DrawingVisual();
                using (var context = visual.RenderOpen())
                {
                    context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x5A, 0x7A, 0x9A)), null, new Rect(0, 0, width, height));
                    context.DrawRectangle(new VisualBrush(DockSurface) { Stretch = Stretch.None }, null, new Rect(20, 20, DockSurface.ActualWidth, DockSurface.ActualHeight));
                }
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(width * 2), (int)(height * 2), 192, 192, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var file = System.IO.File.Create(System.IO.Path.Combine(folder, $"dock-{placement}.png"));
                encoder.Save(file);
            }
            SnapshotSheets(folder);
        };
        timer.Start();
    }

    /// <summary>Opens two cards out of the dock (the AI limits tile, the Control Center tile) and draws each once settled.</summary>
    private void SnapshotSheets(string folder)
    {
        _placementOverride = Models.WidgetPlacement.DockRight;
        ApplyWidgetPlacement();
        UpdateLayout();
        var tiles = new Queue<(string Name, string Hint)>([("ai", "Claude"), ("sys", "CPU"), ("cc", "Denetim"), ("bell", "#bell")]);
        void Next()
        {
            if (tiles.Count == 0)
            {
                SnapshotEdges(folder);
                return;
            }
            var (name, hint) = tiles.Dequeue();
            var button = hint == "#bell" ? NotificationsButton : FindVisualChildren<System.Windows.Controls.Primitives.ButtonBase>(WidgetHost)
                .FirstOrDefault(b => FindVisualChildren<TextBlock>(b).Any(t => t.Text.StartsWith(hint, StringComparison.Ordinal)));
            button?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            wait.Tick += (_, _) =>
            {
                wait.Stop();
                var sheet = Application.Current.Windows.OfType<EdgePanel>().FirstOrDefault(w => w.IsVisible);
                if (sheet?.Content is FrameworkElement content)
                {
                    var width = sheet.ActualWidth;
                    var height = sheet.ActualHeight;
                    var visual = new DrawingVisual();
                    using (var context = visual.RenderOpen())
                    {
                        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x5A, 0x7A, 0x9A)), null, new Rect(0, 0, width, height + 70));
                        context.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
                        var dockLeft = Left + DockSurface.TranslatePoint(new Point(0, 0), this).X - sheet.Left;
                        context.DrawRectangle(new VisualBrush(DockSurface) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(-dockLeft, 0, width, 70) },
                            null, new Rect(0, height - EdgePanel.SeamDepth, width, 70));
                    }
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(width * 2), (int)((height + 70) * 2), 192, 192, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var file = System.IO.File.Create(System.IO.Path.Combine(folder, $"sheet-{name}.png"));
                    encoder.Save(file);
                    // What the screen really shows at the seam (device pixels), sheet foot and dock together.
                    if (PresentationSource.FromVisual(sheet) is System.Windows.Interop.HwndSource sheetSource
                        && SnapNative.GetWindowRect(sheetSource.Handle, out var bounds))
                    {
                        var grab = SnapNative.Grab(bounds.Left - 40, bounds.Bottom - 140, bounds.Right - bounds.Left + 80, 220);
                        var screenEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        screenEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(grab));
                        using var screenFile = System.IO.File.Create(System.IO.Path.Combine(folder, $"screen-{name}.png"));
                        screenEncoder.Save(screenFile);
                    }
                    sheet.Close(animated: false);
                }
                else System.IO.File.WriteAllText(System.IO.Path.Combine(folder, $"sheet-{name}.txt"), button is null ? "tile not found" : "no sheet open");
                Next();
            };
            wait.Start();
        }
        Next();
    }

    /// <summary>Moves the dock to the top, left and right edges in turn (nothing saved), opens the bell's list on
    /// each and records the real screen, then puts the dock back and quits.</summary>
    private void SnapshotEdges(string folder)
    {
        var edges = new Queue<Models.DockEdge>([Models.DockEdge.Top, Models.DockEdge.Left, Models.DockEdge.Right]);
        void After(int milliseconds, Action action)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                action();
            };
            timer.Start();
        }
        void Next()
        {
            if (edges.Count == 0)
            {
                _edgeOverride = null;
                ApplyDockEdge();
                After(800, Close);
                return;
            }
            var edge = edges.Dequeue();
            _edgeOverride = edge;
            ApplyDockEdge();
            After(1500, () =>
            {
                NotificationsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                After(1300, () =>
                {
                    try
                    {
                        if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource dockSource && SnapNative.GetWindowRect(dockSource.Handle, out var dock))
                        {
                            var box = dock;
                            var sheet = Application.Current.Windows.OfType<EdgePanel>().FirstOrDefault(w => w.IsVisible);
                            if (sheet is not null && PresentationSource.FromVisual(sheet) is System.Windows.Interop.HwndSource sheetSource && SnapNative.GetWindowRect(sheetSource.Handle, out var panel))
                            {
                                box.Left = Math.Min(box.Left, panel.Left);
                                box.Top = Math.Min(box.Top, panel.Top);
                                box.Right = Math.Max(box.Right, panel.Right);
                                box.Bottom = Math.Max(box.Bottom, panel.Bottom);
                            }
                            var grab = SnapNative.Grab(box.Left, box.Top, box.Right - box.Left, box.Bottom - box.Top);
                            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(grab));
                            using var file = System.IO.File.Create(System.IO.Path.Combine(folder, $"edge-{edge}.png"));
                            encoder.Save(file);
                            sheet?.Close(animated: false);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.IO.File.WriteAllText(System.IO.Path.Combine(folder, $"edge-{edge}.txt"), ex.ToString());
                    }
                    Next();
                });
            });
        }
        Next();
    }

    /// <summary>
    /// Plays the user's sequence without touching the mouse or saving anything: bottom, the bell on the bottom, the
    /// left edge with the Downloads tile magnified, the bell on the right edge, then back to the bottom. Each step is
    /// a real screen grab of the dock (and the panel), plus the dock's measurements.
    /// </summary>
    private void SnapshotEdgeCycle(string folder)
    {
        System.IO.Directory.CreateDirectory(folder);
        var log = new System.Text.StringBuilder();
        var steps = new Queue<(int Wait, Action Act)>();
        void Grab(string name)
        {
            UpdateLayout();
            log.AppendLine($"{name}: edge={Edge} placement={CurrentPlacement} window={ActualWidth:0}x{ActualHeight:0} area={_dockArea} surface={DockSurface.ActualWidth:0.0}x{DockSurface.ActualHeight:0.0} base={_baseSurfaceWidth:0.0} rowDesired={DockRow.DesiredSize.Width:0.0} rowActual={DockRow.ActualWidth:0.0} widgets={WidgetHost.ActualWidth:0.0} scale={_dockWidgetScale:0.00}");
            if (PresentationSource.FromVisual(this) is not System.Windows.Interop.HwndSource dockSource || !SnapNative.GetWindowRect(dockSource.Handle, out var box)) return;
            var sheet = Application.Current.Windows.OfType<EdgePanel>().FirstOrDefault(w => w.IsVisible);
            if (sheet is not null && PresentationSource.FromVisual(sheet) is System.Windows.Interop.HwndSource sheetSource && SnapNative.GetWindowRect(sheetSource.Handle, out var panel))
            {
                log.AppendLine($"   panel {panel.Left},{panel.Top} {panel.Right - panel.Left}x{panel.Bottom - panel.Top}");
                box.Left = Math.Min(box.Left, panel.Left);
                box.Top = Math.Min(box.Top, panel.Top);
                box.Right = Math.Max(box.Right, panel.Right);
                box.Bottom = Math.Max(box.Bottom, panel.Bottom);
            }
            var grab = SnapNative.Grab(box.Left, box.Top, box.Right - box.Left, box.Bottom - box.Top);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(grab));
            using var file = System.IO.File.Create(System.IO.Path.Combine(folder, $"cycle-{name}.png"));
            encoder.Save(file);
        }
        void MoveTo(Models.DockEdge? edge)
        {
            _edgeOverride = edge;
            Preferences_Changed(null, EventArgs.Empty); // the same path as choosing it in the menu or the settings
            _autoHideController?.EvaluateState();
        }
        void Bell() => NotificationsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        void CloseSheets()
        {
            foreach (var sheet in Application.Current.Windows.OfType<EdgePanel>().Where(w => w.IsVisible).ToList()) sheet.Close(animated: false);
        }
        void Magnify(Button tile)
        {
            var at = tile.TranslatePoint(new Point(tile.ActualWidth / 2, tile.ActualHeight / 2), DockContents);
            MagnifyAt(at.X);
        }
        void Add(int wait, Action act) => steps.Enqueue((wait, act));
        _holdDockUp = true;
        Add(100, () => _autoHideController?.EvaluateState());
        Add(5000, () => Grab("1-bottom"));
        Add(300, Bell);
        Add(1400, () => { Grab("2-bottom-bell"); CloseSheets(); });
        Add(300, () => MoveTo(Models.DockEdge.Left));
        Add(1800, () => { Grab("3-left"); Magnify(DownloadsButton); });
        Add(300, () => { Grab("4-left-magnified"); ResetMagnification(); });
        Add(400, () => MoveTo(Models.DockEdge.Right));
        Add(1800, () => { Grab("5-right"); Bell(); });
        Add(1400, () => { Grab("6-right-bell"); CloseSheets(); });
        Add(300, () => MoveTo(Models.DockEdge.Top));
        Add(1800, () => { Grab("7-top"); Bell(); });
        Add(1400, () => { Grab("8-top-bell"); CloseSheets(); });
        Add(300, () => MoveTo(null));
        Add(2500, () => Grab("9-bottom-again"));
        Add(300, () => Magnify(TrashButton));
        Add(300, () => { Grab("10-bottom-magnified"); ResetMagnification(); });
        // The classic layout: widgets in the top bar, cards grow down out of it.
        void TopTile(string hint)
        {
            if (_topBar is null) return;
            var tile = FindVisualChildren<System.Windows.Controls.Primitives.ButtonBase>(_topBar)
                .FirstOrDefault(b => FindVisualChildren<TextBlock>(b).Any(t => t.Text.StartsWith(hint, StringComparison.Ordinal)));
            log.AppendLine($"top tile {hint}: {(tile is null ? "not found" : "found")}");
            tile?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }
        void GrabTop(string name)
        {
            if (_topBar is null || !SnapNative.GetWindowRect(_topBar.Handle, out var box)) return;
            var sheet = Application.Current.Windows.OfType<EdgePanel>().FirstOrDefault(w => w.IsVisible);
            if (sheet is not null && PresentationSource.FromVisual(sheet) is System.Windows.Interop.HwndSource sheetSource && SnapNative.GetWindowRect(sheetSource.Handle, out var panel))
            {
                log.AppendLine($"   {name} panel {panel.Left},{panel.Top} {panel.Right - panel.Left}x{panel.Bottom - panel.Top}");
                box.Left = Math.Min(box.Left, panel.Left);
                box.Right = Math.Max(box.Right, panel.Right);
                box.Bottom = Math.Max(box.Bottom, panel.Bottom);
            }
            else log.AppendLine($"   {name}: no panel");
            var grab = SnapNative.Grab(box.Left, box.Top, box.Right - box.Left, box.Bottom - box.Top);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(grab));
            using var file = System.IO.File.Create(System.IO.Path.Combine(folder, $"cycle-{name}.png"));
            encoder.Save(file);
        }
        Add(300, () =>
        {
            _placementOverride = Models.WidgetPlacement.TopBar;
            ApplyWidgetPlacement();
            if (_topBar is not null) { _topBar.ForceVisible = true; _topBar.Update(); }
        });
        Add(1500, () => TopTile("Denetim"));
        Add(1400, () => { GrabTop("12-topbar-cc"); CloseSheets(); });
        Add(600, () => TopTile("Claude"));
        Add(1400, () => { GrabTop("13-topbar-ai"); CloseSheets(); foreach (var p in FindVisualChildren<System.Windows.Controls.Primitives.Popup>(_topBar!)) p.IsOpen = false; });
        Add(300, () =>
        {
            if (_topBar is not null) { _topBar.ForceVisible = false; _topBar.Update(); }
            _placementOverride = null;
            ApplyWidgetPlacement();
        });
        Add(300, () => OpenSettings_Click(this, new RoutedEventArgs()));
        Add(800, () =>
        {
            if (_settingsWindow is not null)
                foreach (var scroller in FindVisualChildren<ScrollViewer>(_settingsWindow).Where(v => v.IsVisible)) scroller.ScrollToVerticalOffset(0);
        });
        Add(700, () =>
        {
            if (_settingsWindow?.Content is FrameworkElement page && PresentationSource.FromVisual(_settingsWindow) is System.Windows.Interop.HwndSource settingsSource
                && SnapNative.GetWindowRect(settingsSource.Handle, out var r))
            {
                var grab = SnapNative.Grab(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(grab));
                using var file = System.IO.File.Create(System.IO.Path.Combine(folder, "cycle-11-settings.png"));
                encoder.Save(file);
            }
            _settingsWindow?.Close();
        });
        Add(500, () => { System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "cycle.txt"), log.ToString()); Close(); });
        void Next()
        {
            if (steps.Count == 0) return;
            var (wait, act) = steps.Dequeue();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(wait) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try { act(); }
                catch (Exception ex) { log.AppendLine("ERROR " + ex); }
                Next();
            };
            timer.Start();
        }
        Next();
    }

    private static class SnapNative
    {
        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] public static extern bool GetWindowRect(nint window, out Rect rect);
        [DllImport("user32.dll")] private static extern nint GetDC(nint window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
        [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
        [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
        [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sx, int sy, uint op);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);

        /// <summary>Copies a part of the screen (device pixels).</summary>
        public static System.Windows.Media.Imaging.BitmapSource Grab(int x, int y, int width, int height)
        {
            var screen = GetDC(0);
            var memory = CreateCompatibleDC(screen);
            var bitmap = CreateCompatibleBitmap(screen, width, height);
            var old = SelectObject(memory, bitmap);
            BitBlt(memory, 0, 0, width, height, screen, x, y, 0x00CC0020 /* SRCCOPY */);
            SelectObject(memory, old);
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
            return source;
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }
#endif

    /// <summary>The widgets' shrink, and on a top dock the turn undone so they read upright.</summary>
    private void ApplyWidgetHostTransform()
    {
        var matrix = Matrix.Identity;
        // In the dock the widget tiles grow with the dock's size, as the app icons do.
        var grow = WidgetsInDock ? Services.TaskbarReplacementSession.Scale : 1;
        matrix.Scale(_dockWidgetScale * grow, _dockWidgetScale * grow);
        if (Edge == Models.DockEdge.Top) matrix.Append(Services.DockGeometry.FrameToScreen(Models.DockEdge.Top));
        WidgetHost.LayoutTransform = matrix.IsIdentity ? Transform.Identity : new MatrixTransform(matrix);
    }

    private Border CreateWidgetDivider()
    {
        var divider = new Border { Width = 1, Height = 22, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
        divider.SetResourceReference(Border.BackgroundProperty, "StrokeSubtle");
        return divider;
    }

    /// <summary>Widget tiles sit centred on the app icons (the row below the icons holds the running dots).</summary>
    private void AlignDockWidgets()
    {
        if (!WidgetsInDock) return;
        // Icons stand centred in the dock: so do the widget tiles and the divider.
        WidgetHost.VerticalAlignment = VerticalAlignment.Center;
        WidgetHost.Margin = new Thickness(WidgetHost.Margin.Left, 0, WidgetHost.Margin.Right, 0);
        if (_widgetDivider is not null) _widgetDivider.Margin = new Thickness(4, 0, 4, 0);
    }

    /// <summary>Under the widget bar when it is on screen, otherwise near the top edge.</summary>
    private double VolumeHudTop()
    {
        if (_topBar is not { IsVisible: true } bar || bar.ActualHeight <= 0) return 10;
        return bar.Top + bar.ActualHeight - 18 - SystemParameters.WorkArea.Top + 6;
    }

    /// <summary>
    /// First run with profiles: the existing layout becomes "Varsayılan" (new widgets start hidden
    /// there) and an "İş" profile shows the work-oriented widgets.
    /// </summary>
    private void MigrateWidgetProfiles()
    {
        var options = _preferences.Current.WidgetOptions;
        if (options.Profiles is { Count: > 0 }) return;
        var hidden = (options.Hidden ?? []).Concat(Models.WidgetIds.Added).Distinct().ToList();
        string[] work = [Models.WidgetIds.Clock, Models.WidgetIds.Focus, Models.WidgetIds.Todo, Models.WidgetIds.Folder, Models.WidgetIds.Search, Models.WidgetIds.System, Models.WidgetIds.AppVolume, Models.WidgetIds.Profile];
        string[] tools = [Models.WidgetIds.WorldClock, Models.WidgetIds.Colors, Models.WidgetIds.Tools, Models.WidgetIds.Weather, Models.WidgetIds.Profile];
        var migrated = options with
        {
            Hidden = hidden,
            Profiles =
            [
                new Models.WidgetProfile(Services.UiText.Get("ProfileDefault"), options.Order, hidden),
                new Models.WidgetProfile(Services.UiText.Get("ProfileWork"), work, Models.WidgetIds.All.Except(work).ToList()),
                new Models.WidgetProfile(Services.UiText.Get("ProfileTools"), tools, Models.WidgetIds.All.Except(tools).ToList())
            ],
            ActiveProfile = 0
        };
        SavePreferences(_preferences.Current with { Widgets = migrated });
    }

    /// <summary>
    /// Once per install: the notifications and Control Center tiles start hidden in every profile, since the dock
    /// bell and the Quick controls tile already open those panels. Showing them again later is kept.
    /// </summary>
    private void HideDuplicateTiles()
    {
        var options = _preferences.Current.WidgetOptions;
        if (options.LayoutRevision >= 1) return;
        List<string> Hide(IReadOnlyList<string>? hidden) => (hidden ?? []).Concat(Models.WidgetIds.Duplicates).Distinct().ToList();
        var profiles = options.EffectiveProfiles.Select(profile => profile with { Hidden = Hide(profile.Hidden) }).ToList();
        SavePreferences(_preferences.Current with
        {
            Widgets = options with { Hidden = Hide(options.Hidden), Profiles = profiles, LayoutRevision = 1 }
        });
    }

    /// <summary>
    /// Once per install: the Quick controls tile (Wi-Fi, Bluetooth, volume) makes way for the Control Center tile at
    /// the end of the row, which holds all of them plus brightness and opens like the bell's list.
    /// </summary>
    private void PreferControlCenterTile()
    {
        var options = _preferences.Current.WidgetOptions;
        if (options.LayoutRevision >= 2) return;
        const string quick = Models.WidgetIds.QuickControls, center = Models.WidgetIds.ControlCenter;
        List<string> Hidden(IReadOnlyList<string>? hidden) => (hidden ?? []).Where(id => id != center).Append(quick).Distinct().ToList();
        List<string> Order(IReadOnlyList<string>? order) => (order is { Count: > 0 } ? order : Models.WidgetIds.All).Where(id => id != center).Append(center).ToList();
        var profiles = options.EffectiveProfiles.Select(profile => profile with { Order = Order(profile.Order), Hidden = Hidden(profile.Hidden) }).ToList();
        SavePreferences(_preferences.Current with
        {
            Widgets = options with { Order = Order(options.EffectiveOrder), Hidden = Hidden(options.Hidden), Profiles = profiles, LayoutRevision = 2 }
        });
    }

    /// <summary>
    /// Once per install: the bell leaves the dock's left end and stands as a tile in the corner next to the battery
    /// (shown in every profile). Hiding it again later is kept.
    /// </summary>
    /// <summary>The dock's own bell shows only while the bell tile is hidden (so there is always one, never two).</summary>
    private void ApplyDockBell() =>
        NotificationsButton.Visibility = _preferences.Current.WidgetOptions.IsVisible(Models.WidgetIds.Notifications) ? Visibility.Collapsed : Visibility.Visible;

    private void BellToCorner()
    {
        var options = _preferences.Current.WidgetOptions;
        if (options.LayoutRevision >= 3) return;
        List<string> Shown(IReadOnlyList<string>? hidden) => (hidden ?? []).Except(Models.WidgetIds.Corner).ToList();
        var profiles = options.EffectiveProfiles.Select(profile => profile with { Hidden = Shown(profile.Hidden) }).ToList();
        SavePreferences(_preferences.Current with
        {
            Widgets = options with { Hidden = Shown(options.Hidden), Profiles = profiles, LayoutRevision = 3 }
        });
    }

    /// <summary>Once: the second batch of widgets starts hidden in every profile (they wait in the add list).</summary>
    private void SecondBatchHidden()
    {
        var options = _preferences.Current.WidgetOptions;
        if (options.LayoutRevision >= 4) return;
        List<string> Hide(IReadOnlyList<string>? hidden) => (hidden ?? []).Concat(Models.WidgetIds.SecondBatch).Distinct().ToList();
        var profiles = options.EffectiveProfiles.Select(profile => profile with { Hidden = Hide(profile.Hidden) }).ToList();
        SavePreferences(_preferences.Current with
        {
            Widgets = options with { Hidden = Hide(options.Hidden), Profiles = profiles, LayoutRevision = 4 }
        });
    }

    /// <summary>Once: the profile tile leaves the bar; profiles are switched from the dock's menu.</summary>
    private void ProfileToMenu()
    {
        var options = _preferences.Current.WidgetOptions;
        if (options.LayoutRevision >= 5) return;
        List<string> Hide(IReadOnlyList<string>? hidden) => (hidden ?? []).Append(Models.WidgetIds.Profile).Distinct().ToList();
        var profiles = options.EffectiveProfiles.Select(profile => profile with { Hidden = Hide(profile.Hidden) }).ToList();
        SavePreferences(_preferences.Current with
        {
            Widgets = options with { Hidden = Hide(options.Hidden), Profiles = profiles, LayoutRevision = 5 }
        });
    }

    /// <summary>"Profil" in the dock's menu: switch, add, rename or remove the widget profiles.</summary>
    private MenuItem ProfileMenu(Models.WidgetSettings options)
    {
        var profiles = options.EffectiveProfiles;
        var active = options.EffectiveActiveProfile;
        var root = Item(Services.UiText.Get("MenuProfile"), "IconPerson");
        for (var i = 0; i < profiles.Count; i++)
        {
            var index = i;
            var item = Item(profiles[i].Name, null, i == active);
            if (i < 9) item.InputGestureText = "Ctrl+Alt+Shift+" + (i + 1);
            item.Click += (_, _) =>
            {
                var now = _preferences.Current.WidgetOptions;
                if (index == now.EffectiveActiveProfile || index >= now.EffectiveProfiles.Count) return;
                SaveWidgetOptions(now.SwitchProfile(index));
                Widgets.Ui.ShowHud("IconPerson", now.EffectiveProfiles[index].Name);
            };
            root.Items.Add(item);
        }
        root.Items.Add(new Separator());
        var add = Item(Services.UiText.Get("ProfileAdd"), "IconPlus");
        add.Click += (_, _) =>
        {
            var now = _preferences.Current.WidgetOptions;
            SaveWidgetOptions(now.AddProfile(Services.UiText.Format("ProfileNew", now.EffectiveProfiles.Count + 1)));
        };
        root.Items.Add(add);
        var rename = Item(Services.UiText.Get("ProfileRename"), "IconEdit");
        foreach (var key in new[] { "ProfileDefault", "ProfileWork", "ProfileHome", "ProfileFocus", "ProfileGame", "ProfileSchool", "ProfileCreative", "ProfileTools" })
        {
            var name = Services.UiText.Get(key);
            var item = Item(name, null, profiles[active].Name == name);
            item.Click += (_, _) => SaveWidgetOptions(_preferences.Current.WidgetOptions.RenameProfile(active, name));
            rename.Items.Add(item);
        }
        root.Items.Add(rename);
        var remove = Item(Services.UiText.Get("ProfileRemove"), "IconClose");
        remove.IsEnabled = profiles.Count > 1;
        remove.Click += (_, _) => SaveWidgetOptions(_preferences.Current.WidgetOptions.RemoveProfile(active));
        root.Items.Add(remove);
        return root;
    }

    private void ShowProfileMenu(int index, FrameworkElement anchor)
    {
        var options = _preferences.Current.WidgetOptions;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        menu.Opened += DockContextMenu_Opened;
        menu.Closed += DockContextMenu_Closed;
        var rename = Item(Services.UiText.Get("ProfileRename"), "IconEdit");
        foreach (var key in new[] { "ProfileDefault", "ProfileWork", "ProfileHome", "ProfileFocus", "ProfileGame", "ProfileSchool", "ProfileCreative", "ProfileTools" })
        {
            var name = Services.UiText.Get(key);
            var item = Item(name, null, options.EffectiveProfiles[index].Name == name);
            item.Click += (_, _) => SaveWidgetOptions(_preferences.Current.WidgetOptions.RenameProfile(index, name));
            rename.Items.Add(item);
        }
        menu.Items.Add(rename);
        var remove = Item(Services.UiText.Get("ProfileRemove"), "IconClose");
        remove.IsEnabled = options.EffectiveProfiles.Count > 1;
        remove.Click += (_, _) => SaveWidgetOptions(_preferences.Current.WidgetOptions.RemoveProfile(index));
        menu.Items.Add(remove);
        menu.Items.Add(new Separator());
        var edit = Item(Services.UiText.Get(_widgetBar.IsEditing ? "MenuEditDone" : "MenuEditDock"), "IconLayout");
        edit.Click += (_, _) => _widgetBar.SetEditing(!_widgetBar.IsEditing);
        menu.Items.Add(edit);
        menu.IsOpen = true;
    }

    private void UpdateTopBarContent()
    {
        if (_topBar is null) return;
        var onTop = !WidgetsInDock;
        _topBar.ForceVisible = onTop && _widgetBar.IsEditing;
        if (onTop && _widgetBar.IsEditing) _topBar.ShowTabs(true);
        else if (!_topBar.IsMouseOver) _topBar.ShowTabs(false);
        _topBar.SetHasContent(onTop && (_widgetBar.HasVisibleWidgets || _widgetBar.IsEditing));
        _topBar.Update();
    }

    private void ScheduleDesktopCheck()
    {
        if (_desktopCheck is null) return;
        _desktopCheck.Stop();
        _desktopCheck.Start();
    }

    /// <summary>Clean desktop = no visible, non-minimized app window on the primary monitor.</summary>
    private void RefreshDesktopClean()
    {
        UpdateDeskCovers();
        if (_topBar is null) return;
        var windows = _windowTracker?.Snapshot;
        if (windows is null)
        {
            _topBar.SetDesktopClean(false);
            return;
        }
        _topBar.SetDesktopClean(ComputeDesktopClean());
    }

    private bool ComputeDesktopClean()
    {
        if (_windowTracker?.Snapshot is not { } windows) return false;
        var primary = TopNative.MonitorFromPoint(new TopNative.Point(), 1 /* MONITOR_DEFAULTTOPRIMARY */);
        // Windows the desktop button moved off the screen count as put away.
        return !windows.Any(window => !IsForcedAside(window.Handle) && IsOpenOn(window.Handle, primary));
    }

    private static bool IsOpenOn(nint window, nint monitor) =>
        TopNative.IsWindow(window) && TopNative.IsWindowVisible(window) && !TopNative.IsIconic(window)
        && !IsCloakedWindow(window)
        && TopNative.MonitorFromWindow(window, 2 /* MONITOR_DEFAULTTONEAREST */) == monitor;

    private static bool IsCloakedWindow(nint window) =>
        TopNative.DwmGetWindowAttribute(window, 14 /* DWMWA_CLOAKED */, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private void DisposeTopBar()
    {
        if (_minimizeHook != 0)
        {
            TopNative.UnhookWinEvent(_minimizeHook);
            _minimizeHook = 0;
        }
        _desktopCheck?.Stop();
        _trashTimer?.Stop();
        _volumeWatcher?.Dispose();
        _volumeWatcher = null;
        DisposeHud();
        _volumeHud?.Close();
        _volumeHud = null;
        if (_topBar is not null)
        {
            _topBar.Bar = null;
            _topBar.Close();
            _topBar = null;
        }
    }

    private static class TopNative
    {
        public delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time);

        [StructLayout(LayoutKind.Sequential)]
        public struct Point { public int X, Y; }

        [DllImport("user32.dll")] public static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc callback, uint processId, uint threadId, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool UnhookWinEvent(nint hook);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsIconic(nint window);
        [DllImport("user32.dll")] public static extern nint MonitorFromWindow(nint window, uint flags);
        [DllImport("user32.dll")] public static extern nint MonitorFromPoint(Point point, uint flags);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    }
}

