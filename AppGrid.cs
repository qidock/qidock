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
/// Every installed app on pages of large icons over the blurred desktop, like a phone's home
/// screen: swipe, scroll or use the arrow keys to turn pages, type to search, drag an icon to move
/// it and drop it on another to make a folder. The arrangement is remembered.
/// </summary>
public sealed class AppGridWindow : OverlayWindow
{
    private const double SearchTop = 56;
    private const double DragThreshold = 6;
    // Without the blur the screen is darkened almost fully; over the blur a light shade is enough.
    private static readonly Brush Dim = Ui.Solid("#F00E0F13");
    private static readonly Brush FrostedDim = Ui.Solid("#2E000000");
    private static readonly Brush FolderFill = Ui.Solid("#38FFFFFF");
    private static readonly Brush FolderEdge = Ui.Solid("#2EFFFFFF");
    private static readonly Brush TargetFill = Ui.Solid("#4DFFFFFF");
    private static readonly Brush DotOn = Ui.Solid("#F2FFFFFF");
    private static readonly Brush DotOff = Ui.Solid("#59FFFFFF");
    // Icons stay cached between openings (about 64 KB each) so the pages fill at once next time.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);

    private sealed class TileParts
    {
        public required FrameworkElement Element;
        public required FrameworkElement IconHost;
        public required Border Backdrop;
    }

    /// <summary>A press on a tile or on the empty page, until the button goes up.</summary>
    private sealed class Press
    {
        public Point Start;
        public LaunchEntry? Entry;          // a tile on the pages
        public string? FolderApp;           // an app tile inside the open folder
        public FrameworkElement? Icon;
        public bool Swiping;
        public bool Fixed;                  // a tile in the row of most used apps: opens, never moves
        public double SwipeFrom;
    }

    /// <summary>An icon being dragged.</summary>
    private sealed class Drag
    {
        public required LaunchEntry Entry;  // what moves on the pages (an app or a folder)
        public required FrameworkElement Ghost;
        public Vector Grab;
        public LaunchEntry? Folder;         // set while the app is still inside the open folder
    }

