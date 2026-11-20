using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;

namespace QiDock.Services.Notifications;

/// <summary>One Windows notification as shown on the banner and in the list.</summary>
public sealed record AppNotification(
    long Id,
    string AppId,
    string Title,
    string Body,
    DateTime Arrived,
    string? Launch,
    bool LaunchIsProtocol,
    string? LogoPath);

/// <summary>
/// Windows notifications, read from Windows' own notification database
/// (%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db) with the SQLite that ships in
/// System32. The WinRT listener API needs app identity and is slow; this needs neither. The database
/// is copied first, so Windows' lock never matters and the original is never written.
/// <para>
/// The list follows Windows: a notification cleared in the Windows notification center leaves
/// here too. Items removed here are remembered locally (Windows keeps its own copy).
/// </para>
/// </summary>
public sealed class NotificationFeed : IDisposable
{
    private const int MaxRows = 60;
    private static readonly string SourceDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Notifications");
    private static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QiDock");
    private static readonly string CopyDir = Path.Combine(DataDir, "notifications-copy");
    private static readonly string StateFile = Path.Combine(DataDir, "notifications.json");
    private static readonly string[] Files = ["wpndatabase.db", "wpndatabase.db-wal"];

    /// <summary>The one feed the banner, the bar tile and the dock button share (created on the UI thread).</summary>
    public static NotificationFeed Shared { get; } = new();

    private readonly DispatcherTimer _poll = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private readonly HashSet<long> _seen = [];
    private DateTime _stamp = DateTime.MinValue;
    private bool _primed, _reading;
    private State _state;

    /// <summary>Notifications Windows still holds, newest first, without the ones removed here.</summary>
    public IReadOnlyList<AppNotification> Items { get; private set; } = [];

    /// <summary>Notifications that arrived after the list was last opened.</summary>
    public int UnreadCount => Items.Count(n => n.Arrived > _state.ReadUpTo);

    /// <summary>Whether the feed could read the database at all (false on an unsupported system).</summary>
    public bool Available { get; private set; } = true;

    /// <summary>New notifications since the last poll (never the ones present at start-up).</summary>
    public event Action<IReadOnlyList<AppNotification>>? Arrived;

    /// <summary>The list or the unread count changed.</summary>
    public event Action? Changed;

    private NotificationFeed()
    {
        _state = LoadState();
        _poll.Tick += (_, _) => _ = PollAsync();
    }

    public void Start()
    {
        _poll.Start();
        _ = PollAsync();
    }

    public void Dispose() => _poll.Stop();

    public void MarkAllRead()
    {
        if (Items.Count == 0 || Items[0].Arrived <= _state.ReadUpTo) return;
        _state = _state with { ReadUpTo = Items[0].Arrived };
        SaveState();
        Changed?.Invoke();
    }

    public void Remove(AppNotification notification) => RemoveMany([notification]);

    /// <summary>Removes several notifications with one save and one <see cref="Changed"/>.</summary>
    public void RemoveMany(IReadOnlyCollection<AppNotification> notifications)
    {
        if (notifications.Count == 0) return;
        var ids = notifications.Select(n => n.Id).ToHashSet();
        _state = _state with { Removed = [.. _state.Removed.Concat(ids).TakeLast(400)] };
        SaveState();
        Items = Items.Where(n => !ids.Contains(n.Id)).ToList();
        Changed?.Invoke();
    }

    public void ClearAll()
    {
        if (Items.Count == 0) return;
        var newest = Items.Max(n => n.Arrived);
        _state = _state with { ClearedUpTo = newest, ReadUpTo = newest > _state.ReadUpTo ? newest : _state.ReadUpTo };
        SaveState();
        Items = [];
        Changed?.Invoke();
    }

