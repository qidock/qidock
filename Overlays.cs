using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QiDock.Models;
using QiDock.Services;
using QiDock.Services.Notifications;
using QiDock.Widgets;

namespace QiDock;

/// <summary>
/// A full-screen layer over everything (AppGrid, QuickSearch, Window Overview): fades in, takes
/// the keyboard, and goes away on Esc, a click on the empty background or switching to another app.
/// </summary>
public abstract class OverlayWindow : Window
{
    private static OverlayWindow? _open;
    private bool _dismissing;
    protected readonly Grid Root = new();

    protected OverlayWindow(Brush background)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        GpuTransparency.Prepare(this);
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        SetResourceReference(FontFamilyProperty, "InterfaceFont");
        Root.Background = background;
        Root.Opacity = 0;
        Content = Root;
        Root.MouseLeftButtonUp += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, Root) || IsBackground(e.OriginalSource)) Dismiss();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (!OnEscape()) Dismiss();
        };
        Deactivated += (_, _) => Dismiss();
    }

    /// <summary>Raised after an overlay has closed (its full-screen surface can be given back).</summary>
    public static event Action? AnyClosed;

    /// <summary>True when an overlay of this kind is showing (opening it again closes it).</summary>
    protected static bool CloseIfOpen<T>() where T : OverlayWindow
    {
        if (_open is T open)
        {
            open.Dismiss();
            return true;
        }
        _open?.Dismiss();
        return false;
    }

    /// <summary>Whether the desktop behind is blurred (AppGrid), not just dimmed.</summary>
    protected virtual bool Frosted => false;

    /// <summary>The colour laid over the blurred desktop (alpha = how strongly).</summary>
    protected virtual Color FrostTint => Color.FromArgb(0x73, 0x10, 0x11, 0x16);

    /// <summary>Whether the blur really is on (false where Windows can't; then draw a dark background).</summary>
    protected bool IsFrosted { get; private set; }

    /// <summary>Esc closes an inner layer first (an open folder); true when it did, so the overlay stays.</summary>
    protected virtual bool OnEscape() => false;

    /// <summary>Clicks on these elements count as clicks on the empty background.</summary>
    protected virtual bool IsBackground(object source) => false;

    protected void Present()
    {
        _open = this;
        Show();
        Activate();
        OnShown();
        if (PresentationSource.FromVisual(this) is HwndSource source) SetForegroundWindow(source.Handle);
        // The first frame (building the whole grid) is drawn while still invisible; the fade starts after it, so it never skips.
        Motion.AfterFirstFrame(() =>
        {
            if (_dismissing) return;
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = Motion.Out });
            OnPresented();
            Motion.Measure(GetType().Name + " open");
        });
    }

    /// <summary>Right after the window appears, before anything moves (take the keyboard here).</summary>
    protected virtual void OnShown() { }

    /// <summary>Once the first frame is on screen: start the opening movement here.</summary>
    protected virtual void OnPresented() { }

    protected virtual void OnDismissing() { }

    public void Dismiss()
    {
        if (_dismissing) return;
        _dismissing = true;
        if (ReferenceEquals(_open, this)) _open = null;
        OnDismissing();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)) { EasingFunction = Motion.In };
        fade.Completed += (_, _) =>
        {
            Close();
            Dispatcher.BeginInvoke(() => AnyClosed?.Invoke(), DispatcherPriority.ApplicationIdle);
        };
        Root.BeginAnimation(OpacityProperty, fade);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is not HwndSource source) return;
        GpuTransparency.Apply(this);
        // The blurred desktop stays until the window closes, under the fading content.
        if (Frosted) IsFrosted = GpuTransparency.Frost(this, FrostTint, rounded: false);
        // Keep the overlay out of Alt+Tab.
        var style = GetWindowLongPtr(source.Handle, -20).ToInt64();
        SetWindowLongPtr(source.Handle, -20, new nint((style | 0x80) & ~0x40000));
    }

    /// <summary>A rounded search field with a placeholder.</summary>
    protected static (Border Field, TextBox Box) SearchField(string placeholder, double fontSize, Brush foreground, Brush hint, Brush fill)
    {
        var box = new TextBox
        {
            FontSize = fontSize,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = foreground,
            CaretBrush = foreground,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0)
        };
        var hintText = new TextBlock { Text = placeholder, FontSize = fontSize, Foreground = hint, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Margin = new Thickness(2, 0, 0, 0) };
        box.TextChanged += (_, _) => hintText.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var icon = Ui.Icon("IconSearch", fontSize * 0.8, hint, null);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(0, 0, fontSize * 0.5, 0);
        var text = new Grid();
        text.Children.Add(hintText);
        text.Children.Add(box);
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);
        var field = new Border { Child = row, Background = fill, CornerRadius = new CornerRadius(fontSize * 0.6), Padding = new Thickness(fontSize * 0.6, fontSize * 0.35, fontSize * 0.6, fontSize * 0.35) };
        return (field, box);
    }

    protected static bool Matches(string name, string query) =>
        CultureInfo.CurrentCulture.CompareInfo.IndexOf(name, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}

