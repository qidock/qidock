using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace QiDock.Services;

/// <summary>What a Claude Code session did last, read from the end of its transcript.</summary>
public sealed record ClaudeActivity(string SessionFile, string Project, string Detail, bool Done, DateTime UpdatedAt)
{
    /// <summary>Still at work: not finished and written to within the last minute and a half.</summary>
    public bool Working => !Done && DateTime.UtcNow - UpdatedAt < TimeSpan.FromSeconds(90);
}

/// <summary>
/// The Claude Code sessions on this PC (<c>%USERPROFILE%\.claude\projects\**\*.jsonl</c>), read without any network
/// access: the last few kilobytes of each recent transcript are turned into a short line ("2 dosya düzenleniyor…",
/// "Komut çalışıyor…", "Bitti"). Nothing is written and nothing is kept. Only used while the Claude card is open:
/// <see cref="RecentAsync"/> lists the sessions, and the watcher reports changes while the card stays open.
/// </summary>
public sealed class ClaudeActivityWatcher : IDisposable
{
    private const int TailBytes = 64 * 1024;
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    private readonly Dispatcher _dispatcher;
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _debounce;

    /// <summary>A transcript changed (raised on the UI thread, at most a few times a second).</summary>
    public event Action? Changed;

    public ClaudeActivityWatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _debounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(600) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Changed?.Invoke();
        };
    }

    /// <summary>The sessions written to within <paramref name="within"/>, newest first (at most <paramref name="limit"/>).</summary>
    public static Task<List<ClaudeActivity>> RecentAsync(TimeSpan within, int limit = 5) => Task.Run(() =>
    {
        if (!Directory.Exists(Root)) return new List<ClaudeActivity>();
        var since = DateTime.UtcNow - within;
        try
        {
            return new DirectoryInfo(Root).EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .Where(file => file.LastWriteTimeUtc >= since && !IsSubagent(file.FullName))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(limit)
                .Select(file => Read(file.FullName, file.LastWriteTimeUtc))
                .OfType<ClaudeActivity>()
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<ClaudeActivity>();
        }
    });

    // Subagent transcripts (…\subagents\…) belong to their main session, which reports the Agent step itself.
    private static bool IsSubagent(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}subagents{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    public void Start()
    {
        if (_watcher is not null || !Directory.Exists(Root)) return;
        try
        {
            _watcher = new FileSystemWatcher(Root, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                InternalBufferSize = 16 * 1024
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            AppLog.Write($"Claude oturumları izlenemiyor: {ex.Message}");
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    public void Stop()
    {
        _debounce.Stop();
        _watcher?.Dispose();
        _watcher = null;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (IsSubagent(e.FullPath)) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (!_debounce.IsEnabled) _debounce.Start();
        });
    }

    private static ClaudeActivity? Read(string path, DateTime writtenAt)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - TailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[stream.Length - start];
            var read = stream.Read(buffer, 0, buffer.Length);
            var lines = Encoding.UTF8.GetString(buffer, 0, read).Split('\n');
            // Newest first; the first line may be cut in half and is skipped.
            for (var i = lines.Length - 1; i >= (start > 0 ? 1 : 0); i--)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line[0] != '{') continue;
                (string Project, string Detail, bool Done)? step;
                try { step = Describe(line); }
                catch (JsonException) { continue; } // a line still being written
                if (step is { } found)
                    return new ClaudeActivity(path, found.Project, found.Detail, found.Done, writtenAt);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return null;
    }

    /// <summary>A transcript line turned into (project, what is happening, finished), or null for lines that say nothing.</summary>
    private static (string Project, string Detail, bool Done)? Describe(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type is not ("assistant" or "user")) return null;
        if (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) return null;
        var project = root.TryGetProperty("cwd", out var cwd) && cwd.GetString() is { Length: > 0 } dir
            ? Path.GetFileName(dir.TrimEnd('\\', '/')) : "Claude";
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return null;
        var content = message.TryGetProperty("content", out var c) ? c : default;

        if (type == "user")
        {
            // A tool's result came back, or the user wrote: Claude is thinking about the next step.
            return (project, UiText.Get("ClaudeThinking"), false);
        }

        var stop = message.TryGetProperty("stop_reason", out var s) ? s.GetString() : null;
        var tools = new List<(string Name, JsonElement Input)>();
        if (content.ValueKind == JsonValueKind.Array)
            foreach (var part in content.EnumerateArray())
                if (part.TryGetProperty("type", out var pt) && pt.GetString() == "tool_use" && part.TryGetProperty("name", out var name))
                    tools.Add((name.GetString() ?? "", part.TryGetProperty("input", out var input) ? input : default));
        if (tools.Count == 0)
            return stop is "end_turn" or "stop_sequence" ? (project, UiText.Get("ClaudeDone"), true) : (project, UiText.Get("ClaudeWriting"), false);
        return (project, ToolLine(tools), false);
    }

    private static string ToolLine(List<(string Name, JsonElement Input)> tools)
    {
        static string? Field(JsonElement input, string key) =>
            input.ValueKind == JsonValueKind.Object && input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var edits = tools.Where(x => x.Name is "Edit" or "Write" or "MultiEdit" or "NotebookEdit")
            .Select(x => Field(x.Input, "file_path") ?? Field(x.Input, "notebook_path") ?? x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (edits > 0) return edits == 1 && tools.FirstOrDefault(x => x.Name is "Edit" or "Write" or "MultiEdit").Input is var input && Field(input, "file_path") is { } file
            ? UiText.Format("ClaudeEditingFile", Path.GetFileName(file))
            : UiText.Format("ClaudeEditingFiles", edits);
        var last = tools[^1];
        return last.Name switch
        {
            "Bash" or "PowerShell" => Field(last.Input, "description") is { Length: > 0 } what ? Trim(what) : UiText.Get("ClaudeRunning"),
            "Read" or "Grep" or "Glob" => UiText.Get("ClaudeReading"),
            "WebSearch" or "WebFetch" => UiText.Get("ClaudeBrowsing"),
            "Agent" or "Task" or "Workflow" => UiText.Get("ClaudeAgents"),
            _ when last.Name.StartsWith("mcp__", StringComparison.Ordinal) => UiText.Get("ClaudeTools"),
            _ => UiText.Get("ClaudeWorking")
        };
    }

    private static string Trim(string text) => text.Length <= 42 ? text : text[..41].TrimEnd() + "…";

    public void Dispose() => Stop();
}
