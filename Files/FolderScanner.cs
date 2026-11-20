using System.Collections.Concurrent;
using System.IO;
using System.IO.Enumeration;

namespace QiDock.Files;

/// <summary>How many items a folder holds and what most of its files are.</summary>
internal sealed record FolderPeek(int Count, bool More, FileKind? Kind);

/// <summary>
/// Reads folders without slowing the window: a quick look inside each folder (count and main kind) and, for the
/// "what takes the space" panel, the full size of every folder. Sizes are remembered for the session.
/// </summary>
internal static class FolderScanner
{
    private const int PeekLimit = 999;
    private static readonly ConcurrentDictionary<string, (long Size, DateTime At)> Sizes = new(StringComparer.OrdinalIgnoreCase);

    public static readonly EnumerationOptions Visible = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        RecurseSubdirectories = false
    };

    public static FolderPeek Peek(string folder, bool showHidden)
    {
        try
        {
            var options = showHidden ? new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 } : Visible;
            var count = 0;
            var kinds = new int[8];
            var files = 0;
            var entries = new FileSystemEnumerable<(bool Folder, string Name)>(folder,
                (ref FileSystemEntry entry) => (entry.IsDirectory, entry.FileName.ToString()), options);
            foreach (var (isFolder, name) in entries)
            {
                if (++count > PeekLimit) return new FolderPeek(PeekLimit, true, Dominant(kinds, files));
                if (isFolder) continue;
                files++;
                kinds[(int)FileKinds.Of(name)]++;
            }
            return new FolderPeek(count, false, count == 0 ? null : Dominant(kinds, files));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new FolderPeek(0, false, null);
        }
    }

    /// <summary>The kind most files share; "other" when nothing stands out (or only folders inside).</summary>
    private static FileKind Dominant(int[] kinds, int files)
    {
        if (files == 0) return FileKind.Other;
        var best = 0;
        for (var i = 1; i < kinds.Length; i++)
            if (kinds[i] > kinds[best]) best = i;
        return best != (int)FileKind.Other && kinds[best] * 10 >= files * 4 ? (FileKind)best : FileKind.Other;
    }

    /// <summary>A size worked out earlier in this session (and not older than ten minutes).</summary>
    public static long? KnownSize(string folder) =>
        Sizes.TryGetValue(folder, out var known) && DateTime.Now - known.At < TimeSpan.FromMinutes(10) ? known.Size : null;

    public static long Size(string folder, CancellationToken token)
    {
        if (KnownSize(folder) is { } known) return known;
        long total = 0;
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint // links and cloud placeholders that point elsewhere
            };
            var lengths = new FileSystemEnumerable<long>(folder, (ref FileSystemEntry entry) => entry.Length, options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory
            };
            var step = 0;
            foreach (var length in lengths)
            {
                total += length;
                if ((++step & 1023) == 0) token.ThrowIfCancellationRequested();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        token.ThrowIfCancellationRequested();
        Sizes[folder] = (total, DateTime.Now);
        return total;
    }

    /// <summary>Forgets sizes inside a folder that just changed.</summary>
    public static void Forget(string folder)
    {
        foreach (var key in Sizes.Keys)
            if (key.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || folder.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                Sizes.TryRemove(key, out _);
    }
}
