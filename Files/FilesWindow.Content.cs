using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static QiDock.Files.FileKinds;

namespace QiDock.Files;

internal sealed partial class FilesWindow
{
    // ---- Rows: only the rows on screen are built (the list virtualizes them) -----------------------

    private abstract class Row(FilesWindow owner)
    {
        public FilesWindow Owner { get; } = owner;
    }

    private sealed class HeaderRow(FilesWindow owner, string title, int count) : Row(owner)
    {
        public string Title { get; } = title;
        public int Count { get; } = count;
    }

    private sealed class TileRow(FilesWindow owner, FileEntry[] items, double cell) : Row(owner)
    {
        public FileEntry[] Items { get; } = items;
        public double Cell { get; } = cell;
    }

    private sealed class ListHeaderRow(FilesWindow owner) : Row(owner);

    private sealed class ListRow(FilesWindow owner, FileEntry item) : Row(owner)
    {
        public FileEntry Item { get; } = item;
    }

    private sealed class EmptyRow(FilesWindow owner, string text) : Row(owner)
    {
        public string Text { get; } = text;
    }

    /// <summary>The list's container for one row: builds its picture when it is shown and whenever asked to.</summary>
    private sealed class RowHost : Border
    {
        public RowHost()
        {
            DataContextChanged += (_, _) => Rebuild();
            Loaded += (_, _) => (DataContext as Row)?.Owner._realized.Add(this);
            Unloaded += (_, _) => (DataContext as Row)?.Owner._realized.Remove(this);
        }

        public void Rebuild()
        {
            if (DataContext is not Row row) return;
            row.Owner._realized.Add(this);
            Child = row.Owner.BuildRow(row);
        }
    }

