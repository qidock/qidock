using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace QiDock.Services;

/// <summary>
/// Starting with Windows. A logon task in the Task Scheduler starts the dock the moment the user signs in; the
/// registry's Run list, used before, is started by Windows only after a delay (half a minute was measured), so the
/// screen stayed without a taskbar meanwhile. The Run entry is kept only as a fallback when no task can be made.
/// </summary>
public sealed class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QiDock";
    private const string TaskName = "QiDock";

    public (bool Enabled, string? Error) Read()
    {
        if (TaskExists()) return (true, null);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return (key?.GetValue(ValueName) is string { Length: > 0 }, null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return (false, UiText.Get("StartupReadError"));
        }
    }

    public string? SetEnabled(bool enabled)
    {
        var executable = Environment.ProcessPath;
        if (enabled && (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)
            || !string.Equals(Path.GetFileName(executable), "QiDock.exe", StringComparison.OrdinalIgnoreCase)
            || executable.Length + 2 > 260)) return UiText.Get("StartupPathError");
        try
        {
            if (enabled)
            {
                if (CreateTask(executable!))
                {
                    SetRunValue(null);
                    return null;
                }
                SetRunValue(executable); // no task could be made: the slower Run list still starts the dock
                return null;
            }
            SetRunValue(null);
            return DeleteTask() ? null : UiText.Get("StartupWriteError");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return UiText.Get("StartupWriteError");
        }
    }

    /// <summary>
    /// Once, at start: a dock that still starts from the Run list moves to the quick logon task (and follows the
    /// program file if it was moved).
    /// </summary>
    public void UpgradeToTask()
    {
        try
        {
            // Before the rename the dock started as "OpenTaskbar"; that entry now points at a file that is gone.
            using (var old = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
                if (old?.GetValue("OpenTaskbar") is string legacy && !File.Exists(legacy.Trim().Trim('"')))
                    old.DeleteValue("OpenTaskbar", throwOnMissingValue: false);
            string? runValue;
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) runValue = key?.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(runValue) || TaskExists()) return;
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)
                || !string.Equals(Path.GetFileName(executable), "QiDock.exe", StringComparison.OrdinalIgnoreCase)) return;
            // Only the copy Windows was starting moves to the task; a test build run once must not take its place.
            if (!string.Equals(Path.GetFullPath(runValue.Trim().Trim('"')), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)) return;
            if (CreateTask(executable))
            {
                SetRunValue(null);
                AppLog.Write("Windows ile başlatma oturum açılış görevine taşındı (beklemeden başlar)");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException or InvalidOperationException)
        {
            AppLog.Write($"Başlangıç görevi oluşturulamadı: {ex.Message}");
        }
    }

    /// <summary>
    /// A "QiDock" entry in the Start menu that points at this copy, so a closed dock is always one search away. Made
    /// (or pointed at a moved exe) quietly; nothing happens when it is already right.
    /// </summary>
    public static void EnsureStartMenuShortcut()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)
            || !string.Equals(Path.GetFileName(executable), "QiDock.exe", StringComparison.OrdinalIgnoreCase)) return;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "QiDock.lnk");
        object? shell = null, link = null;
        try
        {
            if (Type.GetTypeFromProgID("WScript.Shell") is not { } type) return;
            shell = Activator.CreateInstance(type);
            if (shell is null) return;
            const System.Reflection.BindingFlags Get = System.Reflection.BindingFlags.GetProperty, Set = System.Reflection.BindingFlags.SetProperty;
            link = type.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [path]);
            if (link is null) return;
            var linkType = link.GetType();
            var current = linkType.InvokeMember("TargetPath", Get, null, link, null) as string;
            if (File.Exists(path) && string.Equals(current, executable, StringComparison.OrdinalIgnoreCase)) return;
            linkType.InvokeMember("TargetPath", Set, null, link, [executable]);
            linkType.InvokeMember("WorkingDirectory", Set, null, link, [Path.GetDirectoryName(executable) ?? ""]);
            linkType.InvokeMember("IconLocation", Set, null, link, [executable + ",0"]);
            linkType.InvokeMember("Description", Set, null, link, ["QiDock"]);
            linkType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, link, null);
            AppLog.Write("Başlat menüsüne QiDock kısayolu eklendi");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Reflection.TargetInvocationException or UnauthorizedAccessException or IOException)
        {
            AppLog.Write($"Başlat menüsü kısayolu yapılamadı: {ex.Message}");
        }
        finally
        {
            if (link is not null && System.Runtime.InteropServices.Marshal.IsComObject(link)) System.Runtime.InteropServices.Marshal.ReleaseComObject(link);
            if (shell is not null && System.Runtime.InteropServices.Marshal.IsComObject(shell)) System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }

    private static void SetRunValue(string? executable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (executable is null) key.DeleteValue(ValueName, throwOnMissingValue: false);
        else key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
    }

    private static bool TaskExists() => Schtasks($"/Query /TN \"{TaskName}\"") == 0;

    private static bool DeleteTask() => !TaskExists() || Schtasks($"/Delete /TN \"{TaskName}\" /F") == 0;

    /// <summary>
    /// The logon task: only this user's sign-in, no delay, normal priority (tasks run below normal unless told),
    /// also on battery, never stopped after a time limit, a single copy.
    /// </summary>
    private static bool CreateTask(string executable)
    {
        var user = WindowsIdentity.GetCurrent().Name;
        var folder = Path.GetDirectoryName(executable) ?? "";
        static string Xml(string text) => SecurityElement.Escape(text) ?? "";
        var definition = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>QiDock: oturum açılınca dock'u hemen başlatır.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{Xml(user)}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{Xml(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>false</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{Xml(executable)}</Command><WorkingDirectory>{Xml(folder)}</WorkingDirectory></Exec></Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), "qidock-task.xml");
        File.WriteAllText(file, definition, System.Text.Encoding.Unicode);
        try { return Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F") == 0; }
        finally { File.Delete(file); }
    }

    private static int Schtasks(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null) return -1;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(10000) ? process.ExitCode : -1;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return -1;
        }
    }
}
