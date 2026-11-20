using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using QiDock.Services;
using QiDock.Services.Notifications;
using QiDock.Widgets;

namespace QiDock;

/// <summary>One spot in the app screen: an app, or a folder holding two or more apps.</summary>
internal sealed class LaunchEntry
{
    public string? App { get; set; }
    public string? Folder { get; set; }
    public List<string>? Apps { get; set; }

    [JsonIgnore] public bool IsFolder => Apps is not null;
}

/// <summary>The user's arrangement of the app screen, kept in launchpad.json next to the settings.</summary>
internal static class LaunchStore
{
    private sealed class Data
    {
        public List<LaunchEntry> Items { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QiDock", "launchpad.json");

    /// <summary>The saved arrangement, or null while the user has never moved anything (alphabetical then).</summary>
    public static List<LaunchEntry>? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath), Json)?.Items : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("App screen layout could not be read: {0}", ex.Message);
            return null;
        }
    }

    public static void Save(List<LaunchEntry> items)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Data { Items = items }, Json));
            File.Move(temp, FilePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("App screen layout could not be saved: {0}", ex.Message);
        }
    }

    /// <summary>
    /// The saved arrangement with uninstalled apps dropped and new apps added at the end (all apps
    /// alphabetically when nothing was saved). A folder left with one app becomes that app.
    /// </summary>
    public static List<LaunchEntry> Merge(List<LaunchEntry>? saved, IReadOnlyList<InstalledApp> apps)
    {
        var known = new HashSet<string>(apps.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<LaunchEntry>();
        foreach (var entry in saved ?? [])
        {
            if (entry.IsFolder)
            {
                var inside = entry.Apps!.Where(id => known.Contains(id) && used.Add(id)).ToList();
                if (inside.Count >= 2) result.Add(new LaunchEntry { Folder = entry.Folder, Apps = inside });
                else if (inside.Count == 1) result.Add(new LaunchEntry { App = inside[0] });
            }
            else if (entry.App is { } id && known.Contains(id) && used.Add(id))
            {
                result.Add(new LaunchEntry { App = id });
            }
        }
        foreach (var app in apps)
        {
            if (used.Add(app.Id)) result.Add(new LaunchEntry { App = app.Id });
        }
        return result;
    }
}

/// <summary>
/// How much each app is used, for the row of most used apps: what Windows itself counts (how
/// often an app was started and how long it was in front, kept for the Start menu) plus the
/// launches from the app screen, which QiDock counts in app-usage.json.
/// </summary>
internal static class AppUsage
{
    private const string UserAssist = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}\Count";

    private static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QiDock", "app-usage.json");

    /// <summary>A score per app id: starts (at most 50, so one app started by a shortcut key all day does not crowd out the rest) plus minutes in front / 20.</summary>
    public static Dictionary<string, double> Scores()
    {
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(UserAssist);
            foreach (var name in key?.GetValueNames() ?? [])
            {
                if (key!.GetValue(name) is not byte[] { Length: >= 16 } data) continue;
                var starts = BitConverter.ToInt32(data, 4);
                var focusMinutes = BitConverter.ToInt32(data, 12) / 60000.0;
                var score = Math.Min(Math.Max(starts, 0), 50) + Math.Max(focusMinutes, 0) / 20;
                if (score > 0) scores[Rot13(name)] = score;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("App usage could not be read: {0}", ex.Message);
        }
        foreach (var (id, launches) in Own())
            scores[id] = scores.GetValueOrDefault(id) + launches * 2;
        return scores;
    }

    /// <summary>Counts a launch from the app screen.</summary>
    public static void Record(string id)
    {
        var own = Own();
        own[id] = own.GetValueOrDefault(id) + 1;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(own));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static Dictionary<string, int> Own()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(FilePath)) is { } saved)
                return new Dictionary<string, int>(saved, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }

    // Windows keeps these names with every letter moved 13 places along the alphabet.
    private static string Rot13(string text) => string.Create(text.Length, text, (span, source) =>
    {
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            span[i] = c is >= 'a' and <= 'z' ? (char)('a' + (c - 'a' + 13) % 26)
                : c is >= 'A' and <= 'Z' ? (char)('A' + (c - 'A' + 13) % 26)
                : c;
        }
    });
}

/// <summary>
/// Every installed app in a glass panel over the blurred desktop: a large search pill on top, category chips, the
/// user's folders and most used apps first, then every app from A to Z below a thin line. Scroll with the wheel,
/// the touchpad or the keys; type to search; drop an icon on another to make a folder. Only the rows in view (and a
/// few around them) are built, and icons stay cached between openings.
/// </summary>
public sealed class AppGridWindow : OverlayWindow
{
    private const double DragThreshold = 6;
    // Around the panel the screen goes almost black, as behind a sheet of dark glass.
    private static readonly Brush Dim = Ui.Solid("#EB040405");
    private static readonly Brush PanelFill = Vertical("#FA202023", "#FA161618");
    private static readonly Brush PanelEdge = Vertical("#38FFFFFF", "#17FFFFFF");
    private static readonly Brush PillFill = Vertical("#FF3B3B3F", "#FF1B1B1E");
    private static readonly Brush PillEdge = Vertical("#66FFFFFF", "#1AFFFFFF");
    private static readonly Brush PillText = Ui.Solid("#FFDCDCDE");
    private static readonly Brush PillGlyph = Ui.Solid("#FFBDBDC1");
    private static readonly Brush SortFill = Ui.Solid("#80FFFFFF");
    private static readonly Brush SortHover = Ui.Solid("#B3FFFFFF");
    private static readonly Brush SortGlyph = Ui.Solid("#FF26262A");
    private static readonly Brush ChipFill = Ui.Solid("#2BFFFFFF");
    private static readonly Brush ChipHover = Ui.Solid("#40FFFFFF");
    private static readonly Brush ChipEdge = Ui.Solid("#1AFFFFFF");
    private static readonly Brush ChipOn = Ui.Solid("#FFE4E4E6");
    private static readonly Brush ChipOnText = Ui.Solid("#FF1A1A1C");
    private static readonly Brush ChipText = Ui.Solid("#FFE8E8EA");
    private static readonly Brush LabelText = Ui.Solid("#FFF2F2F4");
    private static readonly Brush FolderFill = Vertical("#66FFFFFF", "#4DFFFFFF");
    private static readonly Brush FolderEdge = Ui.Solid("#33FFFFFF");
    private static readonly Brush TargetFill = Ui.Solid("#4DFFFFFF");
    private static readonly Brush LineFill = Ui.Solid("#2EFFFFFF");
    private static readonly Brush ThumbFill = Ui.Solid("#33FFFFFF");

    private static Brush Vertical(string top, string bottom)
    {
        var brush = new LinearGradientBrush(((SolidColorBrush)Ui.Solid(top)).Color, ((SolidColorBrush)Ui.Solid(bottom)).Color, 90);
        brush.Freeze();
        return brush;
    }

    private readonly double _k;
    private readonly double _panelTop;
    private const double GridTop = 2;

    /// <summary>
    /// How large the icons are: their size in points, and the screen pixels that covers. Icons are read at exactly
    /// those pixels and drawn on whole pixels, so they are never scaled again (scaling them made them grainy).
    /// </summary>
    private static readonly (double Dip, int Pixels) IconSize = MeasureIcon();

    /// <summary>Screen pixels per point on the main screen.</summary>
    private static double ScreenScale => Application.Current?.MainWindow is { } main && PresentationSource.FromVisual(main) is not null
        ? VisualTreeHelper.GetDpi(main).DpiScaleX
        : VisualTreeHelper.GetDpi(new DrawingVisual()).DpiScaleX;

    private static (double, int) MeasureIcon()
    {
        var k = Math.Clamp(SystemParameters.PrimaryScreenHeight / 1080, 0.78, 1.4);
        var dpi = ScreenScale;
        var pixels = (int)Math.Round(90 * k * dpi);
        return (pixels / dpi, pixels);
    }

