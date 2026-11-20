using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media.Imaging;
using QiDock.Services.Widgets;

namespace QiDock.Services;

/// <summary>One picture the gallery offers: from Bing's picture of the day, from Windows, or a file on this computer.</summary>
internal sealed record WallpaperChoice(string Key, string Title, string ThumbnailSource, string FullSource, bool Online);

/// <summary>
/// The wallpaper gallery behind Settings → Wallpaper. Nothing is bundled with QiDock: the online pictures are Bing's
/// pictures of the day (several countries have different ones), listed and shown as small previews only while the
/// gallery is open; a picture is downloaded in full (4K) only when it is chosen, and kept in a small folder that
/// holds the last few. Windows' own wallpapers and any picture file can be chosen too.
/// </summary>
internal static class WallpaperGallery
{
    private const string Bing = "https://www.bing.com";
    private const int KeepDownloads = 8;

    /// <summary>The countries whose pictures of the day are listed, a few at a time ("more" asks the next ones).</summary>
    private static readonly string[] Markets = ["tr-TR", "en-US", "ja-JP", "en-GB", "de-DE", "fr-FR", "en-IN", "pt-BR", "en-AU", "it-IT", "es-ES", "zh-CN"];

    public static int MarketCount => Markets.Length;

    /// <summary>A 4K picture is a few megabytes: the widgets' client gives up after twenty seconds, this one waits longer.</summary>
    private static readonly Lazy<HttpClient> Download = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("QiDock", "0.3"));
        return client;
    });

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QiDock", "Wallpapers");

    /// <summary>The pictures of the day of markets [<paramref name="from"/>, from + count): the last fifteen days of each, without the ones in <paramref name="seen"/>.</summary>
    public static async Task<List<WallpaperChoice>> BingAsync(int from, int count, HashSet<string> seen, CancellationToken cancel)
    {
        var found = new List<WallpaperChoice>();
        foreach (var market in Markets.Skip(from).Take(count))
            foreach (var index in new[] { 0, 7 }) // Bing gives eight days at a time and fifteen in all
            {
                try
                {
                    var json = await WidgetHttp.Client.GetStringAsync($"{Bing}/HPImageArchive.aspx?format=js&idx={index}&n=8&mkt={market}", cancel);
                    using var document = JsonDocument.Parse(json);
                    foreach (var image in document.RootElement.GetProperty("images").EnumerateArray())
                    {
                        if (image.TryGetProperty("wp", out var allowed) && allowed.ValueKind == JsonValueKind.False) continue; // not offered as a wallpaper
                        if (image.GetProperty("urlbase").GetString() is not { Length: > 0 } urlBase || !urlBase.StartsWith("/th?id=OHR.", StringComparison.Ordinal)) continue;
                        if (!seen.Add(urlBase)) continue;
                        var title = image.TryGetProperty("copyright", out var copyright) ? copyright.GetString() ?? "" : "";
                        found.Add(new WallpaperChoice(urlBase, title, $"{Bing}{urlBase}_320x180.jpg", $"{Bing}{urlBase}_UHD.jpg", true));
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    if (cancel.IsCancellationRequested) throw new OperationCanceledException(cancel);
                    AppLog.Write($"Wallpaper gallery: Bing {market} could not be read ({ex.GetType().Name})");
                }
            }
        return found;
    }

    /// <summary>The wallpapers that come with Windows.</summary>
    public static List<WallpaperChoice> Windows()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Web", "Wallpaper");
        try
        {
            return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(file => file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(file => new WallpaperChoice(file, $"{Path.GetFileName(Path.GetDirectoryName(file))} · {Path.GetFileNameWithoutExtension(file)}", file, file, false))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>A small preview (320 pixels wide), read away from the screen's thread.</summary>
    public static async Task<BitmapSource?> ThumbnailAsync(WallpaperChoice choice, CancellationToken cancel)
    {
        try
        {
            var bytes = choice.Online
                ? await WidgetHttp.Client.GetByteArrayAsync(choice.ThumbnailSource, cancel)
                : await File.ReadAllBytesAsync(choice.ThumbnailSource, cancel);
            return await Task.Run(() => Decode(bytes, 320), cancel);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException or FileFormatException or COMException)
        {
            return null;
        }
    }

    private static BitmapSource Decode(byte[] bytes, int width)
    {
        var image = new BitmapImage();
        using (var stream = new MemoryStream(bytes))
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = width;
            image.StreamSource = stream;
            image.EndInit();
        }
        image.Freeze();
        return image;
    }

    /// <summary>
    /// Makes <paramref name="choice"/> the desktop wallpaper: an online picture is downloaded in full first. The dock,
    /// the cards and the app screen follow by themselves (Windows tells them the desktop changed).
    /// </summary>
    public static async Task<string?> ApplyAsync(WallpaperChoice choice, CancellationToken cancel = default)
    {
        string file;
        if (choice.Online)
        {
            Directory.CreateDirectory(Folder);
            file = FileOf(choice);
            if (!File.Exists(file))
            {
                var bytes = await Download.Value.GetByteArrayAsync(choice.FullSource, cancel);
                var part = file + ".part";
                await File.WriteAllBytesAsync(part, bytes, cancel);
                File.Move(part, file, overwrite: true);
            }
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow); // the newest is kept when old ones are cleared
        }
        else
        {
            file = choice.FullSource;
        }
        var faded = await Task.Run(() => Fade(file));
        if (!faded && !await Task.Run(() => SystemParametersInfo(SetDesktopWallpaper, 0, file, UpdateProfile | SendChange)))
        {
            AppLog.Write($"Wallpaper gallery: Windows did not take the wallpaper ({Marshal.GetLastWin32Error()})");
            return null;
        }
        AppLog.Write($"Wallpaper gallery: wallpaper set ({(choice.Online ? "Bing" : "file")}, {(faded ? "faded in" : "at once")})");
        if (choice.Online) ClearOld(file);
        Applied?.Invoke();
        _ = AgainAfterFadeAsync();
        return file;
    }

    /// <summary>Once more when the fade is over: Windows may write the new picture only then.</summary>
    private static async Task AgainAfterFadeAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        Applied?.Invoke();
    }

    /// <summary>
    /// Raised on the screen's thread after the gallery changed the wallpaper. The faded change does not always tell
    /// every program that the desktop changed, so the dock and the cards hear it here as well.
    /// </summary>
    public static event Action? Applied;

    /// <summary>
    /// Sets the wallpaper the way Windows' own slideshow does, so the old picture fades into the new one (Windows
    /// draws it; QiDock holds nothing for it). Asking the desktop for its background layer first is what turns the
    /// fade on. False when it did not work: the caller then sets the wallpaper at once.
    /// </summary>
    private static bool Fade(string file)
    {
        var done = false;
        var worker = new Thread(() =>
        {
            try
            {
                var desktopWindow = FindWindow("Progman", null);
                if (desktopWindow != 0) SendMessageTimeout(desktopWindow, 0x052C, 0, 0, 0, 1000, out _);
                using var desktop = ShellComObject.Create(ActiveDesktopClass, ActiveDesktopInterface);
                if (desktop.Method<SetWallpaperProc>(5)(desktop.Handle, file, 0) < 0) return;
                done = desktop.Method<ApplyChangesProc>(3)(desktop.Handle, ApplyAll | ApplyForce | ApplyBufferedRefresh) >= 0;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or MarshalDirectiveException)
            {
                AppLog.Write($"Wallpaper gallery: fade not available ({ex.GetType().Name})");
            }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return worker.Join(TimeSpan.FromSeconds(10)) && done;
    }

    private static readonly Guid ActiveDesktopClass = new("75048700-EF1F-11D0-9888-006097DEACF9");
    private static readonly Guid ActiveDesktopInterface = new("F490EB00-1240-11D1-9888-006097DEACF9");
    private const int ApplyAll = 0x07, ApplyForce = 0x08, ApplyBufferedRefresh = 0x10;

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int SetWallpaperProc(nint self, [MarshalAs(UnmanagedType.LPWStr)] string file, int reserved);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ApplyChangesProc(nint self, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

    /// <summary>Keeps the last few downloaded pictures, so the folder never grows past a few tens of megabytes.</summary>
    private static void ClearOld(string keep)
    {
        try
        {
            foreach (var old in new DirectoryInfo(Folder).EnumerateFiles("*.jpg").OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeepDownloads))
                if (!string.Equals(old.FullName, keep, StringComparison.OrdinalIgnoreCase)) old.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Where the picture is (or would be) on this computer: an online one in the download folder.</summary>
    public static string FileOf(WallpaperChoice choice)
    {
        if (!choice.Online) return choice.FullSource;
        var name = string.Concat(choice.Key.Replace("/th?id=OHR.", "", StringComparison.Ordinal).Where(c => char.IsLetterOrDigit(c) || c is '_' or '-'));
        return Path.Combine(Folder, name + ".jpg");
    }

    /// <summary>The picture the desktop shows now, as Windows recorded it (to mark it in the gallery).</summary>
    public static string? Current() =>
        Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", null) as string;

    private const uint SetDesktopWallpaper = 0x0014; // SPI_SETDESKWALLPAPER
    private const uint UpdateProfile = 0x01, SendChange = 0x02;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, string value, uint winIni);
}
