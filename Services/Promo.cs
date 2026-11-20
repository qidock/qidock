#if DEBUG
using System.IO;
using QiDock.Services.Widgets;

namespace QiDock.Services;

/// <summary>
/// Debug builds only: sample data for the promotional pictures and videos (QIDOCK_PROMO=1), so they show lively,
/// made-up numbers instead of this computer's own (an idle processor, no Bluetooth devices, real songs and files).
/// </summary>
internal static class Promo
{
    public static bool On { get; } = Environment.GetEnvironmentVariable("QIDOCK_PROMO") == "1";

    /// <summary>A picture to treat as the wallpaper (QIDOCK_PROMO_WALLPAPER), so the glass matches the video's background.</summary>
    public static string? Wallpaper { get; } = Environment.GetEnvironmentVariable("QIDOCK_PROMO_WALLPAPER") is { Length: > 0 } file && File.Exists(file) ? file : null;

    public static IReadOnlyList<DeviceBattery> Batteries { get; } =
    [
        new("MX Master 3S", 82, "mouse"),
        new("MX Keys", 64, "keyboard"),
        new("WH-1000XM5", 41, "headphones")
    ];

    private static readonly double[] Wave = [22, 26, 31, 28, 35, 41, 38, 44, 52, 47, 39, 36, 42, 48, 55, 51, 46, 40, 37, 43];
    private static int _step;

    /// <summary>The next value of a made-up processor load that rises and falls like real work.</summary>
    public static double NextCpu() => Wave[_step++ % Wave.Length];

    public static IReadOnlyList<CpuUser> CpuUsers()
    {
        static string? Find(params string[] paths) => paths.FirstOrDefault(File.Exists);
        var programsX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var english = UiText.Current.Language == Models.AppLanguage.English;
        // Windows' own programs only: nothing this computer's owner installed.
        return
        [
            new("Microsoft Edge", Find(Path.Combine(programsX86, @"Microsoft\Edge\Application\msedge.exe")), 14.2),
            new(english ? "File Explorer" : "Windows Gezgini", Find(Path.Combine(windows, "explorer.exe")), 6.3),
            new(english ? "Desktop Window Manager" : "Masaüstü Pencere Yöneticisi", Find(Path.Combine(windows, @"System32\dwm.exe")), 3.4),
            new("QiDock", Environment.ProcessPath, 1.1)
        ];
    }

    /// <summary>Apps everyone knows and Windows' own (Microsoft's packages, Edge), never little-known ones this computer's owner added.</summary>
    public static bool IsWindowsApp(string id) =>
        id is "MSEdge" or "Microsoft.Windows.Explorer" or "Chrome" or "Telegram.TelegramDesktop" or "Microsoft.Office.WINWORD.EXE.15"
            or "Microsoft.Office.EXCEL.EXE.15" or "Microsoft.Office.POWERPNT.EXE.15"
        || id.StartsWith("5319275A.WhatsAppDesktop", StringComparison.OrdinalIgnoreCase)
        || (id.Contains("_8wekyb3d8bbwe!", StringComparison.OrdinalIgnoreCase) || id.Contains("_cw5n1h2txyewy!", StringComparison.OrdinalIgnoreCase));

    private static readonly string[] Favourites =
        ["Microsoft.Windows.Explorer", "Chrome", "5319275A.WhatsAppDesktop", "Microsoft.Office.WINWORD", "Microsoft.Office.EXCEL",
         "Telegram.TelegramDesktop", "MSEdge", "Microsoft.Windows.Photos", "Microsoft.WindowsTerminal", "Microsoft.WindowsCalculator",
         "Microsoft.WindowsNotepad", "Microsoft.Paint", "Microsoft.WindowsStore", "windows.immersivecontrolpanel", "Microsoft.WindowsAlarms",
         "Microsoft.OutlookForWindows", "Microsoft.ScreenSketch", "Microsoft.WindowsCamera", "Microsoft.ZuneMusic"];

