using System.IO;
using System.Text.Json;

namespace QiDock.Services.Widgets;

/// <summary>One recorded reading of a weekly limit.</summary>
public sealed record UsageSample(DateTime At, double Percent);

/// <summary>
/// Keeps a small local history of weekly usage so the popup can draw this week's curve and
/// project when the limit would run out. Only percentages and times are stored, never tokens.
/// </summary>
public sealed class AiUsageHistory
{
    private static readonly TimeSpan MinimumSpacing = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(9);

    private readonly string _path;
    private Dictionary<string, List<UsageSample>> _samples = new(StringComparer.Ordinal);
    private bool _loaded;

    public AiUsageHistory(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QiDock",
            "ai-usage-history.json");
    }

    public void Record(string key, double percent, DateTime at)
    {
        EnsureLoaded();
        if (!_samples.TryGetValue(key, out var list))
        {
            list = [];
            _samples[key] = list;
        }

        var changed = list.Count == 0 || at - list[^1].At >= MinimumSpacing || Math.Abs(list[^1].Percent - percent) >= 0.5;
        if (!changed) return;
        list.Add(new UsageSample(at, percent));
        list.RemoveAll(sample => at - sample.At > KeepFor);
        Save();
    }

    /// <summary>Samples between <paramref name="from"/> and now, oldest first.</summary>
    public IReadOnlyList<UsageSample> Since(string key, DateTime from)
    {
        EnsureLoaded();
        return _samples.TryGetValue(key, out var list)
            ? list.Where(sample => sample.At >= from).OrderBy(sample => sample.At).ToList()
            : [];
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(_path)) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, List<UsageSample>>>(File.ReadAllText(_path));
            if (data is not null) _samples = new Dictionary<string, List<UsageSample>>(data, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _samples = new Dictionary<string, List<UsageSample>>(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_samples));
            File.Move(temporary, _path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("AI usage history not saved: {0}", ex.Message);
        }
    }
}