    private readonly double _cols, _rows, _cellW, _cellH, _icon, _gridLeft, _gridTop;
    private readonly Grid _host = new() { ClipToBounds = true };
    private readonly Canvas _strip = new() { Background = Brushes.Transparent };
    private readonly TranslateTransform _stripShift = new();
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Grid _folderLayer = new() { Visibility = Visibility.Collapsed };
    private readonly Canvas _dragLayer = new() { IsHitTestVisible = false };
    private readonly TextBlock _note = new() { Foreground = Ui.WhiteSoft, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly TextBox _box;
    private readonly Dictionary<string, InstalledApp> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<LaunchEntry, TileParts> _tiles = [];
    private readonly Dictionary<string, LaunchEntry> _searchEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _edgeTimer;
    private readonly Queue<(Image Image, string Id)> _iconQueue = new();
    private bool _iconLoaderRunning;
    private bool _closed;

    private List<LaunchEntry> _items = [];
    private List<LaunchEntry> _shown = [];   // what the pages show now (the search results while searching)
    private List<LaunchEntry> _others = [];  // the pages without the dragged icon
    private List<LaunchEntry> _frequent = []; // the most used apps, a row at the top of the first page
    private readonly Border _shelf = new() { CornerRadius = new CornerRadius(22), Background = Ui.Solid("#14FFFFFF"), BorderBrush = Ui.Solid("#1AFFFFFF"), BorderThickness = new Thickness(1), IsHitTestVisible = false };
    private readonly TextBlock _shelfName = new() { Text = UiText.Get("AppGridFrequent"), Foreground = Ui.Solid("#B3FFFFFF"), FontSize = 12, FontWeight = FontWeights.SemiBold, IsHitTestVisible = false };
    private int _dropIndex;
    private LaunchEntry? _folderTarget;
    private LaunchEntry? _selected;         // the icon the arrow keys are on
    private int _page;
    private int _pageCount = 1;
    private (int From, int To) _livePages;  // the pages that have tiles
    private bool _neighborsReady;           // false while opening: only the first page is made
    private Press? _press;
    private Drag? _drag;
    private Point _lastPoint;
    private int _edgeDirection;
    private double _wheel;
    private DateTime _wheelLock;
    private DateTime _wheelLast;

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
    /// Loads the app list and the icons of the first pages quietly in the background (some time
    /// after QiDock starts), so the first opening already shows every icon instead of filling in.
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
                foreach (var id in ids) Icons.GetOrAdd(id, key => NotificationApps.LoadIcon(key, 128));
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "AppGrid icon prewarm" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private AppGridWindow() : base(Dim)
    {
        // Pages of up to 7 × 5 icons, fewer on small screens; the grid sits between the search field and the page dots.
        const double top = 120, bottom = 110;
        var usable = Height - top - bottom;
        _cols = Math.Clamp((int)((Width - 240) / 140), 4, 7);
        _rows = Math.Clamp((int)(usable / 128), 3, 5);
        _cellW = Math.Min(156, (Width - 240) / _cols);
        _cellH = Math.Min(132, usable / _rows);
        // Icons are loaded at 128 pixels: never drawn larger than that on screen, so they stay sharp.
        var scale = Application.Current?.MainWindow is { } main && PresentationSource.FromVisual(main)?.CompositionTarget is { } target ? target.TransformToDevice.M11 : 1.5;
        _icon = Math.Floor(Math.Min(Math.Clamp(Math.Min(_cellW, _cellH) * 0.46, 44, 60), 128 / scale));
        _gridLeft = (Width - _cols * _cellW) / 2;
        _gridTop = top + (usable - _rows * _cellH) / 2;

        _strip.RenderTransform = _stripShift;
        _strip.Height = Height;
        _host.Children.Add(_strip);
        _host.Children.Add(_note);
        Root.Children.Add(_host);

        var (field, box) = SearchField(UiText.Get("AppGridSearch"), 15, Ui.White, Ui.Solid("#A6FFFFFF"), Ui.Solid("#33FFFFFF"));
        _box = box;
        field.Width = 300;
        field.HorizontalAlignment = HorizontalAlignment.Center;
        field.VerticalAlignment = VerticalAlignment.Top;
        field.Margin = new Thickness(0, SearchTop, 0, 0);
        box.TextChanged += (_, _) =>
        {
            _page = 0;
            Refill();
            SnapStrip();
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || (_shown.Count == 0 && _selected is null)) return;
            e.Handled = true;
            Open(_selected ?? _shown[0]);
        };
        Root.Children.Add(field);

        _dots.Margin = new Thickness(0, 0, 0, Math.Max(40, Height - (_gridTop + _rows * _cellH) - 64));
        Root.Children.Add(_dots);
        Root.Children.Add(_folderLayer);
        Root.Children.Add(_dragLayer);

        _edgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        _edgeTimer.Tick += (_, _) => FlipWhileDragging();

        PreviewMouseLeftButtonDown += OnPressDown;
        PreviewMouseMove += OnPressMove;
        PreviewMouseLeftButtonUp += OnPressUp;
        LostMouseCapture += (_, _) => { if (_press is not null && !Root.IsMouseCaptured) CancelPress(); };
        PreviewMouseWheel += (_, e) =>
        {
            // In an open folder the wheel scrolls the folder's own list.
            if (_openFolder is not null) return;
            e.Handled = true;
            Wheel(-e.Delta);
        };
        PreviewKeyDown += OnKey;
        PreviewMouseRightButtonUp += OnMenu;
        Closed += (_, _) => _closed = true;

        Load(AppList.Cached ?? []);
        AppList.RefreshAsync().ContinueWith(t =>
        {
            // Rebuilding the pages takes a moment, so only when the list really changed (and nothing is in hand).
            if (_closed || _drag is not null || _openFolder is not null) return;
            if (t.Result.Count == _known.Count && t.Result.All(a => _known.ContainsKey(a.Id))) return;
            Load(t.Result);
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
    }

    protected override bool Frosted => true;

    protected override bool IsBackground(object source) => ReferenceEquals(source, _strip) || ReferenceEquals(source, _host);

    protected override void OnShown() => _box.Focus();

    /// <summary>
    /// The page settles from slightly large to its size. It moves as one cached picture (so the
    /// graphics card only scales it), and the pages beside it are made once it has stopped.
    /// </summary>
    protected override void OnPresented()
    {
        var scale = new ScaleTransform(1.06, 1.06, Width / 2, Height / 2);
        _host.RenderTransform = scale;
        _host.CacheMode = new BitmapCache { SnapsToDevicePixels = true };
        var time = TimeSpan.FromMilliseconds(280);
        var grow = new DoubleAnimation(1, time) { EasingFunction = Motion.Out };
        grow.Completed += (_, _) =>
        {
            _host.CacheMode = null;
            _host.RenderTransform = Transform.Identity;
            if (_closed) return;
            _neighborsReady = true;
            Dispatcher.BeginInvoke(() => { if (!_closed && _drag is null) Arrange(false); }, DispatcherPriority.Background);
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, time) { EasingFunction = Motion.Out });
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
        return false;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (IsFrosted) Root.Background = FrostedDim;
        // Sideways scrolling (a tilting wheel or two fingers on a touchpad) turns pages too.
        (PresentationSource.FromVisual(this) as HwndSource)?.AddHook((nint _, int message, nint wParam, nint _, ref bool handled) =>
        {
            const int wmMouseHWheel = 0x020E;
            if (message != wmMouseHWheel) return 0;
            Wheel((short)((wParam.ToInt64() >> 16) & 0xFFFF));
            handled = true;
            return 0;
        });
    }

    // ───────────────────────── Content ─────────────────────────

    private void Load(IReadOnlyList<InstalledApp> apps)
    {
        _known.Clear();
        foreach (var app in apps) _known.TryAdd(app.Id, app);
        _items = LaunchStore.Merge(LaunchStore.Load(), apps);
        // The most used apps open the first page, on a row of their own; the arrangement below stays as the user left it.
        var scores = AppUsage.Scores();
        _frequent = apps
            .Where(a => scores.ContainsKey(a.Id))
            .OrderByDescending(a => scores[a.Id])
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take((int)_cols)
            .Select(a => new LaunchEntry { App = a.Id })
            .ToList();
        if (_frequent.Count < 3) _frequent = [];
        foreach (var tile in _tiles.Values) _strip.Children.Remove(tile.Element);
        _tiles.Clear();
        Refill();
        SnapStrip();
    }

    /// <summary>The slots the row of most used apps takes at the start of the first page (none while searching).</summary>
    private int Lead => Searching || _frequent.Count == 0 ? 0 : (int)_cols;

    private bool Searching => _box.Text.Trim().Length > 0;

    /// <summary>Works out what the pages show (everything, or what matches the search) and lays it out.</summary>
    private void Refill()
    {
        Select(null);
        var query = _box.Text.Trim();
        if (query.Length == 0)
        {
            _shown = _items;
        }
        else
        {
            // Apps inside folders are found too, in the order they sit on the pages.
            _shown = _items
                .SelectMany(e => e.IsFolder ? e.Apps! : [e.App!])
                .Where(id => _known.TryGetValue(id, out var app) && Matches(app.Name, query))
                .Select(id => _searchEntries.TryGetValue(id, out var known) ? known : _searchEntries[id] = new LaunchEntry { App = id })
                .ToList();
        }
        _note.Text = _known.Count == 0 ? UiText.Get("AppGridLoading") : _shown.Count == 0 ? UiText.Get("AppGridNoMatch") : "";
        Arrange(false);
    }

    /// <summary>
    /// The icons in the order they stand on the pages: first the row of most used apps, then the
    /// arrangement. Null is an empty slot (the rest of that row, or where a dragged icon would land).
    /// </summary>
    private List<LaunchEntry?> Display()
    {
        var lead = Lead;
        var list = new List<LaunchEntry?>(lead + _shown.Count + 1);
        if (lead > 0)
        {
            list.AddRange(_frequent);
            while (list.Count < lead) list.Add(null);
        }
        if (_drag is null || _drag.Folder is not null)
        {
            list.AddRange(_shown);
            return list;
        }
        var rest = new List<LaunchEntry?>(_others);
        rest.Insert(Math.Clamp(_dropIndex, 0, rest.Count), null);
        list.AddRange(rest);
        return list;
    }

    private int PerPage => (int)(_cols * _rows);

    private Point Slot(int index)
    {
        var page = index / PerPage;
        var at = index % PerPage;
        return new Point(page * Width + _gridLeft + at % (int)_cols * _cellW, _gridTop + at / (int)_cols * _cellH);
    }

    /// <summary>
    /// Puts the icons of the shown page and the pages beside it in their places (the other pages
    /// have no tiles until they come near). Tiles are kept between calls, so with
    /// <paramref name="animate"/> the ones whose place changed glide there.
    /// </summary>
    private void Arrange(bool animate)
    {
        var list = Display();
        _pageCount = Math.Max(1, (list.Count + PerPage - 1) / PerPage);
        _strip.Width = _pageCount * Width;
        if (_page >= _pageCount)
        {
            _page = _pageCount - 1;
            SnapStrip();
        }
        _livePages = (_neighborsReady ? Math.Max(0, _page - 1) : _page, _neighborsReady ? Math.Min(_pageCount - 1, _page + 1) : _page);
        var first = _livePages.From * PerPage;
        var last = Math.Min(list.Count, (_livePages.To + 1) * PerPage);
        var wanted = new HashSet<LaunchEntry>();
        for (var i = first; i < last; i++)
            if (list[i] is { } entry) wanted.Add(entry);
        foreach (var gone in _tiles.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            _strip.Children.Remove(_tiles[gone].Element);
            _tiles.Remove(gone);
        }
        var pending = new List<(Image, string)>();
        for (var i = first; i < last; i++)
        {
            if (list[i] is not { } entry) continue;
            var to = Slot(i);
            if (!_tiles.TryGetValue(entry, out var tile))
            {
                tile = MakeTile(entry, pending);
                if (ReferenceEquals(entry, _selected)) Highlight(tile, true);
                _tiles[entry] = tile;
                _strip.Children.Add(tile.Element);
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
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from.X - to.X + shift.X, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = Motion.Out });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from.Y - to.Y + shift.Y, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = Motion.Out });
        }
        RequestIcons(pending);
        PlaceShelf();
        BuildDots();
    }

    /// <summary>A soft band behind the row of most used apps, with its name above it.</summary>
    private void PlaceShelf()
    {
        var show = Lead > 0;
        if (show && _shelf.Parent is null)
        {
            _strip.Children.Insert(0, _shelf);
            _strip.Children.Insert(0, _shelfName);
        }
        _shelf.Visibility = _shelfName.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        _shelf.Width = _cols * _cellW + 16;
        _shelf.Height = _cellH - 4;
        Canvas.SetLeft(_shelf, _gridLeft - 8);
        Canvas.SetTop(_shelf, _gridTop + 2);
        Canvas.SetLeft(_shelfName, _gridLeft + 6);
        Canvas.SetTop(_shelfName, _gridTop - 22);
    }

    private void GoTo(int page, bool animate = true)
    {
        _page = Math.Clamp(page, 0, _pageCount - 1);
        _neighborsReady = true;
        PaintDots();
        if (!animate)
        {
            SnapStrip();
            Arrange(false);
            return;
        }
        // The page arrives with its tiles already made (it was beside the old one); the pages
        // beside it are made after the slide, so making them never holds the movement up.
        if (_page < _livePages.From || _page > _livePages.To) Arrange(false);
        var slide = new DoubleAnimation(-_page * Width, TimeSpan.FromMilliseconds(360)) { EasingFunction = Motion.Out };
        slide.Completed += (_, _) =>
        {
            if (!_closed && _drag is null && _press is null) Arrange(false);
        };
        _stripShift.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    /// <summary>Shows the current page at once, without sliding.</summary>
    private void SnapStrip()
    {
        _stripShift.BeginAnimation(TranslateTransform.XProperty, null);
        _stripShift.X = -_page * Width;
        PaintDots();
    }

    private void BuildDots()
    {
        if (_dots.Children.Count == _pageCount)
        {
            PaintDots();
            return;
        }
        _dots.Children.Clear();
        for (var i = 0; i < _pageCount; i++)
        {
            var page = i;
            var dot = new Ellipse { Width = 8, Height = 8 };
            // A wider transparent target than the dot itself, so it is easy to hit.
            var hit = new Border { Child = dot, Padding = new Thickness(6), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            hit.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                GoTo(page);
            };
            _dots.Children.Add(hit);
        }
        _dots.Visibility = _pageCount > 1 ? Visibility.Visible : Visibility.Hidden;
        PaintDots();
    }

    private void PaintDots()
    {
        for (var i = 0; i < _dots.Children.Count; i++)
        {
            if (_dots.Children[i] is Border { Child: Ellipse dot }) dot.Fill = i == _page ? DotOn : DotOff;
        }
    }

    // ───────────────────────── Tiles ─────────────────────────

    private string NameOf(LaunchEntry entry) =>
        entry.IsFolder ? (string.IsNullOrWhiteSpace(entry.Folder) ? UiText.Get("AppGridFolder") : entry.Folder!)
        : _known.TryGetValue(entry.App!, out var app) ? app.Name : "";

    private TileParts MakeTile(LaunchEntry entry, List<(Image, string)> pending)
    {
        FrameworkElement icon;
        if (entry.IsFolder)
        {
            // A frosted rounded square showing the first nine apps.
            var minis = new UniformGrid { Rows = 3, Columns = 3, Margin = new Thickness(_icon * 0.1) };
            foreach (var id in entry.Apps!.Take(9))
            {
                var mini = new Image { Width = _icon * 0.22, Height = _icon * 0.22, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(mini, BitmapScalingMode.HighQuality);
                pending.Add((mini, id));
                minis.Children.Add(mini);
            }
            icon = new Border { Width = _icon, Height = _icon, CornerRadius = new CornerRadius(_icon * 0.24), Background = FolderFill, BorderBrush = FolderEdge, BorderThickness = new Thickness(1), Child = minis };
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
        return Assemble(image, _known.TryGetValue(id, out var app) ? app.Name : "", id);
    }

    /// <summary>The icon over its name, in a cell of the grid. No per-icon shadows: a hundred of them made the screen stutter.</summary>
    private TileParts Assemble(FrameworkElement icon, string name, object tag)
    {
        var backdrop = new Border { CornerRadius = new CornerRadius(_icon * 0.3), Background = Brushes.Transparent, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.8, 0.8) };
        var iconHost = new Grid { Width = _icon * 1.24, Height = _icon * 1.24, HorizontalAlignment = HorizontalAlignment.Center };
        iconHost.Children.Add(backdrop);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        iconHost.Children.Add(icon);
        var label = new TextBlock
        {
            Text = name,
            Foreground = Ui.White,
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = _cellW - 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0)
        };
        // Only the icon and its name take clicks; the gaps between icons are the page, so a click there closes the screen.
        var column = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Background = Brushes.Transparent, Cursor = Cursors.Hand };
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
                batch.Add((image, Icons.GetOrAdd(id, key => NotificationApps.LoadIcon(key, 128))));
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
        Select(null);
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
            _press = new Press { Start = point, Entry = entry, Icon = parts.IconHost, Fixed = _frequent.Contains(entry) };
        }
        else if (ReferenceEquals(source, _strip) || ReferenceEquals(source, _host))
        {
            _press = new Press { Start = point };
        }
        else
        {
            return;
        }
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
        var moved = point - _press.Start;
        if (_press.Swiping)
        {
            _stripShift.X = _press.SwipeFrom + moved.X;
            return;
        }
        if (moved.Length < DragThreshold) return;
        if (_press.Entry is not null || _press.FolderApp is not null)
        {
            // While searching the results are a view, not the arrangement, so icons stay put.
            if (Searching || _press.Fixed) return;
            StartDrag(point);
        }
        else if (Math.Abs(moved.X) > Math.Abs(moved.Y))
        {
            _press.Swiping = true;
            var current = _stripShift.X;
            _stripShift.BeginAnimation(TranslateTransform.XProperty, null);
            _stripShift.X = current;
            _press.SwipeFrom = current - moved.X;
        }
    }

    private void OnPressUp(object sender, MouseButtonEventArgs e)
    {
        var press = _press;
        if (press is null) return;
        _press = null;
        if (Root.IsMouseCaptured) Root.ReleaseMouseCapture();
        if (press.Icon is not null) press.Icon.Opacity = 1;
        if (_drag is not null)
        {
            e.Handled = true;
            LetGo();
            return;
        }
        if (press.Swiping)
        {
            e.Handled = true;
            var moved = e.GetPosition(Root).X - press.Start.X;
            GoTo(Math.Abs(moved) > Width * 0.12 ? _page - Math.Sign(moved) : _page);
            return;
        }
        if (press.Entry is not null)
        {
            e.Handled = true;
            Open(press.Entry);
        }
        else if (press.FolderApp is not null)
        {
            e.Handled = true;
            Launch(press.FolderApp);
        }
        else if ((e.GetPosition(Root) - press.Start).Length >= DragThreshold)
        {
            // An up-and-down drag on the empty page is not a click.
            e.Handled = true;
        }
        // A plain click on the empty page goes on to the overlay, which closes.
    }

    private void CancelPress()
    {
        if (_press?.Icon is not null) _press.Icon.Opacity = 1;
        var swiping = _press?.Swiping == true;
        _press = null;
        if (_drag is not null) LetGo();
        else if (swiping) GoTo(_page);
    }

    private void Wheel(double towardsNext)
    {
        if (_openFolder is not null || _drag is not null) return;
        var now = DateTime.UtcNow;
        // A touchpad sends a stream of small steps that keeps going after the fingers lift:
        // one page per gesture, then wait for the stream to stop.
        if (now < _wheelLock)
        {
            if (now - _wheelLast < TimeSpan.FromMilliseconds(120)) _wheelLock = Min(now.AddMilliseconds(120), _wheelLock.AddMilliseconds(60));
            _wheelLast = now;
            return;
        }
        if (now - _wheelLast > TimeSpan.FromMilliseconds(400)) _wheel = 0;
        _wheelLast = now;
        _wheel += towardsNext;
        if (Math.Abs(_wheel) < 100) return;
        var step = Math.Sign(_wheel);
        _wheel = 0;
        _wheelLock = now.AddMilliseconds(380);
        if (_page + step >= 0 && _page + step < _pageCount) GoTo(_page + step);
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    /// <summary>
    /// The arrow keys move a highlight over the icons (left and right only while the search field is
    /// empty, so they still move the cursor in typed text) and Enter opens it; Page Up / Down turn pages.
    /// </summary>
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_openFolder is not null || _drag is not null) return;
        var typing = _box.Text.Length > 0;
        var move = e.Key switch
        {
            Key.Left when !typing => -1,
            Key.Right when !typing => 1,
            Key.Up => -(int)_cols,
            Key.Down => (int)_cols,
            _ => 0
        };
        if (move != 0)
        {
            e.Handled = true;
            MoveSelection(move);
            return;
        }
        var step = e.Key switch
        {
            Key.PageDown => 1,
            Key.PageUp => -1,
            _ => 0
        };
        if (step == 0) return;
        e.Handled = true;
        GoTo(_page + step);
    }

    private void MoveSelection(int move)
    {
        var list = Display();
        if (list.Count == 0) return;
        var at = _selected is null ? -1 : list.IndexOf(_selected);
        // The first press picks the first icon of the page in view.
        var index = at < 0 ? Math.Min(_page * PerPage, list.Count - 1) : Math.Clamp(at + move, 0, list.Count - 1);
        // Empty slots (the end of the row of most used apps) are stepped over.
        var direction = move < 0 ? -1 : 1;
        var found = index;
        while (found >= 0 && found < list.Count && list[found] is null) found += direction;
        if (found < 0 || found >= list.Count)
        {
            found = index;
            while (found >= 0 && found < list.Count && list[found] is null) found -= direction;
        }
        if (found < 0 || found >= list.Count || list[found] is not { } entry) return;
        Select(entry, found / PerPage);
    }

    /// <summary>Highlights the icon the keyboard is on (turning to its page), or none.</summary>
    private void Select(LaunchEntry? entry, int page = -1)
    {
        if (_selected is not null && !ReferenceEquals(_selected, entry) && !ReferenceEquals(_selected, _folderTarget)
            && _tiles.TryGetValue(_selected, out var old))
            Highlight(old, false);
        _selected = entry;
        if (entry is null) return;
        if (page >= 0 && page != _page) GoTo(page);
        if (_tiles.TryGetValue(entry, out var parts)) Highlight(parts, true);
    }

    // ───────────────────────── Menu ─────────────────────────

    /// <summary>Set by the dock: pins a program file to it.</summary>
    public static Action<string, string>? PinToDock { get; set; }

    /// <summary>Set by the dock: whether a program file is already pinned.</summary>
    public static Func<string, bool>? IsPinned { get; set; }

    /// <summary>A right click on an icon: open it, as administrator, its file location, pin it; on a folder: open or ungroup it.</summary>
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
            if (!Searching && _items.Contains(folder)) Add("AppGridUngroup", () => Ungroup(folder));
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
        }
        else
        {
            return;
        }
        e.Handled = true;
        menu.IsOpen = true;
    }

    /// <summary>A folder's apps go back onto the pages where the folder was.</summary>
    private void Ungroup(LaunchEntry folder)
    {
        var index = _items.IndexOf(folder);
        if (index < 0) return;
        if (ReferenceEquals(_openFolder, folder)) CloseFolder(quick: true);
        _items.RemoveAt(index);
        _items.InsertRange(index, folder.Apps!.Select(id => new LaunchEntry { App = id }));
        Forget(folder);
        Refill();
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
            var origin = _tiles[entry].Element.TranslatePoint(new Point(), Root);
            _others = _items.Where(x => !ReferenceEquals(x, entry)).ToList();
            _dropIndex = _items.IndexOf(entry);
            _drag = new Drag { Entry = entry, Ghost = Ghost(entry), Grab = point - origin };
            Arrange(false);
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

        // Holding the icon at the left or right edge turns the page.
        var edge = point.X < 36 ? -1 : point.X > Width - 36 ? 1 : 0;
        if (edge != _edgeDirection)
        {
            _edgeDirection = edge;
            _edgeTimer.Stop();
            if (edge != 0) _edgeTimer.Start();
        }

        var gx = point.X - _gridLeft;
        var gy = point.Y - _gridTop;
        var col = Math.Clamp((int)Math.Floor(gx / _cellW), 0, (int)_cols - 1);
        var row = Math.Clamp((int)Math.Floor(gy / _cellH), 0, (int)_rows - 1);
        // The row of most used apps is not a place to drop on.
        var slotIndex = Math.Max(Lead, _page * PerPage + row * (int)_cols + col);
        var display = Display();
        LaunchEntry? target = null;
        if (gx >= 0 && gx < _cols * _cellW && gy >= 0 && gy < _rows * _cellH && slotIndex < display.Count && display[slotIndex] is { } under && !drag.Entry.IsFolder)
        {
            // Over the middle of another icon: a folder would be made there.
            var iconTop = (_cellH - (_icon * 1.24 + 20)) / 2;
            var cx = (col + 0.5) * _cellW;
            var cy = row * _cellH + iconTop + _icon * 0.62;
            if (Math.Abs(gx - cx) < _icon * 0.42 && Math.Abs(gy - cy) < _icon * 0.42) target = under;
        }
        SetFolderTarget(target);
        if (target is not null) return;
        var index = Math.Min(slotIndex - Lead, _others.Count);
        if (index == _dropIndex) return;
        _dropIndex = index;
        Arrange(true);
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

    private void FlipWhileDragging()
    {
        if (_drag is null || _edgeDirection == 0)
        {
            _edgeTimer.Stop();
            return;
        }
        var next = _page + _edgeDirection;
        if (next < 0 || next >= _pageCount) return;
        GoTo(next);
        DragTo(_lastPoint);
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
            Arrange(false);
            Save();
            return;
        }

        if (_folderTarget is { } target)
        {
            SetFolderTarget(null);
            if (target.IsFolder)
            {
                target.Apps!.Add(drag.Entry.App!);
            }
            else
            {
                // App on app: the two become a folder where the one underneath was.
                var folder = new LaunchEntry { Folder = UiText.Get("AppGridFolder"), Apps = [target.App!, drag.Entry.App!] };
                _others[_others.IndexOf(target)] = folder;
                target = folder;
            }
            _items = _others;
            Forget(target);
            Arrange(true);
            Land(drag.Ghost, _tiles.TryGetValue(target, out var into) ? into.Element : null, shrink: true);
            Save();
            return;
        }

        _items = [.. _others];
        _items.Insert(Math.Clamp(_dropIndex, 0, _items.Count), drag.Entry);
        Refill();
        Land(drag.Ghost, _tiles.TryGetValue(drag.Entry, out var placed) ? placed.Element : null);
        Save();
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
        // The new tile is not laid out yet, so its place comes from its canvas position:
        // on the pages, where the page will stop sliding; in a folder, inside the folder's grid.
        var at = new Point(Canvas.GetLeft(tile), Canvas.GetTop(tile));
        var target = ReferenceEquals(tile.Parent, _strip)
            ? new Point(at.X - _page * Width, at.Y)
            : tile.Parent is Canvas grid ? grid.TranslatePoint(at, Root) : at;
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

    /// <summary>Drops a tile so it is drawn again (a folder whose apps changed).</summary>
    private void Forget(LaunchEntry entry)
    {
        if (!_tiles.TryGetValue(entry, out var tile)) return;
        _strip.Children.Remove(tile.Element);
        _tiles.Remove(entry);
    }

    private void Save()
    {
        // The search results are a view; only the real arrangement is written.
        LaunchStore.Save(_items.Select(e => e.IsFolder ? new LaunchEntry { Folder = e.Folder, Apps = [.. e.Apps!] } : new LaunchEntry { App = e.App }).ToList());
    }

    // ───────────────────────── Folders ─────────────────────────

    private void OpenFolder(LaunchEntry folder)
    {
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
            Arrange(false);
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
            Background = Ui.Solid("#C2303236"),
            BorderBrush = FolderEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(30),
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
        // Typing would otherwise go on filtering the pages behind the folder.
        _folderPanel.Focusable = true;
        _folderPanel.FocusVisualStyle = null;
        Dispatcher.BeginInvoke(() => _folderPanel?.Focus(), DispatcherPriority.Loaded);

        var time = TimeSpan.FromMilliseconds(240);
        var scale = (ScaleTransform)_folderPanel.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, time) { EasingFunction = Motion.Out });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, time) { EasingFunction = Motion.Out });
        _folderPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, time) { EasingFunction = Motion.Out });
        _host.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, time));
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
        _host.BeginAnimation(OpacityProperty, new DoubleAnimation(1, time));
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
    /// The dragged app left the folder panel: it comes out of the folder, the folder closes and
    /// the drag carries on over the pages, starting just after the folder.
    /// </summary>
    private void LeaveFolder()
    {
        var drag = _drag!;
        var folder = drag.Folder!;
        drag.Folder = null;
        folder.Apps = [.. _folderOthers];
        var at = _items.IndexOf(folder);
        if (folder.Apps.Count == 1)
        {
            // A folder of one is just that app again.
            Forget(folder);
            _items[at] = new LaunchEntry { App = folder.Apps[0] };
        }
        else
        {
            Forget(folder);
        }
        CloseFolder(quick: true);
        _others = [.. _items];
        _dropIndex = at + 1;
        Arrange(false);
        Save();
    }
}