    private static ImageSource? ReadIcon(string id) => NotificationApps.LoadIcon(id, IconSize.Pixels, exact: true);
    // Icons stay cached between openings (about 64 KB each) so the panel fills at once next time.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);

    private sealed class TileParts
    {
        public required FrameworkElement Element;
        public required FrameworkElement IconHost;
        public required Border Backdrop;
    }

    /// <summary>A press on a tile, until the button goes up.</summary>
    private sealed class Press
    {
        public Point Start;
        public LaunchEntry? Entry;          // a tile in the panel
        public string? FolderApp;           // an app tile inside the open folder
        public FrameworkElement? Icon;
        public bool Fixed;                  // opens, never moves (most used row, folders, filtered views)
    }

    /// <summary>An icon being dragged.</summary>
    private sealed class Drag
    {
        public required LaunchEntry Entry;
        public required FrameworkElement Ghost;
        public Vector Grab;
        public LaunchEntry? Folder;         // set while the app is still inside the open folder
    }

    private readonly double _cols, _cellW, _cellH, _icon, _gridLeft;
    private readonly Border _pill;
    private readonly Border _panel;
    private readonly Grid _host = new() { ClipToBounds = true, Background = Brushes.Transparent };
    private readonly Canvas _strip = new() { Background = Brushes.Transparent };
    private readonly TranslateTransform _stripShift = new();
    private readonly Border _line = new() { Height = 1, Background = LineFill, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Border _thumb = new() { Width = 4, CornerRadius = new CornerRadius(2), Background = ThumbFill, Opacity = 0, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 7, 0), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _chipScroll;
    private readonly Border _sortButton;
    private readonly Grid _folderLayer = new() { Visibility = Visibility.Collapsed };
    private readonly Canvas _dragLayer = new() { IsHitTestVisible = false };
    private readonly TextBlock _note = new() { Foreground = Ui.WhiteSoft, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly TextBox _box;
    private readonly Dictionary<string, InstalledApp> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<LaunchEntry, TileParts> _tiles = [];
    private readonly Dictionary<LaunchEntry, Point> _slots = [];
    private readonly Dictionary<string, LaunchEntry> _viewEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _edgeTimer;
    private readonly Queue<(Image Image, string Id)> _iconQueue = new();
    private bool _iconLoaderRunning;
    private bool _closed;

    private List<LaunchEntry> _items = [];    // the arrangement: folders and the apps outside them
    private List<LaunchEntry> _frequent = []; // the most used apps, after the folders on top
    private List<LaunchEntry> _order = [];    // what the panel shows, in reading order
    private string _category = AppCategories.All;
    private double _scroll, _contentHeight, _lineY = -1, _thumbAt;
    private LaunchEntry? _folderTarget;
    private LaunchEntry? _selected;           // the icon the arrow keys are on
    private Press? _press;
    private Drag? _drag;
    private Point _lastPoint;
    private int _edgeDirection;

    // The open folder.
    private LaunchEntry? _openFolder;
    private Border? _folderPanel;
    private Canvas? _folderCanvas;
    private int _folderCols;
    private readonly Dictionary<string, TileParts> _folderTiles = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _folderOthers = [];
    private int _folderDropIndex;

    public static bool IsOpen => Application.Current?.Windows.OfType<AppGridWindow>().Any(w => w.IsVisible) == true;

    public static void Toggle()
    {
        if (CloseIfOpen<AppGridWindow>()) return;
        new AppGridWindow().Present();
    }

    /// <summary>
    /// Loads the app list and the first icons quietly in the background (some time after QiDock starts), so the first
    /// opening already shows every icon instead of filling in.
    /// </summary>
    public static void Prewarm()
    {
        AppList.RefreshAsync().ContinueWith(t =>
        {
            var ids = LaunchStore.Merge(LaunchStore.Load(), t.Result)
                .Take(80)
                .SelectMany(e => e.IsFolder ? e.Apps!.Take(9) : [e.App!])
                .Where(id => !Icons.ContainsKey(id))
                .ToList();
            if (ids.Count == 0) return;
            var thread = new Thread(() =>
            {
                foreach (var id in ids) Icons.GetOrAdd(id, ReadIcon);
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "AppGrid icon prewarm" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static string L(string turkish, string english) => DesktopWidgets.Desk.L(turkish, english);

#if DEBUG
    /// <summary>Opens the app screen without taking the keyboard, waits for the icons and saves it as a picture.</summary>
    internal static void RenderTo(string path, Action done)
    {
        var window = new AppGridWindow { ShowActivated = false };
        window.Show();
        window.Root.Opacity = 1;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var dpi = VisualTreeHelper.GetDpi(window);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Math.Ceiling(window.Root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.Root.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(window.Root);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) png.Save(file);
            window.Close();
            done();
        };
        timer.Start();
    }
#endif

    private AppGridWindow() : base(Dim)
    {
        // The screen darkens; the search pill (660 × 108) stands near the top and the glass panel (1114 wide) below
        // it, down to just above the dock, seven columns of 90-point icons. Sizes are measured on a 1080-line screen.
        var screenWidth = Width;
        var screenHeight = Height;
        _k = Math.Clamp(screenHeight / 1080, 0.78, 1.4);
        var k = _k;
        var pillTop = Math.Round(76 * k);
        var pillHeight = Math.Round(108 * k);
        var panelWidth = Math.Round(Math.Min(1114 * k, screenWidth - 80));
        _panelTop = Math.Round(204 * k);
        var dockRoom = Math.Max(135 * k, TaskbarReplacementSession.DockHeightDips + 40 * k);
        var panelHeight = Math.Round(Math.Max(320, screenHeight - _panelTop - dockRoom));
        Root.UseLayoutRounding = true; // icons on whole pixels stay sharp
        var inner = panelWidth - 2 - 48 * k;
        _cols = Math.Clamp(Math.Round(inner / (152 * k)), 4, 9);
        _cellW = Math.Floor(inner / _cols);
        _icon = IconSize.Dip;
        _cellH = Math.Round(144 * k);
        _gridLeft = Math.Round((panelWidth - 2 - _cols * _cellW) / 2);

        // ---- Search pill ------------------------------------------------------------------------
        _box = new TextBox
        {
            FontSize = 44 * k,
            FontWeight = FontWeights.Light,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = PillText,
            CaretBrush = PillText,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0)
        };
        var hint = new TextBlock { Text = L("Uygulamalar", "Applications"), FontSize = 44 * k, FontWeight = FontWeights.Light, Foreground = PillText, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Margin = new Thickness(2, 0, 0, 0) };
        _box.TextChanged += (_, _) =>
        {
            hint.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Refill();
        };
        _box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || (_order.Count == 0 && _selected is null)) return;
            e.Handled = true;
            Open(_selected ?? _order[0]);
        };
        var pillWidth = Math.Round(Math.Min(660 * k, panelWidth));
        var glyph = Ui.Icon("IconApps", 44 * k, PillGlyph, null, 2.2);
        glyph.VerticalAlignment = VerticalAlignment.Center;
        glyph.Margin = new Thickness(0, 0, 30 * k, 0);
        var sortIcon = Ui.Icon("IconArrowUpDown", 17 * k, SortGlyph, null, 2.4);
        sortIcon.HorizontalAlignment = HorizontalAlignment.Center;
        sortIcon.VerticalAlignment = VerticalAlignment.Center;
        _sortButton = new Border
        {
            Width = 40 * k,
            Height = 40 * k,
            CornerRadius = new CornerRadius(20 * k),
            Background = SortFill,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Child = sortIcon
        };
        _sortButton.MouseEnter += (_, _) => _sortButton.Background = SortHover;
        _sortButton.MouseLeave += (_, _) => _sortButton.Background = SortFill;
        _sortButton.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            AppCategories.SortByUse = !AppCategories.SortByUse;
            ShowSortTip();
            Refill();
        };
        ShowSortTip();
        var text = new Grid();
        text.Children.Add(hint);
        text.Children.Add(_box);
        var pillRow = new DockPanel();
        DockPanel.SetDock(glyph, Dock.Left);
        DockPanel.SetDock(_sortButton, Dock.Right);
        pillRow.Children.Add(glyph);
        pillRow.Children.Add(_sortButton);
        pillRow.Children.Add(text);
        _pill = new Border
        {
            Child = pillRow,
            Width = pillWidth,
            Height = pillHeight,
            CornerRadius = new CornerRadius(pillHeight / 2),
            Background = PillFill,
            BorderBrush = PillEdge,
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(34 * k, 0, 32 * k, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, pillTop, 0, 0)
        };

        // ---- Panel: chips on top, the icons below ---------------------------------------------
        _chipScroll = new ScrollViewer
        {
            Content = _chips,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Margin = new Thickness(24 * k, 18 * k, 24 * k, 14 * k)
        };
        _strip.RenderTransform = _stripShift;
        _strip.Width = panelWidth - 2;
        _strip.Children.Add(_line);
        _host.Children.Add(_strip);
        _host.Children.Add(_thumb);
        _host.Children.Add(_note);
        _host.SizeChanged += (_, _) => Realize();
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition());
        body.Children.Add(_chipScroll);
        Grid.SetRow(_host, 1);
        body.Children.Add(_host);
        _panel = new Border
        {
            Child = body,
            Width = panelWidth,
            Height = panelHeight,
            CornerRadius = new CornerRadius(28 * k),
            Background = PanelFill,
            BorderBrush = PanelEdge,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, _panelTop, 0, 0)
        };
        // The icons are cut off by the panel's rounded bottom, as in a scrolling list.
        _host.Margin = new Thickness(0, 0, 0, 2);
        _host.Clip = null;
        body.ClipToBounds = false;
        _panel.SizeChanged += (_, _) =>
        {
            var radius = 28 * k - 1;
            _host.Clip = new RectangleGeometry(new Rect(0, -_chipScroll.ActualHeight, _host.ActualWidth, _host.ActualHeight + _chipScroll.ActualHeight), radius, radius);
        };
        Root.Children.Add(_panel);
        Root.Children.Add(_pill);
        Root.Children.Add(_folderLayer);
        Root.Children.Add(_dragLayer);
        Frost(new Rect((screenWidth - panelWidth) / 2, _panelTop, panelWidth, panelHeight));

        // Holding a dragged icon near the top or bottom of the panel scrolls it.
        _edgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _edgeTimer.Tick += (_, _) =>
        {
            if (_drag is null || _edgeDirection == 0)
            {
                _edgeTimer.Stop();
                return;
            }
            ScrollTo(_scroll + _edgeDirection * 14, animate: false);
            DragTo(_lastPoint);
        };

        PreviewMouseLeftButtonDown += OnPressDown;
        PreviewMouseMove += OnPressMove;
        PreviewMouseLeftButtonUp += OnPressUp;
        LostMouseCapture += (_, _) => { if (_press is not null && !Root.IsMouseCaptured) CancelPress(); };
        PreviewMouseWheel += (_, e) =>
        {
            // In an open folder the wheel scrolls the folder's own list.
            if (_openFolder is not null || _drag is not null) return;
            e.Handled = true;
            ScrollTo(_scroll - e.Delta * 0.9);
        };
        PreviewKeyDown += OnKey;
        PreviewMouseRightButtonUp += OnMenu;
        Closed += (_, _) => _closed = true;

        Load(AppList.Cached ?? []);
        AppList.RefreshAsync().ContinueWith(t =>
        {
            // Rebuilding takes a moment, so only when the list really changed (and nothing is in hand).
            if (_closed || _drag is not null || _openFolder is not null) return;
            if (t.Result.Count == _known.Count && t.Result.All(a => _known.ContainsKey(a.Id))) return;
            Load(t.Result);
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// The panel becomes frosted glass: the desktop picture behind it, softened, with its own colours (as the
    /// widgets show it) under a light veil. Without a desktop picture, what is on the screen behind it, blurred
    /// (read here, before the window shows). Where neither can be had it keeps its solid fill.
    /// </summary>
    private void Frost(Rect panel)
    {
        var veil = Color.FromArgb(0x5C, 0x0C, 0x0C, 0x10);
        if (Services.Widgets.GlassLook.Surface(new Rect(panel.Size), panel, veil, sheen: true, frost: false) is { } wallpaper)
        {
            _panel.Background = wallpaper;
            return;
        }
        var dpi = ScreenScale;
        const double Margin = 24; // read a little more than the window, so the blur at the edges has something to blend
        var screenPixels = new Rect(0, 0, Math.Round(SystemParameters.PrimaryScreenWidth * dpi), Math.Round(SystemParameters.PrimaryScreenHeight * dpi));
        var wanted = new Rect((Left - Margin) * dpi, (Top - Margin) * dpi, (Width + 2 * Margin) * dpi, (Height + 2 * Margin) * dpi);
        wanted.Intersect(screenPixels);
        if (wanted.IsEmpty) return;
        var area = new Int32Rect((int)wanted.X, (int)wanted.Y, (int)wanted.Width, (int)wanted.Height);
        if (ScreenGlass.Capture(area, Colors.Transparent) is not { } glass) return;
        // Where the picture lies, in the window's own points.
        var picture = new Rect(area.X / dpi - Left, area.Y / dpi - Top, area.Width / dpi, area.Height / dpi);
        _panel.Background = Glass(glass, picture, panel, Color.FromArgb(0xA6, 0x16, 0x16, 0x19));
    }

    private static Brush Glass(ImageSource glass, Rect picture, Rect element, Color veil)
    {
        var drawing = new DrawingGroup();
        // Drawn in the element's own points: the picture shifted by where the element stands, the veil over it.
        drawing.Children.Add(new ImageDrawing(glass, new Rect(picture.X - element.X, picture.Y - element.Y, picture.Width, picture.Height)));
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(veil), null, new RectangleGeometry(new Rect(0, 0, element.Width, element.Height))));
        var box = new Rect(0, 0, element.Width, element.Height);
        var brush = new DrawingBrush(drawing)
        {
            Stretch = Stretch.Fill,
            ViewboxUnits = BrushMappingMode.Absolute, Viewbox = box,
            ViewportUnits = BrushMappingMode.Absolute, Viewport = box
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.Linear);
        brush.Freeze();
        return brush;
    }

    protected override void OnShown()
    {
        _box.Focus();
        // The dock and the menu bar stay on top of the darkened screen, as in macOS.
        foreach (Window window in Application.Current.Windows)
        {
            if (window is OverlayWindow || !window.IsVisible || !window.Topmost) continue;
            if (PresentationSource.FromVisual(window) is HwndSource source)
                SetWindowPos(source.Handle, -1 /* topmost */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /* no size, no move, no activate */);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    /// <summary>
    /// The pill and the panel rise a few points into place while they fade in. Nothing is scaled, so the icons are
    /// as sharp while it moves as after.
    /// </summary>
    protected override void OnPresented()
    {
        foreach (var part in new FrameworkElement[] { _pill, _panel })
        {
            var shift = new TranslateTransform(0, Math.Round(10 * _k));
            part.RenderTransform = shift;
            var rise = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = Motion.Out };
            rise.Completed += (_, _) => part.RenderTransform = Transform.Identity;
            shift.BeginAnimation(TranslateTransform.YProperty, rise);
        }
    }

    protected override void OnDismissing()
    {
        _edgeTimer.Stop();
        if (Root.IsMouseCaptured) Root.ReleaseMouseCapture();
    }

    protected override bool OnEscape()
    {
        if (_openFolder is not null)
        {
            CloseFolder();
            return true;
        }
        if (_box.Text.Length > 0)
        {
            _box.Clear();
            return true;
        }
        if (_category != AppCategories.All)
        {
            SetCategory(AppCategories.All);
            return true;
        }
        return false;
    }

    private void ShowSortTip() => _sortButton.ToolTip = AppCategories.SortByUse
        ? L("Kullanıma göre sıralı (tıklayınca ada göre)", "Sorted by use (click for name)")
        : L("Ada göre sıralı (tıklayınca kullanıma göre)", "Sorted by name (click for use)");

    // ───────────────────────── Content ─────────────────────────

    private void Load(IReadOnlyList<InstalledApp> apps)
    {
        _known.Clear();
        foreach (var app in apps) _known.TryAdd(app.Id, app);
        _items = LaunchStore.Merge(LaunchStore.Load(), apps);
        var scores = AppUsage.Scores();
        _frequent = apps
            .Where(a => scores.ContainsKey(a.Id))
            .OrderByDescending(a => scores[a.Id])
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            // Two rows on top with the folders, as many as the folders leave room for.
            .Take(Math.Max((int)_cols, 2 * (int)_cols - _items.Count(e => e.IsFolder)))
            .Select(a => new LaunchEntry { App = a.Id })
            .ToList();
        if (_frequent.Count < 3) _frequent = [];
        foreach (var tile in _tiles.Values) _strip.Children.Remove(tile.Element);
        _tiles.Clear();
        _viewEntries.Clear();
        BuildChips();
        Refill();
    }

    private bool Searching => _box.Text.Trim().Length > 0;

    /// <summary>A search or a category: a view of the apps, not the arrangement (icons stay put there).</summary>
    private bool Filtered => Searching || _category != AppCategories.All;

    /// <summary>"Tümü" and one chip per category that has apps.</summary>
    private void BuildChips()
    {
        _chips.Children.Clear();
        var present = _known.Values.Select(AppCategories.Of).ToHashSet();
        if (_category != AppCategories.All && !present.Contains(_category)) _category = AppCategories.All;
        foreach (var id in new[] { AppCategories.All }.Concat(AppCategories.Order.Where(present.Contains)))
        {
            var on = id == _category;
            var label = new TextBlock { Text = AppCategories.Name(id), FontSize = 16.5 * _k, Foreground = on ? ChipOnText : ChipText, FontWeight = FontWeights.Medium };
            var chip = new Border
            {
                Child = label,
                CornerRadius = new CornerRadius(18 * _k),
                Padding = new Thickness(17 * _k, 6 * _k, 17 * _k, 7 * _k),
                Margin = new Thickness(0, 0, 10 * _k, 0),
                Background = on ? ChipOn : ChipFill,
                BorderBrush = on ? ChipOn : ChipEdge,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Tag = id
            };
            if (!on)
            {
                chip.MouseEnter += (_, _) => chip.Background = ChipHover;
                chip.MouseLeave += (_, _) => chip.Background = ChipFill;
            }
            chip.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                SetCategory(id);
            };
            _chips.Children.Add(chip);
        }
    }

    private void SetCategory(string id)
    {
        if (_category == id) return;
        _category = id;
        BuildChips();
        Refill();
        if (_chips.Children.OfType<Border>().FirstOrDefault(c => Equals(c.Tag, id)) is { } chip) chip.BringIntoView();
    }

    private void NextCategory(int step)
    {
        var ids = _chips.Children.OfType<Border>().Select(c => (string)c.Tag).ToList();
        if (ids.Count == 0) return;
        var at = ids.IndexOf(_category);
        SetCategory(ids[((at + step) % ids.Count + ids.Count) % ids.Count]);
    }

    private string NameOfApp(string id) => _known.TryGetValue(id, out var app) ? app.Name : "";

    private List<LaunchEntry> Sorted(IEnumerable<LaunchEntry> entries)
    {
        if (!AppCategories.SortByUse)
            return entries.OrderBy(e => NameOfApp(e.App!), StringComparer.CurrentCultureIgnoreCase).ToList();
        var scores = AppUsage.Scores();
        return entries.OrderByDescending(e => scores.GetValueOrDefault(e.App!)).ThenBy(e => NameOfApp(e.App!), StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>An app wherever it sits (inside a folder too) as its own tile, for searches and categories.</summary>
    private LaunchEntry ViewEntry(string id) =>
        _items.FirstOrDefault(e => !e.IsFolder && string.Equals(e.App, id, StringComparison.OrdinalIgnoreCase))
        ?? (_viewEntries.TryGetValue(id, out var known) ? known : _viewEntries[id] = new LaunchEntry { App = id });

    private IEnumerable<string> AllApps() => _items.SelectMany(e => e.IsFolder ? e.Apps! : [e.App!]);

    /// <summary>Works out what the panel shows and where every icon stands; then builds the rows in view.</summary>
    private void Refill(bool keepScroll = false)
    {
        Select(null);
        _slots.Clear();
        _order = [];
        _lineY = -1;
        var y = GridTop;
        var column = 0;
        void Put(LaunchEntry entry)
        {
            if (column == (int)_cols)
            {
                column = 0;
                y += _cellH;
            }
            _slots[entry] = new Point(_gridLeft + column * _cellW, y);
            _order.Add(entry);
            column++;
        }
        void EndRow()
        {
            if (column == 0) return;
            column = 0;
            y += _cellH;
        }

        var query = _box.Text.Trim();
        if (query.Length > 0)
        {
            foreach (var id in AllApps().Where(id => _known.TryGetValue(id, out var app) && Matches(app.Name, query)).Distinct(StringComparer.OrdinalIgnoreCase))
                Put(ViewEntry(id));
        }
        else if (_category != AppCategories.All)
        {
            foreach (var entry in Sorted(AllApps().Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(id => _known.TryGetValue(id, out var app) && AppCategories.Of(app) == _category).Select(ViewEntry)))
                Put(entry);
        }
        else
        {
            var top = _items.Where(e => e.IsFolder).Concat(_frequent).ToList();
            foreach (var entry in top) Put(entry);
            EndRow();
            var rest = Sorted(_items.Where(e => !e.IsFolder));
            if (top.Count > 0 && rest.Count > 0)
            {
                _lineY = y + 14 * _k;
                y += 30 * _k;
            }
            foreach (var entry in rest) Put(entry);
        }
        EndRow();
        _contentHeight = y + 10;
        _note.Text = _known.Count == 0 ? UiText.Get("AppGridLoading") : _order.Count == 0 ? UiText.Get("AppGridNoMatch") : "";
        foreach (var gone in _tiles.Keys.Where(k => !_slots.ContainsKey(k)).ToList()) Forget(gone);
        ScrollTo(keepScroll ? _scroll : 0, animate: false);
    }

    private double ViewHeight => _host.ActualHeight > 0 ? _host.ActualHeight : Math.Max(200, _panel.Height - 80);

    private double MaxScroll => Math.Max(0, _contentHeight - ViewHeight);

    private void ScrollTo(double target, bool animate = true)
    {
        target = Math.Clamp(target, 0, MaxScroll);
        if (PresentationSource.FromVisual(this) is { } source) // on a whole screen pixel, or every icon is drawn between two
        {
            var dpi = source.CompositionTarget.TransformToDevice.M22;
            target = Math.Min(Math.Round(target * dpi) / dpi, MaxScroll);
        }
        _scroll = target;
        Realize();
        if (!animate)
        {
            _stripShift.BeginAnimation(TranslateTransform.YProperty, null);
            _stripShift.Y = -target;
            return;
        }
        _stripShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-target, TimeSpan.FromMilliseconds(200)) { EasingFunction = Motion.Out });
    }

    /// <summary>
    /// Builds the tiles of the rows in view and two rows around them, and lets the others go. Tiles are kept between
    /// calls, so with <paramref name="animate"/> the ones whose place changed glide there.
    /// </summary>
    private void Realize(bool animate = false)
    {
        if (_closed) return;
        _strip.Height = Math.Max(_contentHeight, ViewHeight);
        var from = _scroll - _cellH * 2;
        var to = _scroll + ViewHeight + _cellH * 2;
        foreach (var gone in _tiles.Keys.Where(k => !_slots.TryGetValue(k, out var at) || at.Y + _cellH < from || at.Y > to).ToList())
        {
            if (_drag is not null && ReferenceEquals(gone, _drag.Entry)) continue;
            Forget(gone);
        }
        var pending = new List<(Image, string)>();
        foreach (var entry in _order)
        {
            var at = _slots[entry];
            if (at.Y + _cellH < from || at.Y > to) continue;
            if (!_tiles.TryGetValue(entry, out var tile))
            {
                tile = MakeTile(entry, pending);
                if (ReferenceEquals(entry, _selected)) Highlight(tile, true);
                if (_drag is not null && ReferenceEquals(entry, _drag.Entry)) tile.Element.Opacity = 0.25;
                _tiles[entry] = tile;
                _strip.Children.Add(tile.Element);
                Canvas.SetLeft(tile.Element, at.X);
                Canvas.SetTop(tile.Element, at.Y);
                continue;
            }
            var old = new Point(Canvas.GetLeft(tile.Element), Canvas.GetTop(tile.Element));
            if (old == at) continue;
            Canvas.SetLeft(tile.Element, at.X);
            Canvas.SetTop(tile.Element, at.Y);
            if (!animate) continue;
            var shift = Motion.Shift(tile.Element);
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(old.X - at.X + shift.X, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = Motion.Out });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(old.Y - at.Y + shift.Y, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = Motion.Out });
        }
        RequestIcons(pending);

