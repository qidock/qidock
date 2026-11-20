using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace QiDock.Services.Widgets;

/// <summary>One program's share of the processor (all its processes together, e.g. every Chrome tab).</summary>
internal sealed record CpuUser(string Name, string? Path, double Percent);

/// <summary>
/// Which programs use the processor most: each sample compares every process's processor time with the previous
/// sample. Only read while someone shows it (the upright CPU widget), every few seconds.
/// </summary>
internal static class CpuTop
{
    private static readonly Dictionary<int, TimeSpan> Previous = [];
    private static readonly Dictionary<string, (string Name, string? Path)> Names = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _previousAt;

    /// <summary>The busiest programs since the last call (empty on the first call, which only takes the baseline).</summary>
    public static IReadOnlyList<CpuUser> Sample(int count)
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _previousAt).TotalMilliseconds;
        var first = _previousAt == default || elapsed > 30000;
        _previousAt = now;
        var totals = new Dictionary<string, (double Ms, int Pid)>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id is 0 or 4) continue; // idle and the kernel
                TimeSpan time;
                try { time = process.TotalProcessorTime; }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { continue; }
                seen.Add(process.Id);
                var before = Previous.TryGetValue(process.Id, out var last) ? last : time;
                Previous[process.Id] = time;
                if (first) continue;
                var used = (time - before).TotalMilliseconds;
                if (used <= 0) continue;
                var key = process.ProcessName;
                totals[key] = totals.TryGetValue(key, out var sum) ? (sum.Ms + used, sum.Pid) : (used, process.Id);
            }
        }
        foreach (var gone in Previous.Keys.Where(id => !seen.Contains(id)).ToList()) Previous.Remove(gone);
        if (first || elapsed <= 0) return [];
        var capacity = elapsed * Environment.ProcessorCount;
        return totals
            .OrderByDescending(t => t.Value.Ms)
            .Take(count)
            .Select(t =>
            {
                var (name, path) = Describe(t.Key, t.Value.Pid);
                return new CpuUser(name, path, Math.Min(100, t.Value.Ms / capacity * 100));
            })
            .ToList();
    }

    /// <summary>A readable name ("Google Chrome" rather than "chrome") and the program file for its icon.</summary>
    private static (string Name, string? Path) Describe(string processName, int pid)
    {
        if (Names.TryGetValue(processName, out var known)) return known;
        var path = ImagePath(pid);
        var name = processName;
        if (path is not null)
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                if (!string.IsNullOrEmpty(description) && description.Length <= 40) name = description;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        if (name == processName && name.Length > 0) name = char.ToUpperInvariant(name[0]) + name[1..];
        return Names[processName] = (name, path);
    }

    private static string? ImagePath(int pid)
    {
        const uint QueryLimitedInformation = 0x1000;
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == 0) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
