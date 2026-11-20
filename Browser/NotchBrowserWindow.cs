using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using QiDock.Services;

namespace QiDock.Browser;

/// <summary>
/// A small browser that drops out of the top edge of the screen, as from a MacBook's notch: tabs, an address bar and
/// the page, on black with rounded lower corners. It grows from the notch and folds back into it. Pages run in
/// WebView2 (the Chromium engine Windows already has); the engine is let go a few minutes after the browser is put
/// away, so it costs nothing while unused. Its cookies and sign-ins live in its own profile under QiDock's folder.
/// </summary>
internal sealed class NotchBrowserWindow : Window
{
    private const string Home = "https://www.google.com/";
    private const double NotchWidth = 220, Corner = 22, Fillet = 14, TabWidth = 210;

    private static readonly Brush Ink = Solid(0xFF, 0x0A, 0x0A, 0x0B);
    private static readonly Brush TabSelected = Solid(0xFF, 0x22, 0x22, 0x25);
    private static readonly Brush TabHover = Solid(0xFF, 0x17, 0x17, 0x19);
    private static readonly Brush Field = Solid(0xFF, 0x13, 0x13, 0x15);
    private static readonly Brush FieldBorder = Solid(0xFF, 0x2A, 0x2A, 0x2E);
    private static readonly Brush Bright = Solid(0xF2, 0xFF, 0xFF, 0xFF);
    private static readonly Brush Dim = Solid(0x99, 0xFF, 0xFF, 0xFF);
    private static readonly Brush Hover = Solid(0x24, 0xFF, 0xFF, 0xFF);
    private static readonly Brush Gold = Solid(0xFF, 0xF5, 0xC5, 0x42);

    private sealed class Tab
    {
        public string Url = Home;
        public string Title = "";
        public WebView2? View;
        public Border Chip = null!;
        public TextBlock Caption = null!;
    }

