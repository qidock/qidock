using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using static QiDock.Files.FileKinds;

namespace QiDock.Files;

/// <summary>
/// What can be done with files: copy, cut, paste, delete, rename, new folder, drag and drop, and QiDock's own
/// right-click menu. The Windows right-click menu is left out on purpose: it loads other programs' add-ons (archivers,
/// cloud drives…) into QiDock's process, which costs memory and can take the dock down when one of them crashes.
/// </summary>
internal sealed partial class FilesWindow
{
    private string? _renaming;
    private TextBox? _renameBox;
    private Point _pressedAt;
    private FileEntry? _pressed;
    private bool _selectOnRelease;

    private nint Handle => new WindowInteropHelper(this).Handle;

    private List<FileEntry> SelectedEntries() => _shown.Where(s => _selected.Contains(s.Path)).ToList();

    private List<string> SelectedPaths() => SelectedEntries().Select(s => s.Path).ToList();

    /// <summary>Keys for file work; returns whether the key was used.</summary>
    private bool OnOperationKey(Key key)
    {
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (key)
        {
            case Key.C when control && shift: CopyPaths(); return true;
            case Key.C when control: ToClipboard(cut: false); return true;
            case Key.X when control: ToClipboard(cut: true); return true;
            case Key.V when control: Paste(_path); return true;
            case Key.Delete: Delete(permanently: shift); return true;
            case Key.F2: BeginRename(); return true;
            case Key.N when control && shift: NewFolder(); return true;
            default: return false;
        }
    }

    // ---- Clipboard ------------------------------------------------------------------------------

    private void ToClipboard(bool cut)
    {
        var paths = SelectedPaths();
        if (paths.Count == 0) return;
        FileOperations.ToClipboard(paths, cut);
        ShowStatusMessage(cut
            ? L($"{paths.Count} öğe kesildi; yapıştırınca taşınacak.", $"{paths.Count} cut; they move when pasted.")
            : L($"{paths.Count} öğe kopyalandı.", $"{paths.Count} copied."));
    }