    private async Task PollAsync()
    {
        if (_reading) return;
        var stamp = DateTime.MinValue;
        try
        {
            foreach (var file in Files)
            {
                var path = Path.Combine(SourceDir, file);
                if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > stamp) stamp = File.GetLastWriteTimeUtc(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        if (stamp == _stamp && _primed) return;

        _reading = true;
        List<AppNotification>? rows;
        try { rows = await Task.Run(ReadDatabase); }
        finally { _reading = false; }
        if (rows is null)
        {
            if (!_primed) Available = File.Exists(Path.Combine(SourceDir, Files[0]));
            return; // Windows held a lock; the next tick tries again
        }
        _stamp = stamp;

        var visible = rows.Where(n => n.Arrived > _state.ClearedUpTo && !_state.Removed.Contains(n.Id)).ToList();
        var added = new List<AppNotification>();
        foreach (var n in visible)
            if (_seen.Add(n.Id) && _primed && n.Arrived > DateTime.UtcNow.AddMinutes(-2)) added.Add(n);
        foreach (var n in rows) _seen.Add(n.Id);
        var changed = !_primed || !visible.Select(n => n.Id).SequenceEqual(Items.Select(n => n.Id));
        Items = visible;
        _primed = true;
        if (changed) Changed?.Invoke();
        if (added.Count > 0) Arrived?.Invoke(added);
    }

    // ---- Database ----------------------------------------------------------------------------

    private static List<AppNotification>? ReadDatabase()
    {
        try
        {
            Directory.CreateDirectory(CopyDir);
            // SQLite rebuilds the index file from the log; a stale one from the last copy must not be reused.
            var index = Path.Combine(CopyDir, "wpndatabase.db-shm");
            if (File.Exists(index)) File.Delete(index);
            foreach (var file in Files)
            {
                var source = Path.Combine(SourceDir, file);
                var target = Path.Combine(CopyDir, file);
                if (!File.Exists(source))
                {
                    if (File.Exists(target)) File.Delete(target);
                    continue;
                }
                if (!CopyShared(source, target)) return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            // Read-write on the private copy so SQLite can replay the write-ahead log.
            if (Sqlite.sqlite3_open_v2(Path.Combine(CopyDir, "wpndatabase.db"), out var db, 2, 0) != 0) return null;
            try
            {
                const string sql = "SELECT n.Id, h.PrimaryId, n.ArrivalTime, n.Payload FROM Notification n " +
                                   "JOIN NotificationHandler h ON n.HandlerId = h.RecordId WHERE n.Type = 'toast' " +
                                   "ORDER BY n.ArrivalTime DESC LIMIT 60";
                if (Sqlite.sqlite3_prepare_v2(db, sql, -1, out var statement, 0) != 0) return null;
                try
                {
                    var result = new List<AppNotification>();
                    while (Sqlite.sqlite3_step(statement) == 100 /* SQLITE_ROW */ && result.Count < MaxRows)
                    {
                        var id = Sqlite.sqlite3_column_int64(statement, 0);
                        var appId = Marshal.PtrToStringUni(Sqlite.sqlite3_column_text16(statement, 1)) ?? "";
                        var arrival = Sqlite.sqlite3_column_int64(statement, 2);
                        var length = Sqlite.sqlite3_column_bytes(statement, 3);
                        var blob = Sqlite.sqlite3_column_blob(statement, 3);
                        if (blob == 0 || length <= 0) continue;
                        var payload = new byte[length];
                        Marshal.Copy(blob, payload, 0, length);
                        if (Parse(id, appId, arrival, payload) is { } notification) result.Add(notification);
                    }
                    return result;
                }
                finally { Sqlite.sqlite3_finalize(statement); }
            }
            finally { Sqlite.sqlite3_close(db); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static bool CopyShared(string source, string target)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var output = new FileStream(target, FileMode.Create, FileAccess.Write);
                input.CopyTo(output);
                return true;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(40); // Windows may hold a byte-range lock for a moment
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    /// <summary>Title, text, launch target and logo from the toast XML.</summary>
    internal static AppNotification? Parse(long id, string appId, long arrival, byte[] payload)
    {
        string xml;
        if (payload.Length >= 2 && payload[1] == 0) xml = Encoding.Unicode.GetString(payload);
        else xml = Encoding.UTF8.GetString(payload);
        xml = xml.TrimStart('﻿').TrimEnd('\0');
        XElement toast;
        try { toast = XElement.Parse(xml); }
        catch (System.Xml.XmlException) { return null; }
        if (toast.Name.LocalName != "toast") return null;

        var binding = toast.Descendants().FirstOrDefault(e => e.Name.LocalName == "binding");
        if (binding is null) return null;
        bool IsAttribution(XElement e) => (string?)e.Attribute("placement") == "attribution";
        var texts = binding.Elements().Where(e => e.Name.LocalName == "text" && !IsAttribution(e)).Select(e => e.Value.Trim()).Where(t => t.Length > 0).ToList();
        if (texts.Count == 0)
            texts = binding.Descendants().Where(e => e.Name.LocalName == "text" && !IsAttribution(e)).Select(e => e.Value.Trim()).Where(t => t.Length > 0).ToList();
        if (texts.Count == 0) return null;

        var logo = binding.Elements().FirstOrDefault(e => e.Name.LocalName == "image" && (string?)e.Attribute("placement") == "appLogoOverride");
        var launch = (string?)toast.Attribute("launch");
        var activation = (string?)toast.Attribute("activationType");
        DateTime arrived;
        try { arrived = DateTime.FromFileTimeUtc(arrival); }
        catch (ArgumentOutOfRangeException) { arrived = DateTime.UtcNow; }
        return new AppNotification(id, appId, texts[0], string.Join(Environment.NewLine, texts.Skip(1)), arrived,
            string.IsNullOrWhiteSpace(launch) ? null : launch, string.Equals(activation, "protocol", StringComparison.OrdinalIgnoreCase), LocalImage((string?)logo?.Attribute("src")));
    }

    private static string? LocalImage(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile) source = uri.LocalPath;
        return Path.IsPathFullyQualified(source) && File.Exists(source) ? source : null;
    }

    // ---- Local state ------------------------------------------------------------------------

    private sealed record State(DateTime ReadUpTo, DateTime ClearedUpTo, IReadOnlyList<long> Removed);

    private static State LoadState()
    {
        try
        {
            if (File.Exists(StateFile) && JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) is { } state)
                return state with { Removed = state.Removed ?? [] };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        // First run: what is already there counts as read.
        return new State(DateTime.UtcNow, DateTime.MinValue, []);
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var temporary = StateFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_state));
            File.Move(temporary, StateFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static class Sqlite
    {
        private const string Library = "winsqlite3.dll";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string file, out nint db, int flags, nint vfs);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_close(nint db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_prepare_v2(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out nint statement, nint tail);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_step(nint statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_finalize(nint statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern long sqlite3_column_int64(nint statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern nint sqlite3_column_text16(nint statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern nint sqlite3_column_blob(nint statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_column_bytes(nint statement, int column);
    }
}

/// <summary>Name, icon and activation of the app behind a notification (by its AppUserModelID).</summary>
public static class NotificationApps
{
    private static readonly Dictionary<string, (string Name, BitmapSource? Icon)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string Name(string appId) => Resolve(appId).Name;
    public static BitmapSource? Icon(string appId) => Resolve(appId).Icon;

    private static (string Name, BitmapSource? Icon) Resolve(string appId)
    {
        if (Cache.TryGetValue(appId, out var known)) return known;
        string? name = null;
        BitmapSource? icon = null;
        if (SHCreateItemFromParsingName(@"shell:AppsFolder\" + appId, 0, typeof(IShellItem).GUID, out var item) == 0 && item is not null)
        {
            try
            {
                if (item.GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out var pointer) == 0 && pointer != 0)
                {
                    name = Marshal.PtrToStringUni(pointer);
                    Marshal.FreeCoTaskMem(pointer);
                }
                icon = ImageOf(item, 64);
            }
            finally { Marshal.ReleaseComObject(item); }
        }
        var result = (string.IsNullOrWhiteSpace(name) || name == appId ? Fallback(appId) : name, icon);
        Cache[appId] = result;
        return result;
    }

    /// <summary>
    /// An app's icon at the given pixel size, not cached (the launcher loads them while it is open). With
    /// <paramref name="exact"/> the picture is brought to exactly that size here (smoothly, on the calling thread),
    /// so nothing scales it again while it is drawn.
    /// </summary>
    public static BitmapSource? LoadIcon(string appId, int size, bool exact = false)
    {
        if (SHCreateItemFromParsingName(@"shell:AppsFolder\" + appId, 0, typeof(IShellItem).GUID, out var item) != 0 || item is null) return null;
        try { return ImageOf(item, size, exact); }
        finally { Marshal.ReleaseComObject(item); }
    }

    private static BitmapSource? ImageOf(IShellItem item, int size, bool exact = false)
    {
        // "Bigger is fine": Windows hands over the icon's own largest picture, scaled here afterwards.
        if (item is not IShellItemImageFactory factory || factory.GetImage(new NativeSize(size, size), 0x1 | 0x4 /* bigger ok, icon only */, out var bitmap) != 0 || bitmap == 0) return null;
        BitmapSource? image;
        try { image = FromHBitmap(bitmap); }
        finally { DeleteObject(bitmap); }
        return exact && image is not null ? Fit(image, size) : image;
    }

    /// <summary>
    /// The picture brought to <paramref name="size"/> pixels, worked out at once: made smaller by averaging (Fant),
    /// made larger by blending neighbours (averaging would only repeat pixels and show them as squares).
    /// </summary>
    private static BitmapSource Fit(BitmapSource image, int size)
    {
        var scale = (double)size / Math.Max(image.PixelWidth, image.PixelHeight);
        if (Math.Abs(scale - 1) < 0.001) return image;
        if (scale < 1)
        {
            var smaller = new CachedBitmap(new TransformedBitmap(image, new System.Windows.Media.ScaleTransform(scale, scale)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            smaller.Freeze();
            return smaller;
        }
        int width = (int)Math.Round(image.PixelWidth * scale), height = (int)Math.Round(image.PixelHeight * scale);
        var visual = new System.Windows.Media.DrawingVisual();
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(visual, System.Windows.Media.BitmapScalingMode.Linear);
        using (var dc = visual.RenderOpen()) dc.DrawImage(image, new System.Windows.Rect(0, 0, width, height));
        var larger = new RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        larger.Render(visual);
        larger.Freeze();
        return larger;
    }

    private static string Fallback(string appId)
    {
        var name = appId.Contains('!') ? appId[(appId.IndexOf('!') + 1)..] : appId;
        name = Path.GetFileNameWithoutExtension(name.Split('\\', '/').Last());
        var dot = name.LastIndexOf('.');
        if (dot > 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : appId;
    }

    /// <summary>A 32-bit shell bitmap with its alpha channel (CreateBitmapSourceFromHBitmap drops it).</summary>
    private static BitmapSource? FromHBitmap(nint bitmap)
    {
        var section = new DibSection();
        if (GetObject(bitmap, Marshal.SizeOf<DibSection>(), ref section) == 0 || section.Bits == 0 || section.BitsPixel != 32) return null;
        int width = section.Width, height = Math.Abs(section.Height), stride = width * 4;
        var pixels = new byte[stride * height];
        Marshal.Copy(section.Bits, pixels, 0, pixels.Length);
        if (section.HeaderHeight > 0) // bottom-up rows
        {
            var flipped = new byte[pixels.Length];
            for (var row = 0; row < height; row++) Buffer.BlockCopy(pixels, row * stride, flipped, (height - 1 - row) * stride, stride);
            pixels = flipped;
        }
        var source = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    /// <summary>
    /// Opens what the notification points to: its link for protocol toasts, otherwise the app's
    /// open window (windows carry the AppUserModelID their toasts use), otherwise the app itself.
    /// </summary>
    public static void Activate(AppNotification notification)
    {
        try
        {
            if (notification.LaunchIsProtocol && notification.Launch is { } link
                && Uri.TryCreate(link, UriKind.Absolute, out var uri) && !uri.IsFile && !uri.IsUnc)
            {
                Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
                return;
            }
            if (FindWindow(notification.AppId) is { } window && NativeWindowTracker.Activate(window)) return;
            if (notification.AppId.Length > 0)
                Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + notification.AppId) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private static nint? FindWindow(string appId)
    {
        nint? found = null;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindow(window, 4 /* GW_OWNER */) != 0) return true;
            if ((GetWindowLongPtr(window, -20).ToInt64() & 0x80 /* WS_EX_TOOLWINDOW */) != 0) return true;
            if (string.Equals(NativeJumpListSession.AppIdForWindow(window), appId, StringComparison.OrdinalIgnoreCase))
            {
                found = window;
                return false;
            }
            return true;
        }, 0);
        return found;
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(nint handle, int size, ref DibSection section);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint context, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize(int width, int height)
    {
        public int Width = width;
        public int Height = height;
    }

    /// <summary>DIBSECTION: BITMAP followed by BITMAPINFOHEADER (only the fields read here are named).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DibSection
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public nint Bits;
        public int HeaderSize, HeaderWidth, HeaderHeight;
        public ushort HeaderPlanes, HeaderBitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
        public int BitFields0, BitFields1, BitFields2;
        public nint Section;
        public int Offset;
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint context, ref Guid handler, ref Guid iid, out nint result);
        [PreserveSig] int GetParent(out nint parent);
        [PreserveSig] int GetDisplayName(uint form, out nint name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(nint other, uint hint, out int order);
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, uint flags, out nint bitmap);
    }
}