    private readonly List<Tab> _tabs = [];
    private readonly System.Windows.Controls.Primitives.UniformGrid _strip = new() { Rows = 1, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Grid _stripHost = new();
    private readonly TextBox _address;
    private readonly Grid _pages = new();
    private readonly Grid _chrome;
    private readonly Border _back, _forward, _star, _capture;
    private readonly Path _starPath, _capturePath;
    private readonly DispatcherTimer _pointerWatch = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(100) };
    private static readonly TimeSpan LeaveDelay = TimeSpan.FromMilliseconds(700);
    private DateTime _outsideSince;
    private ContextMenu? _menu;
    private Tab? _active;
    private NotchBrowserState _state;
    private Task<CoreWebView2Environment>? _environment;
    private nint _handle;
    private double _progress;
    private bool _expanded, _fullScreen;
    private DateTime _animationStart;
    private double _animationFrom, _animationTo, _animationLength;
    private IEasingFunction _animationEase = Motion.Out;
    private Action? _animationDone;
    private readonly Grid _layout = new();
    private readonly DispatcherTimer _showPages = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(150) };
    private Rect _restore;

    public NotchBrowserWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Ink;
        Title = "QiDock Browser";
        UseLayoutRounding = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SetResourceReference(FontFamilyProperty, "InterfaceFont");
        _state = NotchBrowserState.Load();

        // Row 1: the tabs, a new-tab button and the fold-away arrow.
        var newTab = Button(Icon("M12 5v14 M5 12h14", 15), () => AddTab(Home, true, focusAddress: true), Desk("Yeni sekme (Ctrl+T)", "New tab (Ctrl+T)"));
        var fold = Button(Icon("M6 15l6-6 6 6", 15), Collapse, Desk("Kapat (Esc)", "Put away (Esc)"));
        _stripHost.Children.Add(_strip);
        _stripHost.SizeChanged += (_, _) => FitStrip();
        var tabsRow = new Grid { Margin = new Thickness(14, 10, 10, 0), Height = 32 };
        tabsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tabsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tabsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tabsRow.Children.Add(_stripHost);
        Grid.SetColumn(newTab, 1);
        tabsRow.Children.Add(newTab);
        Grid.SetColumn(fold, 2);
        fold.Margin = new Thickness(4, 0, 0, 0);
        tabsRow.Children.Add(fold);

        // Row 2: back, forward, reload, the address, the bookmark star and "hide from screen sharing".
        _back = Button(Icon("M15 18l-6-6 6-6", 16), () => Core()?.GoBack(), Desk("Geri (Alt+←)", "Back (Alt+←)"));
        _forward = Button(Icon("M9 18l6-6-6-6", 16), () => Core()?.GoForward(), Desk("İleri (Alt+→)", "Forward (Alt+→)"));
        var reload = Button(Icon("M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8 M21 3v5h-5 M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16 M8 16H3v5", 14), () => Core()?.Reload(), Desk("Yenile (F5)", "Reload (F5)"));
        _address = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Bright,
            CaretBrush = Bright,
            SelectionBrush = Solid(0xFF, 0x3A, 0x6E, 0xD8),
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(10, 0, 10, 0)
        };
        _address.KeyDown += Address_KeyDown;
        _address.GotKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(_address.SelectAll, DispatcherPriority.Input);
        _address.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_address.IsKeyboardFocusWithin) return;
            e.Handled = true;
            _address.Focus();
        };
        var field = new Border { Background = Field, BorderBrush = FieldBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(15), Height = 32, Child = _address, Margin = new Thickness(8, 0, 6, 0) };
        _starPath = Icon("M12 3.5l2.6 5.3 5.9.9-4.3 4.1 1 5.8L12 16.9l-5.2 2.7 1-5.8-4.3-4.1 5.9-.9z", 16);
        _star = Button(_starPath, ShowBookmarks, Desk("Yer imleri", "Bookmarks"));
        _capturePath = Icon("M3 3l18 18 M10.6 10.6a2 2 0 0 0 2.8 2.8 M9.9 5.1A9.8 9.8 0 0 1 12 5c5 0 9 4.5 10 7-.4 1-1.2 2.3-2.4 3.5 M6.6 6.6C4.4 8 2.8 10.2 2 12c1 2.5 5 7 10 7 1.7 0 3.3-.5 4.6-1.3", 16);
        _capture = Button(_capturePath, ToggleCapture, Desk("Ekran kaydında ve paylaşımında gizle", "Hide from screen recording and sharing"));
        var toolRow = new Grid { Margin = new Thickness(12, 8, 12, 10), Height = 34 };
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            toolRow.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        // The address takes about two thirds, as in the reference; the rest stays empty before the right-hand button.
        toolRow.ColumnDefinitions[3].Width = new GridLength(3, GridUnitType.Star);
        Place(toolRow, _back, 0);
        Place(toolRow, _forward, 1);
        Place(toolRow, reload, 2);
        Place(toolRow, field, 3);
        Place(toolRow, _star, 4);
        Place(toolRow, _capture, 6);

        _chrome = new Grid();
        _chrome.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _chrome.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _chrome.Children.Add(tabsRow);
        Grid.SetRow(toolRow, 1);
        _chrome.Children.Add(toolRow);

        var layout = _layout;
        // Room left and right for the curves that join the panel to the screen edge.
        layout.Margin = new Thickness(Fillet, 0, Fillet, 0);
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(_chrome);
        _pages.Margin = new Thickness(10, 0, 10, 10);
        Grid.SetRow(_pages, 1);
        layout.Children.Add(_pages);
        Content = layout;
        _showPages.Tick += (_, _) =>
        {
            _showPages.Stop();
            if (_expanded) _pages.Visibility = Visibility.Visible;
        };

        PreviewKeyDown += Window_PreviewKeyDown;
        Deactivated += (_, _) => Dispatcher.BeginInvoke(CollapseIfLeft, DispatcherPriority.Background);
        SizeChanged += (_, _) => ApplyRegion();
        _pointerWatch.Tick += (_, _) => WatchPointer();
        Application.Current.Exit += (_, _) => SaveState();
    }

    public bool IsExpanded => _expanded;

    // ---- Opening and closing ------------------------------------------------------------------

    public void Expand()
    {
        if (_expanded) return;
        _expanded = true;
        Place();
        if (_handle == 0)
        {
            _handle = new WindowInteropHelper(this).EnsureHandle();
            // A tool window: no Alt+Tab entry and no dock button of its own.
            var style = GetWindowLongPtr(_handle, -20).ToInt64();
            SetWindowLongPtr(_handle, -20, new nint(style | 0x80));
            ApplyCapture();
        }
        if (_tabs.Count == 0) RestoreTabs();
        _progress = 0;
        ApplyRegion();
        if (!IsVisible) Show();
        Activate();
        _outsideSince = default;
        _pointerWatch.Start();
        if (_active is { } tab) _ = ShowTab(tab);
        Wake();
        // As the cards on the dock open: a narrow bump grows out of the edge to the full panel, settling with a
        // little give; the bars follow once it is nearly full, coming down a few pixels; the page shows last.
        _pages.Visibility = Visibility.Hidden;
        _showPages.Start();
        _chrome.BeginAnimation(OpacityProperty, Motion.FromTo(0, 1, 220, delay: 170));
        _chrome.Opacity = 1;
        Motion.Shift(_chrome).BeginAnimation(TranslateTransform.YProperty, Motion.FromTo(-12, 0, 380, delay: 170));
        Animate(1, 430, Motion.Settle, 0, () =>
        {
            if (_active?.View?.CoreWebView2 is null || string.IsNullOrEmpty(_active.Url) || _active.Url == Home) _address.Focus();
            else _active.View.Focus();
        });
    }

    public void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;
        _pointerWatch.Stop();
        if (_menu is not null) _menu.IsOpen = false;
        if (_fullScreen) LeaveFullScreen();
        SaveState();
        // Content first, then the shape sinks back into the edge.
        _showPages.Stop();
        _pages.Visibility = Visibility.Hidden;
        _chrome.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(110)) { EasingFunction = Motion.In });
        Animate(0, 250, Motion.In, 60, () =>
        {
            if (_expanded) return;
            Hide();
            _pages.Visibility = Visibility.Visible;
            Rest();
        });
    }

    /// <summary>
    /// Like the notch it comes from, the browser goes away when the pointer leaves it for a moment or clicks elsewhere;
    /// it stays while the address is being typed, its bookmark menu is open or a page shows a video full screen.
    /// </summary>
    private void WatchPointer()
    {
        if (!_expanded || _fullScreen || _menu?.IsOpen == true || !GetCursorPos(out var cursor))
        {
            _outsideSince = default;
            return;
        }
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var margin = 24 * scale;
        var inside = cursor.X >= Left * scale - margin && cursor.X <= (Left + Width) * scale + margin
            && cursor.Y <= (Top + Height) * scale + margin;
        if (inside)
        {
            _outsideSince = default;
            return;
        }
        var clicked = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
        if (clicked && !OwnDialogInFront())
        {
            Collapse();
            return;
        }
        if (_address.IsKeyboardFocusWithin && IsActive) return;
        var now = DateTime.UtcNow;
        if (_outsideSince == default) _outsideSince = now;
        else if (now - _outsideSince >= LeaveDelay && !OwnDialogInFront()) Collapse();
    }

    /// <summary>A page's own window (an alert, a sign-in pop-up) is in front; it belongs to the browser.</summary>
    private static bool OwnDialogInFront()
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0) return false;
        GetWindowThreadProcessId(foreground, out var processId);
        if (processId == Environment.ProcessId) return false;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Clicking elsewhere puts the browser away; its own dialogs (alerts, sign-in pop-ups) do not.</summary>
    private void CollapseIfLeft()
    {
        if (!_expanded || IsActive) return;
        var foreground = GetForegroundWindow();
        if (foreground == 0) return;
        GetWindowThreadProcessId(foreground, out var processId);
        if (processId == Environment.ProcessId) return;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (process.ProcessName.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase)) return;
        }
        catch (ArgumentException)
        {
        }
        Collapse();
    }

    private void Place()
    {
        if (_fullScreen) return;
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var screenHeight = SystemParameters.PrimaryScreenHeight;
        Width = Math.Round(Math.Clamp(screenWidth * 0.66, Math.Min(760, screenWidth - 40), 1320)) + 2 * Fillet;
        Height = Math.Round(Math.Max(420, Math.Min(screenHeight * 0.76, screenHeight - 150)));
        Left = Math.Round((screenWidth - Width) / 2);
        Top = 0;
    }

    private void Animate(double to, double milliseconds, IEasingFunction ease, double delay, Action? done)
    {
        CompositionTarget.Rendering -= Animation_Frame;
        if (!NotchBrowser.Animations)
        {
            _progress = to;
            ApplyRegion();
            _showPages.Stop();
            if (_expanded) _pages.Visibility = Visibility.Visible;
            done?.Invoke();
            return;
        }
        _animationFrom = _progress;
        _animationTo = to;
        _animationLength = milliseconds;
        _animationEase = ease;
        _animationStart = DateTime.UtcNow.AddMilliseconds(delay);
        _animationDone = done;
        CompositionTarget.Rendering += Animation_Frame;
    }

    private void Animation_Frame(object? sender, EventArgs e)
    {
        var t = Math.Clamp((DateTime.UtcNow - _animationStart).TotalMilliseconds / _animationLength, 0, 1);
        var eased = _animationEase.Ease(t);
        _progress = _animationFrom + (_animationTo - _animationFrom) * eased;
        ApplyRegion();
        if (t < 1) return;
        CompositionTarget.Rendering -= Animation_Frame;
        var done = _animationDone;
        _animationDone = null;
        done?.Invoke();
    }

    /// <summary>
    /// The visible shape for the progress (0 closed, 1 open; a little over 1 while it settles): as the cards on the
    /// dock, a bump half as wide as the notch growing to the whole panel, hanging from the screen edge, rounded
    /// below, with inward curves where it meets the edge. Cutting the window this way also cuts the page inside it.
    /// </summary>
    private void ApplyRegion()
    {
        if (_handle == 0) return;
        if (_fullScreen)
        {
            SetWindowRgn(_handle, 0, true);
            return;
        }
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var fullWidth = Width - 2 * Fillet;
        var fullHeight = Height;
        var p = Math.Max(0, _progress);
        var startWidth = NotchWidth * 0.62;
        var w = Math.Clamp(startWidth + (fullWidth - startWidth) * p, 0, fullWidth + 2 * Fillet - 4);
        var h = Math.Clamp(fullHeight * p, 0, fullHeight);
        if (h < 0.5)
        {
            SetWindowRgn(_handle, CreateRectRgn(0, 0, 0, 0), true);
            return;
        }
        var fillet = Math.Max(0, Math.Min(Fillet, h * 0.45));
        var corner = Math.Max(0, Math.Min(Math.Min(Corner, w / 2), h - fillet));
        var left = (Width - w) / 2;
        var right = left + w;

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(left - fillet, 0), true, true);
            if (fillet > 0) g.ArcTo(new Point(left, fillet), new Size(fillet, fillet), 0, false, SweepDirection.Clockwise, true, false);
            g.LineTo(new Point(left, h - corner), true, false);
            if (corner > 0) g.ArcTo(new Point(left + corner, h), new Size(corner, corner), 0, false, SweepDirection.Counterclockwise, true, false);
            g.LineTo(new Point(right - corner, h), true, false);
            if (corner > 0) g.ArcTo(new Point(right, h - corner), new Size(corner, corner), 0, false, SweepDirection.Counterclockwise, true, false);
            g.LineTo(new Point(right, fillet), true, false);
            if (fillet > 0) g.ArcTo(new Point(right + fillet, 0), new Size(fillet, fillet), 0, false, SweepDirection.Clockwise, true, false);
        }
        var points = new List<NativePoint>();
        foreach (var figure in geometry.GetFlattenedPathGeometry(0.2, ToleranceType.Absolute).Figures)
        {
            points.Add(Pixel(figure.StartPoint, scale));
            foreach (var segment in figure.Segments)
            {
                if (segment is PolyLineSegment poly) points.AddRange(poly.Points.Select(point => Pixel(point, scale)));
                else if (segment is LineSegment line) points.Add(Pixel(line.Point, scale));
            }
        }
        if (points.Count < 3) return;
        SetWindowRgn(_handle, CreatePolygonRgn(points.ToArray(), points.Count, 2), true);
    }

    private static NativePoint Pixel(Point point, double scale) => new() { X = (int)Math.Round(point.X * scale), Y = (int)Math.Round(point.Y * scale) };

    // ---- Tabs -----------------------------------------------------------------------------------

    private void RestoreTabs()
    {
        var urls = _state.Tabs is { Count: > 0 } saved ? saved : [Home];
        foreach (var url in urls) AddTab(url, false);
        Select(_tabs[Math.Clamp(_state.Active, 0, _tabs.Count - 1)]);
    }

    private Tab AddTab(string url, bool select, bool focusAddress = false)
    {
        var tab = new Tab { Url = url, Title = TitleOf(url) };
        tab.Caption = new TextBlock { Text = tab.Title, FontSize = 12.5, Foreground = Dim, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var close = Button(Icon("M6 6l12 12 M18 6L6 18", 10), () => CloseTab(tab), Desk("Sekmeyi kapat (Ctrl+W)", "Close tab (Ctrl+W)"), 22, 22);
        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.Children.Add(tab.Caption);
        Grid.SetColumn(close, 1);
        inner.Children.Add(close);
        tab.Chip = new Border { Child = inner, CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 0, 4, 0), Margin = new Thickness(0, 0, 6, 0), Background = Brushes.Transparent, Cursor = Cursors.Arrow };
        tab.Chip.MouseEnter += (_, _) => { if (tab != _active) tab.Chip.Background = TabHover; };
        tab.Chip.MouseLeave += (_, _) => { if (tab != _active) tab.Chip.Background = Brushes.Transparent; };
        tab.Chip.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && close.IsAncestorOf(source)) return;
            Select(tab);
        };
        tab.Chip.MouseUp += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) CloseTab(tab);
        };
        _tabs.Add(tab);
        _strip.Children.Add(tab.Chip);
        FitStrip();
        if (select) Select(tab);
        if (focusAddress) Dispatcher.BeginInvoke(() => _address.Focus(), DispatcherPriority.Input);
        return tab;
    }

    private void FitStrip()
    {
        var room = Math.Max(0, _stripHost.ActualWidth);
        _strip.Width = Math.Min(room, _tabs.Count * (TabWidth + 6));
    }

    private void Select(Tab tab)
    {
        _active = tab;
        foreach (var other in _tabs)
        {
            var on = other == tab;
            other.Chip.Background = on ? TabSelected : Brushes.Transparent;
            other.Caption.Foreground = on ? Bright : Dim;
            if (other.View is not null) other.View.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateAddress();
        UpdateButtons();
        if (_expanded) _ = ShowTab(tab);
    }

    private void CloseTab(Tab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;
        _tabs.RemoveAt(index);
        _strip.Children.Remove(tab.Chip);
        if (tab.View is not null)
        {
            _pages.Children.Remove(tab.View);
            tab.View.Dispose();
        }
        if (_tabs.Count == 0)
        {
            AddTab(Home, true);
            Collapse();
            return;
        }
        FitStrip();
        if (tab == _active) Select(_tabs[Math.Min(index, _tabs.Count - 1)]);
    }

    /// <summary>Creates the tab's page on first view (restored tabs load only when opened).</summary>
    private async Task ShowTab(Tab tab)
    {
        if (tab.View is not null) return;
        var view = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 10, 10, 11), Visibility = tab == _active ? Visibility.Visible : Visibility.Collapsed };
        tab.View = view;
        _pages.Children.Add(view);
        try
        {
            _environment ??= CoreWebView2Environment.CreateAsync(null, NotchBrowserState.ProfileFolder);
            await view.EnsureCoreWebView2Async(await _environment);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException or ObjectDisposedException)
        {
            AppLog.Write("Tarayıcı açılamadı: " + ex.Message);
            if (tab.View == view)
            {
                _pages.Children.Remove(view);
                tab.View = null;
                view.Dispose();
            }
            ShowEngineMissing();
            return;
        }
        if (tab.View != view) return; // closed or released while starting
        var core = view.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
        core.DocumentTitleChanged += (_, _) =>
        {
            tab.Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? TitleOf(tab.Url) : core.DocumentTitle;
            tab.Caption.Text = tab.Title;
            tab.Chip.ToolTip = tab.Title;
        };
        core.SourceChanged += (_, _) =>
        {
            tab.Url = core.Source;
            if (tab == _active) UpdateAddress();
            SaveState();
        };
        core.HistoryChanged += (_, _) =>
        {
            if (tab == _active) UpdateButtons();
        };
        // Links that would open a window open a tab here instead.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            AddTab(e.Uri, true);
        };
        core.ContainsFullScreenElementChanged += (_, _) =>
        {
            if (tab != _active) return;
            if (core.ContainsFullScreenElement) EnterFullScreen();
            else LeaveFullScreen();
        };
        core.Navigate(tab.Url);
    }

    private void ShowEngineMissing()
    {
        if (_pages.Children.OfType<TextBlock>().Any()) return;
        _pages.Children.Add(new TextBlock
        {
            Text = Desk("Sayfaları göstermek için Microsoft Edge WebView2 gerekli; Windows Update ile gelir.", "Pages need Microsoft Edge WebView2; it comes with Windows Update."),
            Foreground = Dim,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
    }

    /// <summary>
    /// Put away: the pages stay as they are (scroll, sign-ins, a half-written form) but are paused and asked to give
    /// back what memory they can until the browser opens again. Sound that is playing keeps playing.
    /// </summary>
    private void Rest()
    {
        foreach (var core in _tabs.Select(t => t.View?.CoreWebView2).OfType<CoreWebView2>())
        {
            try
            {
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                _ = core.TrySuspendAsync();
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
                // Still visible, or playing sound: it simply stays awake.
            }
        }
    }

    private void Wake()
    {
        foreach (var core in _tabs.Select(t => t.View?.CoreWebView2).OfType<CoreWebView2>())
        {
            try
            {
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
                if (core.IsSuspended) core.Resume();
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
            }
        }
    }

    public void SaveState()
    {
        if (_tabs.Count == 0) return;
        _state = _state with
        {
            Tabs = _tabs.Select(t => t.Url).Where(u => !string.IsNullOrWhiteSpace(u)).ToList(),
            Active = _active is null ? 0 : Math.Max(0, _tabs.IndexOf(_active))
        };
        _state.Save();
    }

    // ---- Address, buttons, bookmarks ------------------------------------------------------------

    private CoreWebView2? Core() => _active?.View?.CoreWebView2;

    private void UpdateAddress()
    {
        if (_active is null || _address.IsKeyboardFocusWithin) return;
        _address.Text = _active.Url == Home && _active.View?.CoreWebView2 is null ? "" : _active.Url;
        var marked = _state.Bookmarks?.Any(b => SameUrl(b.Url, _active.Url)) == true;
        _starPath.Fill = marked ? Gold : null;
        _starPath.Stroke = marked ? Gold : Bright;
    }

    private void UpdateButtons()
    {
        _back.Opacity = Core()?.CanGoBack == true ? 1 : 0.35;
        _forward.Opacity = Core()?.CanGoForward == true ? 1 : 0.35;
    }

    private void Address_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Go(_address.Text);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _address.Text = _active?.Url ?? "";
            _active?.View?.Focus();
        }
    }

    private void Go(string text)
    {
        var url = UrlOf(text);
        if (url is null || _active is null) return;
        _active.Url = url;
        if (_active.View?.CoreWebView2 is { } core) core.Navigate(url);
        else _ = ShowTab(_active);
        _active.View?.Focus();
    }

    /// <summary>An address as typed ("github.com", "https://…") or, for anything else, a Google search.</summary>
    private static string? UrlOf(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https" or "file" or "edge" or "about") return absolute.ToString();
        var looksLikeHost = !text.Contains(' ') && (text.Contains('.') || text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase));
        if (looksLikeHost && Uri.TryCreate("https://" + text, UriKind.Absolute, out var host)) return host.ToString();
        return "https://www.google.com/search?q=" + Uri.EscapeDataString(text);
    }

    private static string TitleOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host) ? uri.Host.Replace("www.", "") : Desk("Yeni sekme", "New tab");

    private static bool SameUrl(string a, string b) => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private void ShowBookmarks()
    {
        var menu = new ContextMenu { PlacementTarget = _star, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        _menu = menu;
        var bookmarks = (_state.Bookmarks ?? []).ToList();
        if (_active is { } tab && !string.IsNullOrWhiteSpace(tab.Url))
        {
            var marked = bookmarks.FindIndex(b => SameUrl(b.Url, tab.Url));
            var toggle = new MenuItem { Header = marked >= 0 ? Desk("Yer imlerinden çıkar", "Remove bookmark") : Desk("Yer imlerine ekle", "Add bookmark") };
            toggle.Click += (_, _) =>
            {
                if (marked >= 0) bookmarks.RemoveAt(marked);
                else bookmarks.Insert(0, new BrowserBookmark(tab.Title, tab.Url));
                _state = _state with { Bookmarks = bookmarks };
                _state.Save();
                UpdateAddress();
            };
            menu.Items.Add(toggle);
        }
        if (bookmarks.Count > 0) menu.Items.Add(new Separator());
        foreach (var bookmark in bookmarks.Take(30))
        {
            var item = new MenuItem { Header = bookmark.Title.Length > 60 ? bookmark.Title[..60] + "…" : bookmark.Title, ToolTip = bookmark.Url };
            item.Click += (_, _) => Go(bookmark.Url);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void ToggleCapture()
    {
        _state = _state with { HideFromCapture = !_state.HideFromCapture };
        _state.Save();
        ApplyCapture();
    }

    /// <summary>Hidden from screen recording and sharing (others see nothing where the browser is).</summary>
    private void ApplyCapture()
    {
        if (_handle != 0) SetWindowDisplayAffinity(_handle, _state.HideFromCapture ? 0x11u : 0u);
        _capturePath.Stroke = _state.HideFromCapture ? Gold : Dim;
        _capture.ToolTip = _state.HideFromCapture
            ? Desk("Ekran kaydında ve paylaşımında gizli (göstermek için tıklayın)", "Hidden from screen recording and sharing (click to show)")
            : Desk("Ekran kaydında ve paylaşımında gizle", "Hide from screen recording and sharing");
    }

    // ---- Full screen video ----------------------------------------------------------------------

    private void EnterFullScreen()
    {
        if (_fullScreen) return;
        _fullScreen = true;
        _restore = new Rect(Left, Top, Width, Height);
        _chrome.Visibility = Visibility.Collapsed;
        _layout.Margin = new Thickness(0);
        _pages.Margin = new Thickness(0);
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        ApplyRegion();
    }

    private void LeaveFullScreen()
    {
        if (!_fullScreen) return;
        _fullScreen = false;
        _chrome.Visibility = Visibility.Visible;
        _layout.Margin = new Thickness(Fillet, 0, Fillet, 0);
        _pages.Margin = new Thickness(10, 0, 10, 10);
        Left = _restore.Left;
        Top = _restore.Top;
        Width = _restore.Width;
        Height = _restore.Height;
        ApplyRegion();
    }

    // ---- Keys -----------------------------------------------------------------------------------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var handled = true;
        if (control && key == Key.T) AddTab(Home, true, focusAddress: true);
        else if (control && key == Key.W && _active is { } tab) CloseTab(tab);
        else if ((control && key == Key.L) || key == Key.F6) _address.Focus();
        else if (control && key == Key.Tab && _tabs.Count > 1 && _active is { } current)
            Select(_tabs[(_tabs.IndexOf(current) + (shift ? _tabs.Count - 1 : 1)) % _tabs.Count]);
        else if (key == Key.F5 || (control && key == Key.R)) Core()?.Reload();
        else if (alt && key == Key.Left) Core()?.GoBack();
        else if (alt && key == Key.Right) Core()?.GoForward();
        else if (key == Key.Escape && !_address.IsKeyboardFocusWithin)
        {
            if (_fullScreen) LeaveFullScreen();
            else Collapse();
        }
        else handled = false;
        e.Handled = handled;
    }

    // ---- Small helpers ---------------------------------------------------------------------------

    private static string Desk(string turkish, string english) => QiDock.DesktopWidgets.Desk.L(turkish, english);

    private static SolidColorBrush Solid(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>A 24-unit outline icon kept at its own proportions.</summary>
    private static Path Icon(string data, double size)
    {
        var path = new Path
        {
            Data = Geometry.Parse(data),
            Stroke = Bright,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);
        _ = new Viewbox { Width = size, Height = size, Child = canvas };
        return path;
    }

    private static Border Button(UIElement content, Action click, string tooltip, double width = 32, double height = 30)
    {
        if (content is Path { Parent: Canvas { Parent: Viewbox box } }) content = box;
        var button = new Border { Width = width, Height = height, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Child = content, ToolTip = tooltip, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        button.MouseEnter += (_, _) => button.Background = Hover;
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseLeftButtonDown += (_, e) => e.Handled = true;
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return button;
    }

    private static void Place(Grid grid, UIElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    [DllImport("gdi32.dll")]
    private static extern nint CreatePolygonRgn(NativePoint[] points, int count, int fillMode);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}
