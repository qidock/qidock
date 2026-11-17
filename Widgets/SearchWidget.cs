using System.IO;
using System.Windows;
using System.Windows.Controls;
using QiDock.Models;
using QiDock.Services;

namespace QiDock.Widgets;

/// <summary>
/// The search tile: opens the same search as Alt+Shift+Space (apps, files, calculator), so there is one search
/// everywhere. <see cref="Collect"/> builds that search's list of apps and files.
/// </summary>
public sealed class SearchWidget : DockWidget
{
    private const int MaxIndex = 150_000;
    private readonly Button _tile;

    internal sealed record Entry(string Name, string Path, bool IsApp);

    public SearchWidget()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = Ui.Icon("IconSearch", 14, brushKey: "TextSecondary");
        icon.Margin = new Thickness(0, 0, 8, 0);
        row.Children.Add(icon);
        var hint = Ui.Text(UiText.Get("SearchHint"), 12, brushKey: "TextSecondary");
        hint.Width = 110;
        row.Children.Add(hint);
        _tile = Ui.Tile(row, (_, _) => QuickSearchWindow.Toggle(), 12);
        _tile.ToolTip = UiText.Get("SearchTileTip");
        UiText.Current.PropertyChanged += (_, _) =>
        {
            hint.Text = UiText.Get("SearchHint");
            _tile.ToolTip = UiText.Get("SearchTileTip");
        };
    }

    public override string Id => WidgetIds.Search;
    public override FrameworkElement View => _tile;

    internal static List<Entry> Collect(CancellationToken cancel)
    {
        var list = new List<Entry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Apps first: Start menu shortcuts.
        foreach (var start in new[] { Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.StartMenu })
        {
            foreach (var path in Enumerate(Environment.GetFolderPath(start), "*.lnk", 6, cancel))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(path);
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || name.Contains("kaldır", StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Add(name)) list.Add(new Entry(name, path, true));
            }
        }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            System.IO.Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        };
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root) || !done.Add(root)) continue;
            foreach (var path in Enumerate(root, "*", 7, cancel))
            {
                list.Add(new Entry(System.IO.Path.GetFileName(path), path, false));
                if (list.Count >= MaxIndex) return list;
            }
        }
        return list;
    }

    private static IEnumerable<string> Enumerate(string root, string pattern, int depth, CancellationToken cancel)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) yield break;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = depth,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };
        IEnumerator<string> items;
        try
        {
            items = Directory.EnumerateFileSystemEntries(root, pattern, options).GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        using (items)
        {
            while (true)
            {
                if (cancel.IsCancellationRequested) yield break;
                bool next;
                try { next = items.MoveNext(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
                if (!next) yield break;
                var path = items.Current;
                // Skip build and package caches that flood results.
                if (path.Contains("\\node_modules\\", StringComparison.OrdinalIgnoreCase) || path.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase)) continue;
                yield return path;
            }
        }
    }
}
