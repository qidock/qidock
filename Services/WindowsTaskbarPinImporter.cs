using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using QiDock.Models;

namespace QiDock.Services;

public static class WindowsTaskbarPinImporter
{
    public static (IReadOnlyList<DockApp> Apps, string? Error) Import()
    {
        var taskbarPinsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Internet Explorer",
            "Quick Launch",
            "User Pinned",
            "TaskBar");

        if (!Directory.Exists(taskbarPinsPath))
        {
            return ([], null);
        }

        List<string> shortcutPaths;
        try
        {
            shortcutPaths = Directory.EnumerateFiles(taskbarPinsPath, "*.lnk", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (IOException)
        {
            return ([], "Windows görev çubuğu kısayol klasörü okunamadı.");
        }
        catch (UnauthorizedAccessException)
        {
            return ([], "Windows görev çubuğu kısayol klasörüne erişim izni yok.");
        }

        if (shortcutPaths.Count == 0)
        {
            return ([], null);
        }

        Type? shellType;
        object? shell;
        try
        {
            shellType = Type.GetTypeFromProgID("WScript.Shell");
            shell = shellType is null ? null : Activator.CreateInstance(shellType);
        }
        catch (COMException)
        {
            return ([], "Windows kısayol hizmeti başlatılamadı; görev çubuğu uygulamaları içe aktarılamadı.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is COMException)
        {
            return ([], "Windows kısayol hizmeti başlatılamadı; görev çubuğu uygulamaları içe aktarılamadı.");
        }

        if (shellType is null || shell is null)
        {
            return ([], "Windows kısayol hizmeti başlatılamadı; görev çubuğu uygulamaları içe aktarılamadı.");
        }

        var apps = new List<DockApp>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedCount = 0;
        try
        {
            foreach (var shortcutPath in shortcutPaths)
            {
                try
                {
                    var shortcut = shellType.InvokeMember(
                        "CreateShortcut",
                        BindingFlags.InvokeMethod,
                        binder: null,
                        target: shell,
                        args: [shortcutPath]);
                    if (shortcut is null)
                    {
                        skippedCount++;
                        continue;
                    }

                    try
                    {
                        var shortcutType = shortcut.GetType();
                        var targetPath = ReadStringProperty(shortcutType, shortcut, "TargetPath");
                        var arguments = ReadStringProperty(shortcutType, shortcut, "Arguments");
                        var iconLocation = ReadStringProperty(shortcutType, shortcut, "IconLocation");
                        var executablePath = ResolveExecutablePath(targetPath, iconLocation);
                        if (executablePath is null || !seenPaths.Add(executablePath))
                        {
                            skippedCount++;
                            continue;
                        }

                        apps.Add(new DockApp(Path.GetFileNameWithoutExtension(shortcutPath), executablePath)
                        {
                            LaunchArguments = string.IsNullOrWhiteSpace(arguments) ? null : arguments,
                            ShortcutPath = Path.GetFullPath(shortcutPath)
                        });
                    }
                    finally
                    {
                        if (Marshal.IsComObject(shortcut))
                        {
                            _ = Marshal.FinalReleaseComObject(shortcut);
                        }
                    }
                }
                catch (COMException)
                {
                    skippedCount++;
                }
                catch (TargetInvocationException ex) when (ex.InnerException is COMException)
                {
                    skippedCount++;
                }
                catch (IOException)
                {
                    skippedCount++;
                }
                catch (UnauthorizedAccessException)
                {
                    skippedCount++;
                }
            }
        }
        catch (COMException)
        {
            return (apps, "Windows kısayol hizmeti durdu; görev çubuğu uygulamalarının bir bölümü içe aktarılamadı.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is COMException)
        {
            return (apps, "Windows kısayol hizmeti durdu; görev çubuğu uygulamalarının bir bölümü içe aktarılamadı.");
        }
        finally
        {
            if (Marshal.IsComObject(shell))
            {
                _ = Marshal.FinalReleaseComObject(shell);
            }
        }

        var error = skippedCount == 0
            ? null
            : UiText.Format("SkippedPins", skippedCount);
        return (apps, error);
    }

    public static (string? ShortcutPath, string? Error) AddPin(string appName, string executablePath, string? launchArguments = null)
    {
        var pinDirectory = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Internet Explorer",
            "Quick Launch",
            "User Pinned",
            "TaskBar"));

        try
        {
            Directory.CreateDirectory(pinDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, "Windows görev çubuğu sabitleme klasörü oluşturulamadı.");
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var safeName = new string(appName.Where(ch => !invalidChars.Contains(ch)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = Path.GetFileNameWithoutExtension(executablePath);
        }

        var shortcutPath = Path.Combine(pinDirectory, safeName + ".lnk");

        Type? shellType;
        object? shell;
        try
        {
            shellType = Type.GetTypeFromProgID("WScript.Shell");
            shell = shellType is null ? null : Activator.CreateInstance(shellType);
        }
        catch (COMException)
        {
            return (null, "Windows kısayol hizmeti başlatılamadı; sabitleme oluşturulamadı.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is COMException)
        {
            return (null, "Windows kısayol hizmeti başlatılamadı; sabitleme oluşturulamadı.");
        }

        if (shellType is null || shell is null)
        {
            return (null, "Windows kısayol hizmeti başlatılamadı; sabitleme oluşturulamadı.");
        }

        try
        {
            var shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [shortcutPath]);

            if (shortcut is null)
            {
                return (null, "Kısayol nesnesi oluşturulamadı.");
            }

            try
            {
                var shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [executablePath]);
                var workingDir = Path.GetDirectoryName(executablePath);
                if (!string.IsNullOrEmpty(workingDir))
                {
                    shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [workingDir]);
                }
                if (!string.IsNullOrWhiteSpace(launchArguments))
                {
                    shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, [launchArguments]);
                }
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                return (shortcutPath, null);
            }
            finally
            {
                if (Marshal.IsComObject(shortcut))
                {
                    _ = Marshal.FinalReleaseComObject(shortcut);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or IOException or UnauthorizedAccessException)
        {
            return (null, "Uygulama Windows görev çubuğuna sabitlenemedi.");
        }
        finally
        {
            if (Marshal.IsComObject(shell))
            {
                _ = Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    public static string? RemovePin(string shortcutPath)
    {
        var pinDirectory = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Internet Explorer",
            "Quick Launch",
            "User Pinned",
            "TaskBar"));
        var fullPath = Path.GetFullPath(shortcutPath);
        if (!string.Equals(Path.GetDirectoryName(fullPath), pinDirectory, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(fullPath), ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return "Kısayol Windows görev çubuğu sabitleme klasöründe değil.";
        }

        try
        {
            File.Delete(fullPath);
            return null;
        }
        catch (IOException)
        {
            return "Uygulama Windows görev çubuğu sabitlerinden kaldırılamadı.";
        }
        catch (UnauthorizedAccessException)
        {
            return "Görev çubuğu sabitini kaldırmak için erişim izni yok.";
        }
    }

    private static string? ResolveExecutablePath(string targetPath, string iconLocation)
    {
        var normalizedTarget = Environment.ExpandEnvironmentVariables(targetPath.Trim().Trim('"'));
        if (Path.GetExtension(normalizedTarget).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(normalizedTarget))
        {
            return Path.GetFullPath(normalizedTarget);
        }

        if (string.IsNullOrWhiteSpace(normalizedTarget))
        {
            var iconPath = iconLocation.Split(',', 2)[0].Trim().Trim('"');
            var normalizedIconPath = Environment.ExpandEnvironmentVariables(iconPath);
            if (string.Equals(Path.GetFileName(normalizedIconPath), "explorer.exe", StringComparison.OrdinalIgnoreCase)
                && File.Exists(normalizedIconPath))
            {
                return Path.GetFullPath(normalizedIconPath);
            }
        }

        return null;
    }

    private static string ReadStringProperty(Type shortcutType, object shortcut, string propertyName)
    {
        return shortcutType.InvokeMember(
            propertyName,
            BindingFlags.GetProperty,
            binder: null,
            target: shortcut,
            args: null) as string ?? string.Empty;
    }
}
