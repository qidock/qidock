using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace QiDock.Services;

/// <summary>An installed app as Windows lists it in "All apps": its name and shell parsing name.</summary>
/// <param name="Target">The program file a desktop app starts (null for Store apps, which have none).</param>
public sealed record InstalledApp(string Name, string Id, string? Target = null)
{
    /// <summary>Starts the app's program file as administrator (Windows asks for permission first).</summary>
    public void LaunchAsAdministrator()
    {
        if (Target is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(Target) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(Target) ?? "" })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Declining the permission prompt lands here too.
            Trace.TraceWarning("Could not start {0} as administrator: {1}", Target, ex.Message);
        }
    }

    /// <summary>Opens the folder of the app's program file with the file selected.</summary>
    public void ShowFileLocation()
    {
        if (Target is null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Target}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Trace.TraceWarning("Could not show {0}: {1}", Target, ex.Message);
        }
    }

    public void Launch()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + Id) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Trace.TraceWarning("Could not start {0}: {1}", Id, ex.Message);
        }
    }
}

/// <summary>
/// The apps of the Start menu's "All apps" list (desktop and Store apps alike), read from the
/// shell's Applications folder. The list is read when asked and not kept.
/// </summary>
public static class AppCatalog
{
    // Start menu entries that are documents or links rather than apps.
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".chm", ".pdf", ".htm", ".html", ".url", ".rtf", ".ini", ".log", ".md", ".xml", ".hlp", ".doc", ".docx"
    };

    public static Task<List<InstalledApp>> LoadAsync()
    {
        var done = new TaskCompletionSource<List<InstalledApp>>();
        // Shell.Application needs a single-threaded apartment.
        var thread = new Thread(() =>
        {
            try { done.SetResult(Read()); }
            catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                Trace.TraceWarning("Could not list apps: {0}", ex.Message);
                done.SetResult([]);
            }
        }) { IsBackground = true, Name = "App catalog" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static List<InstalledApp> Read()
    {
        var list = new List<InstalledApp>();
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type is null) return list;
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic? folder = shell.NameSpace("shell:AppsFolder");
            if (folder is null) return list;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name ?? "";
                string id = item.Path ?? "";
                if (name.Length == 0 || id.Length == 0 || IsNoise(name, id) || !seen.Add(name)) continue;
                // The program a desktop app's Start menu entry points at (empty for Store apps).
                string? target = null;
                try { target = item.ExtendedProperty("System.Link.TargetParsingPath") as string; }
                catch (COMException) { }
                if (string.IsNullOrWhiteSpace(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) target = null;
                list.Add(new InstalledApp(name, id, target));
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return list;
    }

    private static bool IsNoise(string name, string id)
    {
        if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || name.Contains("kaldır", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return true;
        var extension = Path.GetExtension(id.Contains('!') ? "" : id);
        return extension.Length > 0 && DocumentExtensions.Contains(extension);
    }
}
