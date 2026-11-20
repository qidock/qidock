using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QiDock.Services.Widgets;
using QiDock.Widgets;
using Windows.Media.Control;

namespace QiDock.DesktopWidgets;

/// <summary>Counts listeners; the source only runs while at least one visible widget listens.</summary>
internal abstract class HubBase
{
    private int _listeners;
    public event Action? Updated;

    public void Subscribe(Action handler)
    {
        Updated += handler;
        if (++_listeners == 1) Activate();
    }

    public void Unsubscribe(Action handler)
    {
        Updated -= handler;
        if (_listeners > 0 && --_listeners == 0) Deactivate();
    }

    protected void Raise()
    {
        try { Updated?.Invoke(); }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or ArgumentException or FormatException)
        {
            // One broken widget must not stop the others from updating.
            System.Diagnostics.Trace.TraceWarning("Desktop widget update failed: {0}", ex);
        }
    }
    protected abstract void Activate();
    protected abstract void Deactivate();
}

/// <summary>CPU, memory, network, disk and battery, sampled once a second while needed.</summary>
internal sealed class SystemHub : HubBase
{
    public static readonly SystemHub Instance = new();
    private const int HistoryLength = 40;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private ulong _lastIdle, _lastTotal;
    private long _lastDown = -1, _lastUp = -1;
    private DateTime _lastNet;
    private int _ticks;

    public double Cpu { get; private set; }
    public List<double> CpuHistory { get; } = [];
    public double RamPercent { get; private set; }
    public ulong RamUsed { get; private set; }
    public ulong RamTotal { get; private set; }
    public double Down { get; private set; }
    public double Up { get; private set; }
    public List<double> DownHistory { get; } = [];
    public List<double> UpHistory { get; } = [];
    public double DiskPercent { get; private set; }
    public ulong DiskFree { get; private set; }
    public ulong DiskTotal { get; private set; }
    public BatteryStatus.State? Battery { get; private set; }

    private SystemHub() => _timer.Tick += (_, _) => Sample();

    protected override void Activate()
    {
        _ticks = 0;
        Sample();
        _timer.Start();
    }

    protected override void Deactivate() => _timer.Stop();

    private void Sample()
    {
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var total = kernel + user;
            if (_lastTotal != 0 && total > _lastTotal)
                Cpu = Math.Clamp((1 - (double)(idle - _lastIdle) / (total - _lastTotal)) * 100, 0, 100);
            _lastIdle = idle;
            _lastTotal = total;
        }
#if DEBUG
        if (Services.Promo.On)
        {
            if (CpuHistory.Count == 0)
                for (var i = 0; i < HistoryLength; i++) Push(CpuHistory, Services.Promo.NextCpu());
            Cpu = Services.Promo.NextCpu();
        }
#endif
        Push(CpuHistory, Cpu);

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref memory))
        {
            RamPercent = memory.MemoryLoad;
            RamTotal = memory.TotalPhys;
            RamUsed = memory.TotalPhys - memory.AvailPhys;
        }

        SampleNetwork();

        if (_ticks % 30 == 0) SampleDisk();
        if (_ticks % 10 == 0) Battery = BatteryStatus.Read();
        _ticks++;
        Raise();
    }

    private void SampleNetwork()
    {
        long down = 0, up = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var stats = nic.GetIPStatistics();
                down += stats.BytesReceived;
                up += stats.BytesSent;
            }
        }
        catch (NetworkInformationException) { return; }

        var now = DateTime.UtcNow;
        if (_lastDown >= 0)
        {
            var seconds = Math.Max(0.2, (now - _lastNet).TotalSeconds);
            Down = Math.Max(0, (down - _lastDown) / seconds);
            Up = Math.Max(0, (up - _lastUp) / seconds);
        }
        _lastDown = down;
        _lastUp = up;
        _lastNet = now;
        Push(DownHistory, Down);
        Push(UpHistory, Up);
    }

    private void SampleDisk()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            DiskTotal = (ulong)drive.TotalSize;
            DiskFree = (ulong)drive.AvailableFreeSpace;
            DiskPercent = DiskTotal == 0 ? 0 : (1 - (double)DiskFree / DiskTotal) * 100;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void Push(List<double> history, double value)
    {
        history.Add(value);
        if (history.Count > HistoryLength) history.RemoveAt(0);
    }

    public static string Speed(double bytesPerSecond)
    {
        var culture = CultureInfo.CurrentCulture;
        if (bytesPerSecond >= 1024 * 1024) return (bytesPerSecond / 1024 / 1024).ToString("0.0", culture) + " MB/s";
        if (bytesPerSecond >= 1024) return (bytesPerSecond / 1024).ToString("0", culture) + " KB/s";
        return bytesPerSecond.ToString("0", culture) + " B/s";
    }

    public static string Gb(ulong bytes) => (bytes / 1073741824d).ToString(bytes >= 100UL * 1073741824 ? "0" : "0.0", CultureInfo.CurrentCulture) + " GB";

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}

