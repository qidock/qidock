using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using static QiDock.Files.FileKinds;

namespace QiDock.Files;

internal sealed partial class FilesWindow
{
    private readonly List<(Border Item, string Path)> _places = [];
    private StackPanel? _locations;
    private Border? _diskCard;

    // ---- Sidebar --------------------------------------------------------------------------------

    private FrameworkElement BuildSidebar(TextBox search)
    {
        var tint = new Border { Opacity = 0.5 };
        tint.SetResourceReference(Border.BackgroundProperty, "SurfaceHover");
        var edge = new Border { BorderThickness = new Thickness(0, 0, 1, 0) };
        edge.SetResourceReference(Border.BorderBrushProperty, "MenuDivider");

        var header = new StackPanel { Orientation = Orientation.Horizontal, Height = 52, Margin = new Thickness(18, 0, 0, 0) };
        var logo = FolderArt.Create(FolderColor(FileKind.Other), null, GlyphBox, 24);
        logo.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(logo);
        var title = Text(L("Dosyalar", "Files"), 16, FontWeights.Bold);
        title.Margin = new Thickness(10, 0, 0, 0);
        header.Children.Add(title);

        var searchIcon = FileKinds.Line(Geometry.Parse("M11 3a8 8 0 1 0 0 16a8 8 0 1 0 0-16z M21 21l-4.35-4.35"), 14, (Brush)FindResource("TextSecondary"), 2);
        searchIcon.Margin = new Thickness(0, 0, 8, 0);
        var hint = Text(L("Bu klasörde ara", "Search this folder"), 13, brushKey: "TextSecondary");
        hint.IsHitTestVisible = false;
        search.TextChanged += (_, _) => hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var keys = Text("Ctrl+F", 10.5, brushKey: "TextSecondary");
        keys.Opacity = 0.6;
        var field = new DockPanel();
        DockPanel.SetDock(searchIcon, Dock.Left);
        DockPanel.SetDock(keys, Dock.Right);
        field.Children.Add(searchIcon);
        field.Children.Add(keys);
        var input = new Grid();
        input.Children.Add(hint);
        input.Children.Add(search);
        field.Children.Add(input);
        var searchBox = new Border { Height = 34, Margin = new Thickness(12, 0, 12, 10), Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(9), Child = field };
        searchBox.SetResourceReference(Border.BackgroundProperty, "SurfaceHover");

        var list = new StackPanel { Margin = new Thickness(8, 0, 8, 12) };
        list.Children.Add(SectionTitle(L("Sık kullanılanlar", "Favourites")));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AddPlace(list, GlyphDesktop, L("Masaüstü", "Desktop"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        AddPlace(list, GlyphDocument, L("Belgeler", "Documents"), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        AddPlace(list, GlyphDownload, L("İndirilenler", "Downloads"), KnownFolders.Downloads);
        AddPlace(list, GlyphImage, L("Resimler", "Pictures"), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        AddPlace(list, GlyphAudio, L("Müzik", "Music"), Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        AddPlace(list, GlyphVideo, L("Videolar", "Videos"), Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        AddPlace(list, GlyphHome, Path.GetFileName(home), home);

        list.Children.Add(SectionTitle(L("Konumlar", "Locations")));
        _locations = new StackPanel();
        list.Children.Add(_locations);
        var trash = Place(GlyphTrash, L("Çöp Kutusu", "Recycle Bin"), null);
        trash.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            OpenItem("shell:RecycleBinFolder");
        };
        list.Children.Add(trash);
        _ = LoadLocationsAsync();

        var scroller = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        _diskCard = new Border { Margin = new Thickness(12, 6, 12, 12) };

        var column = new Grid();
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        column.RowDefinitions.Add(new RowDefinition());
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        column.Children.Add(header);
        Grid.SetRow(searchBox, 1);
        column.Children.Add(searchBox);
        Grid.SetRow(scroller, 2);
        column.Children.Add(scroller);
        Grid.SetRow(_diskCard, 3);
        column.Children.Add(_diskCard);

        var sidebar = new Grid();
        sidebar.Children.Add(tint);
        sidebar.Children.Add(edge);
        sidebar.Children.Add(column);
        return sidebar;
    }

    private static FrameworkElement SectionTitle(string text)
    {
        var title = Text(text, 11.5, FontWeights.SemiBold, "TextSecondary");
        title.Margin = new Thickness(10, 14, 0, 6);
        title.Opacity = 0.85;
        return title;
    }

    private void AddPlace(Panel list, Geometry glyph, string name, string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        list.Children.Add(Place(glyph, name, path));
    }

    private Border Place(Geometry glyph, string name, string? path, string? note = null)
    {
        var line = new DockPanel();
        var icon = FileKinds.Line(glyph, 16, (Brush)FindResource("WidgetAccent"), 1.8);
        icon.Margin = new Thickness(0, 0, 11, 0);
        DockPanel.SetDock(icon, Dock.Left);
        line.Children.Add(icon);
        if (note is not null)
        {
            var extra = Text(note, 11, brushKey: "TextSecondary");
            extra.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(extra, Dock.Right);
            line.Children.Add(extra);
        }
        line.Children.Add(Text(name, 13.5));
        var item = new Border { Child = line, Height = 34, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = path };
        item.MouseEnter += (_, _) =>
        {
            if (!IsCurrentPlace(path)) item.SetResourceReference(Border.BackgroundProperty, "SurfaceHover");
        };
        item.MouseLeave += (_, _) =>
        {
            if (!IsCurrentPlace(path)) item.Background = Brushes.Transparent;
        };
        if (path is not null)
        {
            item.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                Navigate(path);
            };
            _places.Add((item, path));
        }
        return item;
    }

    private bool IsCurrentPlace(string? path) => path is not null && string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(_path), StringComparison.OrdinalIgnoreCase);

    private void HighlightSidebar()
    {
        foreach (var (item, path) in _places)
            item.Background = IsCurrentPlace(path) ? SelectedFill : Brushes.Transparent;
        UpdateDiskCard();
    }

    /// <summary>Drives and cloud folders, read in the background (a sleeping network drive can take seconds).</summary>
    private async Task LoadLocationsAsync()
    {
        var places = await Task.Run(() =>
        {
            var found = new List<(Geometry Glyph, string Name, string Path, string? Note)>();
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var variable in new[] { "OneDriveCommercial", "OneDriveConsumer", "OneDrive" })
            {
                var path = Environment.GetEnvironmentVariable(variable);
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && found.All(f => !string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
                    found.Add((GlyphCloud, Path.GetFileName(path).Replace(" - ", " · "), path, null));
            }
            var iCloud = Path.Combine(home, "iCloudDrive");
            if (Directory.Exists(iCloud)) found.Add((GlyphCloud, "iCloud Drive", iCloud, null));
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType is DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown || !drive.IsReady) continue;
                    var letter = drive.Name.TrimEnd('\\');
                    var label = drive.VolumeLabel;
                    if (label.Equals("Google Drive", StringComparison.OrdinalIgnoreCase))
                    {
                        var mine = Directory.GetDirectories(drive.Name).FirstOrDefault(d => Path.GetFileName(d) is "My Drive" or "Drive'ım");
                        found.Add((GlyphCloud, "Google Drive", mine ?? drive.Name, null));
                        continue;
                    }
                    var glyph = drive.DriveType == DriveType.Removable ? GlyphUsb : GlyphDrive;
                    var name = label.Length > 0 ? $"{label} ({letter})" : drive.DriveType == DriveType.Removable ? L($"USB bellek ({letter})", $"USB drive ({letter})") : L($"Yerel Disk ({letter})", $"Local Disk ({letter})");
                    found.Add((glyph, name, drive.Name, FileKinds.Size(drive.AvailableFreeSpace)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return found;
        });
        if (_locations is null) return;
        foreach (var (glyph, name, path, note) in places) _locations.Children.Add(Place(glyph, name, path, note));
        HighlightSidebar();
    }

    /// <summary>The bottom card: the open folder's drive with its free space as a bar.</summary>
    private void UpdateDiskCard()
    {
        if (_diskCard is null) return;
        try
        {
            if (Path.GetPathRoot(_path) is not { Length: > 0 } root || new DriveInfo(root) is not { IsReady: true } drive || drive.TotalSize <= 0)
            {
                _diskCard.Child = null;
                return;
            }
            var used = 1 - (double)drive.AvailableFreeSpace / drive.TotalSize;
            var name = Text(drive.VolumeLabel.Length > 0 ? drive.VolumeLabel : L("Yerel Disk", "Local Disk") + $" ({root.TrimEnd('\\')})", 12.5, FontWeights.SemiBold);
            var free = Text(L($"{FileKinds.Size(drive.AvailableFreeSpace)} boş · {FileKinds.Size(drive.TotalSize)}", $"{FileKinds.Size(drive.AvailableFreeSpace)} free of {FileKinds.Size(drive.TotalSize)}"), 11, brushKey: "TextSecondary");
            var bar = Bar(used, used > 0.9 ? Color.FromRgb(0xFF, 0x45, 0x3A) : Color.FromRgb(0x0A, 0x84, 0xFF));
            bar.Margin = new Thickness(0, 7, 0, 5);
            var stack = new StackPanel();
            stack.Children.Add(name);
            stack.Children.Add(bar);
            stack.Children.Add(free);
            _diskCard.Child = stack;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _diskCard.Child = null;
        }
    }

    private static Grid Bar(double share, Color color)
    {
        var grid = new Grid { Height = 5 };
        var track = new Border { CornerRadius = new CornerRadius(2.5) };
        track.SetResourceReference(Border.BackgroundProperty, "SurfaceHover");
        grid.Children.Add(track);
        var fill = new Border
        {
            CornerRadius = new CornerRadius(2.5),
            Background = Frozen(new SolidColorBrush(color)),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        grid.SizeChanged += (_, _) => fill.Width = Math.Max(share > 0 ? 5 : 0, grid.ActualWidth * Math.Clamp(share, 0, 1));
        grid.Children.Add(fill);
        return grid;
    }

    // ---- Details panel --------------------------------------------------------------------------

    private void UpdateDetails()
    {
        if (!_state.Details || _details is null) return;
        var selected = _shown.Where(s => _selected.Contains(s.Path)).ToList();
        _details.Child = selected.Count == 1 ? ItemDetails(selected[0]) : FolderDetails(selected.Count > 1 ? selected : null);
    }

    /// <summary>The open folder (or several selected items): their total size and what takes the space.</summary>
    private FrameworkElement FolderDetails(List<FileEntry>? selection)
    {
        var items = selection ?? _entries;
        var stack = new StackPanel();
        var heading = Text(selection is null ? _title.Text : L($"{selection.Count} öğe seçili", $"{selection.Count} items selected"), 16, FontWeights.SemiBold);
        stack.Children.Add(heading);

        var folders = items.Where(e => e.IsFolder).ToList();
        var pending = folders.Count(f => f.Size is null);
        var total = items.Sum(e => e.SizeOrLength);
        var size = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        size.Children.Add(Text(FileKinds.Size(total), 26, FontWeights.SemiBold));
        var waiting = pending > 0 && !_sizesWanted;
        var suffix = Text(waiting ? L("  dosyalar", "  in files") : pending > 0 ? L("  hesaplanıyor…", "  adding up…") : L("  toplam", "  in all"), 13, brushKey: "TextSecondary");
        suffix.VerticalAlignment = VerticalAlignment.Bottom;
        suffix.Margin = new Thickness(0, 0, 0, 5);
        size.Children.Add(suffix);
        stack.Children.Add(size);
        var files = items.Count - folders.Count;
        var counts = new List<string>();
        if (folders.Count > 0) counts.Add(L($"{folders.Count} klasör", folders.Count == 1 ? "1 folder" : $"{folders.Count} folders"));
        if (files > 0) counts.Add(L($"{files} dosya", files == 1 ? "1 file" : $"{files} files"));
        if (counts.Count > 0) stack.Children.Add(Text(string.Join(" · ", counts), 12.5, brushKey: "TextSecondary"));
        if (waiting)
        {
            var note = new TextBlock
            {
                Text = L("Burada klasör boyutları kendiliğinden hesaplanmaz: tüm diski okumak dakikalar sürebilir.",
                    "Folder sizes are not added up here by themselves: reading the whole disk can take minutes."),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 14, 0, 10)
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            stack.Children.Add(note);
            var start = Widgets.Ui.Pill(L("Boyutları hesapla", "Add up sizes"), (_, _) => AddUpSizesNow());
            start.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(start);
        }

        var biggest = items.Where(e => e.SizeOrLength > 0).OrderByDescending(e => e.SizeOrLength).ToList();
        if (biggest.Count > 0)
        {
            var title = Text(L("YERİ NE KAPLIYOR", "WHAT TAKES THE SPACE"), 11, FontWeights.SemiBold, "TextSecondary");
            title.Margin = new Thickness(0, 24, 0, 10);
            stack.Children.Add(title);
            var largest = Math.Max(1, biggest[0].SizeOrLength);
            foreach (var entry in biggest.Take(7))
            {
                var line = new DockPanel();
                var icon = entry.IsFolder ? FolderPicture(entry, 18, small: true) : FilePicture(entry, 16);
                icon.Margin = new Thickness(0, 0, 9, 0);
                DockPanel.SetDock(icon, Dock.Left);
                line.Children.Add(icon);
                var amount = Text(FileKinds.Size(entry.SizeOrLength), 12, brushKey: "TextSecondary");
                amount.Margin = new Thickness(8, 0, 0, 0);
                DockPanel.SetDock(amount, Dock.Right);
                line.Children.Add(amount);
                line.Children.Add(Text(entry.Name, 13, FontWeights.Medium));
                var color = entry.IsFolder ? FolderColor(entry.Peek is { Count: 0 } ? null : entry.Peek?.Kind ?? FileKind.Other) : FolderColor(entry.Kind);
                var bar = Bar((double)entry.SizeOrLength / largest, color);
                bar.Margin = new Thickness(0, 6, 0, 0);
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 12), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = entry.Path };
                row.Children.Add(line);
                row.Children.Add(bar);
                Interactive(row, entry);
                stack.Children.Add(row);
            }
            if (biggest.Count > 7)
                stack.Children.Add(Text(L($"ve {biggest.Count - 7} tane daha", $"and {biggest.Count - 7} more"), 12, brushKey: "TextSecondary"));
        }
        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    }

    /// <summary>One selected item: a big picture and its facts.</summary>
    private FrameworkElement ItemDetails(FileEntry entry)
    {
        var stack = new StackPanel();
        FrameworkElement picture = entry.IsFolder ? FolderPicture(entry, 170) : FilePicture(entry, 200);
        picture.HorizontalAlignment = HorizontalAlignment.Center;
        picture.Margin = new Thickness(0, 6, 0, 16);
        stack.Children.Add(picture);
        var name = new TextBlock { Text = entry.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        stack.Children.Add(name);
        var kind = Text(entry.IsFolder ? L("Klasör", "Folder") : KindLabel(entry), 12.5, brushKey: "TextSecondary");
        kind.HorizontalAlignment = HorizontalAlignment.Center;
        kind.Margin = new Thickness(0, 3, 0, 18);
        stack.Children.Add(kind);
        void Fact(string label, string value)
        {
            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 9) };
            var key = Text(label, 12.5, brushKey: "TextSecondary");
            DockPanel.SetDock(key, Dock.Left);
            line.Children.Add(key);
            var text = Text(value, 12.5);
            text.HorizontalAlignment = HorizontalAlignment.Right;
            text.Margin = new Thickness(12, 0, 0, 0);
            line.Children.Add(text);
            stack.Children.Add(line);
        }
        if (entry.IsFolder)
        {
            if (Count(entry.Peek) is { } count) Fact(L("İçinde", "Contains"), count);
            Fact(L("Boyut", "Size"), entry.Size is { } size ? FileKinds.Size(size) : _sizesWanted ? L("hesaplanıyor…", "adding up…") : "—");
        }
        else Fact(L("Boyut", "Size"), FileKinds.Size(entry.Length));
        Fact(L("Değiştirilme", "Modified"), Date(entry.Modified));
        try
        {
            var created = entry.IsFolder ? Directory.GetCreationTime(entry.Path) : File.GetCreationTime(entry.Path);
            Fact(L("Oluşturulma", "Created"), Date(created));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var where = new TextBlock { Text = Path.GetDirectoryName(entry.Path), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 16), Opacity = 0.8 };
        where.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        stack.Children.Add(where);
        var open = Widgets.Ui.Pill(L("Aç", "Open"), (_, _) => Open(entry));
        open.HorizontalAlignment = HorizontalAlignment.Stretch;
        stack.Children.Add(open);
        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    }
}
