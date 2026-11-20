using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace QiDock.Services;

/// <summary>
/// Looks for a newer QiDock on GitHub and installs it in place: the new exe is downloaded next to the running one,
/// checked against the SHA-256 GitHub publishes for it, swapped in, and QiDock restarts. One small request when asked,
/// and at most one a day by itself; nothing keeps running in between.
/// </summary>
internal static class Updater
{
    private const string LatestRelease = "https://api.github.com/repos/qidock/qidock/releases/latest";

    internal sealed record Release(Version Version, string Page, string Download, long Size, string? Sha256);

    public static Version Current { get; } = Assembly.GetExecutingAssembly().GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // A generic name only: nothing about the user goes with the request.
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("QiDock", Current.ToString()));
        return client;
    }

    /// <summary>The newest release when it is newer than this copy; null when this copy is up to date.</summary>
    public static async Task<Release?> CheckAsync(CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        version = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        if (version <= Current) return null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), "QiDock.exe", StringComparison.OrdinalIgnoreCase)) continue;
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            return new Release(
                version,
                root.GetProperty("html_url").GetString() ?? "",
                asset.GetProperty("browser_download_url").GetString() ?? "",
                asset.GetProperty("size").GetInt64(),
                digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null);
        }
        return null;
    }

    /// <summary>Downloads, checks and swaps in the new exe; returns an error text, or null when QiDock can restart.</summary>
    public static async Task<string?> InstallAsync(Release release, IProgress<int>? progress, CancellationToken token = default)
    {
        if (Environment.ProcessPath is not { } exe) return "QiDock'un dosyası bulunamadı.";
        var download = exe + ".download";
        var old = exe + ".old";
        try
        {
            using (var response = await Http.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using var target = new FileStream(download, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                var buffer = new byte[1 << 16];
                long done = 0;
                var lastPercent = -1;
                int read;
                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    done += read;
                    var percent = release.Size > 0 ? (int)(done * 100 / release.Size) : 0;
                    if (percent / 10 != lastPercent / 10)
                    {
                        lastPercent = percent;
                        progress?.Report(percent);
                    }
                }
            }
            if (new FileInfo(download).Length != release.Size)
                return Fail("İndirilen dosya eksik geldi.");
            if (release.Sha256 is { } expected)
            {
                await using var check = File.OpenRead(download);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(check, token).ConfigureAwait(false));
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    return Fail("İndirilen dosya GitHub'daki ile aynı değil; güncelleme yapılmadı.");
            }
            // A running exe can be renamed but not overwritten: the old one steps aside and is deleted at the next start.
            File.Move(exe, old, overwrite: true);
            try
            {
                File.Move(download, exe);
            }
            catch
            {
                File.Move(old, exe);
                throw;
            }
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            return Fail("Güncelleme indirilemedi: " + ex.Message);
        }

        string Fail(string message)
        {
            try { File.Delete(download); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return message;
        }
    }

    /// <summary>Deletes what the last update left behind (the old exe, a broken download).</summary>
    public static void CleanUp()
    {
        if (Environment.ProcessPath is not { } exe) return;
        foreach (var leftover in new[] { exe + ".old", exe + ".download" })
        {
            try { if (File.Exists(leftover)) File.Delete(leftover); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // ---- Once a day by itself ---------------------------------------------------------------------

    private sealed class State
    {
        public DateTime LastCheck { get; set; }
        public string? Announced { get; set; }
    }

    private static string StatePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QiDock", "update-check.json");

    private static State LoadState()
    {
        try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    private static void SaveState(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Checks quietly when the last check is more than a day old; a new version is announced once (a short card on
    /// the dock), never installed without asking.
    /// </summary>
    public static async void CheckDaily(Action<Release> announce)
    {
        var state = LoadState();
        if (DateTime.UtcNow - state.LastCheck < TimeSpan.FromDays(1)) return;
        Release? release;
        try { release = await CheckAsync(); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException) { return; }
        state.LastCheck = DateTime.UtcNow;
        if (release is not null && state.Announced != release.Version.ToString())
        {
            state.Announced = release.Version.ToString();
            Application.Current?.Dispatcher.BeginInvoke(() => announce(release));
        }
        SaveState(state);
    }
}