        _line.Visibility = _lineY >= 0 ? Visibility.Visible : Visibility.Collapsed;
        _line.Width = _cols * _cellW - 8;
        Canvas.SetLeft(_line, _gridLeft + 4);
        Canvas.SetTop(_line, _lineY);

        // A thin bar at the right shows where in the list the panel is.
        var view = ViewHeight;
        if (_contentHeight <= view + 1)
        {
            _thumb.Visibility = Visibility.Collapsed;
            return;
        }
        _thumb.Visibility = Visibility.Visible;
        // Shown only while the list moves, then it fades away.
        if (_scroll != _thumbAt)
            _thumb.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
        {
            KeyFrames =
            {
                new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))),
                new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900))),
                new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1300)))
            }
        });
        _thumb.Height = Math.Max(36, view * view / _contentHeight);
        _thumb.Margin = new Thickness(0, (view - _thumb.Height) * (_scroll / MaxScroll), 7, 0);
        _thumbAt = _scroll;
    }

    // ───────────────────────── Tiles ─────────────────────────

    private string NameOf(LaunchEntry entry) =>
        entry.IsFolder ? (string.IsNullOrWhiteSpace(entry.Folder) ? UiText.Get("AppGridFolder") : entry.Folder!) : NameOfApp(entry.App!);

    private TileParts MakeTile(LaunchEntry entry, List<(Image, string)> pending)
    {
        FrameworkElement icon;
        if (entry.IsFolder)
        {
            // A frosted rounded square showing the first nine apps.
            var minis = new UniformGrid { Rows = 3, Columns = 3, Margin = new Thickness(_icon * 0.11), VerticalAlignment = VerticalAlignment.Top };
            foreach (var id in entry.Apps!.Take(9))
            {
                var mini = new Image { Width = _icon * 0.2, Height = _icon * 0.2, Stretch = Stretch.Uniform, Margin = new Thickness(_icon * 0.02) };
                RenderOptions.SetBitmapScalingMode(mini, BitmapScalingMode.HighQuality);
                pending.Add((mini, id));
                minis.Children.Add(mini);
            }
            icon = new Border { Width = _icon, Height = _icon, CornerRadius = new CornerRadius(_icon * 0.23), Background = FolderFill, BorderBrush = FolderEdge, BorderThickness = new Thickness(1), Child = minis };
        }
        else
        {
            var image = new Image { Width = _icon, Height = _icon, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            pending.Add((image, entry.App!));
            icon = image;
        }
        return Assemble(icon, NameOf(entry), entry);
    }

    private TileParts MakeAppTile(string id, List<(Image, string)> pending)
    {
        var image = new Image { Width = _icon, Height = _icon, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        pending.Add((image, id));
        return Assemble(image, NameOfApp(id), id);
    }

    /// <summary>The icon over its name, in a cell of the grid. No per-icon shadows: a hundred of them made the screen stutter.</summary>
    private TileParts Assemble(FrameworkElement icon, string name, object tag)
    {
        var backdrop = new Border { CornerRadius = new CornerRadius(_icon * 0.3), Background = Brushes.Transparent, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.8, 0.8) };
        var iconHost = new Grid { Width = Math.Round(_icon + 12 * _k), Height = Math.Round(_icon + 12 * _k), HorizontalAlignment = HorizontalAlignment.Center };
        iconHost.Children.Add(backdrop);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        iconHost.Children.Add(icon);
        var label = new TextBlock
        {
            Text = name,
            Foreground = LabelText,
            FontSize = 17 * _k,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = _cellW - 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3 * _k, 0, 0)
        };
        var column = new StackPanel { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        column.Children.Add(iconHost);
        column.Children.Add(label);
        var cell = new Grid { Width = _cellW, Height = _cellH, Tag = tag };
        cell.Children.Add(column);
        return new TileParts { Element = cell, IconHost = iconHost, Backdrop = backdrop };
    }

    /// <summary>Icons load on one background thread and drop into their tiles in small batches.</summary>
    private void RequestIcons(List<(Image Image, string Id)> pending)
    {
        foreach (var (image, id) in pending)
        {
            if (Icons.TryGetValue(id, out var known)) image.Source = known;
            else _iconQueue.Enqueue((image, id));
        }
        if (_iconLoaderRunning || _iconQueue.Count == 0) return;
        _iconLoaderRunning = true;
        var work = _iconQueue.ToList();
        _iconQueue.Clear();
        var thread = new Thread(() =>
        {
            var batch = new List<(Image Image, ImageSource? Source)>();
            void Flush()
            {
                if (batch.Count == 0) return;
                var ready = batch.ToArray();
                batch.Clear();
                Dispatcher.BeginInvoke(() =>
                {
                    foreach (var (image, source) in ready) image.Source = source;
                }, DispatcherPriority.Background);
            }
            foreach (var (image, id) in work)
            {
                if (_closed) return;
                batch.Add((image, Icons.GetOrAdd(id, ReadIcon)));
                if (batch.Count >= 16) Flush();
            }
            Flush();
            Dispatcher.BeginInvoke(() =>
            {
                // Tiles made while this thread ran wait in the queue; start again for them.
                _iconLoaderRunning = false;
                if (_iconQueue.Count > 0) RequestIcons([]);
            });
        }) { IsBackground = true, Name = "AppGrid icons" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    // ───────────────────────── Pointer ─────────────────────────

    private static T? Ancestor<T>(DependencyObject? from, Func<T, bool> match) where T : DependencyObject
    {
        while (from is not null)
        {
            if (from is T found && match(found)) return found;
            from = from is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(from) : LogicalTreeHelper.GetParent(from);
        }
        return null;
    }

    private void OnPressDown(object sender, MouseButtonEventArgs e)
    {
        if (_drag is not null) return;
        var source = e.OriginalSource as DependencyObject;
        if (Ancestor<TextBox>(source, _ => true) is not null) return;
        var cell = Ancestor<FrameworkElement>(source, f => f.Tag is LaunchEntry or string && f is Grid);
        var point = e.GetPosition(Root);
        if (_openFolder is not null)
        {
            // In a folder only its own apps can be picked up; the dim around it closes it on its own.
            if (cell?.Tag is not string id || !_folderTiles.TryGetValue(id, out var parts)) return;
            _press = new Press { Start = point, FolderApp = id, Icon = parts.IconHost };
        }
        else if (cell?.Tag is LaunchEntry entry && _tiles.TryGetValue(entry, out var parts))
        {
            _press = new Press { Start = point, Entry = entry, Icon = parts.IconHost, Fixed = Filtered || entry.IsFolder || _frequent.Contains(entry) };
        }
        else
        {
            return;
        }
        Select(null);
        if (_press.Icon is not null) _press.Icon.Opacity = 0.65;
        Root.CaptureMouse();
    }

    private void OnPressMove(object sender, MouseEventArgs e)
    {
        if (_press is null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(Root);
        _lastPoint = point;
        if (_drag is not null)
        {
            DragTo(point);
            return;
        }
        if ((point - _press.Start).Length < DragThreshold || _press.Fixed) return;
        if (_press.Entry is not null || _press.FolderApp is not null) StartDrag(point);
    }

    private void OnPressUp(object sender, MouseButtonEventArgs e)
    {
        var press = _press;
        if (press is null) return;
        _press = null;
        if (Root.IsMouseCaptured) Root.ReleaseMouseCapture();
        if (press.Icon is not null) press.Icon.Opacity = 1;
        e.Handled = true;
        if (_drag is not null)
        {
            LetGo();
            return;
        }
        if ((e.GetPosition(Root) - press.Start).Length >= DragThreshold * 2) return;
        if (press.Entry is not null) Open(press.Entry);
        else if (press.FolderApp is not null) Launch(press.FolderApp);
    }

    private void CancelPress()
    {
        if (_press?.Icon is not null) _press.Icon.Opacity = 1;
        _press = null;
        if (_drag is not null) LetGo();
    }

    // ───────────────────────── Keyboard ─────────────────────────

    /// <summary>
    /// The arrow keys move a highlight over the icons (left and right only while the search field is empty, so they
    /// still move the cursor in typed text) and Enter opens it; Page Up / Down scroll; Tab and Shift+Tab change the
    /// category.
    /// </summary>
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_openFolder is not null || _drag is not null) return;
        var typing = _box.Text.Length > 0;
        switch (e.Key)
        {
            case Key.Left when !typing: Step(-1); break;
            case Key.Right when !typing: Step(1); break;
            case Key.Up: Vertical(-1); break;
            case Key.Down: Vertical(1); break;
            case Key.PageDown: ScrollTo(_scroll + ViewHeight * 0.9); break;
            case Key.PageUp: ScrollTo(_scroll - ViewHeight * 0.9); break;
            case Key.Tab: NextCategory((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1); break;
            default: return;
        }
        e.Handled = true;
    }

    private LaunchEntry? FirstInView() => _order.FirstOrDefault(entry => _slots[entry].Y >= _scroll - 1) ?? _order.FirstOrDefault();

    private void Step(int move)
    {
        if (_order.Count == 0) return;
        var at = _selected is null ? -1 : _order.IndexOf(_selected);
        Select(at < 0 ? FirstInView() : _order[Math.Clamp(at + move, 0, _order.Count - 1)]);
    }

    /// <summary>Up and down go to the nearest icon in the row above or below (the rows above and below the line differ in length).</summary>
    private void Vertical(int move)
    {
        if (_order.Count == 0) return;
        if (_selected is null || !_slots.TryGetValue(_selected, out var current))
        {
            Select(FirstInView());
            return;
        }
        var rows = _order.Select(entry => _slots[entry].Y).Distinct().OrderBy(y => y).ToList();
        var row = rows.IndexOf(current.Y) + move;
        if (row < 0 || row >= rows.Count) return;
        Select(_order.Where(entry => _slots[entry].Y == rows[row]).MinBy(entry => Math.Abs(_slots[entry].X - current.X)));
    }

    /// <summary>Highlights the icon the keyboard is on (scrolling it into view), or none.</summary>
    private void Select(LaunchEntry? entry)
    {
        if (_selected is not null && !ReferenceEquals(_selected, entry) && !ReferenceEquals(_selected, _folderTarget)
            && _tiles.TryGetValue(_selected, out var old))
            Highlight(old, false);
        _selected = entry;
        if (entry is null || !_slots.TryGetValue(entry, out var at)) return;
        if (at.Y < _scroll) ScrollTo(at.Y - 8);
        else if (at.Y + _cellH > _scroll + ViewHeight) ScrollTo(at.Y + _cellH - ViewHeight + 8);
        if (_tiles.TryGetValue(entry, out var parts)) Highlight(parts, true);
    }

    // ───────────────────────── Menu ─────────────────────────

    /// <summary>Set by the dock: pins a program file to it.</summary>
    public static Action<string, string>? PinToDock { get; set; }

    /// <summary>Set by the dock: whether a program file is already pinned.</summary>
    public static Func<string, bool>? IsPinned { get; set; }

    /// <summary>A right click on an icon: open it, as administrator, its file location, pin it, its category; on a folder: open or ungroup it.</summary>
    private void OnMenu(object sender, MouseButtonEventArgs e)
    {
        if (_drag is not null) return;
        var source = e.OriginalSource as DependencyObject;
        if (Ancestor<TextBox>(source, _ => true) is not null) return;
        var cell = Ancestor<FrameworkElement>(source, f => f.Tag is LaunchEntry or string && f is Grid);
        if (cell is null) return;
        var menu = new ContextMenu { PlacementTarget = cell, Placement = PlacementMode.MousePoint };
        void Add(string text, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = UiText.Get(text), IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        if (cell.Tag is LaunchEntry { IsFolder: true } folder)
        {
            Add("AppGridOpenFolder", () => OpenFolder(folder));
            if (_items.Contains(folder)) Add("AppGridUngroup", () => Ungroup(folder));
        }
        else if (((cell.Tag as LaunchEntry)?.App ?? (cell.Tag as string)) is { } id && _known.TryGetValue(id, out var app))
        {
            Add("AppGridOpen", () => Launch(id));
            if (app.Target is { } target)
            {
                Add("AppGridRunAsAdmin", () =>
                {
                    Dismiss();
                    app.LaunchAsAdministrator();
                    AppUsage.Record(id);
                });
                Add("AppGridShowLocation", () =>
                {
                    Dismiss();
                    app.ShowFileLocation();
                });
                if (PinToDock is { } pin)
                {
                    var pinned = IsPinned?.Invoke(target) == true;
                    Add(pinned ? "AppGridPinned" : "AppGridPin", () => pin(target, app.Name), !pinned);
                }
            }
            // The category is a guess from the app's name; it can be put right here.
            var current = AppCategories.Of(app);
            var category = new MenuItem { Header = L("Kategori", "Category") };
            foreach (var choice in AppCategories.Order)
            {
                var item = new MenuItem { Header = AppCategories.Name(choice), IsChecked = choice == current };
                item.Click += (_, _) =>
                {
                    AppCategories.Choose(app.Id, choice);
                    BuildChips();
                    Refill(keepScroll: true);
                };
                category.Items.Add(item);
            }
            menu.Items.Add(category);
        }
        else
        {
            return;
        }
        e.Handled = true;
        menu.IsOpen = true;
    }

    /// <summary>A folder's apps go back among the others.</summary>
    private void Ungroup(LaunchEntry folder)
    {
        var index = _items.IndexOf(folder);
        if (index < 0) return;
        if (ReferenceEquals(_openFolder, folder)) CloseFolder(quick: true);
        _items.RemoveAt(index);
        _items.InsertRange(index, folder.Apps!.Select(id => new LaunchEntry { App = id }));
        Forget(folder);
        Refill(keepScroll: true);
        Save();
    }

    private void Open(LaunchEntry entry)
    {
        if (entry.IsFolder) OpenFolder(entry);
        else Launch(entry.App!);
    }

    private void Launch(string id)
    {
        if (!_known.TryGetValue(id, out var app)) return;
        // After the press has fully ended (a finger on a touch screen too), so the window under it is
        // never pulled away mid-touch.
        Dispatcher.BeginInvoke(() =>
        {
            Dismiss();
            app.Launch();
            AppUsage.Record(id);
        }, DispatcherPriority.Input);
    }

    // ───────────────────────── Dragging ─────────────────────────

    private void StartDrag(Point point)
    {
        var press = _press!;
        if (press.Icon is not null) press.Icon.Opacity = 1;
        if (press.FolderApp is { } id)
        {
            var parts = _folderTiles[id];
            var origin = parts.Element.TranslatePoint(new Point(), Root);
            _folderOthers = _openFolder!.Apps!.Where(a => !string.Equals(a, id, StringComparison.OrdinalIgnoreCase)).ToList();
            _folderDropIndex = _openFolder.Apps!.FindIndex(a => string.Equals(a, id, StringComparison.OrdinalIgnoreCase));
            _drag = new Drag { Entry = new LaunchEntry { App = id }, Ghost = Ghost(new LaunchEntry { App = id }), Grab = point - origin, Folder = _openFolder };
            ArrangeFolder(false);
        }
        else
        {
            var entry = press.Entry!;
            var tile = _tiles[entry].Element;
            var origin = tile.TranslatePoint(new Point(), Root);
            _drag = new Drag { Entry = entry, Ghost = Ghost(entry), Grab = point - origin };
            // Its place stays, faint, until it lands somewhere.
            tile.Opacity = 0.25;
        }
        DragTo(point);
    }

    /// <summary>A copy of the tile that follows the pointer, a little larger, over everything else.</summary>
    private FrameworkElement Ghost(LaunchEntry entry)
    {
        var pending = new List<(Image, string)>();
        var parts = entry.IsFolder ? MakeTile(entry, pending) : MakeAppTile(entry.App!, pending);
        RequestIcons(pending);
        var ghost = parts.Element;
        ghost.Opacity = 0.95;
        ghost.RenderTransformOrigin = new Point(0.5, 0.45);
        var scale = new ScaleTransform(1, 1);
        ghost.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.12, TimeSpan.FromMilliseconds(140)) { EasingFunction = Motion.Out });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.12, TimeSpan.FromMilliseconds(140)) { EasingFunction = Motion.Out });
        _dragLayer.Children.Add(ghost);
        return ghost;
    }

    private void DragTo(Point point)
    {
        var drag = _drag!;
        Canvas.SetLeft(drag.Ghost, point.X - drag.Grab.X);
        Canvas.SetTop(drag.Ghost, point.Y - drag.Grab.Y);

        if (drag.Folder is not null)
        {
            if (InsideFolderPanel(point))
            {
                var slot = FolderSlotAt(point);
                if (slot != _folderDropIndex)
                {
                    _folderDropIndex = slot;
                    ArrangeFolder(true);
                }
                return;
            }
            LeaveFolder();
        }

        var inHost = Root.TranslatePoint(point, _host);
        var edge = inHost.Y < 48 ? -1 : inHost.Y > _host.ActualHeight - 48 ? 1 : 0;
        if (edge != _edgeDirection)
        {
            _edgeDirection = edge;
            _edgeTimer.Stop();
            if (edge != 0) _edgeTimer.Start();
        }

        // Over the middle of another icon (or a folder): a folder would be made there.
        var inStrip = Root.TranslatePoint(point, _strip);
        LaunchEntry? target = null;
        var iconMiddle = (_icon + 12 * _k) / 2;
        foreach (var entry in _order)
        {
            if (ReferenceEquals(entry, drag.Entry) || string.Equals(entry.App, drag.Entry.App, StringComparison.OrdinalIgnoreCase)) continue;
            var at = _slots[entry];
            if (Math.Abs(inStrip.X - (at.X + _cellW / 2)) < _icon * 0.5 && Math.Abs(inStrip.Y - (at.Y + iconMiddle)) < _icon * 0.5)
            {
                target = entry;
                break;
            }
        }
        SetFolderTarget(target);
    }

    private void SetFolderTarget(LaunchEntry? target)
    {
        if (ReferenceEquals(target, _folderTarget)) return;
        if (_folderTarget is not null && _tiles.TryGetValue(_folderTarget, out var old)) Highlight(old, false);
        _folderTarget = target;
        if (target is not null && _tiles.TryGetValue(target, out var parts)) Highlight(parts, true);
    }

    private static void Highlight(TileParts parts, bool on)
    {
        parts.Backdrop.Background = on ? TargetFill : Brushes.Transparent;
        var scale = (ScaleTransform)parts.Backdrop.RenderTransform;
        var to = on ? 1.0 : 0.8;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)) { EasingFunction = Motion.Out });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)) { EasingFunction = Motion.Out });
    }

    private void LetGo()
    {
        var drag = _drag!;
        _drag = null;
        _edgeTimer.Stop();
        _edgeDirection = 0;

        if (drag.Folder is not null)
        {
            // Let go inside the folder: the app takes its new place there.
            drag.Folder.Apps = [.. _folderOthers];
            drag.Folder.Apps.Insert(Math.Clamp(_folderDropIndex, 0, drag.Folder.Apps.Count), drag.Entry.App!);
            ArrangeFolder(true);
            Land(drag.Ghost, _folderTiles.TryGetValue(drag.Entry.App!, out var inFolder) ? inFolder.Element : null);
            Forget(drag.Folder);
            Refill(keepScroll: true);
            Save();
            return;
        }

        if (_folderTarget is { } target)
        {
            SetFolderTarget(null);
            // The most used row shows copies; the folder is made where the app itself stands.
            var real = target.IsFolder ? target
                : _items.FirstOrDefault(e => !e.IsFolder && string.Equals(e.App, target.App, StringComparison.OrdinalIgnoreCase))
                ?? _items.FirstOrDefault(e => e.IsFolder && e.Apps!.Contains(target.App!, StringComparer.OrdinalIgnoreCase))
                ?? target;
            _items.Remove(drag.Entry);
            Forget(drag.Entry);
            if (real.IsFolder)
            {
                real.Apps!.Add(drag.Entry.App!);
                Forget(real);
            }
            else
            {
                // App on app: the two become a folder (folders stand on top).
                var folder = new LaunchEntry { Folder = UiText.Get("AppGridFolder"), Apps = [real.App!, drag.Entry.App!] };
                var at = _items.IndexOf(real);
                if (at >= 0) _items[at] = folder;
                else _items.Add(folder);
                Forget(real);
                real = folder;
            }
            Refill(keepScroll: true);
            Land(drag.Ghost, _tiles.TryGetValue(real, out var into) ? into.Element : null, shrink: true);
            Save();
            return;
        }

        // Nowhere in particular: back to its place.
        Refill(keepScroll: true);
        Land(drag.Ghost, _tiles.TryGetValue(drag.Entry, out var placed) ? placed.Element : null);
    }

    /// <summary>The ghost glides into the tile's place (or shrinks into a folder) and the real tile shows again.</summary>
    private void Land(FrameworkElement ghost, FrameworkElement? tile, bool shrink = false)
    {
        if (tile is null)
        {
            _dragLayer.Children.Remove(ghost);
            return;
        }
        if (!shrink) tile.Opacity = 0;
        var at = new Point(Canvas.GetLeft(tile), Canvas.GetTop(tile));
        var target = tile.Parent is Canvas canvas ? canvas.TranslatePoint(at, Root) : at;
        var time = TimeSpan.FromMilliseconds(220);
        var left = new DoubleAnimation(target.X, time) { EasingFunction = Motion.Out };
        left.Completed += (_, _) =>
        {
            _dragLayer.Children.Remove(ghost);
            tile.Opacity = 1;
        };
        ghost.BeginAnimation(Canvas.LeftProperty, left);
        ghost.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(target.Y, time) { EasingFunction = Motion.Out });
        var scale = (ScaleTransform)ghost.RenderTransform;
        var size = shrink ? 0.3 : 1;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(size, time) { EasingFunction = Motion.Out });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(size, time) { EasingFunction = Motion.Out });
        if (shrink) ghost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, time));
    }

    /// <summary>Drops a tile so it is drawn again (a folder whose apps changed) or because it went out of view.</summary>
    private void Forget(LaunchEntry entry)
    {
        if (!_tiles.TryGetValue(entry, out var tile)) return;
        _strip.Children.Remove(tile.Element);
        _tiles.Remove(entry);
    }

    private void Save()
    {
        LaunchStore.Save(_items.Select(e => e.IsFolder ? new LaunchEntry { Folder = e.Folder, Apps = [.. e.Apps!] } : new LaunchEntry { App = e.App }).ToList());
    }

    // ───────────────────────── Folders ─────────────────────────

    private void OpenFolder(LaunchEntry folder)
    {
        Select(null);
        _openFolder = folder;
        _folderLayer.Children.Clear();
        _folderTiles.Clear();

        var dim = new Border { Background = Ui.Solid("#59000000") };
        dim.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            CloseFolder();
        };
        _folderLayer.Children.Add(dim);

        var name = new TextBox
        {
            Text = NameOf(folder),
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ui.White,
            CaretBrush = Ui.White,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 18),
            MaxLength = 40
        };
        void Rename()
        {
            var text = name.Text.Trim();
            if (text.Length == 0 || text == NameOf(folder)) return;
            folder.Folder = text;
            Forget(folder);
            Realize();
            Save();
        }
        name.LostKeyboardFocus += (_, _) => Rename();
        name.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Rename();
            _box.Focus();
        };

        _folderCols = Math.Clamp(folder.Apps!.Count, 3, Math.Min(6, (int)_cols));
        _folderCanvas = new Canvas { Width = _folderCols * _cellW, Background = Brushes.Transparent };
        var scroll = new ScrollViewer { Content = _folderCanvas, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, MaxHeight = _cellH * 3.5, Focusable = false };
        var column = new StackPanel();
        column.Children.Add(name);
        column.Children.Add(scroll);
        _folderPanel = new Border
        {
            Child = column,
            Background = Ui.Solid("#E62A2C30"),
            BorderBrush = FolderEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(30 * _k),
            Padding = new Thickness(24, 22, 24, 18),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.86, 0.86),
            Opacity = 0
        };
        _folderPanel.MouseLeftButtonUp += (_, e) => e.Handled = true;
        _folderLayer.Children.Add(_folderPanel);
        ArrangeFolder(false);
        _folderLayer.Visibility = Visibility.Visible;
        // Typing would otherwise go on filtering the panel behind the folder.
        _folderPanel.Focusable = true;
        _folderPanel.FocusVisualStyle = null;
        Dispatcher.BeginInvoke(() => _folderPanel?.Focus(), DispatcherPriority.Loaded);

        var time = TimeSpan.FromMilliseconds(240);
        var scale = (ScaleTransform)_folderPanel.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, time) { EasingFunction = Motion.Out });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, time) { EasingFunction = Motion.Out });
        _folderPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, time) { EasingFunction = Motion.Out });
        _panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, time));
    }

    private void CloseFolder(bool quick = false)
    {
        if (_openFolder is null || _folderPanel is null) return;
        _openFolder = null;
        var panel = _folderPanel;
        _folderPanel = null;
        _folderCanvas = null;
        Keyboard.Focus(_box);
        var time = TimeSpan.FromMilliseconds(quick ? 120 : 180);
        var fade = new DoubleAnimation(0, time) { EasingFunction = Motion.In };
        fade.Completed += (_, _) =>
        {
            if (_openFolder is not null) return;
            _folderLayer.Visibility = Visibility.Collapsed;
            _folderLayer.Children.Clear();
            _folderTiles.Clear();
        };
        var scale = (ScaleTransform)panel.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, time) { EasingFunction = Motion.In });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, time) { EasingFunction = Motion.In });
        panel.BeginAnimation(OpacityProperty, fade);
        _folderLayer.Children[0].BeginAnimation(OpacityProperty, new DoubleAnimation(0, time));
        _panel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, time));
    }

    /// <summary>Lays out the apps of the open folder; while one is dragged, a gap stands where it would land.</summary>
    private void ArrangeFolder(bool animate)
    {
        if (_openFolder is null || _folderCanvas is null) return;
        var dragging = _drag?.Folder is not null ? _drag.Entry.App : null;
        var list = new List<string?>();
        if (dragging is null) list.AddRange(_openFolder.Apps!);
        else
        {
            list.AddRange(_folderOthers);
            list.Insert(Math.Clamp(_folderDropIndex, 0, list.Count), null);
        }
        foreach (var gone in _folderTiles.Keys.Where(k => !list.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            _folderCanvas.Children.Remove(_folderTiles[gone].Element);
            _folderTiles.Remove(gone);
        }
        var pending = new List<(Image, string)>();
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is not { } id) continue;
            var to = new Point(i % _folderCols * _cellW, i / _folderCols * _cellH);
            if (!_folderTiles.TryGetValue(id, out var tile))
            {
                tile = MakeAppTile(id, pending);
                _folderTiles[id] = tile;
                _folderCanvas.Children.Add(tile.Element);
                Canvas.SetLeft(tile.Element, to.X);
                Canvas.SetTop(tile.Element, to.Y);
                continue;
            }
            var from = new Point(Canvas.GetLeft(tile.Element), Canvas.GetTop(tile.Element));
            if (from == to) continue;
            Canvas.SetLeft(tile.Element, to.X);
            Canvas.SetTop(tile.Element, to.Y);
            if (!animate) continue;
            var shift = Motion.Shift(tile.Element);
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from.X - to.X + shift.X, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = Motion.Out });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from.Y - to.Y + shift.Y, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = Motion.Out });
        }
        RequestIcons(pending);
        _folderCanvas.Height = Math.Max(1, (list.Count + _folderCols - 1) / _folderCols) * _cellH;
    }

    private bool InsideFolderPanel(Point point)
    {
        if (_folderPanel is null) return false;
        var origin = _folderPanel.TranslatePoint(new Point(), Root);
        return new Rect(origin, new Size(_folderPanel.ActualWidth, _folderPanel.ActualHeight)).Contains(point);
    }

    private int FolderSlotAt(Point point)
    {
        var origin = _folderCanvas!.TranslatePoint(new Point(), Root);
        var col = Math.Clamp((int)Math.Floor((point.X - origin.X) / _cellW), 0, _folderCols - 1);
        var row = Math.Max(0, (int)Math.Floor((point.Y - origin.Y) / _cellH));
        return Math.Min(row * _folderCols + col, _folderOthers.Count);
    }

    /// <summary>
    /// The dragged app left the folder panel: it comes out of the folder (back among the other apps), the folder
    /// closes and the drag carries on over the panel, where it can still be dropped on another icon.
    /// </summary>
    private void LeaveFolder()
    {
        var drag = _drag!;
        var folder = drag.Folder!;
        drag.Folder = null;
        folder.Apps = [.. _folderOthers];
        var at = _items.IndexOf(folder);
        Forget(folder);
        if (folder.Apps.Count == 1 && at >= 0)
        {
            // A folder of one is just that app again.
            _items[at] = new LaunchEntry { App = folder.Apps[0] };
        }
        _items.Insert(at < 0 ? _items.Count : at + 1, drag.Entry);
        CloseFolder(quick: true);
        Refill(keepScroll: true);
        if (_tiles.TryGetValue(drag.Entry, out var placed)) placed.Element.Opacity = 0.25;
        Save();
    }
}