    private void CopyPaths()
    {
        var paths = SelectedPaths();
        if (paths.Count == 0) paths = [_path];
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, paths.Select(p => p.Contains(' ') ? $"\"{p}\"" : p)));
            ShowStatusMessage(L("Yol kopyalandı.", "Path copied."));
        }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private async void Paste(string target)
    {
        var (paths, cut) = FileOperations.FromClipboard();
        if (paths.Count == 0) return;
        if (cut && paths.Any(p => IsInside(target, p)))
        {
            ShowStatusMessage(L("Bir klasör kendi içine taşınamaz.", "A folder can't be moved into itself."));
            return;
        }
        var done = cut ? await FileOperations.MoveAsync(paths, target, Handle) : await FileOperations.CopyAsync(paths, target, Handle);
        if (done && cut)
        {
            try { Clipboard.Clear(); }
            catch (System.Runtime.InteropServices.COMException) { }
        }
        AfterChange(target, paths);
    }

    /// <summary>Refreshes and selects what just arrived (when it landed in the open folder).</summary>
    private void AfterChange(string target, IEnumerable<string> arrived)
    {
        FolderScanner.Forget(target);
        if (!string.Equals(Path.TrimEndingDirectorySeparator(target), _path, StringComparison.OrdinalIgnoreCase)) return;
        _selected.Clear();
        foreach (var path in arrived) _selected.Add(Path.Combine(_path, Path.GetFileName(path)));
        Load(keepScroll: true);
    }

    private static bool IsInside(string target, string path) =>
        string.Equals(target, path, StringComparison.OrdinalIgnoreCase)
        || target.StartsWith(Path.TrimEndingDirectorySeparator(path) + "\\", StringComparison.OrdinalIgnoreCase);

    // ---- Delete, new folder, rename -------------------------------------------------------------

    private async void Delete(bool permanently)
    {
        var paths = SelectedPaths();
        if (paths.Count == 0) return;
        // Move the selection on to the next item, so a run of deletes goes smoothly.
        var next = _shown.FirstOrDefault(s => !_selected.Contains(s.Path) && _shown.IndexOf(s) > _shown.FindIndex(x => _selected.Contains(x.Path)));
        if (await FileOperations.DeleteAsync(paths, permanently, Handle))
        {
            _selected.Clear();
            if (next is not null) _selected.Add(next.Path);
        }
        FolderScanner.Forget(_path);
        Load(keepScroll: true);
    }

    private void NewFolder()
    {
        var path = FileOperations.FreeName(_path, L("Yeni klasör", "New folder"));
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowStatusMessage(L("Klasör oluşturulamadı: ", "Could not create the folder: ") + ex.Message);
            return;
        }
        if (_search.Text.Length > 0) _search.Text = "";
        _selected.Clear();
        _selected.Add(path);
        _anchor = path;
        _renaming = path;
        Load(keepScroll: true, select: path);
    }

    private void BeginRename()
    {
        var selected = SelectedEntries();
        if (selected.Count != 1) return;
        _renaming = selected[0].Path;
        BringIntoView(_renaming);
        RedrawVisible();
    }

    /// <summary>The name box shown in place of a name while renaming.</summary>
    private TextBox RenameBox(FileEntry entry, bool centered)
    {
        var box = new TextBox
        {
            Text = entry.Name,
            FontSize = centered ? 12.5 : 13,
            TextAlignment = centered ? TextAlignment.Center : TextAlignment.Left,
            TextWrapping = centered ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MaxHeight = 52,
            Padding = new Thickness(2, 1, 2, 1),
            Margin = centered ? new Thickness(0, 4, 0, 0) : new Thickness(0),
            BorderThickness = new Thickness(1),
            BorderBrush = SelectedName,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        box.SetResourceReference(BackgroundProperty, "SettingsBackground");
        box.SetResourceReference(ForegroundProperty, "TextPrimary");
        box.SetResourceReference(TextBoxBase.CaretBrushProperty, "TextPrimary");
        var done = false;
        void Finish(bool keep)
        {
            if (done) return;
            done = true;
            _renaming = null;
            _renameBox = null;
            if (keep && box.Text.Trim() != entry.Name)
            {
                var (renamed, error) = FileOperations.Rename(entry.Path, box.Text);
                if (error is not null) ShowStatusMessage(error);
                if (renamed is not null)
                {
                    _selected.Clear();
                    _selected.Add(renamed);
                    _anchor = renamed;
                    FolderScanner.Forget(_path);
                    Load(keepScroll: true, select: renamed);
                    _list.Focus();
                    return;
                }
            }
            RedrawVisible();
            _list.Focus();
        }
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Finish(true);
            else if (e.Key == Key.Escape) Finish(false);
            else return;
            e.Handled = true;
        };
        box.LostKeyboardFocus += (_, _) => Finish(true);
        box.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _renameBox = box;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            box.Focus();
            // The name without its extension, as Explorer does.
            var stem = entry.IsFolder ? entry.Name.Length : Path.GetFileNameWithoutExtension(entry.Name).Length;
            box.Select(0, stem > 0 ? stem : entry.Name.Length);
        });
        return box;
    }

    // ---- Drag and drop --------------------------------------------------------------------------

    /// <summary>Press, drag out (to Explorer, a mail, the desktop…) and drop onto folders.</summary>
    private void DragAndDrop(FrameworkElement element, FileEntry entry)
    {
        element.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _pressed = entry;
            _pressedAt = e.GetPosition(this);
        };
        element.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _pressed != entry || _renaming is not null) return;
            var moved = e.GetPosition(this) - _pressedAt;
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _pressed = null;
            _selectOnRelease = false;
            if (!_selected.Contains(entry.Path))
            {
                _selected.Clear();
                _selected.Add(entry.Path);
                SelectionChanged();
            }
            var paths = SelectedPaths().ToArray();
            if (paths.Length == 0) return;
            var data = new DataObject(DataFormats.FileDrop, paths);
            try { DragDrop.DoDragDrop(element, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
            catch (System.Runtime.InteropServices.COMException) { }
        };
        element.MouseLeftButtonUp += (_, _) =>
        {
            if (_selectOnRelease && _pressed == entry)
            {
                _selectOnRelease = false;
                _selected.Clear();
                _selected.Add(entry.Path);
                _anchor = entry.Path;
                SelectionChanged();
            }
            _pressed = null;
        };
        if (!entry.IsFolder) return;
        element.AllowDrop = true;
        element.DragEnter += (_, e) =>
        {
            if (DropEffect(e, entry.Path) != DragDropEffects.None && element is Border border) border.SetResourceReference(Border.BackgroundProperty, "SurfaceHover");
        };
        element.DragLeave += (_, _) =>
        {
            if (element is Border border && !_selected.Contains(entry.Path)) border.Background = Brushes.Transparent;
        };
        element.DragOver += (_, e) =>
        {
            e.Effects = DropEffect(e, entry.Path);
            e.Handled = true;
        };
        element.Drop += (_, e) =>
        {
            e.Handled = true;
            if (element is Border border && !_selected.Contains(entry.Path)) border.Background = Brushes.Transparent;
            DropInto(e, entry.Path);
        };
    }

    /// <summary>Drops onto empty space go into the open folder.</summary>
    private void AcceptDrops()
    {
        _list.AllowDrop = true;
        _list.DragOver += (_, e) =>
        {
            e.Effects = DropEffect(e, _path);
            e.Handled = true;
        };
        _list.Drop += (_, e) =>
        {
            e.Handled = true;
            DropInto(e, _path);
        };
    }

    private static string[] DroppedPaths(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] ?? [] : [];

    /// <summary>Same drive moves, another drive copies; Ctrl copies, Shift moves (as in Explorer).</summary>
    private static DragDropEffects DropEffect(DragEventArgs e, string target)
    {
        var paths = DroppedPaths(e);
        if (paths.Length == 0) return DragDropEffects.None;
        // Not into itself, and not back into the folder it came from.
        if (paths.Any(p => IsInside(target, p))) return DragDropEffects.None;
        if (paths.All(p => string.Equals(Path.GetDirectoryName(p), Path.TrimEndingDirectorySeparator(target), StringComparison.OrdinalIgnoreCase))
            && (e.KeyStates & DragDropKeyStates.ControlKey) == 0) return DragDropEffects.None;
        DragDropEffects wanted;
        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0) wanted = DragDropEffects.Copy;
        else if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0) wanted = DragDropEffects.Move;
        else wanted = string.Equals(Path.GetPathRoot(paths[0]), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase) ? DragDropEffects.Move : DragDropEffects.Copy;
        return (e.AllowedEffects & wanted) != 0 ? wanted : e.AllowedEffects & DragDropEffects.Copy;
    }

    private async void DropInto(DragEventArgs e, string target)
    {
        var effect = DropEffect(e, target);
        var paths = DroppedPaths(e).ToList();
        if (effect == DragDropEffects.None || paths.Count == 0) return;
        var done = effect == DragDropEffects.Move
            ? await FileOperations.MoveAsync(paths, target, Handle)
            : await FileOperations.CopyAsync(paths, target, Handle);
        if (done) AfterChange(target, paths);
    }

    // ---- Right-click menu -----------------------------------------------------------------------

    private void ItemMenu(FrameworkElement anchor, FileEntry entry)
    {
        if (!_selected.Contains(entry.Path))
        {
            _selected.Clear();
            _selected.Add(entry.Path);
            _anchor = entry.Path;
            SelectionChanged();
        }
        var selected = SelectedEntries();
        if (selected.Count == 0) return;
        var single = selected.Count == 1 ? selected[0] : null;
        var menu = NewMenu(anchor);

        Add(menu, L("Aç", "Open"), "IconOpen", () =>
        {
            if (single is not null) Open(single);
            else foreach (var file in selected.Where(s => !s.IsFolder)) OpenItem(file.Path);
        });
        if (single is { IsFolder: false })
            Add(menu, L("Birlikte aç…", "Open with…"), "IconApps", () => Run("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {single.Path}"));
        if (single is { IsFolder: true })
            Add(menu, L("Terminalde aç", "Open in Terminal"), "IconTerminal", () => OpenTerminal(single.Path));
        menu.Items.Add(new Separator());
        Add(menu, L("Kes", "Cut"), "IconScissors", () => ToClipboard(cut: true));
        Add(menu, L("Kopyala", "Copy"), "IconCopy", () => ToClipboard(cut: false));
        if (single is { IsFolder: true })
            Add(menu, L("Bu klasöre yapıştır", "Paste into folder"), "IconClipboard", () => Paste(single.Path), FileOperations.ClipboardHasFiles());
        Add(menu, L("Yolu kopyala", "Copy path"), "IconLink", CopyPaths);
        menu.Items.Add(new Separator());
        if (single is not null) Add(menu, L("Yeniden adlandır", "Rename"), "IconEdit", BeginRename);
        if (single is { IsFolder: false } && single.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            Add(menu, L("Buraya çıkar", "Extract here"), "IconArchive", () => Unzip(single.Path));
        else
            Add(menu, L("Sıkıştır (zip)", "Compress (zip)"), "IconArchive", () => Zip(selected.Select(s => s.Path).ToList()));
        Add(menu, L("Dock'a sabitle", "Pin to dock"), "IconPin", () => (Application.Current.MainWindow as MainWindow)?.PinToDock(selected.Select(s => s.Path)));
        Add(menu, L("Gezgin'de göster", "Show in Explorer"), "IconFolderOpen", () => Run("explorer.exe", $"/select,\"{selected[0].Path}\""));
        menu.Items.Add(new Separator());
        var trash = Add(menu, L("Çöp kutusuna taşı", "Move to Recycle Bin"), "IconTrash", () => Delete(permanently: false));
        trash.SetResourceReference(StyleProperty, "DestructiveMenuItem");
        menu.IsOpen = true;
    }

    private void FolderMenu()
    {
        if (_selected.Count > 0)
        {
            _selected.Clear();
            SelectionChanged();
        }
        var menu = NewMenu(_list);
        Add(menu, L("Yapıştır", "Paste"), "IconClipboard", () => Paste(_path), FileOperations.ClipboardHasFiles());
        Add(menu, L("Yeni klasör", "New folder"), "IconPlus", NewFolder);
        menu.Items.Add(new Separator());
        Add(menu, L("Terminalde aç", "Open in Terminal"), "IconTerminal", () => OpenTerminal(_path));
        Add(menu, L("Gezgin'de aç", "Open in Explorer"), "IconFolderOpen", () => Run("explorer.exe", $"\"{_path}\""));
        Add(menu, L("Yolu kopyala", "Copy path"), "IconLink", CopyPaths);
        menu.Items.Add(new Separator());
        Add(menu, L("Yenile", "Refresh"), "IconRotate", () =>
        {
            FolderScanner.Forget(_path);
            Load(keepScroll: true);
        });
        menu.IsOpen = true;
    }

    private static ContextMenu NewMenu(UIElement anchor) => new() { PlacementTarget = anchor, Placement = PlacementMode.MousePoint };

    private static MenuItem Add(ContextMenu menu, string header, string icon, Action click, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        if (Application.Current.TryFindResource(icon) is Geometry geometry) item.Icon = geometry;
        item.Click += (_, _) => click();
        menu.Items.Add(item);
        return item;
    }

    private async void Zip(List<string> paths)
    {
        ShowStatusMessage(L("Sıkıştırılıyor…", "Compressing…"));
        var zip = await FileOperations.ZipAsync(paths, _path);
        if (zip is null)
        {
            ShowStatusMessage(L("Sıkıştırılamadı.", "Could not compress."));
            return;
        }
        AfterChange(_path, [zip]);
    }

    private async void Unzip(string path)
    {
        ShowStatusMessage(L("Çıkarılıyor…", "Extracting…"));
        var folder = await FileOperations.UnzipAsync(path);
        if (folder is null)
        {
            ShowStatusMessage(L("Arşiv açılamadı.", "Could not extract."));
            return;
        }
        AfterChange(_path, [folder]);
    }

    private void OpenTerminal(string folder)
    {
        // Windows Terminal when there is one, otherwise PowerShell.
        if (!Run("wt.exe", $"-d \"{folder.TrimEnd('\\')}\\.\"", quiet: true))
            Run("powershell.exe", "-NoExit", workingDirectory: folder);
    }

    private bool Run(string file, string arguments, bool quiet = false, string? workingDirectory = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true, WorkingDirectory = workingDirectory ?? _path })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            if (!quiet) ShowStatusMessage(L("Açılamadı: ", "Could not open: ") + file);
            return false;
        }
    }
}