    private static readonly Dictionary<string, string> EnglishNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.Windows.Explorer"] = "File Explorer", ["Microsoft.WindowsCalculator"] = "Calculator", ["Microsoft.WindowsNotepad"] = "Notepad",
        ["Microsoft.Windows.Photos"] = "Photos", ["windows.immersivecontrolpanel"] = "Settings", ["Microsoft.WindowsAlarms"] = "Clock",
        ["Microsoft.ScreenSketch"] = "Snipping Tool", ["Microsoft.WindowsCamera"] = "Camera", ["Microsoft.ZuneMusic"] = "Media Player",
        ["Microsoft.WindowsSoundRecorder"] = "Sound Recorder", ["Microsoft.BingWeather"] = "Weather", ["Microsoft.GetHelp"] = "Get Help",
        ["Microsoft.Getstarted"] = "Tips", ["Microsoft.WindowsFeedbackHub"] = "Feedback Hub", ["Microsoft.Todos"] = "To Do",
        ["Microsoft.MicrosoftStickyNotes"] = "Sticky Notes", ["Microsoft.People"] = "People", ["Microsoft.WindowsMaps"] = "Maps",
        ["Microsoft.XboxGamingOverlay"] = "Game Bar", ["Microsoft.Windows.DevHome"] = "Dev Home", ["MicrosoftWindows.Client.CBS"] = "Phone Link",
        ["Microsoft.YourPhone"] = "Phone Link", ["Microsoft.WindowsStore"] = "Microsoft Store", ["Microsoft.MicrosoftOfficeHub"] = "Microsoft 365",
        ["Microsoft.WindowsTerminal"] = "Terminal", ["Microsoft.Paint"] = "Paint", ["Microsoft.BingNews"] = "News"
    };

    /// <summary>In the English pictures, Windows' own apps under their English names (this Windows speaks Turkish).</summary>
    public static IReadOnlyList<InstalledApp> Named(IReadOnlyList<InstalledApp> apps) =>
        UiText.Current.Language != Models.AppLanguage.English ? apps : apps.Select(a =>
        {
            var key = EnglishNames.Keys.FirstOrDefault(k => a.Id.StartsWith(k, StringComparison.OrdinalIgnoreCase));
            return key is null ? a : a with { Name = EnglishNames[key] };
        }).ToList();

    /// <summary>A made-up "most used" order for the app screen.</summary>
    public static Dictionary<string, double> AppScores(IEnumerable<InstalledApp> apps)
    {
        var scores = new Dictionary<string, double>();
        foreach (var app in apps)
        {
            var rank = Array.FindIndex(Favourites, f => app.Id.StartsWith(f, StringComparison.OrdinalIgnoreCase));
            if (rank >= 0) scores[app.Id] = Favourites.Length - rank;
        }
        return scores;
    }

    public static IReadOnlyList<ProviderUsage> Ai()
    {
        var now = DateTime.Now;
        return
        [
            new(AiUsageService.ClaudeId, "Claude", "Max", [
                new UsageWindow(UiText.Get("AiSession"), 46, now.AddHours(2.4), TimeSpan.FromHours(5), "session"),
                new UsageWindow(UiText.Get("AiWeekly"), 23, now.AddDays(4.2), TimeSpan.FromDays(7), "weekly")], now, null),
            new(AiUsageService.CodexId, "Codex", "Plus", [
                new UsageWindow(UiText.Get("AiSession"), 31, now.AddHours(3.1), TimeSpan.FromHours(5), "session"),
                new UsageWindow(UiText.Get("AiWeekly"), 12, now.AddDays(5.5), TimeSpan.FromDays(7), "weekly")], now, null),
            new(AiUsageService.GeminiId, "Gemini", "AI Ultra", [
                new UsageWindow("Gemini", 18, now.AddHours(3.6), TimeSpan.FromHours(5), "gemini"),
                new UsageWindow(UiText.Get("AiGeminiOthers"), 9, now.AddHours(3.6), TimeSpan.FromHours(5), "others")], now, null),
            new(AiUsageService.OpenCodeId, "OpenCode", "Go", [
                new UsageWindow(UiText.Format("AiHoursWindow", 5), 27, now.AddHours(1.8), TimeSpan.FromHours(5), "session"),
                new UsageWindow(UiText.Get("AiWeekly"), 34, now.AddDays(2.4), TimeSpan.FromDays(7), "weekly"),
                new UsageWindow(UiText.Get("AiMonthly"), 58, now.AddDays(15), TimeSpan.FromDays(30), "monthly")], now, null)
        ];
    }
}
#endif
