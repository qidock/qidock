using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QiDock.Services.Widgets;

/// <param name="Length">How long the window lasts (5 hours, 7 days…) when known; used for pace projections.</param>
/// <param name="Key">Stable id for the usage history (labels are localized).</param>
public sealed record UsageWindow(string Label, double UsedPercent, DateTime? ResetsAt, TimeSpan? Length = null, string? Key = null)
{
    public DateTime? StartsAt => ResetsAt is { } reset && Length is { } length ? reset - length : null;
}

public sealed record ProviderUsage(
    string Provider,
    string DisplayName,
    string? Plan,
    IReadOnlyList<UsageWindow> Windows,
    DateTime UpdatedAt,
    string? Error);

/// <summary>
/// Usage limits for AI subscriptions that are already signed in on this PC.
/// Claude: reads Claude Code's OAuth session and asks Anthropic's usage endpoint (the same one Claude Code's /usage uses).
/// ChatGPT: reads the latest rate-limit snapshot Codex CLI writes into its local session logs (no network).
/// Tokens are only read from disk and sent to their own provider; they are never stored or logged.
/// </summary>
public sealed class AiUsageService
{
    public const string ClaudeId = "claude";
    public const string CodexId = "chatgpt";

    private static readonly TimeSpan ClaudeMinimumInterval = TimeSpan.FromMinutes(2);
    private ProviderUsage? _claude;
    private DateTime _claudeFetchedAt = DateTime.MinValue;
    private DateTime _claudeBackoffUntil = DateTime.MinValue;

    public AiUsageService()
    {
        Cached = LastKnown();
        _claude = Cached.FirstOrDefault(u => u.Provider == ClaudeId);
    }

    /// <summary>
    /// The last readings saved on this PC (percentages and reset times only), so the tiles show numbers the moment
    /// QiDock starts instead of a dash until the first answer arrives. Limits that have reset since are left out.
    /// </summary>
    public IReadOnlyList<ProviderUsage> Cached { get; }

    public async Task<IReadOnlyList<ProviderUsage>> FetchAsync(bool force, CancellationToken token)
    {
        var results = new List<ProviderUsage>();
        var claude = await GetClaudeAsync(force, token).ConfigureAwait(false);
        if (claude is not null) results.Add(claude);
        var codex = await Task.Run(ReadCodex, token).ConfigureAwait(false);
        if (codex is not null) results.Add(codex);
        SaveLastKnown(results);
        return results;
    }

    private static readonly string LastKnownPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QiDock", "ai-usage-last.json");
    private static readonly object LastKnownLock = new();