    private const double TileCell = 124;
    private const double SidePadding = 18;
    private static readonly Brush SelectedFill = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0x0A, 0x84, 0xFF)));
    private static readonly Brush SelectedStroke = Frozen(new SolidColorBrush(Color.FromArgb(0x80, 0x0A, 0x84, 0xFF)));
    private static readonly Brush SelectedName = Frozen(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)));
    private List<FileEntry[]> _tileRows = [];
    private VirtualizingStackPanel? _panel;

    private ScrollViewer? Scroller => _list.Template?.FindName("Scroller", _list) as ScrollViewer;

    private ItemsControl CreateList()
    {
        var scroller = new FrameworkElementFactory(typeof(ScrollViewer), "Scroller");
        scroller.SetValue(ScrollViewer.CanContentScrollProperty, true);
        scroller.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        scroller.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        scroller.SetValue(FocusableProperty, false);
        scroller.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        var panel = new FrameworkElementFactory(typeof(VirtualizingStackPanel));
        panel.AddHandler(LoadedEvent, new RoutedEventHandler((sender, _) => _panel = sender as VirtualizingStackPanel));
        var list = new ItemsControl
        {
            Template = new ControlTemplate(typeof(ItemsControl)) { VisualTree = scroller },
            ItemsPanel = new ItemsPanelTemplate(panel),
            ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(RowHost)) },
            Focusable = true,
            FocusVisualStyle = null,
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 6, 0, 16)
        };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        list.SizeChanged += (_, e) =>
        {
            if (_state.Grid && e.WidthChanged && Columns() != _columns)
            {
                var offset = Scroller?.VerticalOffset ?? 0;
                Rebuild();
                Scroller?.ScrollToVerticalOffset(offset);
            }
        };
        // A click on empty space clears the selection.
        list.MouseLeftButtonDown += (_, _) =>
        {
            list.Focus();
            if (_selected.Count == 0) return;
            _selected.Clear();
            SelectionChanged();
        };
        list.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            FolderMenu();
        };
        return list;
    }

    private int Columns()
    {
        var width = (_list.ActualWidth > 0 ? _list.ActualWidth : 800) - SidePadding * 2 - 12;
        return Math.Max(1, (int)(width / TileCell));
    }

    private static readonly Func<string, string, int> CompareNames = MakeNameComparison();

    /// <summary>"Bölüm 2" before "Bölüm 10", in Turkish order, when the system can do it.</summary>
    private static Func<string, string, int> MakeNameComparison()
    {
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        try
        {
            compare.Compare("a2", "a10", CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
            return (a, b) => compare.Compare(a, b, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
        }
        catch (ArgumentException)
        {
            return (a, b) => compare.Compare(a, b, CompareOptions.IgnoreCase);
        }
    }

    private void Rebuild()
    {
        IEnumerable<FileEntry> items = _entries;
        if (_filter.Length > 0) items = items.Where(e => e.Name.Contains(_filter, StringComparison.CurrentCultureIgnoreCase));
        int Compare(FileEntry a, FileEntry b)
        {
            var result = _state.Sort switch
            {
                FileSort.Kind => a.Kind != b.Kind ? Name(a.Kind).CompareTo(Name(b.Kind)) : string.Compare(Path.GetExtension(a.Name), Path.GetExtension(b.Name), StringComparison.OrdinalIgnoreCase),
                FileSort.Date => a.Modified.CompareTo(b.Modified),
                FileSort.Size => a.SizeOrLength.CompareTo(b.SizeOrLength),
                _ => 0
            };
            if (result == 0) result = CompareNames(a.Name, b.Name);
            return _state.Descending ? -result : result;
        }
        var folders = items.Where(e => e.IsFolder).ToList();
        var files = items.Where(e => !e.IsFolder).ToList();
        folders.Sort(Compare);
        files.Sort(Compare);
        _shown = [.. folders, .. files];

        var rows = new List<object>();
        _tileRows = [];
        if (_state.Grid)
        {
            _columns = Columns();
            var cell = Math.Floor(((_list.ActualWidth > 0 ? _list.ActualWidth : 800) - SidePadding * 2 - 12) / _columns);
            void Section(string title, List<FileEntry> section)
            {
                if (section.Count == 0) return;
                rows.Add(new HeaderRow(this, title, section.Count));
                for (var i = 0; i < section.Count; i += _columns)
                {
                    var chunk = section.Skip(i).Take(_columns).ToArray();
                    _tileRows.Add(chunk);
                    rows.Add(new TileRow(this, chunk, cell));
                }
            }
            Section(L("Klasörler", "Folders"), folders);
            Section(L("Dosyalar", "Files"), files);
        }
        else
        {
            if (_shown.Count > 0) rows.Add(new ListHeaderRow(this));
            foreach (var entry in _shown)
            {
                _tileRows.Add([entry]);
                rows.Add(new ListRow(this, entry));
            }
        }
        if (_shown.Count == 0)
            rows.Add(new EmptyRow(this, _filter.Length > 0 ? L($"\"{_filter}\" ile eşleşen bir şey yok", $"Nothing matches \"{_filter}\"") : L("Bu klasör boş", "This folder is empty")));
        _realized.Clear();
        _list.ItemsSource = rows;
        UpdateStatus();
        UpdateDetails();
    }

    private void RedrawVisible()
    {
        foreach (var host in _realized.ToList())
            if (_renameBox is null || !_renameBox.IsDescendantOf(host)) host.Rebuild();
    }

    private FrameworkElement BuildRow(Row row) => row switch
    {
        HeaderRow header => BuildHeader(header),
        TileRow tiles => BuildTiles(tiles),
        ListHeaderRow => BuildListHeader(),
        ListRow line => BuildListRow(line.Item),
        EmptyRow empty => new TextBlock
        {
            Text = empty.Text,
            Margin = new Thickness(0, 60, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)FindResource("TextSecondary"),
            FontSize = 14
        },
        _ => new Border()
    };

    private FrameworkElement BuildHeader(HeaderRow header)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(SidePadding + 6, 14, 0, 8) };
        line.Children.Add(Text(header.Title, 12.5, FontWeights.SemiBold, "TextSecondary"));
        var count = Text(header.Count.ToString(CultureInfo.CurrentCulture), 12, brushKey: "TextSecondary");
        count.Margin = new Thickness(7, 0, 0, 0);
        count.Opacity = 0.7;
        line.Children.Add(count);
        return line;
    }

    private FrameworkElement BuildTiles(TileRow row)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(SidePadding, 0, 0, 6) };
        foreach (var entry in row.Items)
            line.Children.Add(new Border { Width = row.Cell, Child = BuildTile(entry) });
        return line;
    }

    private FrameworkElement BuildTile(FileEntry entry)
    {
        var selected = _selected.Contains(entry.Path);
        var stack = new StackPanel { Width = 112 };
        FrameworkElement picture = entry.IsFolder ? FolderPicture(entry, 92) : FilePicture(entry, 80);
        picture.HorizontalAlignment = HorizontalAlignment.Center;
        picture.Height = 80;
        stack.Children.Add(picture);
        var name = new TextBlock
        {
            Text = SpecialNames.TryGetValue(entry.Path, out var shown) ? shown : entry.Name,
            FontSize = 12.5,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 34,
            Margin = new Thickness(2, 6, 2, 0),
            FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal
        };
        if (selected) name.Foreground = SelectedName;
        else name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        if (entry.Hidden) stack.Opacity = 0.55;
        stack.Children.Add(string.Equals(_renaming, entry.Path, StringComparison.OrdinalIgnoreCase) ? RenameBox(entry, centered: true) : name);
        var tile = new Border
        {
            Child = stack,
            Padding = new Thickness(4, 8, 4, 8),
            CornerRadius = new CornerRadius(12),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = selected ? SelectedFill : Brushes.Transparent,
            BorderBrush = selected ? SelectedStroke : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            ToolTip = Describe(entry)
        };
        if (!selected)
        {
            tile.MouseEnter += (_, _) => tile.SetResourceReference(Border.BackgroundProperty, "SurfaceHover");
            tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
        }
        Interactive(tile, entry);
        return tile;
    }

    private FrameworkElement BuildListHeader()
    {
        var grid = ListColumns();
        grid.Margin = new Thickness(SidePadding, 4, SidePadding, 4);
        void Head(string text, int column, FileSort sort, bool right = false)
        {
            var label = Text(text + (_state.Sort == sort ? (_state.Descending ? "  ↓" : "  ↑") : ""), 11.5, FontWeights.SemiBold, "TextSecondary");
            label.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            var cell = new Border { Background = Brushes.Transparent, Child = label, Cursor = Cursors.Hand, Padding = new Thickness(column == 0 ? 30 : 8, 4, 8, 4) };
            cell.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                _state.Descending = _state.Sort == sort ? !_state.Descending : sort is FileSort.Date or FileSort.Size;
                _state.Sort = sort;
                _state.Save();
                Rebuild();
            };
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }
        Head(L("Ad", "Name"), 0, FileSort.Name);
        Head(L("Değiştirilme", "Modified"), 1, FileSort.Date);
        Head(L("Tür", "Kind"), 2, FileSort.Kind);
        Head(L("Boyut", "Size"), 3, FileSort.Size, right: true);
        var line = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
        line.SetResourceReference(Border.BorderBrushProperty, "MenuDivider");
        return line;
    }

    private static Grid ListColumns()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        return grid;
    }

    private FrameworkElement BuildListRow(FileEntry entry)
    {
        var selected = _selected.Contains(entry.Path);
        var grid = ListColumns();
        var icon = entry.IsFolder ? FolderPicture(entry, 22, small: true) : FilePicture(entry, 20);
        icon.Margin = new Thickness(0, 0, 10, 0);
        FrameworkElement name = string.Equals(_renaming, entry.Path, StringComparison.OrdinalIgnoreCase)
            ? RenameBox(entry, centered: false)
            : Text(entry.Name, 13, selected ? FontWeights.SemiBold : FontWeights.Normal);
        var first = new DockPanel { Margin = new Thickness(6, 0, 8, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        first.Children.Add(icon);
        first.Children.Add(name);
        grid.Children.Add(first);
        void Cell(string text, int column, bool right = false)
        {
            var block = Text(text, 12, brushKey: "TextSecondary");
            block.Margin = new Thickness(8, 0, 8, 0);
            block.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            Grid.SetColumn(block, column);
            grid.Children.Add(block);
        }
        Cell(Date(entry.Modified), 1);
        Cell(entry.IsFolder ? L("Klasör", "Folder") : KindLabel(entry), 2);
        Cell(entry.IsFolder ? (entry.Size is { } size ? FileKinds.Size(size) : Count(entry.Peek) ?? "—") : FileKinds.Size(entry.Length), 3, right: true);
        var row = new Border
        {
            Child = grid,
            Height = 32,
            Margin = new Thickness(SidePadding - 6, 0, SidePadding - 6, 0),
            CornerRadius = new CornerRadius(8),
            Background = selected ? SelectedFill : Brushes.Transparent,
            Opacity = entry.Hidden ? 0.55 : 1
        };
        if (!selected)
        {
            row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "SurfaceHover");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        }
        Interactive(row, entry);
        return row;
    }

    private static string KindLabel(FileEntry entry)
    {
        var extension = Path.GetExtension(entry.Name).TrimStart('.').ToUpperInvariant();
        return entry.Kind == FileKind.Other && extension.Length > 0 ? extension + " " + L("dosyası", "file") : Name(entry.Kind);
    }

    private static string? Count(FolderPeek? peek) => peek switch
    {
        null => null,
        { More: true } => L($"{peek.Count}+ öğe", $"{peek.Count}+ items"),
        _ => L($"{peek.Count} öğe", peek.Count == 1 ? "1 item" : $"{peek.Count} items")
    };

    private static string Describe(FileEntry entry)
    {
        var parts = new List<string> { entry.Name };
        if (entry.IsFolder)
        {
            if (Count(entry.Peek) is { } count) parts.Add(count);
            if (entry.Size is { } size) parts.Add(FileKinds.Size(size));
        }
        else
        {
            parts.Add(KindLabel(entry) + " · " + FileKinds.Size(entry.Length));
        }
        parts.Add(Date(entry.Modified));
        return string.Join("\n", parts);
    }

    // ---- Pictures -------------------------------------------------------------------------------

    private static readonly Dictionary<string, Geometry> SpecialFolders = BuildSpecialFolders();

    private static Dictionary<string, Geometry> BuildSpecialFolders()
    {
        var map = new Dictionary<string, Geometry>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path, Geometry glyph)
        {
            if (!string.IsNullOrEmpty(path)) map[Path.TrimEndingDirectorySeparator(path)] = glyph;
        }
        Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), GlyphDesktop);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), GlyphDocument);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), GlyphImage);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), GlyphAudio);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), GlyphVideo);
        Add(KnownFolders.Downloads, GlyphDownload);
        return map;
    }

    private static FrameworkElement FolderPicture(FileEntry entry, double width, bool small = false)
    {
        var peek = entry.Peek;
        var color = peek is null ? FolderColor(FileKind.Other) : FolderColor(peek.Count == 0 ? null : peek.Kind);
        var glyph = SpecialFolders.TryGetValue(entry.Path, out var special) ? special : peek is { Count: > 0 } ? Glyph(peek.Kind ?? FileKind.Other) : GlyphBox;
        return FolderArt.Create(color, small ? null : Count(peek), glyph, width);
    }

    private FrameworkElement FilePicture(FileEntry entry, double size)
    {
        var color = FolderColor(entry.Kind == FileKind.Other ? FileKind.Other : entry.Kind);
        var placeholder = new Border
        {
            Width = size * 0.78,
            Height = size * 0.92,
            CornerRadius = new CornerRadius(size * 0.12),
            Background = Frozen(new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B))),
            BorderBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x66, color.R, color.G, color.B))),
            BorderThickness = new Thickness(1),
            Child = FileKinds.Line(Glyph(entry.Kind), size * 0.36, Frozen(new SolidColorBrush(color)), 1.8)
        };
        var image = new Image { Width = size, Height = size, Stretch = Stretch.Uniform, Tag = entry.Path };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var host = new Grid { Width = size, Height = size };
        host.Children.Add(placeholder);
        host.Children.Add(image);
        var pixels = (int)Math.Ceiling(size * DpiScale());
        void Show(BitmapSource? source)
        {
            if (source is null || !Equals(image.Tag, entry.Path)) return;
            image.Source = source;
            placeholder.Visibility = Visibility.Collapsed;
        }
        if (ShellThumbnails.Cached(entry.Path, pixels) is { } cached) Show(cached);
        else ShellThumbnails.Load(entry.Path, pixels, Show);
        return host;
    }

    private double DpiScale() => PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.5;

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    // ---- Selection ------------------------------------------------------------------------------

    private void Interactive(FrameworkElement element, FileEntry entry)
    {
        element.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _list.Focus();
            if (e.ClickCount >= 2)
            {
                Open(entry);
                return;
            }
            // Pressing one of several selected items keeps them all, so they can be dragged together.
            if (_selected.Contains(entry.Path) && _selected.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
            {
                _selectOnRelease = true;
                return;
            }
            Click(entry);
        };
        element.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ItemMenu(element, entry);
        };
        DragAndDrop(element, entry);
    }

    private void Click(FileEntry entry)
    {
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (shift && _anchor is not null)
        {
            var from = _shown.FindIndex(s => string.Equals(s.Path, _anchor, StringComparison.OrdinalIgnoreCase));
            var to = _shown.IndexOf(entry);
            if (from >= 0 && to >= 0)
            {
                if (!control) _selected.Clear();
                for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++) _selected.Add(_shown[i].Path);
                SelectionChanged();
                return;
            }
        }
        if (control)
        {
            if (!_selected.Remove(entry.Path)) _selected.Add(entry.Path);
        }
        else
        {
            _selected.Clear();
            _selected.Add(entry.Path);
        }
        _anchor = entry.Path;
        SelectionChanged();
    }

    private void SelectionChanged()
    {
        RedrawVisible();
        UpdateStatus();
        UpdateDetails();
    }

    /// <summary>Arrow keys move through the grid (up/down by a whole row) or the list; Shift extends the selection.</summary>
    private void MoveSelection(Key key, bool extend)
    {
        if (_tileRows.Count == 0) return;
        var current = _anchor ?? _selected.FirstOrDefault();
        int row = 0, column = 0;
        var found = false;
        for (var r = 0; r < _tileRows.Count && !found; r++)
            for (var c = 0; c < _tileRows[r].Length; c++)
                if (string.Equals(_tileRows[r][c].Path, current, StringComparison.OrdinalIgnoreCase))
                {
                    (row, column, found) = (r, c, true);
                    break;
                }
        if (found)
        {
            switch (key)
            {
                case Key.Left: if (column > 0) column--; else if (row > 0) column = _tileRows[--row].Length - 1; break;
                case Key.Right: if (column < _tileRows[row].Length - 1) column++; else if (row < _tileRows.Count - 1) (row, column) = (row + 1, 0); break;
                case Key.Up: if (row > 0) row--; break;
                case Key.Down: if (row < _tileRows.Count - 1) row++; break;
                case Key.Home: (row, column) = (0, 0); break;
                case Key.End: row = _tileRows.Count - 1; column = _tileRows[row].Length - 1; break;
            }
            column = Math.Min(column, _tileRows[row].Length - 1);
        }
        var target = _tileRows[row][column];
        if (!extend) _selected.Clear();
        _selected.Add(target.Path);
        _anchor = target.Path;
        SelectionChanged();
        BringIntoView(target.Path);
    }

    private void BringIntoView(string path)
    {
        if (_list.ItemsSource is not List<object> rows || _panel is null) return;
        var index = rows.FindIndex(r => r switch
        {
            TileRow tiles => tiles.Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)),
            ListRow line => string.Equals(line.Item.Path, path, StringComparison.OrdinalIgnoreCase),
            _ => false
        });
        if (index >= 0) _panel.BringIndexIntoViewPublic(index);
    }

    private void UpdateStatus()
    {
        var parts = new List<string> { L($"{_shown.Count} öğe", _shown.Count == 1 ? "1 item" : $"{_shown.Count} items") };
        var selected = _shown.Where(s => _selected.Contains(s.Path)).ToList();
        if (selected.Count > 0)
        {
            var size = selected.Sum(s => s.SizeOrLength);
            parts.Add(L($"{selected.Count} seçili", $"{selected.Count} selected") + (size > 0 ? $" ({FileKinds.Size(size)})" : ""));
        }
        try
        {
            if (Path.GetPathRoot(_path) is { Length: > 0 } root && new DriveInfo(root) is { IsReady: true } drive)
                parts.Add(L($"{FileKinds.Size(drive.AvailableFreeSpace)} boş", $"{FileKinds.Size(drive.AvailableFreeSpace)} free"));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
        _status.Text = string.Join("  ·  ", parts);
    }

    // ---- Path bar -------------------------------------------------------------------------------

    private void BuildCrumbs()
    {
        _crumbHost.Child = _crumbs;
        _crumbs.Children.Clear();
        var parts = new List<(string Name, string Path)>();
        for (var path = _path; path is not null; path = Path.GetDirectoryName(path))
            parts.Insert(0, (DisplayName(path), path));
        var start = Math.Max(0, parts.Count - 6);
        if (start > 0)
        {
            var more = Text("…", 13, brushKey: "TextSecondary");
            more.Margin = new Thickness(0, 0, 4, 0);
            _crumbs.Children.Add(more);
        }
        for (var i = start; i < parts.Count; i++)
        {
            var (name, path) = parts[i];
            var last = i == parts.Count - 1;
            var label = Text(name, 13, last ? FontWeights.SemiBold : FontWeights.Normal, last ? "TextPrimary" : "TextSecondary");
            var crumb = new Border { Child = label, Padding = new Thickness(6, 3, 6, 3), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            crumb.MouseEnter += (_, _) => crumb.SetResourceReference(Border.BackgroundProperty, "SurfaceHover");
            crumb.MouseLeave += (_, _) => crumb.Background = Brushes.Transparent;
            crumb.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (!last) Navigate(path);
            };
            _crumbs.Children.Add(crumb);
            if (last) continue;
            var arrow = FileKinds.Line(Geometry.Parse("M9 18l6-6-6-6"), 10, (Brush)FindResource("TextSecondary"), 2);
            arrow.Opacity = 0.6;
            arrow.Margin = new Thickness(2, 0, 2, 0);
            _crumbs.Children.Add(arrow);
        }
        var hint = Text(L("Ctrl+L ile yol yazın", "Ctrl+L to type a path"), 11, brushKey: "TextSecondary");
        hint.Opacity = 0.6;
        hint.Margin = new Thickness(18, 0, 0, 0);
        _crumbs.Children.Add(hint);
    }

    private void EditPath()
    {
        var box = new TextBox
        {
            Text = _path,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 13
        };
        box.SetResourceReference(ForegroundProperty, "TextPrimary");
        box.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty, "TextPrimary");
        var done = false;
        void Finish(bool go)
        {
            if (done) return;
            done = true;
            var text = box.Text;
            BuildCrumbs();
            if (go && !string.Equals(text, _path, StringComparison.OrdinalIgnoreCase)) Navigate(text);
            _list.Focus();
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Finish(true);
            else if (e.Key == Key.Escape) Finish(false);
            else return;
            e.Handled = true;
        };
        box.LostKeyboardFocus += (_, _) => Finish(false);
        _crumbHost.Child = box;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            box.Focus();
            box.SelectAll();
        });
    }
}

/// <summary>Folders Windows knows by id (Downloads has no SpecialFolder value).</summary>
internal static class KnownFolders
{
    public static readonly string? Downloads = Get(new Guid("374DE290-123F-4565-9164-39C4925E467B"));

    private static string? Get(Guid id)
    {
        if (SHGetKnownFolderPath(ref id, 0, 0, out var path) != 0) return null;
        try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(path); }
        finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem(path); }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, nint token, out nint path);
}
