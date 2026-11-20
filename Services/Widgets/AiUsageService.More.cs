using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace QiDock.Services.Widgets;

/// <summary>
/// Gemini (through the Antigravity CLI's sign-in) and OpenCode Go (through the OpenCode app's console sign-in).
/// Both sign-ins are only read from where their own app keeps them and sent to their own provider; nothing is stored.
/// </summary>
public sealed partial class AiUsageService
{
    public const string GeminiId = "gemini";
    public const string OpenCodeId = "opencode";

    private static readonly TimeSpan OtherMinimumInterval = TimeSpan.FromMinutes(2);

    // ---- Gemini (Antigravity) ----------------------------------------------------------------

    private const string CodeAssist = "https://daily-cloudcode-pa.googleapis.com/v1internal:";
    private ProviderUsage? _gemini;
    private DateTime _geminiFetchedAt = DateTime.MinValue;
    private string? _geminiProject, _geminiPlan;
    private static DateTime _agyRenewedAt = DateTime.MinValue;

    private static string AgyPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin", "agy.exe");

    private async Task<ProviderUsage?> GetGeminiAsync(CancellationToken token)
    {
        var now = DateTime.Now;
        if (_gemini is not null && now - _geminiFetchedAt < OtherMinimumInterval) return _gemini;
        var signIn = ReadAntigravitySignIn();
        if (signIn is null)
        {
            if (_gemini is null && File.Exists(AgyPath)) AppLog.Write("Gemini: Antigravity girişi bulunamadı");
            return null;
        }
        var (accessToken, expiresAt) = signIn.Value;
        // The sign-in lasts an hour; Antigravity renews it whenever it runs, so a short "agy models" does it here.
        if (expiresAt <= now.AddMinutes(1))
        {
            if (now - _agyRenewedAt > TimeSpan.FromMinutes(30) && File.Exists(AgyPath))
            {
                _agyRenewedAt = now;
                await RenewAntigravityAsync(token).ConfigureAwait(false);
                if (ReadAntigravitySignIn() is { } renewed) (accessToken, expiresAt) = renewed;
            }
            if (expiresAt <= now.AddMinutes(1)) return KeepGemini(now, UiText.Get("AiGeminiExpired"));
        }

        try
        {
            if (_geminiProject is null)
            {
                using var load = await PostCodeAssistAsync("loadCodeAssist", """{"metadata":{"ideType":"ANTIGRAVITY","pluginType":"GEMINI"}}""", accessToken, token).ConfigureAwait(false);
                if (load.RootElement.TryGetProperty("cloudaicompanionProject", out var project))
                    _geminiProject = project.ValueKind == JsonValueKind.String ? project.GetString()
                        : project.ValueKind == JsonValueKind.Object && project.TryGetProperty("id", out var id) ? id.GetString() : null;
                _geminiPlan = TierName(load.RootElement, "paidTier") ?? TierName(load.RootElement, "currentTier");
            }
            using var quota = await PostCodeAssistAsync("retrieveUserQuota", JsonSerializer.Serialize(new { project = _geminiProject }), accessToken, token).ConfigureAwait(false);
            var windows = ReadGeminiBuckets(quota.RootElement);
            _gemini = new ProviderUsage(GeminiId, "Gemini", _geminiPlan, windows, now, null);
            _geminiFetchedAt = now;
            return _gemini;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return KeepGemini(now, UiText.Get("AiGeminiExpired"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            AppLog.Write($"Gemini kullanımı okunamadı: {ex.GetType().Name} {(ex as HttpRequestException)?.StatusCode}");
            return KeepGemini(now, UiText.Get("AiFetchError"));
        }
    }

    private ProviderUsage KeepGemini(DateTime now, string error)
    {
        _gemini = new ProviderUsage(GeminiId, "Gemini", _geminiPlan, _gemini?.Windows ?? [], now, error);
        _geminiFetchedAt = now;
        return _gemini;
    }

    private static string? TierName(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var tier) || tier.ValueKind != JsonValueKind.Object) return null;
        var id = tier.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() ?? "" : "";
        if (id.Contains("ultra", StringComparison.OrdinalIgnoreCase)) return "AI Ultra";
        if (id.Contains("pro", StringComparison.OrdinalIgnoreCase)) return "AI Pro";
        return tier.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
    }