/// <summary>Open-Meteo weather for the city set in the widget settings; refreshed every 15 minutes while shown.</summary>
internal sealed class WeatherHub : HubBase
{
    public static readonly WeatherHub Instance = new();
    private readonly WeatherService _service = new();
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(15) };
    private CancellationTokenSource? _load;
    private string? _city;

    public WeatherReport? Report { get; private set; }
    public string? Error { get; private set; }

    private WeatherHub() => _timer.Tick += (_, _) => _ = RefreshAsync();

    protected override void Activate()
    {
        _timer.Start();
        if (Report is null || DateTime.Now - Report.FetchedAt > TimeSpan.FromMinutes(15) || _city != City) _ = RefreshAsync();
    }

    protected override void Deactivate() => _timer.Stop();

    private static string City
    {
        get
        {
            var city = Desk.Settings().WeatherCity;
            return string.IsNullOrWhiteSpace(city) ? "İstanbul" : city.Trim();
        }
    }

    public void CityMayHaveChanged()
    {
        if (_city is not null && _city != City) _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        _load?.Cancel();
        var load = _load = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var city = City;
        try
        {
            var report = await _service.FetchAsync(city, load.Token);
            if (load.IsCancellationRequested) return;
            Report = report;
            _city = city;
            Error = null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Net.Http.HttpRequestException or InvalidOperationException or JsonException or KeyNotFoundException or FormatException)
        {
            if (load.IsCancellationRequested && ex is OperationCanceledException && !ReferenceEquals(load, _load)) return;
            Error = ex is InvalidOperationException ? ex.Message : Desk.L("Hava durumu alınamadı", "Weather unavailable");
        }
        Raise();
    }

    public static string Temp(double value) => double.IsNaN(value) ? "–" : Math.Round(value).ToString(CultureInfo.InvariantCulture) + "°";
}

/// <summary>The iCal feed from the widget settings, refreshed every 15 minutes while shown.</summary>
internal sealed class CalendarHub : HubBase
{
    public static readonly CalendarHub Instance = new();
    private readonly CalendarFeedService _feed = new();
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(15) };
    private string? _url;
    private DateTime _loaded;

    public bool HasFeed => _feed.HasFeed;

    private CalendarHub() => _timer.Tick += (_, _) => _ = RefreshAsync();

    protected override void Activate()
    {
        _timer.Start();
        if (_url != Desk.Settings().CalendarFeedUrl || DateTime.Now - _loaded > TimeSpan.FromMinutes(15)) _ = RefreshAsync();
    }

    protected override void Deactivate() => _timer.Stop();

    public void UrlMayHaveChanged()
    {
        if (_url != Desk.Settings().CalendarFeedUrl) _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        _url = Desk.Settings().CalendarFeedUrl;
        _loaded = DateTime.Now;
        using var load = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await _feed.RefreshAsync(_url, load.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Net.Http.HttpRequestException or IOException or InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceWarning("Desktop calendar refresh failed: {0}", ex.Message);
        }
        Raise();
    }

    public IReadOnlyList<CalendarOccurrence> Between(DateTime from, DateTime to) =>
        _feed.HasFeed ? _feed.GetOccurrences(from, to) : [];
}

