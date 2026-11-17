using System.IO;

namespace QiDock.Services;

/// <summary>A small log of starts, exits and errors (%LOCALAPPDATA%\QiDock\app.log), so an unexpected exit can be traced.</summary>
public static class AppLog
{
    private static readonly object Gate = new();

    private static string Path => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QiDock", "app.log");

    private static System.Windows.Threading.DispatcherTimer? _memoryTimer;

    /// <summary>
    /// Writes how much memory the app holds a minute after start and then every 30 minutes (the whole process and
    /// the managed heap apart), so a growing footprint shows up in the log.
    /// </summary>
    public static void StartMemoryReports()
    {
        _memoryTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
        _memoryTimer.Tick += (_, _) =>
        {
            _memoryTimer.Interval = TimeSpan.FromMinutes(30);
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var gc = GC.GetGCMemoryInfo();
            Write($"Bellek: çalışma kümesi {process.WorkingSet64 >> 20} MB, özel {process.PrivateMemorySize64 >> 20} MB, " +
                  $"yönetilen yığın {gc.HeapSizeBytes >> 20} MB (ayrılan {gc.TotalCommittedBytes >> 20} MB), iş parçacığı {process.Threads.Count}");
        };
        _memoryTimer.Start();
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var file = new FileInfo(Path);
                if (file.Exists && file.Length > 512 * 1024) File.Move(Path, Path + ".old", overwrite: true);
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging never takes the app down
        }
    }
}
