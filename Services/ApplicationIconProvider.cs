using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace QiDock.Services;

public static class ApplicationIconProvider
{
    private const uint ShellIcon = 0x00000100;
    private const int TargetIconResolution = 128;

    public static BitmapSource? GetForFile(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            // Store apps (Calculator, Photos…) keep their icon in the package, not in the program file.
            if (GetPackagedLogo(path) is { } packaged) return packaged;
            var executableIcon = GetExecutableIcon(path);
            if (executableIcon is not null)
            {
                return executableIcon;
            }

            return null;
        }

        if (File.Exists(path))
        {
            var directIcon = GetExecutableIcon(path);
            if (directIcon is not null)
            {
                return directIcon;
            }
        }

        var fileInfo = new ShellFileInfo();
        var result = SHGetFileInfo(path, 0, ref fileInfo, (uint)Marshal.SizeOf<ShellFileInfo>(), ShellIcon);
        if (result == 0 || fileInfo.Icon == 0)
        {
            return null;
        }

        try
        {
            return ToBitmapSource(fileInfo.Icon);
        }
        finally
        {
            DestroyIcon(fileInfo.Icon);
        }
    }

    public static BitmapSource? GetForResource(string path, int index)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        return File.Exists(expanded) ? GetExecutableIcon(expanded, index) : null;
    }

    /// <summary>
    /// The logo a Store app shows in Windows' own taskbar: its package manifest names an image (Square44x44Logo) that
    /// exists in several sizes and variants; the largest "unplated" one (no coloured backplate) is used.
    /// </summary>
    private static BitmapSource? GetPackagedLogo(string executablePath)
    {
        try
        {
            var root = Path.GetDirectoryName(executablePath);
            for (var depth = 0; root is not null && depth < 4 && !File.Exists(Path.Combine(root, "AppxManifest.xml")); depth++)
                root = Path.GetDirectoryName(root);
            if (root is null || !File.Exists(Path.Combine(root, "AppxManifest.xml"))) return null;
            var relative = Path.GetRelativePath(root, executablePath);
            var manifest = System.Xml.Linq.XDocument.Load(Path.Combine(root, "AppxManifest.xml"));
            var applications = manifest.Descendants().Where(e => e.Name.LocalName == "Application").ToList();
            var application = applications.FirstOrDefault(e => string.Equals((string?)e.Attribute("Executable"), relative, StringComparison.OrdinalIgnoreCase))
                ?? (applications.Count == 1 ? applications[0] : null);
            var visuals = application?.Elements().FirstOrDefault(e => e.Name.LocalName == "VisualElements");
            var logo = (string?)visuals?.Attribute("Square44x44Logo") ?? (string?)visuals?.Attribute("Square150x150Logo");
            if (string.IsNullOrEmpty(logo)) return null;
            var logoPath = Path.Combine(root, logo);
            var folder = Path.GetDirectoryName(logoPath);
            if (folder is null || !Directory.Exists(folder)) return null;
            var stem = Path.GetFileNameWithoutExtension(logoPath);
            var extension = Path.GetExtension(logoPath);
            static int Number(string qualifiers, string key)
            {
                var at = qualifiers.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                if (at < 0) return 0;
                var digits = new string(qualifiers.Skip(at + key.Length).TakeWhile(char.IsDigit).ToArray());
                return int.TryParse(digits, out var value) ? value : 0;
            }
            var best = Directory.EnumerateFiles(folder, stem + ".*" + extension)
                .Select(file => (File: file, Qualifiers: Path.GetFileNameWithoutExtension(file)[stem.Length..]))
                .Where(c => !c.Qualifiers.Contains("contrast-", StringComparison.OrdinalIgnoreCase)
                    && !c.Qualifiers.Contains("lightunplated", StringComparison.OrdinalIgnoreCase))
                .Select(c => (c.File, Score:
                    (c.Qualifiers.Contains("altform-unplated", StringComparison.OrdinalIgnoreCase) ? 100000 : 0)
                    + Math.Min(Number(c.Qualifiers, "targetsize-"), 256) * 10
                    + Number(c.Qualifiers, "scale-")))
                .OrderByDescending(c => c.Score)
                .Select(c => c.File)
                .FirstOrDefault() ?? (File.Exists(logoPath) ? logoPath : null);
            if (best is null) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(best);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null; // an unreadable package falls back to the program file's own icon
        }
    }

    private static BitmapSource? GetExecutableIcon(string path, int index = 0)
    {
        // Primary path: Extract high-resolution icon resource (up to 128x128) for the large dock icons.
        var icons = new nint[1];
        var ids = new uint[1];
        try
        {
            var extracted = PrivateExtractIcons(path, index, TargetIconResolution, TargetIconResolution, icons, ids, 1, 0);
            if (extracted > 0 && icons[0] != 0)
            {
                try
                {
                    return ToBitmapSource(icons[0]);
                }
                finally
                {
                    DestroyIcon(icons[0]);
                }
            }
        }
        catch
        {
            // Fall through to ExtractIconEx when PrivateExtractIcons is unavailable
        }

        var count = ExtractIconEx(path, index, out var largeIcon, out var smallIcon, 1);
        if (count == 0 || (largeIcon == 0 && smallIcon == 0))
        {
            return null;
        }

        try
        {
            return ToBitmapSource(largeIcon != 0 ? largeIcon : smallIcon);
        }
        finally
        {
            if (largeIcon != 0)
            {
                DestroyIcon(largeIcon);
            }

            if (smallIcon != 0)
            {
                DestroyIcon(smallIcon);
            }
        }
    }

    private static BitmapSource? ToBitmapSource(nint iconHandle)
    {
        var iconCopy = CopyIcon(iconHandle);
        if (iconCopy == 0)
        {
            return null;
        }

        try
        {
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(iconCopy, System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            DestroyIcon(iconCopy);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string? TypeName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint PrivateExtractIcons(
        string szFileName,
        int nIconIndex,
        int cxIcon,
        int cyIcon,
        nint[] phicon,
        uint[] piconid,
        uint nIcons,
        uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
    private static extern uint ExtractIconEx(string file, int iconIndex, out nint largeIcon, out nint smallIcon, uint iconCount);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint fileAttributes, ref ShellFileInfo fileInfo, uint fileInfoSize, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint iconHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CopyIcon(nint iconHandle);
}