/// <summary>Remembers the app list between openings so the overlays open instantly; refreshed in the background.</summary>
internal static class AppList
{
    private static List<InstalledApp>? _apps;
    private static Task<List<InstalledApp>>? _loading;

    public static List<InstalledApp>? Cached => _apps;

    public static Task<List<InstalledApp>> RefreshAsync()
    {
        if (_loading is { IsCompleted: false }) return _loading;
        _loading = AppCatalog.LoadAsync();
        _loading.ContinueWith(t => _apps = t.Result, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
        return _loading;
    }
}

/// <summary>A search box in the middle of the screen for apps, files and quick sums.</summary>
public sealed class QuickSearchWindow : OverlayWindow
{
    private const int MaxResults = 8;
    private readonly TextBox _box;
    private readonly StackPanel _results = new();
    private readonly Border _card;
    private readonly DispatcherTimer _debounce;
    private List<SearchWidget.Entry>? _files;
    private CancellationTokenSource? _indexing;
    private readonly List<(FrameworkElement Row, Action Open)> _rows = [];
    private int _selected;

    public static void Toggle()
    {
        if (CloseIfOpen<QuickSearchWindow>()) return;
        new QuickSearchWindow().Present();
    }

    private QuickSearchWindow() : base(Ui.Solid("#01000000"))
    {
        var text = (Brush)Application.Current.Resources["TextPrimary"];
        var hint = (Brush)Application.Current.Resources["TextSecondary"];
        var (field, box) = SearchField(UiText.Get("QuickSearchHint"), 22, text, hint, Brushes.Transparent);
        _box = box;
        field.Padding = new Thickness(18, 14, 18, 14);
        var column = new StackPanel();
        column.Children.Add(field);
        _results.Margin = new Thickness(8, 0, 8, 8);
        column.Children.Add(_results);
        _card = new Border
        {
            Width = 680,
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            Child = column,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, Height * 0.22, 0, 0),
            Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 10, Direction = 270, Opacity = 0.4 }
        };
        _card.SetResourceReference(Border.BackgroundProperty, "WidgetPopupFill");
        _card.SetResourceReference(Border.BorderBrushProperty, "DockBorderMaterial");
        Root.Children.Add(_card);
        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(90) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Query();
        };
        box.TextChanged += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Start();
        };
        box.PreviewKeyDown += Box_KeyDown;
        AppList.RefreshAsync();
        var cancel = _indexing = new CancellationTokenSource();
        Task.Run(() => SearchWidget.Collect(cancel.Token), cancel.Token).ContinueWith(t =>
        {
            if (cancel.IsCancellationRequested) return;
            _files = t.Result;
            if (_box.Text.Length > 0) Query();
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
    }

    protected override void OnShown() => _box.Focus();

    protected override void OnDismissing()
    {
        _indexing?.Cancel();
        _files = null;
    }

    private void Box_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when _rows.Count > 0:
                Select((_selected + 1) % _rows.Count);
                e.Handled = true;
                break;
            case Key.Up when _rows.Count > 0:
                Select((_selected - 1 + _rows.Count) % _rows.Count);
                e.Handled = true;
                break;
            case Key.Enter:
                var pending = _debounce.IsEnabled;
                _debounce.Stop();
                if (pending || _rows.Count == 0) Query();
                if (_rows.Count > 0) _rows[_selected].Open();
                e.Handled = true;
                break;
        }
    }

    private void Query()
    {
        _results.Children.Clear();
        _rows.Clear();
        var query = _box.Text.Trim();
        if (query.Length == 0) return;
        if (Calculate(query) is { } sum)
        {
            AddRow(Ui.Icon("IconCalculator", 18), sum, UiText.Get("QuickSearchCopy"), () =>
            {
                try { Clipboard.SetText(sum); } catch (COMException) { }
                Dismiss();
            });
        }
        var apps = (AppList.Cached ?? [])
            .Select(a => (App: a, At: CultureInfo.CurrentCulture.CompareInfo.IndexOf(a.Name, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace)))
            .Where(m => m.At >= 0)
            .OrderBy(m => m.At == 0 ? 0 : 1).ThenBy(m => m.App.Name.Length)
            .Take(5).Select(m => m.App).ToList();
        foreach (var app in apps)
        {
            var icon = new Image { Source = NotificationApps.Icon(app.Id), Width = 28, Height = 28 };
            AddRow(icon, app.Name, UiText.Get("SearchApp"), () =>
            {
                Dismiss();
                app.Launch();
            });
        }
        if (_files is not null)
        {
            var names = new HashSet<string>(apps.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
            var files = _files
                .Select(f => (File: f, At: CultureInfo.CurrentCulture.CompareInfo.IndexOf(f.Name, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace)))
                .Where(m => m.At >= 0 && !(m.File.IsApp && names.Contains(m.File.Name)))
                .OrderBy(m => m.File.IsApp ? 0 : 1).ThenBy(m => m.At == 0 ? 0 : 1).ThenBy(m => m.File.Name.Length)
                .Take(Math.Max(0, MaxResults - _rows.Count - 1)).Select(m => m.File);
            foreach (var file in files)
            {
                AddRow(Ui.FileIcon(file.Path, 28), file.Name, file.IsApp ? UiText.Get("SearchApp") : System.IO.Path.GetDirectoryName(file.Path) ?? "", () =>
                {
                    Dismiss();
                    Ui.Open(file.Path);
                });
            }
        }
        else AddNote(UiText.Get("SearchIndexing"));
        AddRow(Ui.Icon("IconGlobe", 18), UiText.Format("QuickSearchWeb", query), "", () =>
        {
            Dismiss();
            Ui.Open("https://www.google.com/search?q=" + Uri.EscapeDataString(query));
        });
        Select(0);
    }

    private void AddNote(string text)
    {
        var note = Ui.Text(text, 11.5, brushKey: "TextSecondary");
        note.Margin = new Thickness(12, 4, 0, 4);
        _results.Children.Add(note);
    }

    private void AddRow(FrameworkElement icon, string title, string detail, Action open)
    {
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var iconBox = new Grid { Width = 28, Height = 28, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        iconBox.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = Ui.Text(title, 14, FontWeights.SemiBold);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(name);
        if (detail.Length > 0)
        {
            var sub = Ui.Text(detail, 11.5, brushKey: "TextSecondary");
            sub.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(sub);
        }
        var content = new DockPanel();
        DockPanel.SetDock(iconBox, Dock.Left);
        content.Children.Add(iconBox);
        content.Children.Add(text);
        var row = new Border { Child = content, Padding = new Thickness(12, 7, 12, 7), CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        var index = _rows.Count;
        row.MouseEnter += (_, _) => Select(index);
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            open();
        };
        _rows.Add((row, open));
        _results.Children.Add(row);
    }

    private void Select(int index)
    {
        _selected = Math.Clamp(index, 0, Math.Max(0, _rows.Count - 1));
        for (var i = 0; i < _rows.Count; i++)
        {
            if (i == _selected) ((Border)_rows[i].Row).SetResourceReference(Border.BackgroundProperty, "SurfaceSelected");
            else ((Border)_rows[i].Row).Background = Brushes.Transparent;
        }
    }

    /// <summary>"12*4+3" → "51" for simple sums typed into the box (digits, + - * / and parentheses).</summary>
    private static string? Calculate(string query)
    {
        var expression = query.Replace(',', '.').Replace('x', '*').Replace('×', '*').Replace('÷', '/');
        if (!expression.Any(char.IsDigit) || !expression.Any(c => "+-*/".Contains(c))) return null;
        if (expression.Any(c => !(char.IsDigit(c) || "+-*/(). ".Contains(c)))) return null;
        try
        {
            var value = Convert.ToDouble(new System.Data.DataTable().Compute(expression, null), CultureInfo.InvariantCulture);
            if (double.IsNaN(value) || double.IsInfinity(value)) return null;
            return Math.Round(value, 8).ToString("#,0.########", CultureInfo.CurrentCulture);
        }
        catch (Exception ex) when (ex is System.Data.EvaluateException or System.Data.SyntaxErrorException or InvalidCastException or OverflowException or DivideByZeroException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>All open windows side by side as live thumbnails; click one to switch to it.</summary>
public sealed class WindowOverviewWindow : OverlayWindow
{
    private const double Gap = 34;
    private const double TitleHeight = 30;
    private static readonly Brush Dim = Ui.Solid("#C80B0C10");
    private readonly IReadOnlyList<TrackedWindow> _windows;
    private readonly Canvas _canvas = new();
    private readonly List<nint> _thumbnails = [];

    public static void Toggle(IEnumerable<TrackedWindow> windows)
    {
        if (CloseIfOpen<WindowOverviewWindow>()) return;
        new WindowOverviewWindow(windows.Where(w => IsWindow(w.Handle)).ToList()).Present();
    }

    private WindowOverviewWindow(IReadOnlyList<TrackedWindow> windows) : base(Dim)
    {
        _windows = windows;
        Root.Children.Add(_canvas);
        if (windows.Count == 0)
        {
            var empty = new TextBlock { Text = UiText.Get("WindowOverviewEmpty"), Foreground = Ui.WhiteSoft, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Root.Children.Add(empty);
        }
    }

    protected override bool IsBackground(object source) => ReferenceEquals(source, _canvas);

    protected override void OnPresented()
    {
        if (_windows.Count == 0 || PresentationSource.FromVisual(this) is not HwndSource source) return;
        var scale = source.CompositionTarget?.TransformToDevice.M11 ?? 1;
        var area = new Rect(70, 70, Width - 140, Height - 70 - 150); // room for the dock
        var count = _windows.Count;
        var (columns, rows) = (1, count);
        var best = 0.0;
        for (var c = 1; c <= count; c++)
        {
            var r = (int)Math.Ceiling(count / (double)c);
            var w = (area.Width - Gap * (c - 1)) / c;
            var h = (area.Height - Gap * (r - 1)) / r - TitleHeight;
            var size = Math.Min(w, h * 1.6);
            if (size > best)
            {
                best = size;
                (columns, rows) = (c, r);
            }
        }
        var cellWidth = (area.Width - Gap * (columns - 1)) / columns;
        var cellHeight = (area.Height - Gap * (rows - 1)) / rows;
        // Center a short last row and the whole block vertically.
        var usedHeight = rows * cellHeight + (rows - 1) * Gap;
        var top = area.Top + (area.Height - usedHeight) / 2;
        for (var i = 0; i < count; i++)
        {
            var row = i / columns;
            var inRow = row == rows - 1 ? count - row * columns : columns;
            var rowWidth = inRow * cellWidth + (inRow - 1) * Gap;
            var left = area.Left + (area.Width - rowWidth) / 2 + (i % columns) * (cellWidth + Gap);
            AddCell(_windows[i], new Rect(left, top + row * (cellHeight + Gap), cellWidth, cellHeight), source.Handle, scale);
        }
    }

    private void AddCell(TrackedWindow window, Rect cell, nint host, double scale)
    {
        var frame = new Rect(cell.X, cell.Y, cell.Width, cell.Height - TitleHeight);
        var picture = frame;
        var minimized = NativeWindowTracker.IsMinimized(window.Handle);
        nint thumbnail = 0;
        if (!minimized && DwmRegisterThumbnail(host, window.Handle, out thumbnail) == 0 && thumbnail != 0
            && DwmQueryThumbnailSourceSize(thumbnail, out var size) == 0 && size.Width > 0 && size.Height > 0)
        {
            var fit = Math.Min(frame.Width / size.Width, frame.Height / size.Height);
            var w = size.Width * fit;
            var h = size.Height * fit;
            picture = new Rect(frame.X + (frame.Width - w) / 2, frame.Bottom - h, w, h);
            var properties = new ThumbnailProperties
            {
                Flags = 0x1 | 0x4 | 0x8 | 0x10, // destination, opacity, visible, client area only
                Destination = new NativeRect((int)(picture.Left * scale), (int)(picture.Top * scale), (int)(picture.Right * scale), (int)(picture.Bottom * scale)),
                Opacity = 255,
                Visible = true,
                SourceClientAreaOnly = false
            };
            DwmUpdateThumbnailProperties(thumbnail, ref properties);
            _thumbnails.Add(thumbnail);
        }
        else
        {
            if (thumbnail != 0) DwmUnregisterThumbnail(thumbnail);
            // Minimized windows have no live picture: show the app icon on a card instead.
            var w = Math.Min(frame.Width, 260);
            var h = Math.Min(frame.Height, 170);
            picture = new Rect(frame.X + (frame.Width - w) / 2, frame.Bottom - h, w, h);
            var icon = window.ExecutablePath is { } exe ? Ui.FileIcon(exe, 64) : Ui.Icon("IconApps", 48, Ui.White, null);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            var card = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(12), Background = Ui.Solid("#33FFFFFF"), Child = icon, IsHitTestVisible = false };
            Canvas.SetLeft(card, picture.X);
            Canvas.SetTop(card, picture.Y);
            _canvas.Children.Add(card);
        }

        // A frame drawn around the picture on hover; it also takes the click.
        var outline = new Border
        {
            Width = picture.Width + 12,
            Height = picture.Height + 12,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(3),
            BorderBrush = Brushes.Transparent,
            Background = Ui.Solid("#01FFFFFF"),
            Cursor = Cursors.Hand
        };
        Canvas.SetLeft(outline, picture.X - 6);
        Canvas.SetTop(outline, picture.Y - 6);
        var accent = Ui.Solid("#FF0A84FF");
        outline.MouseEnter += (_, _) => outline.BorderBrush = accent;
        outline.MouseLeave += (_, _) => outline.BorderBrush = Brushes.Transparent;
        outline.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Dismiss();
            NativeWindowTracker.Activate(window.Handle);
        };
        _canvas.Children.Add(outline);

        var icon16 = window.ExecutablePath is { } path ? Ui.FileIcon(path, 18) : new Border();
        icon16.Margin = new Thickness(0, 0, 7, 0);
        icon16.VerticalAlignment = VerticalAlignment.Center;
        var title = new TextBlock { Text = window.Title, Foreground = Ui.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, MaxWidth = Math.Max(60, cell.Width - 40) };
        var label = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
        label.Children.Add(icon16);
        label.Children.Add(title);
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(label, cell.X + (cell.Width - label.DesiredSize.Width) / 2);
        Canvas.SetTop(label, frame.Bottom + 10);
        _canvas.Children.Add(label);
    }

    protected override void OnDismissing()
    {
        foreach (var thumbnail in _thumbnails) DwmUnregisterThumbnail(thumbnail);
        _thumbnails.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThumbnailProperties
    {
        public uint Flags;
        public NativeRect Destination;
        public NativeRect Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool SourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect(int left, int top, int right, int bottom)
    {
        public int Left = left, Top = top, Right = right, Bottom = bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int Width, Height; }

    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(nint thumbnail, out NativeSize size);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(nint thumbnail, ref ThumbnailProperties properties);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(nint thumbnail);
}
