using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace QiDock.Files;

/// <summary>
/// Lets "QiDock.exe --files [folder]" open the Files window of the copy that is already running: the new copy
/// leaves the folder in a small file and rings a named event the running copy waits on.
/// </summary>
internal static class FilesLauncher
{
    private const string EventName = "Local\\QiDock.OpenFiles";
    private static readonly string RequestPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QiDock", "open-files.txt");
    private static EventWaitHandle? _event;
    private static RegisteredWaitHandle? _wait;

    public static void Signal(string folder)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RequestPath)!);
            File.WriteAllText(RequestPath, folder);
            // The user started this copy, so it may hand the right to come to the front to the running one.
            AllowSetForegroundWindow(-1);
            if (EventWaitHandle.TryOpenExisting(EventName, out var ring))
            {
                using (ring) ring.Set();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException) { }
    }

    public static void Listen(Dispatcher dispatcher)
    {
        if (_event is not null) return;
        _event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        _wait = ThreadPool.RegisterWaitForSingleObject(_event, (_, _) =>
        {
            string folder;
            try { folder = File.Exists(RequestPath) ? File.ReadAllText(RequestPath).Trim() : ""; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { folder = ""; }
            dispatcher.BeginInvoke(() => FilesWindow.Open(folder.Length > 0 ? folder : null));
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
