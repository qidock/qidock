using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace QiDock.Services;

/// <summary>
/// Which shelf of the app screen an app belongs on. Windows keeps no categories for desktop apps, so the guess comes
/// from the words in the app's name and program file (a small table, read once); a choice made from the app's
/// right-click menu wins and is kept in app-categories.json.
/// </summary>
internal static class AppCategories
{
    public const string All = "All", Other = "Other";

    /// <summary>The shelves in the order their chips stand.</summary>
    public static readonly string[] Order = ["Productivity", "Creativity", "Development", "Communication", "Internet", "Media", "Games", "Utilities", Other];

    public static string Name(string id) => id switch
    {
        All => L("Tümü", "All"),
        "Productivity" => L("Verimlilik", "Productivity"),
        "Creativity" => L("Yaratıcılık", "Creativity"),
        "Development" => L("Geliştirme", "Development"),
        "Communication" => L("İletişim", "Communication"),
        "Internet" => L("İnternet", "Internet"),
        "Media" => L("Eğlence", "Entertainment"),
        "Games" => L("Oyunlar", "Games"),
        "Utilities" => L("Araçlar", "Utilities"),
        _ => L("Diğer", "Other")
    };

    private static string L(string turkish, string english) => DesktopWidgets.Desk.L(turkish, english);

    // Whole words (or word runs) of a lower-case name. Checked from top to bottom: the first match wins.
    private static readonly (string Category, string[] Words)[] Rules =
    [
        ("Games", ["steam", "epic games", "xbox", "gog galaxy", "battle net", "ea app", "origin", "ubisoft connect", "riot client", "valorant", "league of legends",
            "minecraft", "roblox", "game", "games", "oyun", "solitaire", "playnite", "rockstar games", "bluestacks", "geforce now", "fortnite", "counter strike"]),
        ("Development", ["visual studio", "code", "vscode", "git", "github", "github desktop", "gitkraken", "terminal", "powershell", "python", "node js", "nodejs", "docker",
            "android studio", "intellij", "pycharm", "webstorm", "rider", "clion", "goland", "jetbrains", "postman", "insomnia", "wsl", "ubuntu", "debian", "antigravity",
            "cursor", "windsurf", "zed", "notepad++", "sublime text", "unity", "unity hub", "unreal", "arduino", "putty", "winscp", "filezilla", "dbeaver", "heidisql",
            "sql server", "mysql", "pgadmin", "ssms", "command prompt", "komut istemi", "developer", "sdk", "git bash", "claude code", "codex", "ollama", "lm studio"]),
        ("Creativity", ["adobe", "photoshop", "illustrator", "premiere", "after effects", "lightroom", "indesign", "davinci", "resolve", "blender", "gimp", "inkscape",
            "krita", "figma", "canva", "obs", "obs studio", "audacity", "fl studio", "ableton", "reaper", "capcut", "clipchamp", "paint", "paint net", "affinity",
            "bambu studio", "cura", "prusaslicer", "orcaslicer", "fusion 360", "sketchup", "autocad", "kicad", "freecad", "handbrake", "shotcut", "kdenlive",
            "blip", "procreate", "photopea", "darktable", "rawtherapee", "musescore", "cubase", "logic", "vegas", "filmora"]),
        ("Communication", ["whatsapp", "telegram", "discord", "slack", "teams", "microsoft teams", "zoom", "skype", "signal", "messenger", "outlook", "thunderbird",
            "mail", "posta", "e posta", "webex", "viber", "wechat", "element", "mattermost", "gmail"]),
        ("Internet", ["chrome", "google chrome", "edge", "microsoft edge", "firefox", "brave", "opera", "opera gx", "vivaldi", "tor browser", "browser", "tarayıcı",
            "vpn", "proton vpn", "protonvpn", "nordvpn", "expressvpn", "windscribe", "qbittorrent", "utorrent", "bittorrent", "download manager", "free download manager",
            "internet download manager", "idm", "onedrive", "dropbox", "google drive", "mega", "megasync", "localsend", "arc", "zen browser", "librewolf"]),
        ("Media", ["spotify", "vlc", "netflix", "youtube", "youtube music", "media player", "medya oynatıcı", "music", "müzik", "movies", "filmler", "tv", "plex", "kodi",
            "mpc hc", "mpc be", "potplayer", "itunes", "apple music", "apple tv", "deezer", "prime video", "disney", "twitch", "stremio", "nuvio", "foobar2000",
            "winamp", "photos", "fotoğraflar", "audible", "kindle", "podcasts", "jellyfin", "mpv"]),
        ("Productivity", ["word", "excel", "powerpoint", "onenote", "office", "access", "publisher", "visio", "project", "libreoffice", "writer", "calc", "impress",
            "notion", "obsidian", "evernote", "todo", "to do", "microsoft to do", "yapılacaklar", "calendar", "takvim", "acrobat", "pdf", "reader", "sumatrapdf",
            "foxit", "chatgpt", "copilot", "claude", "gemini", "perplexity", "notepad", "not defteri", "sticky notes", "yapışkan notlar", "wps office", "trello",
            "clickup", "anki", "zotero", "joplin", "logseq", "todoist", "ticktick", "onlyoffice", "keepass", "bitwarden", "1password", "kişiler", "contacts"]),
        ("Utilities", ["calculator", "hesap makinesi", "7 zip", "7zip", "winrar", "peazip", "everything", "powertoys", "ccleaner", "snipping tool", "ekran alıntısı",
            "uninstall", "uninstaller", "kaldır", "clock", "saat", "alarms", "camera", "kamera", "settings", "ayarlar", "control panel", "denetim masası",
            "task manager", "görev yöneticisi", "logi options", "logitech", "lenovo", "vantage", "hp", "dell", "asus", "armoury crate", "msi", "nvidia", "amd",
            "radeon", "realtek", "intel", "driver", "backup", "yedekleme", "file explorer", "dosya gezgini", "windows tools", "windows araçları", "registry",
            "regedit", "disk", "iriun", "phone link", "telefon bağlantısı", "quick assist", "hızlı yardım", "remote desktop", "uzak masaüstü", "teamviewer",
            "anydesk", "rufus", "cpu z", "hwinfo", "crystaldiskinfo", "crystaldiskmark", "speedtest", "qidock", "microsoft store", "store", "weather", "hava durumu",
            "maps", "haritalar", "recorder", "kaydedici", "ses kaydedici", "magnifier", "büyüteç", "narrator", "ekran okuyucu", "on screen keyboard", "dosyalar",
            "files", "explorer", "windows security", "windows güvenliği", "get help", "yardım al", "tips", "ipuçları", "feedback hub", "geri bildirim"])
    ];

