using System.Collections.Specialized;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Windows;

namespace QiDock.Files;

/// <summary>
/// Copy, move, delete and rename through Windows' own file engine: its progress window, its "replace or skip"
/// questions, the Recycle Bin and undo all work as in Explorer. Each runs on its own thread so a long copy never
/// freezes the dock (which shares QiDock's window thread).
/// </summary>
internal static class FileOperations
{
    private const uint Move = 1, Copy = 2, Delete = 3;
    private const ushort AllowUndo = 0x40, RenameOnCollision = 0x8, NoConfirmMakeFolder = 0x200, WantNukeWarning = 0x4000;

    public static Task<bool> CopyAsync(IReadOnlyList<string> paths, string target, nint owner)
    {
        // Copying into the folder the items are already in makes "name - Kopya" instead of asking to replace.
        var sameFolder = paths.All(p => string.Equals(Path.GetDirectoryName(p), Path.TrimEndingDirectorySeparator(target), StringComparison.OrdinalIgnoreCase));
        return RunAsync(Copy, paths, target, owner, (ushort)(AllowUndo | NoConfirmMakeFolder | (sameFolder ? RenameOnCollision : 0)));
    }

    public static Task<bool> MoveAsync(IReadOnlyList<string> paths, string target, nint owner) =>
        RunAsync(Move, paths, target, owner, AllowUndo | NoConfirmMakeFolder);

    /// <summary>To the Recycle Bin, or gone for good when <paramref name="permanently"/> (Windows asks first).</summary>
    public static Task<bool> DeleteAsync(IReadOnlyList<string> paths, bool permanently, nint owner) =>
        RunAsync(Delete, paths, null, owner, permanently ? (ushort)0 : (ushort)(AllowUndo | WantNukeWarning));

    private static Task<bool> RunAsync(uint function, IReadOnlyList<string> paths, string? target, nint owner, ushort flags)
    {
        var done = new TaskCompletionSource<bool>();
        if (paths.Count == 0)
        {
            done.SetResult(false);
            return done.Task;
        }
        var thread = new Thread(() =>
        {
            var operation = new FileOperation
            {
                Window = owner,
                Function = function,
                From = string.Join('\0', paths) + "\0\0",
                To = target is null ? null : target + "\0\0",
                Flags = flags
            };
            var result = SHFileOperation(ref operation);
            done.SetResult(result == 0 && !operation.AnyOperationsAborted);
        }) { IsBackground = true, Name = "QiDock file operation" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    /// <summary>Renames in place; returns the new path, or an error text.</summary>
    public static (string? Path, string? Error) Rename(string path, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
            return (null, FileKinds.L("Bu ad kullanılamaz (\\ / : * ? \" < > | olamaz).", "That name can't be used (no \\ / : * ? \" < > |)."));
        var folder = Path.GetDirectoryName(path)!;
        var target = Path.Combine(folder, name);
        if (string.Equals(target, path, StringComparison.Ordinal)) return (path, null);
        var caseOnly = string.Equals(target, path, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
            return (null, FileKinds.L($"Bu klasörde zaten \"{name}\" var.", $"\"{name}\" already exists here."));
        try
        {
            if (Directory.Exists(path))
            {
                if (caseOnly)
                {
                    // Windows ignores a change of letter case alone; go through a temporary name.
                    var step = path + ".qidock-rename";
                    Directory.Move(path, step);
                    Directory.Move(step, target);
                }
                else Directory.Move(path, target);
            }
            else File.Move(path, target);
            return (target, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, FileKinds.L("Yeniden adlandırılamadı: ", "Could not rename: ") + ex.Message);
        }
    }

    /// <summary>A free name in a folder: "Yeni klasör", "Yeni klasör (2)"…</summary>
    public static string FreeName(string folder, string name, string extension = "")
    {
        var candidate = Path.Combine(folder, name + extension);
        for (var i = 2; File.Exists(candidate) || Directory.Exists(candidate); i++)
            candidate = Path.Combine(folder, $"{name} ({i}){extension}");
        return candidate;
    }

    // ---- Clipboard (understood by Explorer both ways) -------------------------------------------

    public static void ToClipboard(IReadOnlyList<string> paths, bool cut)
    {
        var list = new StringCollection();
        list.AddRange(paths.ToArray());
        var data = new DataObject();
        data.SetFileDropList(list);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
        try { Clipboard.SetDataObject(data, true); }
        catch (COMException) { }
    }

    public static (List<string> Paths, bool Cut) FromClipboard()
    {
        try
        {
            if (Clipboard.GetDataObject() is not { } data || !data.GetDataPresent(DataFormats.FileDrop)) return ([], false);
            var paths = (data.GetData(DataFormats.FileDrop) as string[])?.ToList() ?? [];
            var cut = false;
            if (data.GetData("Preferred DropEffect") is MemoryStream stream && stream.Length >= 4)
            {
                var bytes = new byte[4];
                stream.Position = 0;
                _ = stream.Read(bytes, 0, 4);
                var effect = BitConverter.ToInt32(bytes, 0);
                cut = (effect & 2) != 0 && (effect & 1) == 0;
            }
            return (paths, cut);
        }
        catch (Exception ex) when (ex is COMException or ExternalException or InvalidOperationException)
        {
            return ([], false);
        }
    }

    public static bool ClipboardHasFiles()
    {
        try { return Clipboard.ContainsFileDropList(); }
        catch (COMException) { return false; }
    }

    // ---- Zip (built into .NET, no extra code shipped) -------------------------------------------

    public static Task<string?> ZipAsync(IReadOnlyList<string> paths, string folder) => Task.Run(() =>
    {
        try
        {
            var name = paths.Count == 1 ? Path.GetFileNameWithoutExtension(paths[0].TrimEnd('\\')) : FileKinds.L("Arşiv", "Archive");
            var target = FreeName(folder, name, ".zip");
            using var archive = ZipFile.Open(target, ZipArchiveMode.Create);
            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                {
                    var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path))!;
                    foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                        archive.CreateEntryFromFile(file, Path.GetRelativePath(root, file).Replace('\\', '/'), CompressionLevel.Optimal);
                }
                else if (File.Exists(path))
                    archive.CreateEntryFromFile(path, Path.GetFileName(path), CompressionLevel.Optimal);
            }
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    });

    public static Task<string?> UnzipAsync(string zip) => Task.Run(() =>
    {
        try
        {
            var target = FreeName(Path.GetDirectoryName(zip)!, Path.GetFileNameWithoutExtension(zip));
            ZipFile.ExtractToDirectory(zip, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (string?)null;
        }
    });

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileOperation
    {
        public nint Window;
        public uint Function;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public nint NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref FileOperation operation);
}
