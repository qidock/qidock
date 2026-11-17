using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QiDock.Models;
using QiDock.Services;

namespace QiDock.Widgets;

/// <summary>
/// A folder (the Desktop by default) as a tile with its item count; the card shows its files and
/// folders as a grid you can browse into, open, or drag out to other apps.
/// </summary>
public sealed class FolderWidget : DockWidget
{
    private const int MaxItems = 150;
    private readonly Button _tile;
    private readonly WidgetPopup _popup;
    private readonly TextBlock _name;
    private readonly TextBlock _count;
    private string _root = DefaultFolder;
    private string _current = DefaultFolder;
    private int _build;

    private static string DefaultFolder => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public FolderWidget()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = Ui.Glyph("IconFolder", 26, Ui.Vertical("#FF6CC4FF", "#FF2E8FE8"));
        icon.Margin = new Thickness(0, 0, 9, 0);
        row.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _name = Ui.Text("", 12.5, FontWeights.SemiBold);
        _name.MaxWidth = 110;
        _count = Ui.Text("", 10.5, brushKey: "TextSecondary");
        text.Children.Add(_name);
        text.Children.Add(_count);
        row.Children.Add(text);
        _tile = Ui.Tile(row, (_, _) => Toggle());
        _popup = new WidgetPopup(_tile);
        UiText.Current.PropertyChanged += (_, _) => RefreshTile();
    }

    public override string Id => WidgetIds.Folder;
    public override FrameworkElement View => _tile;
    public override bool IsPopupOpen => _popup.IsOpen;
    public override void ClosePopup() => _popup.Close();
    public override void Start() => RefreshTile();
    public override void Stop() => _popup.Close();

    public override void SetLive(bool live)
    {
        if (live) RefreshTile();
    }

    public override void ApplySettings(WidgetSettings settings)
    {
        var root = string.IsNullOrWhiteSpace(settings.FolderPath) || !Directory.Exists(settings.FolderPath) ? DefaultFolder : settings.FolderPath;
        if (string.Equals(root, _root, StringComparison.OrdinalIgnoreCase)) return;
        _root = _current = root;
        RefreshTile();
        if (_popup.IsOpen) BuildPopup();
    }

    private void RefreshTile()
    {
        _name.Text = DisplayName(_root);
        var count = 0;
        try
        {
            count = Directory.EnumerateFileSystemEntries(_root, "*", new EnumerationOptions { AttributesToSkip = FileAttributes.Hidden | FileAttributes.System }).Take(9999).Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _count.Text = UiText.Format("FolderItems", count);
        _tile.ToolTip = _root;
    }

    private static string DisplayName(string path)
    {
        if (string.Equals(path.TrimEnd('\\'), DefaultFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return UiText.Get("FolderDesktop");
        var name = Path.GetFileName(path.TrimEnd('\\'));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private void Toggle()
    {
        if (!_popup.IsOpen)
        {
            _current = _root;
            BuildPopup();
        }
        _popup.Toggle();
    }

    private void BuildPopup()
    {
        var build = ++_build;
        var root = new StackPanel();
        var buttons = new List<UIElement>
        {
            Ui.IconButton(Ui.Icon("IconFolderOpen", 13), (_, _) => ChooseFolder(), UiText.Get("FolderChoose")),
            Ui.IconButton(Ui.Icon("IconOpen", 13), (_, _) => { _popup.Close(); Ui.Open(_current); }, UiText.Get("FolderOpenExplorer"))
        };
        var header = Ui.Header("", () => _popup.Close(), buttons.ToArray());
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        if (!string.Equals(_current, _root, StringComparison.OrdinalIgnoreCase))
        {
            var back = Ui.IconButton(Ui.Icon("IconChevronLeft", 13), (_, _) => Navigate(Path.GetDirectoryName(_current.TrimEnd('\\')) ?? _root), UiText.Get("FolderBack"));
            back.Margin = new Thickness(-4, 0, 4, 0);
            title.Children.Add(back);
            title.Children.Add(Ui.Text(DisplayName(_root) + "  ›  ", 13, brushKey: "TextSecondary"));
        }
        title.Children.Add(Ui.Text(DisplayName(_current), 13, FontWeights.SemiBold));
        header.Children.RemoveAt(header.Children.Count - 1);
        header.Children.Add(title);
        root.Children.Add(header);

        var entries = ReadEntries(_current);
        // Five cells fill the card edge to edge, lined up with the title; no scroll bar, just a fade where more follows.
        var grid = new WrapPanel { Width = 480, Margin = new Thickness(-2, 0, -2, 0) };
        var pending = new List<(Border Host, string Path)>();
        foreach (var entry in entries)
        {
            var (cell, iconHost) = Cell(entry);
            grid.Children.Add(cell);
            pending.Add((iconHost, entry.FullName));
        }
        if (entries.Count == 0)
        {
            var empty = Ui.Text(UiText.Get("FolderEmpty"), 12, brushKey: "TextSecondary");
            empty.Margin = new Thickness(4, 10, 0, 16);
            root.Children.Add(empty);
        }
        else
        {
            root.Children.Add(Ui.FadingScroller(grid, 360));
        }
        _popup.SetContent(root, 510);

        // Shell icons are slow-ish; fill them in small batches after the card is up.
        var index = 0;
        void LoadBatch()
        {
            if (build != _build) return;
            for (var end = Math.Min(index + 10, pending.Count); index < end; index++)
                pending[index].Host.Child = Ui.FileIcon(pending[index].Path, 44);
            if (index < pending.Count) _tile.Dispatcher.BeginInvoke(DispatcherPriority.Background, LoadBatch);
        }
        _tile.Dispatcher.BeginInvoke(DispatcherPriority.Background, LoadBatch);
    }

    private static List<FileSystemInfo> ReadEntries(string folder)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            var options = new EnumerationOptions { AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            return info.EnumerateFileSystemInfos("*", options)
                .OrderByDescending(e => e is DirectoryInfo)
                .ThenByDescending(e => e.LastWriteTime)
                .Take(MaxItems)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    private (FrameworkElement Cell, Border IconHost) Cell(FileSystemInfo entry)
    {
        var iconHost = new Border { Width = 46, Height = 46, HorizontalAlignment = HorizontalAlignment.Center };
        iconHost.Child = Ui.Icon(entry is DirectoryInfo ? "IconFolder" : "IconFile", 30, brushKey: "TextSecondary");
        var name = Ui.Text(entry is FileInfo ? Path.GetFileNameWithoutExtension(entry.Name) : entry.Name, 11, FontWeights.SemiBold);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        name.MaxWidth = 82;
        name.Margin = new Thickness(0, 5, 0, 0);
        var age = Ui.Text(Ui.Ago(entry.LastWriteTime), 9.5, brushKey: "TextSecondary");
        age.HorizontalAlignment = HorizontalAlignment.Center;
        var stack = new StackPanel { Margin = new Thickness(0, 8, 0, 6) };
        stack.Children.Add(iconHost);
        stack.Children.Add(name);
        stack.Children.Add(age);
        var cell = new Button { Content = stack, Width = 92, Margin = new Thickness(2), ToolTip = entry.FullName, Padding = new Thickness(2, 0, 2, 0), Height = double.NaN };
        cell.SetResourceReference(FrameworkElement.StyleProperty, "WidgetTileButton");
        cell.Height = double.NaN;
        cell.Click += (_, _) =>
        {
            if (entry is DirectoryInfo) Navigate(entry.FullName);
            else
            {
                _popup.Close();
                Ui.Open(entry.FullName);
            }
        };
        AttachDragOut(cell, entry.FullName);
        return (cell, iconHost);
    }

    /// <summary>Drag a file or folder from the card into another app or folder.</summary>
    internal static void AttachDragOut(FrameworkElement element, string path)
    {
        Point? start = null;
        element.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(element);
        element.PreviewMouseMove += (_, e) =>
        {
            if (start is not { } origin || e.LeftButton != MouseButtonState.Pressed) return;
            var position = e.GetPosition(element);
            if (Math.Abs(position.X - origin.X) < 6 && Math.Abs(position.Y - origin.Y) < 6) return;
            start = null;
            var data = new DataObject(DataFormats.FileDrop, new[] { path });
            DragDrop.DoDragDrop(element, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        };
        element.PreviewMouseLeftButtonUp += (_, _) => start = null;
    }

    private void Navigate(string folder)
    {
        if (!Directory.Exists(folder)) return;
        _current = folder;
        BuildPopup();
    }

    private void ChooseFolder()
    {
        _popup.Close();
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = UiText.Get("FolderChoose"), InitialDirectory = _root };
        if (dialog.ShowDialog() != true) return;
        var path = dialog.FolderName;
        UpdateSettings(s => s with { FolderPath = string.Equals(path, DefaultFolder, StringComparison.OrdinalIgnoreCase) ? null : path });
    }
}