    private static Dictionary<string, string>? _choices;

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QiDock", "app-categories.json");

    private static Dictionary<string, string> Choices()
    {
        if (_choices is not null) return _choices;
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) is { } saved)
                return _choices = new Dictionary<string, string>(saved, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return _choices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Puts an app on a shelf by hand (null: back to the guess).</summary>
    public static void Choose(string appId, string? category)
    {
        var choices = Choices();
        if (category is null) choices.Remove(appId);
        else choices[appId] = category;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(choices));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static bool IsChosen(string appId) => Choices().ContainsKey(appId);

    /// <summary>The app screen lists the apps by use instead of by name (kept while QiDock runs).</summary>
    public static bool SortByUse { get; set; }

    private static readonly Dictionary<string, string> Guesses = new(StringComparer.OrdinalIgnoreCase);

    public static string Of(InstalledApp app)
    {
        if (Choices().TryGetValue(app.Id, out var chosen)) return chosen;
        lock (Guesses)
        {
            if (Guesses.TryGetValue(app.Id, out var known)) return known;
            return Guesses[app.Id] = Guess(app);
        }
    }

    private static string Guess(InstalledApp app)
    {
        var words = Words(app.Name);
        if (app.Target is { } target) words += Words(Path.GetFileNameWithoutExtension(target));
        // Games installed through a launcher live under its folder.
        if (app.Target is { } path && (path.Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\Epic Games\", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\GOG Galaxy\Games\", StringComparison.OrdinalIgnoreCase)))
            return "Games";
        foreach (var (category, list) in Rules)
            foreach (var word in list)
                if (words.Contains(" " + word + " ", StringComparison.Ordinal)) return category;
        return Other;
    }

    /// <summary>" adobe premiere pro 2025 ": lower case, anything but letters and digits as single spaces.</summary>
    private static string Words(string text)
    {
        var builder = new StringBuilder(" ");
        // Invariant lower case keeps "Intel" as "intel"; the dot that "İ" leaves behind is dropped.
        foreach (var c in text.ToLowerInvariant())
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c) || c == '+') builder.Append(c);
            else if (builder[^1] != ' ') builder.Append(' ');
        }
        if (builder[^1] != ' ') builder.Append(' ');
        return builder.ToString();
    }
}