    private static IReadOnlyList<ProviderUsage> LastKnown()
    {
        try
        {
            lock (LastKnownLock)
            {
                if (!File.Exists(LastKnownPath)) return [];
                var saved = JsonSerializer.Deserialize<List<ProviderUsage>>(File.ReadAllText(LastKnownPath)) ?? [];
                var now = DateTime.Now;
                return saved
                    .Select(u => u with { Windows = u.Windows.Where(w => w.ResetsAt is not { } reset || reset > now).ToList(), Error = null })
                    .Where(u => u.Windows.Count > 0)
                    .ToList();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>Keeps each provider's latest good reading; a failed reading leaves the saved one as it was.</summary>
    private static void SaveLastKnown(IReadOnlyList<ProviderUsage> results)
    {
        var good = results.Where(u => u.Error is null && u.Windows.Count > 0).ToList();
        if (good.Count == 0) return;
        try
        {
            lock (LastKnownLock)
            {
                List<ProviderUsage> saved = [];
                if (File.Exists(LastKnownPath))
                {
                    try { saved = JsonSerializer.Deserialize<List<ProviderUsage>>(File.ReadAllText(LastKnownPath)) ?? []; }
                    catch (JsonException) { }
                }
                var merged = saved.Where(s => good.All(g => g.Provider != s.Provider)).Concat(good).ToList();
                Directory.CreateDirectory(Path.GetDirectoryName(LastKnownPath)!);
                var temporary = LastKnownPath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(merged));
                File.Move(temporary, LastKnownPath, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            System.Diagnostics.Trace.TraceWarning("AI usage not saved: {0}", ex.Message);
        }
    }

    // ---- Claude ----------------------------------------------------------------------------

    private async Task<ProviderUsage?> GetClaudeAsync(bool force, CancellationToken token)
    {
        var now = DateTime.Now;
        if (_claude is not null && (now < _claudeBackoffUntil || (!force && now - _claudeFetchedAt < ClaudeMinimumInterval)
            || (force && now - _claudeFetchedAt < TimeSpan.FromSeconds(20))))
            return _claude;

        var credentials = ReadClaudeCredentials();
        if (credentials is null) return null;
        var (accessToken, expiresAt, plan) = credentials.Value;
        // Claude used from the desktop app keeps its own sign-in, so this file's key runs out unless renewed here.
        if (expiresAt is { } expiry && expiry <= DateTime.Now.AddMinutes(1))
        {
            if (await RenewClaudeSignInAsync(false, token).ConfigureAwait(false) && ReadClaudeCredentials() is { } renewed)
                (accessToken, expiresAt, plan) = renewed;
            else
            {
                _claude = new ProviderUsage(ClaudeId, "Claude", plan, _claude?.Windows ?? [], now, UiText.Get("AiClaudeExpired"));
                _claudeFetchedAt = now;
                return _claude;
            }
        }

        try
        {
            var response = await AskUsageAsync(accessToken, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized && await RenewClaudeSignInAsync(true, token).ConfigureAwait(false)
                && ReadClaudeCredentials() is { } fresh)
            {
                response.Dispose();
                accessToken = fresh.AccessToken;
                response = await AskUsageAsync(accessToken, token).ConfigureAwait(false);
            }
            using var _ = response;
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _claudeBackoffUntil = now.AddMinutes(10);
                _claude ??= new ProviderUsage(ClaudeId, "Claude", plan, [], now, UiText.Get("AiRateLimited"));
                return _claude;
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _claude = new ProviderUsage(ClaudeId, "Claude", plan, _claude?.Windows ?? [], now, UiText.Get("AiClaudeExpired"));
                _claudeFetchedAt = now;
                return _claude;
            }
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            var windows = ReadClaudeLimits(json.RootElement);
            if (windows.Count == 0)
            {
                AddClaudeWindow(json.RootElement, "five_hour", UiText.Get("AiSession"), windows);
            AddClaudeWindow(json.RootElement, "seven_day", UiText.Get("AiWeekly"), windows);
            AddClaudeWindow(json.RootElement, "seven_day_opus", UiText.Get("AiWeeklyOpus"), windows, optional: true);
            AddClaudeWindow(json.RootElement, "seven_day_sonnet", UiText.Get("AiWeeklySonnet"), windows, optional: true);
            }
            _claude = new ProviderUsage(ClaudeId, "Claude", plan, windows, now, null);
            _claudeFetchedAt = now;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _claude = new ProviderUsage(ClaudeId, "Claude", plan, _claude?.Windows ?? [], now, UiText.Get("AiFetchError"));
            _claudeFetchedAt = now;
        }

        return _claude;
    }

    /// <summary>
    /// Newer responses carry a "limits" list (session, weekly_all, weekly_scoped per model).
    /// Session and the overall weekly limit come first so the dock tile shows those two.
    /// </summary>
    private static List<UsageWindow> ReadClaudeLimits(JsonElement root)
    {
        var windows = new List<UsageWindow>();
        if (!root.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array) return windows;
        var scoped = new List<UsageWindow>();
        foreach (var limit in limits.EnumerateArray())
        {
            if (limit.ValueKind != JsonValueKind.Object) continue;
            var kind = limit.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
            if (!limit.TryGetProperty("percent", out var p) || p.ValueKind != JsonValueKind.Number) continue;
            DateTime? resets = null;
            if (limit.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                resets = parsed.LocalDateTime;
            var percent = Math.Clamp(p.GetDouble(), 0, 100);
            switch (kind)
            {
                case "session":
                    windows.Insert(0, new UsageWindow(UiText.Get("AiSession"), percent, resets, TimeSpan.FromHours(5), "session"));
                    break;
                case "weekly_all":
                    windows.Add(new UsageWindow(UiText.Get("AiWeekly"), percent, resets, TimeSpan.FromDays(7), "weekly"));
                    break;
                case "weekly_scoped":
                    var model = limit.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object
                        && scope.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object
                        && m.TryGetProperty("display_name", out var name) && name.ValueKind == JsonValueKind.String
                        ? name.GetString() : null;
                    scoped.Add(new UsageWindow(model is null ? UiText.Get("AiWeekly") : $"{UiText.Get("AiWeekly")} · {model}", percent, resets, TimeSpan.FromDays(7), "weekly-" + (model ?? "scoped")));
                    break;
            }
        }
        windows.AddRange(scoped);
        return windows;
    }

    private static void AddClaudeWindow(JsonElement root, string property, string label, List<UsageWindow> windows, bool optional = false)
    {
        if (!root.TryGetProperty(property, out var window) || window.ValueKind != JsonValueKind.Object) return;
        if (!window.TryGetProperty("utilization", out var utilization) || utilization.ValueKind != JsonValueKind.Number) return;
        var used = utilization.GetDouble();
        DateTime? resets = null;
        if (window.TryGetProperty("resets_at", out var reset) && reset.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(reset.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            resets = parsed.LocalDateTime;
        if (optional && used <= 0) return;
        var length = property == "five_hour" ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7);
        var key = property == "five_hour" ? "session" : property == "seven_day" ? "weekly" : property;
        windows.Add(new UsageWindow(label, Math.Clamp(used, 0, 100), resets, length, key));
    }

    private static async Task<HttpResponseMessage> AskUsageAsync(string accessToken, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        return await WidgetHttp.Client.SendAsync(request, token).ConfigureAwait(false);
    }

    private static string ClaudeCredentialsPath()
    {
        var directory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(directory))
            directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return Path.Combine(directory, ".credentials.json");
    }

    // The same renewal Claude Code does itself: its public app id and Anthropic's token address.
    private const string ClaudeTokenUrl = "https://platform.claude.com/v1/oauth/token";
    private const string ClaudeClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private static readonly SemaphoreSlim RenewLock = new(1, 1);
    private static DateTime _renewFailedAt = DateTime.MinValue;

    /// <summary>
    /// Renews Claude Code's expired sign-in with its refresh key and writes the new keys back into the same file, so
    /// Claude Code keeps working with them. True when the file now holds a usable key (also when Claude Code or the
    /// other usage reader renewed it meanwhile). A failed attempt waits ten minutes before the next one.
    /// </summary>
    private static async Task<bool> RenewClaudeSignInAsync(bool force, CancellationToken token)
    {
        await RenewLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var path = ClaudeCredentialsPath();
            if (!File.Exists(path)) return false;
            if (JsonNode.Parse(await File.ReadAllTextAsync(path, token).ConfigureAwait(false)) is not JsonObject root
                || root["claudeAiOauth"] is not JsonObject oauth) return false;
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!force && oauth["expiresAt"] is JsonValue e && e.TryGetValue<long>(out var expires) && expires > nowMs + 60_000) return true;
            if (DateTime.Now - _renewFailedAt < TimeSpan.FromMinutes(10)) return false;
            if (oauth["refreshToken"] is not JsonValue r || !r.TryGetValue<string>(out var refreshToken) || string.IsNullOrEmpty(refreshToken)) return false;

            var body = new JsonObject
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = ClaudeClientId
            };
            if (oauth["scopes"] is JsonArray scopes && scopes.Count > 0)
                body["scope"] = string.Join(' ', scopes.Select(s => s?.GetValue<string>()).Where(s => !string.IsNullOrEmpty(s)));
            using var request = new HttpRequestMessage(HttpMethod.Post, ClaudeTokenUrl)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            using var response = await WidgetHttp.Client.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _renewFailedAt = DateTime.Now;
                System.Diagnostics.Trace.TraceWarning("Claude sign-in renewal failed: {0}", (int)response.StatusCode);
                return false;
            }
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false), cancellationToken: token).ConfigureAwait(false);
            var answer = json.RootElement;
            if (!answer.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String
                || !answer.TryGetProperty("expires_in", out var lifetime) || lifetime.ValueKind != JsonValueKind.Number)
            {
                _renewFailedAt = DateTime.Now;
                return false;
            }
            oauth["accessToken"] = access.GetString();
            if (answer.TryGetProperty("refresh_token", out var nextRefresh) && nextRefresh.ValueKind == JsonValueKind.String)
                oauth["refreshToken"] = nextRefresh.GetString();
            oauth["expiresAt"] = nowMs + (long)(lifetime.GetDouble() * 1000);
            if (answer.TryGetProperty("refresh_token_expires_in", out var refreshLifetime) && refreshLifetime.ValueKind == JsonValueKind.Number)
                oauth["refreshTokenExpiresAt"] = nowMs + (long)(refreshLifetime.GetDouble() * 1000);
            if (answer.TryGetProperty("scope", out var granted) && granted.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(granted.GetString()))
                oauth["scopes"] = new JsonArray(granted.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => (JsonNode?)s).ToArray());

            // Written beside it first and then swapped in, so Claude Code never reads a half-written file.
            var temporary = path + ".qidock-renew";
            await File.WriteAllTextAsync(temporary, root.ToJsonString(), token).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            if (ex is TaskCanceledException && token.IsCancellationRequested) throw;
            _renewFailedAt = DateTime.Now;
            System.Diagnostics.Trace.TraceWarning("Claude sign-in renewal failed: {0}", ex.GetType().Name);
            return false;
        }
        finally
        {
            RenewLock.Release();
        }
    }

    private static (string AccessToken, DateTime? ExpiresAt, string? Plan)? ReadClaudeCredentials()
    {
        var path = ClaudeCredentialsPath();
        if (!File.Exists(path)) return null;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (!json.RootElement.TryGetProperty("claudeAiOauth", out var oauth)) return null;
            var accessToken = oauth.TryGetProperty("accessToken", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(accessToken)) return null;
            DateTime? expiresAt = oauth.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeMilliseconds(e.GetInt64()).LocalDateTime : null;
            var plan = oauth.TryGetProperty("subscriptionType", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            return (accessToken, expiresAt, Capitalize(plan));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    // ---- ChatGPT / Codex ---------------------------------------------------------------------

    private static ProviderUsage? ReadCodex()
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var sessions = Path.Combine(home, "sessions");
        if (!Directory.Exists(sessions)) return null;

        try
        {
            foreach (var file in NewestSessionFiles(sessions).Take(25))
            {
                var line = FindLastLine(file, "\"rate_limits\"");
                if (line is null) continue;
                using var json = JsonDocument.Parse(line);
                if (FindProperty(json.RootElement, "rate_limits") is not { ValueKind: JsonValueKind.Object } limits) continue;
                var updated = json.RootElement.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp)
                    ? stamp.LocalDateTime : File.GetLastWriteTime(file);

                var windows = new List<UsageWindow>();
                AddCodexWindow(limits, "primary", windows);
                AddCodexWindow(limits, "secondary", windows);
                if (windows.Count == 0) continue;
                var plan = limits.TryGetProperty("plan_type", out var p) && p.ValueKind == JsonValueKind.String ? Capitalize(p.GetString()) : null;
                return new ProviderUsage(CodexId, "ChatGPT", plan, windows, updated, null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ProviderUsage(CodexId, "ChatGPT", null, [], DateTime.Now, UiText.Get("AiFetchError"));
        }

        return null;
    }

    private static void AddCodexWindow(JsonElement limits, string property, List<UsageWindow> windows)
    {
        if (!limits.TryGetProperty(property, out var window) || window.ValueKind != JsonValueKind.Object) return;
        var used = window.TryGetProperty("used_percent", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : 0;
        var minutes = window.TryGetProperty("window_minutes", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 0;
        DateTime? resets = null;
        if (window.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.Number)
            resets = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64()).LocalDateTime;
        else if (window.TryGetProperty("resets_in_seconds", out var rs) && rs.ValueKind == JsonValueKind.Number)
            resets = DateTime.Now.AddSeconds(rs.GetDouble());
        // A snapshot taken before the window reset no longer applies.
        if (resets is { } reset && reset <= DateTime.Now) used = 0;
        windows.Add(new UsageWindow(WindowLabel(minutes), Math.Clamp(used, 0, 100), resets is { } r2 && r2 > DateTime.Now ? r2 : null,
            minutes > 0 ? TimeSpan.FromMinutes(minutes) : null, property));
    }

    private static string WindowLabel(int minutes) => minutes switch
    {
        <= 0 => UiText.Get("AiSession"),
        300 => UiText.Get("AiSession"),
        10080 => UiText.Get("AiWeekly"),
        >= 40000 and <= 46000 => UiText.Get("AiMonthly"),
        < 1440 => UiText.Format("AiHoursWindow", minutes / 60),
        _ => UiText.Format("AiDaysWindow", minutes / 1440)
    };

    private static IEnumerable<string> NewestSessionFiles(string root)
    {
        // sessions/YYYY/MM/DD/rollout-*.jsonl — walk newest folders first instead of scanning everything.
        foreach (var year in Directory.EnumerateDirectories(root).OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
        foreach (var month in Directory.EnumerateDirectories(year).OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
        foreach (var day in Directory.EnumerateDirectories(month).OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
        foreach (var file in Directory.EnumerateFiles(day, "*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc))
            yield return file;
    }

    private static string? FindLastLine(string path, string marker)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        const int chunk = 512 * 1024;
        var start = Math.Max(0, stream.Length - chunk);
        while (true)
        {
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[stream.Length - start];
            stream.ReadExactly(buffer);
            var text = Encoding.UTF8.GetString(buffer);
            var lines = text.Split('\n');
            for (var i = lines.Length - 1; i >= (start == 0 ? 0 : 1); i--)
                if (lines[i].Contains(marker, StringComparison.Ordinal)) return lines[i];
            if (start == 0 || stream.Length - start > 16 * chunk) return null;
            start = Math.Max(0, start - chunk * 4);
        }
    }

    private static JsonElement? FindProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(name)) return property.Value;
                if (FindProperty(property.Value, name) is { } nested) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (FindProperty(item, name) is { } nested) return nested;
        }
        return null;
    }

    private static string? Capitalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : char.ToUpperInvariant(value[0]) + value[1..];
}