/// <summary>The system media session (Spotify, browsers, players) shared by the music widgets.</summary>
internal sealed class MediaHub : HubBase
{
    public static readonly MediaHub Instance = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _active;
    private int _version;
    private string? _artKey;
    private string? _artSignature;

    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string App { get; private set; } = "";
    public bool Playing { get; private set; }
    public bool HasMedia => Title.Length > 0 || Artist.Length > 0;
    public ImageSource? Art { get; private set; }
    public Color ArtColor { get; private set; } = Color.FromRgb(0x5E, 0x3A, 0x8C);
    public TimeSpan Position { get; private set; }
    public TimeSpan Duration { get; private set; }
    public DateTime PositionAt { get; private set; }

    /// <summary>Playback position now, moved on from the last update while playing.</summary>
    public TimeSpan LivePosition
    {
        get
        {
            var position = Playing ? Position + (DateTime.UtcNow - PositionAt) : Position;
            return Duration > TimeSpan.Zero && position > Duration ? Duration : position;
        }
    }

    protected override async void Activate()
    {
        _active = true;
        try
        {
            if (_manager is null)
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _manager.CurrentSessionChanged += (_, _) => _dispatcher.InvokeAsync(AttachSession);
                _manager.SessionsChanged += (_, _) => _dispatcher.InvokeAsync(AttachSession);
            }
            AttachSession();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            _manager = null;
        }
    }

    protected override void Deactivate()
    {
        _active = false;
        DetachSession();
    }

    private void AttachSession()
    {
        if (!_active || _manager is null) return;
        GlobalSystemMediaTransportControlsSession? session;
        try { session = _manager.GetCurrentSession() ?? _manager.GetSessions().FirstOrDefault(); }
        catch (COMException) { session = null; }
        if (!ReferenceEquals(session, _session))
        {
            DetachSession();
            _session = session;
            if (session is not null)
            {
                session.MediaPropertiesChanged += SessionChanged;
                session.PlaybackInfoChanged += SessionChanged;
                session.TimelinePropertiesChanged += SessionChanged;
            }
        }
        _ = RefreshAsync();
    }

    private void DetachSession()
    {
        if (_session is null) return;
        _session.MediaPropertiesChanged -= SessionChanged;
        _session.PlaybackInfoChanged -= SessionChanged;
        _session.TimelinePropertiesChanged -= SessionChanged;
        _session = null;
    }

    private void SessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) =>
        _dispatcher.InvokeAsync(() => _ = RefreshAsync());

    private async Task RefreshAsync()
    {
        var version = ++_version;
        var session = _session;
        if (session is null)
        {
            Title = Artist = App = "";
            Playing = false;
            Art = null;
            _artKey = null;
            Raise();
            return;
        }
        try
        {
            var playback = session.GetPlaybackInfo();
            var properties = await session.TryGetMediaPropertiesAsync();
            if (version != _version) return;
            Title = properties?.Title ?? "";
            Artist = properties?.Artist ?? "";
            App = AppName(session.SourceAppUserModelId);
            Playing = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var timeline = session.GetTimelineProperties();
            Position = timeline.Position;
            Duration = timeline.EndTime - timeline.StartTime;
            PositionAt = timeline.LastUpdatedTime.UtcDateTime == default ? DateTime.UtcNow : timeline.LastUpdatedTime.UtcDateTime;
            if (PositionAt > DateTime.UtcNow) PositionAt = DateTime.UtcNow;
            // Browsers send their logo first and the real cover a moment later for the same song:
            // read the cover on every change, and once more shortly after a new song.
            var key = $"{Title}|{Artist}|{properties?.AlbumTitle}";
            var newSong = key != _artKey;
            _artKey = key;
            await LoadArtAsync(properties?.Thumbnail, version, newSong);
            if (newSong && version == _version)
            {
                var again = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
                again.Tick += (_, _) =>
                {
                    again.Stop();
                    if (_active) _ = RefreshAsync();
                };
                again.Start();
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (version != _version) return;
            Title = Artist = "";
        }
        Raise();
    }

    private static string AppName(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        var name = id.Split('!')[0];
        name = Path.GetFileNameWithoutExtension(name);
        if (name.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        if (name.Contains("chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (name.Contains("msedge", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (name.Contains("firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (name.Contains("ZuneMusic", StringComparison.OrdinalIgnoreCase)) return Desk.L("Medya Oynatıcı", "Media Player");
        return name.Length > 0 ? char.ToUpper(name[0], CultureInfo.CurrentCulture) + name[1..] : name;
    }

    private async Task LoadArtAsync(Windows.Storage.Streams.IRandomAccessStreamReference? reference, int version, bool newSong)
    {
        if (reference is null)
        {
            if (!newSong) return;
            Art = null;
            _artSignature = null;
            return;
        }
        try
        {
            using var winStream = await reference.OpenReadAsync();
            using var source = winStream.AsStreamForRead();
            var memory = new MemoryStream();
            await source.CopyToAsync(memory);
            memory.Position = 0;
            if (version != _version) return;
            var signature = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(memory.GetBuffer().AsSpan(0, (int)memory.Length)));
            if (signature == _artSignature && Art is not null) return;
            _artSignature = signature;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 300;
            image.StreamSource = memory;
            image.EndInit();
            image.Freeze();
            Art = image;
            ArtColor = Average(image);
        }
        catch (Exception ex) when (ex is COMException or IOException or NotSupportedException or InvalidOperationException or FileFormatException)
        {
            Art = null;
        }
    }

    private static Color Average(BitmapSource image)
    {
        var small = new FormatConvertedBitmap(new TransformedBitmap(image, new ScaleTransform(16.0 / image.PixelWidth, 16.0 / image.PixelHeight)), PixelFormats.Bgra32, null, 0);
        var pixels = new byte[small.PixelWidth * small.PixelHeight * 4];
        small.CopyPixels(pixels, small.PixelWidth * 4, 0);
        double r = 0, g = 0, b = 0;
        var count = Math.Max(1, pixels.Length / 4);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            b += pixels[i];
            g += pixels[i + 1];
            r += pixels[i + 2];
        }
        return Color.FromRgb((byte)(r / count), (byte)(g / count), (byte)(b / count));
    }

    public async void Command(Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> command)
    {
        if (_session is null) return;
        try { await command(_session); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceWarning("Media command failed: {0}", ex.Message);
        }
    }
}

/// <summary>Exchange rates from the European Central Bank via Frankfurter (free, no key); cached for 6 hours.</summary>
internal static class RatesHub
{
    private static readonly Dictionary<string, (DateTime At, Dictionary<string, double> Rates)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static readonly string[] Currencies = ["TRY", "USD", "EUR", "GBP", "CHF", "JPY", "CNY", "RUB", "SAR", "AED", "AUD", "CAD"];

    public static async Task<double?> RateAsync(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return 1;
        if (!Cache.TryGetValue(from, out var entry) || DateTime.Now - entry.At > TimeSpan.FromHours(6))
        {
            try
            {
                using var token = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var response = await WidgetHttp.Client.GetAsync("https://api.frankfurter.app/latest?from=" + Uri.EscapeDataString(from.ToUpperInvariant()), token.Token);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(token.Token);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token.Token);
                var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var rate in json.RootElement.GetProperty("rates").EnumerateObject()) rates[rate.Name] = rate.Value.GetDouble();
                entry = (DateTime.Now, rates);
                Cache[from] = entry;
            }
            catch (Exception ex) when (ex is OperationCanceledException or System.Net.Http.HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return Cache.TryGetValue(from, out var stale) && stale.Rates.TryGetValue(to, out var old) ? old : null;
            }
        }
        return entry.Rates.TryGetValue(to, out var value) ? value : null;
    }
}