    private static async Task<JsonDocument> PostCodeAssistAsync(string method, string body, string accessToken, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, CodeAssist + method)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        // The service answers only Antigravity's own client (anything else gets "no valid licence"); this is its sign-in.
        request.Headers.UserAgent.ParseAdd("antigravity");
        using var response = await WidgetHttp.Client.SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
    }

    /// <summary>
    /// Each model has its own five-hour allowance; the tile shows the most used Gemini model and the most used of the
    /// others Antigravity offers (Claude, GPT-OSS). A spent allowance comes without "remainingFraction" (zero is left out).
    /// </summary>
    private static List<UsageWindow> ReadGeminiBuckets(JsonElement root)
    {
        (double Used, DateTime? Reset)? gemini = null, other = null;
        if (root.TryGetProperty("buckets", out var buckets) && buckets.ValueKind == JsonValueKind.Array)
            foreach (var bucket in buckets.EnumerateArray())
            {
                var model = bucket.TryGetProperty("modelId", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
                if (model.Length == 0 || model.StartsWith("tab_", StringComparison.Ordinal)) continue;
                if (!bucket.TryGetProperty("resetTime", out var r) || r.ValueKind != JsonValueKind.String
                    || !DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var reset)) continue;
                var left = bucket.TryGetProperty("remainingFraction", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetDouble() : 0;
                var used = Math.Clamp((1 - left) * 100, 0, 100);
                ref var slot = ref model.StartsWith("gemini", StringComparison.OrdinalIgnoreCase) ? ref gemini : ref other;
                if (slot is null || used > slot.Value.Used) slot = (used, reset.LocalDateTime);
            }
        var windows = new List<UsageWindow>();
        if (gemini is { } g) windows.Add(new UsageWindow("Gemini", g.Used, g.Reset, TimeSpan.FromHours(5), "gemini"));
        if (other is { } o) windows.Add(new UsageWindow(UiText.Get("AiGeminiOthers"), o.Used, o.Reset, TimeSpan.FromHours(5), "others"));
        return windows;
    }

    private static async Task RenewAntigravityAsync(CancellationToken token)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(AgyPath, "models")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetTempPath()
            });
            if (process is null) return;
            _ = process.StandardOutput.ReadToEndAsync(token);
            _ = process.StandardError.ReadToEndAsync(token);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(25));
            try { await process.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Trace.TraceWarning("Antigravity sign-in not renewed: {0}", ex.Message);
        }
    }

    /// <summary>Antigravity keeps its Google sign-in in Windows Credential Manager under "gemini:antigravity".</summary>
    private static (string AccessToken, DateTime ExpiresAt)? ReadAntigravitySignIn()
    {
        var blob = Credentials.Read("gemini:antigravity");
        if (blob is null) return null;
        try
        {
            using var json = JsonDocument.Parse(blob);
            if (!json.RootElement.TryGetProperty("token", out var t) || t.ValueKind != JsonValueKind.Object) return null;
            var access = t.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            if (string.IsNullOrEmpty(access)) return null;
            var expires = t.TryGetProperty("expiry", out var e) && e.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(e.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at.LocalDateTime : DateTime.MinValue;
            return (access, expires);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static class Credentials
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Credential
        {
            public int Flags, Type;
            public IntPtr TargetName, Comment;
            public long LastWritten;
            public int BlobSize;
            public IntPtr Blob;
            public int Persist, AttributeCount;
            public IntPtr Attributes, TargetAlias, UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredFree")]
        private static extern void CredFree(IntPtr buffer);

        public static string? Read(string target)
        {
            if (!CredRead(target, 1, 0, out var pointer)) return null;
            try
            {
                var credential = Marshal.PtrToStructure<Credential>(pointer);
                if (credential.BlobSize <= 0 || credential.Blob == IntPtr.Zero) return null;
                var bytes = new byte[credential.BlobSize];
                Marshal.Copy(credential.Blob, bytes, 0, bytes.Length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                CredFree(pointer);
            }
        }
    }

    // ---- OpenCode Go -------------------------------------------------------------------------

    private ProviderUsage? _openCode;
    private DateTime _openCodeFetchedAt = DateTime.MinValue;

    private static string OpenCodeDatabase => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "opencode", "opencode.db");

    private async Task<ProviderUsage?> GetOpenCodeAsync(CancellationToken token)
    {
        var now = DateTime.Now;
        if (_openCode is not null && now - _openCodeFetchedAt < OtherMinimumInterval) return _openCode;
        var signIn = await Task.Run(ReadOpenCodeSignIn, token).ConfigureAwait(false);
        if (signIn is null) return null;
        var (access, expiresAt, server, org) = signIn.Value;
        if (expiresAt <= now) return KeepOpenCode(now, UiText.Get("AiOpenCodeExpired"));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, server.TrimEnd('/') + "/api/go/status");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            if (!string.IsNullOrEmpty(org)) request.Headers.Add("x-org-id", org);
            using var response = await WidgetHttp.Client.SendAsync(request, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null; // no Go subscription
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return KeepOpenCode(now, UiText.Get("AiOpenCodeExpired"));
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            var windows = new List<UsageWindow>();
            if (json.RootElement.TryGetProperty("access", out var accessInfo) && accessInfo.ValueKind == JsonValueKind.Object
                && accessInfo.TryGetProperty("meters", out var meters) && meters.ValueKind == JsonValueKind.Object)
            {
                AddMeter(meters, "fiveHour", UiText.Format("AiHoursWindow", 5), TimeSpan.FromHours(5), "session", windows, now);
                AddMeter(meters, "week", UiText.Get("AiWeekly"), TimeSpan.FromDays(7), "weekly", windows, now);
                AddMeter(meters, "month", UiText.Get("AiMonthly"), TimeSpan.FromDays(30), "monthly", windows, now);
            }
            _openCode = new ProviderUsage(OpenCodeId, "OpenCode", "Go", windows, now, null);
            _openCodeFetchedAt = now;
            return _openCode;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException)
        {
            AppLog.Write($"OpenCode kullanımı okunamadı: {ex.GetType().Name} {(ex as HttpRequestException)?.StatusCode}");
            return KeepOpenCode(now, UiText.Get("AiFetchError"));
        }
    }

    private ProviderUsage KeepOpenCode(DateTime now, string error)
    {
        _openCode = new ProviderUsage(OpenCodeId, "OpenCode", "Go", _openCode?.Windows ?? [], now, error);
        _openCodeFetchedAt = now;
        return _openCode;
    }

    private static double Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.String => double.Parse(value.GetString()!, CultureInfo.InvariantCulture),
        _ => 0
    };

    /// <summary>A meter in millionths of a cent; a five-hour window that has run out starts again on the next use.</summary>
    private static void AddMeter(JsonElement meters, string property, string label, TimeSpan length, string key, List<UsageWindow> windows, DateTime now)
    {
        if (!meters.TryGetProperty(property, out var meter) || meter.ValueKind != JsonValueKind.Object) return;
        var limit = meter.TryGetProperty("limitMicroCents", out var l) ? Number(l) : 0;
        if (limit <= 0) return;
        var used = meter.TryGetProperty("usedMicroCents", out var u) ? Number(u) : 0;
        DateTime? reset = meter.TryGetProperty("resetsAt", out var r) && r.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at.LocalDateTime : null;
        if (reset is { } passed && passed <= now) (used, reset) = (0, null);
        windows.Add(new UsageWindow(label, Math.Clamp(used / limit * 100, 0, 100), reset, length, key));
    }

    /// <summary>The OpenCode app's console sign-in, kept in its own database (read-only, through Windows' SQLite).</summary>
    private static (string Access, DateTime ExpiresAt, string Server, string? Org)? ReadOpenCodeSignIn()
    {
        if (!File.Exists(OpenCodeDatabase)) return null;
        var value = Sqlite.ReadText(OpenCodeDatabase, "SELECT value FROM credential WHERE integration_id = 'opencode' ORDER BY active DESC, time_updated DESC LIMIT 1");
        if (value is null) return null;
        try
        {
            using var json = JsonDocument.Parse(value);
            var root = json.RootElement;
            var access = root.TryGetProperty("access", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            if (string.IsNullOrEmpty(access)) return null;
            var expires = root.TryGetProperty("expires", out var e) && e.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeMilliseconds(e.GetInt64()).LocalDateTime : DateTime.MaxValue;
            string server = "https://opencode.ai/console";
            string? org = null;
            if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
            {
                if (metadata.TryGetProperty("server", out var s) && s.ValueKind == JsonValueKind.String
                    && Uri.TryCreate(s.GetString(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.EndsWith("opencode.ai", StringComparison.OrdinalIgnoreCase))
                    server = uri.ToString();
                if (metadata.TryGetProperty("orgID", out var o) && o.ValueKind == JsonValueKind.String) org = o.GetString();
            }
            return (access, expires, server, org);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The SQLite that ships with Windows (winsqlite3.dll), so reading one value adds nothing to QiDock.</summary>
    private static class Sqlite
    {
        private const int OpenReadOnly = 0x1, Row = 100;

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2")]
        private static extern int Open(byte[] path, out IntPtr db, int flags, IntPtr vfs);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_busy_timeout")]
        private static extern int BusyTimeout(IntPtr db, int milliseconds);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2")]
        private static extern int Prepare(IntPtr db, byte[] sql, int length, out IntPtr statement, IntPtr tail);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step")]
        private static extern int Step(IntPtr statement);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_text")]
        private static extern IntPtr ColumnText(IntPtr statement, int column);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize")]
        private static extern int Finalize(IntPtr statement);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close")]
        private static extern int Close(IntPtr db);

        private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + '\u0000'.ToString()); // C strings end in a zero byte

        public static string? ReadText(string path, string sql)
        {
            try
            {
                if (Open(Utf8(path), out var db, OpenReadOnly, IntPtr.Zero) != 0)
                {
                    if (db != IntPtr.Zero) Close(db);
                    return null;
                }
                try
                {
                    BusyTimeout(db, 2000);
                    if (Prepare(db, Utf8(sql), -1, out var statement, IntPtr.Zero) != 0) return null;
                    try
                    {
                        return Step(statement) == Row ? Marshal.PtrToStringUTF8(ColumnText(statement, 0)) : null;
                    }
                    finally
                    {
                        Finalize(statement);
                    }
                }
                finally
                {
                    Close(db);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
        }
    }
}
