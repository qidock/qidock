using System.IO;
using System.Text.Json;

namespace QiDock.Browser;

internal sealed record BrowserBookmark(string Title, string Url);

/// <summary>What the notch browser keeps between uses: its open tabs, its bookmarks and the capture choice.</summary>
internal sealed record NotchBrowserState(
    IReadOnlyList<string>? Tabs = null,
    int Active = 0,
    IReadOnlyList<BrowserBookmark>? Bookmarks = null,
    bool HideFromCapture = false)
{
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QiDock");
    private static string FilePath => Path.Combine(Folder, "browser.json");

    /// <summary>Where the browser keeps its cookies and sign-ins (its own profile, separate from Chrome or Edge).</summary>
    public static string ProfileFolder => Path.Combine(Folder, "Browser");

    public static NotchBrowserState Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<NotchBrowserState>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Trace.TraceWarning("Browser state not read: {0}", ex.Message);
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, FilePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Browser state not saved: {0}", ex.Message);
        }
    }
}
