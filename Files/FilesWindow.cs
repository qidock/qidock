using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using QiDock.Services;
using static QiDock.Files.FileKinds;

namespace QiDock.Files;

/// <summary>One file or folder in the open folder.</summary>
internal sealed class FileEntry
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public bool IsFolder { get; init; }
    public long Length { get; init; }
    public DateTime Modified { get; init; }
    public FileKind Kind { get; init; }
    public bool Hidden { get; init; }

    /// <summary>For a folder: its count and main kind, once looked at.</summary>
    public FolderPeek? Peek { get; set; }

    /// <summary>For a folder: its full size, once added up.</summary>
    public long? Size { get; set; }

    public long SizeOrLength => IsFolder ? Size ?? 0 : Length;
}

/// <summary>
/// QiDock's own file window: favourites and drives on the left, a path bar, colourful folders with their item counts
/// (coloured by what they mostly hold) and file thumbnails in a grid or a list, and a panel showing what takes the
/// space. Everything slow (listing, thumbnails, sizes) happens in the background; only visible rows are drawn.
/// </summary>
internal sealed partial class FilesWindow : Window
{
    private static FilesWindow? _shared;

    public static void Open(string? path = null)
    {
        if (_shared is null)
        {
            _shared = new FilesWindow();
            _shared.Closed += (_, _) => _shared = null;
            _shared.Show();
            _shared.Navigate(path ?? FilesState.Load().LastFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        else
        {
            if (_shared.WindowState == WindowState.Minimized) _shared.WindowState = WindowState.Normal;
            if (path is not null) _shared.Navigate(path);
        }
        _shared.Activate();
    }

    private readonly FilesState _state = FilesState.Load();
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();
    private readonly ObservableCollection<object> _rows = [];
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<RowHost> _realized = [];
    private readonly DispatcherTimer _refreshSoon = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _redrawSoon = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
    private List<FileEntry> _entries = [];
    private List<FileEntry> _shown = [];
    private string _path = "";
    private string _filter = "";
    private string? _anchor;
    private int _columns;
    private CancellationTokenSource? _load;
    private FileSystemWatcher? _watcher;

    private readonly Border _frame;
    private readonly ItemsControl _list;
    private readonly TextBlock _title;
    private readonly StackPanel _crumbs;
    private readonly Border _crumbHost;
    private readonly TextBlock _status;
    private readonly TextBox _search;
    private readonly Button _backButton, _forwardButton, _upButton, _gridButton, _listButton, _detailsButton;
    private readonly Border _details;
    private readonly ColumnDefinition _detailsColumn;

    private FilesWindow()
    {
        Title = L("Dosyalar", "Files");
        Width = Math.Min(1220, SystemParameters.WorkArea.Width * 0.9);
        Height = Math.Min(780, SystemParameters.WorkArea.Height * 0.88);
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontFamily = (FontFamily)FindResource("InterfaceFont");
        FontSize = 13;
        SetResourceReference(ForegroundProperty, "TextPrimary");
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/QiDock;component/Design/SettingsControls.xaml", UriKind.Relative) });
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 52,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(-1),
            UseAeroCaptionButtons = false
        });

        // ---- Right side: toolbar, path bar, content + details, status line ----------------------
        _backButton = ToolButton(GlyphChevron(left: true), L("Geri (Alt+←)", "Back (Alt+←)"), GoBack);
        _forwardButton = ToolButton(GlyphChevron(left: false), L("İleri (Alt+→)", "Forward (Alt+→)"), GoForward);
        _upButton = ToolButton(GlyphUp, L("Üst klasör (Alt+↑)", "Up (Alt+↑)"), GoUp);
        _title = Text("", 15, FontWeights.SemiBold);
        _title.Margin = new Thickness(10, 0, 0, 0);
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        navigation.Children.Add(Chrome(_backButton));
        navigation.Children.Add(Chrome(_forwardButton));
        navigation.Children.Add(Chrome(_upButton));
        navigation.Children.Add(_title);

        _gridButton = ToolButton(GlyphGrid, L("Izgara", "Grid"), () => SetView(grid: true));
        _listButton = ToolButton(GlyphList, L("Liste", "List"), () => SetView(grid: false));
        var sort = ToolButton(GlyphSort, L("Sırala", "Sort"), () => { });
        sort.Click += (_, _) => ShowSortMenu(sort);
        _detailsButton = ToolButton(GlyphPanel, L("Ayrıntı paneli", "Details panel"), ToggleDetails);
        var views = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var button in new[] { _gridButton, _listButton, sort, _detailsButton }) views.Children.Add(Chrome(button));
        var captions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 5, 6, 5) };
        captions.Children.Add(Chrome(Caption("", L("Simge durumuna küçült", "Minimize"), "CaptionButton", () => WindowState = WindowState.Minimized)));
        captions.Children.Add(Chrome(Caption(null, null, "MaximizeCaptionButton", () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized)));
        captions.Children.Add(Chrome(Caption("", L("Kapat", "Close"), "CloseCaptionButton", Close)));
        var toolbar = new DockPanel { Height = 52 };
        DockPanel.SetDock(captions, Dock.Right);
        DockPanel.SetDock(views, Dock.Right);
        toolbar.Children.Add(captions);
        toolbar.Children.Add(views);
        toolbar.Children.Add(navigation);

        _crumbs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _crumbHost = new Border { Height = 34, Padding = new Thickness(14, 0, 14, 0), Background = Brushes.Transparent, Child = _crumbs, Cursor = Cursors.IBeam };
        _crumbHost.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource == _crumbHost || e.OriginalSource == _crumbs) EditPath();
        };
        var crumbLine = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = _crumbHost };
        crumbLine.SetResourceReference(Border.BorderBrushProperty, "MenuDivider");

        _list = CreateList();
        _details = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(20, 18, 20, 18) };
        _details.SetResourceReference(Border.BorderBrushProperty, "MenuDivider");
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition());
        _detailsColumn = new ColumnDefinition { Width = new GridLength(300) };
        body.ColumnDefinitions.Add(_detailsColumn);
        body.Children.Add(_list);
        Grid.SetColumn(_details, 1);
        body.Children.Add(_details);

        _status = Text("", 11.5, brushKey: "TextSecondary");
        var statusLine = new Border { Height = 30, Padding = new Thickness(16, 0, 16, 0), BorderThickness = new Thickness(0, 1, 0, 0), Child = _status };
        statusLine.SetResourceReference(Border.BorderBrushProperty, "MenuDivider");

        var main = new Grid();
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition());
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.Children.Add(toolbar);
        Grid.SetRow(crumbLine, 1);
        main.Children.Add(crumbLine);
        Grid.SetRow(body, 2);
        main.Children.Add(body);
        Grid.SetRow(statusLine, 3);
        main.Children.Add(statusLine);

        // ---- Left side: search, favourites, places --------------------------------------------
        _search = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 13
        };
        _search.SetResourceReference(ForegroundProperty, "TextPrimary");
        _search.SetResourceReference(TextBoxBase.CaretBrushProperty, "TextPrimary");
        _search.TextChanged += (_, _) =>
        {
            _filter = _search.Text.Trim();
            Rebuild();
        };
        var sidebar = BuildSidebar(_search);

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(232) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.Children.Add(sidebar);
        Grid.SetColumn(main, 1);
        root.Children.Add(main);

        _frame = new Border { BorderThickness = new Thickness(1), Child = root };
        _frame.SetResourceReference(Border.BorderBrushProperty, "MenuStroke");
        _frame.SetResourceReference(Border.BackgroundProperty, "SettingsBackground");
        Content = _frame;

        SourceInitialized += (_, _) =>
        {
            var glass = SettingsBackdrop.TryEnable(this, ThemeManager.CurrentTheme);
            _frame.SetResourceReference(Border.BackgroundProperty, glass ? "SettingsGlassMaterial" : "SettingsBackground");
            if (!glass) WindowChrome.GetWindowChrome(this).GlassFrameThickness = new Thickness(0);
        };
        PreviewKeyDown += OnKey;
        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1) GoBack();
            else if (e.ChangedButton == MouseButton.XButton2) GoForward();
        };
        Closed += (_, _) =>
        {
            _load?.Cancel();
            _watcher?.Dispose();
            _state.LastFolder = _path;
            _state.Save();
            // Nothing of the window stays behind: pictures and the visual tree go, and the memory is handed back.
            ShellThumbnails.Clear();
            _list.ItemsSource = null;
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                // Thumbnail pixels live in the large-object heap, which .NET never shrinks unless asked.
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                using var self = Process.GetCurrentProcess();
                SetProcessWorkingSetSize(self.Handle, -1, -1);
            });
        };
        _refreshSoon.Tick += (_, _) =>
        {
            _refreshSoon.Stop();
            // Not under someone typing a new name; it refreshes once they are done.
            if (_renaming is not null)
            {
                _refreshSoon.Start();
                return;
            }
            Load(keepScroll: true);
        };
        _redrawSoon.Tick += (_, _) =>
        {
            _redrawSoon.Stop();
            RedrawVisible();
            UpdateDetails();
        };
        AcceptDrops();
        ApplyViewButtons();
        ApplyDetailsVisibility();
    }

    // ---- Navigation -----------------------------------------------------------------------------

    public void Navigate(string path, bool remember = true)
    {
        path = Path.TrimEndingDirectorySeparator(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        if (path.Length == 2 && path[1] == ':') path += "\\";
        if (!Directory.Exists(path))
        {
            if (File.Exists(path))
            {
                OpenItem(path);
                return;
            }
            ShowStatusMessage(L("Klasör bulunamadı: ", "Folder not found: ") + path);
            return;
        }
        if (remember && _path.Length > 0 && !string.Equals(_path, path, StringComparison.OrdinalIgnoreCase))
        {
            _back.Push(_path);
            _forward.Clear();
        }
        var cameFrom = _path;
        _path = path;
        _sizesWanted = SizesByThemselves(path);
        _selected.Clear();
        _anchor = null;
        if (_search.Text.Length > 0) _search.Text = "";
        _title.Text = DisplayName(path);
        Title = _title.Text + " – " + L("Dosyalar", "Files");
        BuildCrumbs();
        HighlightSidebar();
        Watch(path);
        Load(keepScroll: false, select: cameFrom.Length > 0 && string.Equals(Path.GetDirectoryName(cameFrom), path, StringComparison.OrdinalIgnoreCase) ? cameFrom : null);
        _backButton.IsEnabled = _back.Count > 0;
        _forwardButton.IsEnabled = _forward.Count > 0;
        _upButton.IsEnabled = Path.GetDirectoryName(path) is not null;
    }

    private void GoBack()
    {
        if (_back.Count == 0) return;
        _forward.Push(_path);
        Navigate(_back.Pop(), remember: false);
        _forwardButton.IsEnabled = _forward.Count > 0;
    }

    private void GoForward()
    {
        if (_forward.Count == 0) return;
        _back.Push(_path);
        Navigate(_forward.Pop(), remember: false);
    }

    private void GoUp()
    {
        if (Path.GetDirectoryName(_path) is { } parent) Navigate(parent);
    }

    private static string DisplayName(string path)
    {
        if (Path.GetPathRoot(path) is { } root && string.Equals(root, path, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var drive = new DriveInfo(root);
                var label = drive.IsReady && drive.VolumeLabel.Length > 0 ? drive.VolumeLabel : L("Yerel Disk", "Local Disk");
                return $"{label} ({root.TrimEnd('\\')})";
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                return root;
            }
        }
        return SpecialNames.TryGetValue(Path.TrimEndingDirectorySeparator(path), out var special) ? special : Path.GetFileName(path);
    }

    /// <summary>Windows' own folders by the names Windows shows for them ("Belgeler", not "Documents").</summary>
    private static readonly Dictionary<string, string> SpecialNames = BuildSpecialNames();

    private static Dictionary<string, string> BuildSpecialNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path, string turkish, string english)
        {
            if (!string.IsNullOrEmpty(path)) names[Path.TrimEndingDirectorySeparator(path)] = L(turkish, english);
        }
        Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Masaüstü", "Desktop");
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Belgeler", "Documents");
        Add(KnownFolders.Downloads, "İndirilenler", "Downloads");
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Resimler", "Pictures");
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Müzik", "Music");
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Videolar", "Videos");
        var users = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Add(users, "Kullanıcılar", "Users");
        return names;
    }

    private void OpenItem(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowStatusMessage(L("Açılamadı: ", "Could not open: ") + Path.GetFileName(path));
        }
    }

    private void Open(FileEntry entry)
    {
        if (entry.IsFolder) Navigate(entry.Path);
        else OpenItem(entry.Path);
    }

    // ---- Loading --------------------------------------------------------------------------------

    private void Load(bool keepScroll, string? select = null)
    {
        _load?.Cancel();
        ShellThumbnails.CancelPending();
        var load = _load = new CancellationTokenSource();
        var token = load.Token;
        var path = _path;
        var showHidden = _state.ShowHidden;
        _ = Task.Run(() => Read(path, showHidden, token), token).ContinueWith(task =>
        {
            if (token.IsCancellationRequested || task.IsCanceled) return;
            if (task.IsFaulted)
            {
                _entries = [];
                Rebuild();
                ShowStatusMessage(L("Bu klasör okunamadı.", "This folder could not be read."));
                return;
            }
            // Keep what was already found out about folders that are still there.
            var known = _entries.Where(e => e.IsFolder).ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in task.Result.Where(e => e.IsFolder))
            {
                if (!known.TryGetValue(entry.Path, out var old)) continue;
                entry.Peek = old.Peek;
                entry.Size = old.Size;
            }
            _entries = task.Result;
            _selected.IntersectWith(_entries.Select(e => e.Path));
            if (select is not null && _entries.Any(e => string.Equals(e.Path, select, StringComparison.OrdinalIgnoreCase))) _selected.Add(select);
            var offset = keepScroll ? Scroller?.VerticalOffset ?? 0 : 0;
            Rebuild();
            if (keepScroll) Scroller?.ScrollToVerticalOffset(offset);
            else Scroller?.ScrollToTop();
            if (select is not null) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => BringIntoView(select));
            var entries = task.Result;
            _ = Task.Run(() => LookInside(entries, token), token);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static List<FileEntry> Read(string path, bool showHidden, CancellationToken token)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = showHidden ? 0 : FileAttributes.Hidden | FileAttributes.System
        };
        var entries = new FileSystemEnumerable<FileEntry>(path, (ref FileSystemEntry entry) =>
        {
            var full = entry.ToFullPath();
            var name = entry.FileName.ToString();
            return new FileEntry
            {
                Path = full,
                Name = name,
                IsFolder = entry.IsDirectory,
                Length = entry.IsDirectory ? 0 : entry.Length,
                Modified = entry.LastWriteTimeUtc.LocalDateTime,
                Kind = entry.IsDirectory ? FileKind.Other : FileKinds.Of(name),
                Hidden = (entry.Attributes & FileAttributes.Hidden) != 0
            };
        }, options);
        var list = new List<FileEntry>();
        foreach (var entry in entries)
        {
            list.Add(entry);
            if ((list.Count & 255) == 0) token.ThrowIfCancellationRequested();
        }
        return list;
    }

    /// <summary>Counts what is inside each folder, then (for the panel) adds up every folder's size.</summary>
    private void LookInside(List<FileEntry> entries, CancellationToken token)
    {
        var folders = entries.Where(e => e.IsFolder).ToList();
        var showHidden = _state.ShowHidden;
        foreach (var folder in folders)
        {
            if (token.IsCancellationRequested) return;
            if (folder.Peek is not null) continue;
            folder.Peek = FolderScanner.Peek(folder.Path, showHidden);
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (!token.IsCancellationRequested) _redrawSoon.Start(); });
        }
        if (_sizesWanted) AddUpSizes(folders, token);
    }

    /// <summary>
    /// Whether sizes are added up without asking: not for a whole drive or Windows' own folders, where reading every
    /// file keeps the disk busy for minutes (the panel offers a button there instead).
    /// </summary>
    private static bool SizesByThemselves(string path)
    {
        if (string.Equals(Path.GetPathRoot(path), Path.TrimEndingDirectorySeparator(path) + "\\", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase)) return false;
        // These and everything inside them.
        var heavy = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) ?? "" // AppData
        };
        // Only these themselves: the user folder holds AppData, the users folder holds every user's.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var exact = new[] { home, Path.GetDirectoryName(home) ?? "" };
        return !exact.Any(e => string.Equals(path, e, StringComparison.OrdinalIgnoreCase))
            && !heavy.Any(h => h.Length > 0 && (string.Equals(path, h, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(h + "\\", StringComparison.OrdinalIgnoreCase)));
    }

    private bool _sizesWanted;

    /// <summary>The panel's "add up sizes" button for heavy places.</summary>
    private void AddUpSizesNow()
    {
        if (_sizesWanted || _load is null) return;
        _sizesWanted = true;
        var token = _load.Token;
        var folders = _entries.Where(e => e.IsFolder).ToList();
        _ = Task.Run(() => AddUpSizes(folders, token), token);
        UpdateDetails();
    }

    private void AddUpSizes(List<FileEntry> folders, CancellationToken token)
    {
        foreach (var folder in folders.Where(f => f.Size is null).OrderBy(f => f.Peek?.Count ?? 0))
        {
            if (token.IsCancellationRequested) return;
            try { folder.Size = FolderScanner.Size(folder.Path, token); }
            catch (OperationCanceledException) { return; }
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (!token.IsCancellationRequested) _redrawSoon.Start(); });
        }
    }

    private void Watch(string path)
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                IncludeSubdirectories = false
            };
            void Changed(object? sender, FileSystemEventArgs e) => Dispatcher.BeginInvoke(() =>
            {
                FolderScanner.Forget(path);
                _refreshSoon.Stop();
                _refreshSoon.Start();
            });
            watcher.Created += Changed;
            watcher.Deleted += Changed;
            watcher.Renamed += (s, e) => Changed(s, e);
            watcher.Changed += Changed;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
    }

    // ---- Keyboard -------------------------------------------------------------------------------

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var typing = Keyboard.FocusedElement is TextBox;
        if (!typing && !alt && OnOperationKey(key))
        {
            e.Handled = true;
            return;
        }
        switch (key)
        {
            case Key.Left when alt: GoBack(); break;
            case Key.Right when alt: GoForward(); break;
            case Key.Up when alt: GoUp(); break;
            case Key.Back when !typing: GoBack(); break;
            case Key.F5: FolderScanner.Forget(_path); Load(keepScroll: true); break;
            case Key.F when control: _search.Focus(); _search.SelectAll(); break;
            case Key.L when control: EditPath(); break;
            case Key.A when control && !typing:
                _selected.UnionWith(_shown.Select(s => s.Path));
                SelectionChanged();
                break;
            case Key.Escape when typing && Keyboard.FocusedElement == _search:
                _search.Text = "";
                _list.Focus();
                break;
            case Key.Escape when !typing && _selected.Count > 0:
                _selected.Clear();
                SelectionChanged();
                break;
            case Key.Enter when !typing:
                var selected = _shown.Where(s => _selected.Contains(s.Path)).ToList();
                if (selected.Count == 1) Open(selected[0]);
                else foreach (var file in selected.Where(s => !s.IsFolder)) OpenItem(file.Path);
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End when !typing && !alt:
                MoveSelection(key, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                break;
            default: return;
        }
        e.Handled = true;
    }

    private void ShowStatusMessage(string text) => _status.Text = text;

    // ---- Small pieces ---------------------------------------------------------------------------

    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(nint process, nint minimum, nint maximum);

    private static readonly Geometry GlyphUp = Geometry.Parse("M12 19V5 M5 12l7-7 7 7");

    private static Geometry GlyphChevron(bool left) => Geometry.Parse(left ? "M15 18l-6-6 6-6" : "M9 18l6-6-6-6");

    internal static TextBlock Text(string text, double size, FontWeight? weight = null, string brushKey = "TextPrimary")
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }

    private static T Chrome<T>(T element) where T : UIElement
    {
        WindowChrome.SetIsHitTestVisibleInChrome(element, true);
        return element;
    }

    private Button ToolButton(Geometry glyph, string tip, Action click)
    {
        var icon = new System.Windows.Shapes.Path
        {
            Data = glyph,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.9,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextPrimary");
        var button = new Button { Content = icon, Width = 34, Height = 32, ToolTip = tip, Margin = new Thickness(1, 0, 1, 0) };
        button.SetResourceReference(StyleProperty, "WidgetIconButton");
        System.Windows.Automation.AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private static Button Caption(string? glyph, string? tip, string style, Action click)
    {
        var button = new Button { Height = 32 };
        if (glyph is not null) button.Content = glyph;
        if (tip is not null)
        {
            button.ToolTip = tip;
            System.Windows.Automation.AutomationProperties.SetName(button, tip);
        }
        button.SetResourceReference(StyleProperty, style);
        button.Click += (_, _) => click();
        return button;
    }

    private void SetView(bool grid)
    {
        if (_state.Grid == grid) return;
        _state.Grid = grid;
        _state.Save();
        ApplyViewButtons();
        Rebuild();
    }

    private void ApplyViewButtons()
    {
        var (on, off) = _state.Grid ? (_gridButton, _listButton) : (_listButton, _gridButton);
        on.SetResourceReference(BackgroundProperty, "SurfaceHover");
        off.Background = Brushes.Transparent;
    }

    private void ToggleDetails()
    {
        _state.Details = !_state.Details;
        _state.Save();
        ApplyDetailsVisibility();
    }

    private void ApplyDetailsVisibility()
    {
        _details.Visibility = _state.Details ? Visibility.Visible : Visibility.Collapsed;
        _detailsColumn.Width = _state.Details ? new GridLength(300) : new GridLength(0);
        if (_state.Details) _detailsButton.SetResourceReference(BackgroundProperty, "SurfaceHover");
        else _detailsButton.Background = Brushes.Transparent;
        UpdateDetails();
    }

    private void ShowSortMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        void Choice(string label, FileSort sort)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = _state.Sort == sort };
            item.Click += (_, _) =>
            {
                _state.Descending = _state.Sort == sort ? !_state.Descending : sort is FileSort.Date or FileSort.Size;
                _state.Sort = sort;
                _state.Save();
                Rebuild();
            };
            menu.Items.Add(item);
        }
        Choice(L("Ada göre", "Name"), FileSort.Name);
        Choice(L("Türe göre", "Kind"), FileSort.Kind);
        Choice(L("Tarihe göre", "Date"), FileSort.Date);
        Choice(L("Boyuta göre", "Size"), FileSort.Size);
        menu.Items.Add(new Separator());
        var descending = new MenuItem { Header = L("Ters sırada", "Descending"), IsCheckable = true, IsChecked = _state.Descending };
        descending.Click += (_, _) =>
        {
            _state.Descending = !_state.Descending;
            _state.Save();
            Rebuild();
        };
        menu.Items.Add(descending);
        var hidden = new MenuItem { Header = L("Gizli öğeleri göster", "Show hidden items"), IsCheckable = true, IsChecked = _state.ShowHidden };
        hidden.Click += (_, _) =>
        {
            _state.ShowHidden = !_state.ShowHidden;
            _state.Save();
            foreach (var entry in _entries) entry.Peek = null;
            Load(keepScroll: true);
        };
        menu.Items.Add(hidden);
        menu.IsOpen = true;
    }
}

internal enum FileSort { Name, Kind, Date, Size }

/// <summary>The file window's own choices, kept between runs (view, sort, panel, last folder).</summary>
internal sealed class FilesState
{
    public bool Grid { get; set; } = true;
    public bool Details { get; set; } = true;
    public bool ShowHidden { get; set; }
    public FileSort Sort { get; set; } = FileSort.Name;
    public bool Descending { get; set; }
    public string? LastFolder { get; set; }

    private static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QiDock", "files.json");

    public static FilesState Load()
    {
        try
        {
            return File.Exists(StorePath) ? System.Text.Json.JsonSerializer.Deserialize<FilesState>(File.ReadAllText(StorePath)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, System.Text.Json.JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
