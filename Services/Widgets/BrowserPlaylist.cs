using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace QiDock.Services.Widgets;

/// <summary>A song in the list a browser shows beside the video (a YouTube mix or playlist).</summary>
internal sealed record PlaylistSong(string VideoId, int Index, string Title, string Channel, bool Current, AutomationElement Link)
{
    /// <summary>YouTube's small picture of the video (320 × 180).</summary>
    public Uri Thumbnail => new($"https://i.ytimg.com/vi/{VideoId}/mqdefault.jpg");
}

/// <summary>What could be read: the songs, or (when there are none) a YouTube tab that is not in front.</summary>
internal sealed record PlaylistView(IReadOnlyList<PlaylistSong> Songs, AutomationElement? HiddenTab);

/// <summary>
/// Reads the list of songs beside a YouTube video in Chrome (or another Chromium browser) through Windows' screen
/// reader interface, the way a screen reader would; Windows does not tell other apps a player's queue. A browser
/// shows the page of the tab in front only, so a YouTube tab behind another tab can be brought forward first.
/// Everything here runs on a worker thread and only when the music card's back is open.
/// </summary>
internal static partial class BrowserPlaylist
{
    private static readonly Condition Windows = new PropertyCondition(AutomationElement.ClassNameProperty, "Chrome_WidgetWin_1");
    private static readonly Condition Links = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink);
    private static readonly Condition Texts = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text);
    private static readonly Condition Tabs = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
    private static readonly Condition Fields = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);

    /// <summary>Whether the player is a Chromium browser (the only kind whose list can be read).</summary>
    public static bool IsBrowser(string? appId) =>
        appId is not null && (appId.Contains("chrome", StringComparison.OrdinalIgnoreCase) || appId.Contains("msedge", StringComparison.OrdinalIgnoreCase)
            || appId.Contains("brave", StringComparison.OrdinalIgnoreCase) || appId.Contains("vivaldi", StringComparison.OrdinalIgnoreCase)
            || appId.Contains("opera", StringComparison.OrdinalIgnoreCase));

    public static Task<PlaylistView> ReadAsync(string nowTitle) => Task.Run(() => Read(nowTitle));

    /// <summary>Plays a song of the list: the same as clicking it in the browser.</summary>
    public static Task<bool> PlayAsync(PlaylistSong song) => Task.Run(() => Try(() =>
    {
        if (!song.Link.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)) return false;
        ((InvokePattern)pattern).Invoke();
        return true;
    }));

    /// <summary>Brings a browser tab to the front of its window (the window itself stays where it is).</summary>
    public static Task<bool> ShowTabAsync(AutomationElement tab) => Task.Run(() => Try(() =>
    {
        if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
        {
            ((SelectionItemPattern)select).Select();
            return true;
        }
        if (!tab.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) return false;
        ((InvokePattern)invoke).Invoke();
        return true;
    }));

    private static PlaylistView Read(string nowTitle)
    {
        AutomationElement? hidden = null;
        Try(() =>
        {
            foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, Windows))
            {
                var name = Try(() => window.Current.Name) ?? "";
                if (name.Contains("YouTube", StringComparison.OrdinalIgnoreCase))
                {
                    var songs = Try(() => Songs(window, nowTitle)) ?? [];
                    if (songs.Count > 0) return new PlaylistView(songs, null);
                }
                else
                {
                    hidden ??= Try(() => YouTubeTab(window, nowTitle));
                }
            }
            return null;
        }, out var found);
        return found ?? new PlaylistView([], hidden);
    }

    private static List<PlaylistSong> Songs(AutomationElement window, string nowTitle)
    {
        var current = CurrentVideo(window);
        var byIndex = new SortedDictionary<int, (string Id, string Title, string Channel, AutomationElement Link)>();
        foreach (AutomationElement link in window.FindAll(TreeScope.Descendants, Links))
        {
            var url = link.TryGetCurrentPattern(ValuePattern.Pattern, out var value) ? ((ValuePattern)value).Current.Value : "";
            if (!url.Contains("/watch?", StringComparison.Ordinal) || !url.Contains("list=", StringComparison.Ordinal)) continue;
            var query = Query(url);
            if (!query.TryGetValue("v", out var id) || !query.TryGetValue("index", out var indexText) || !int.TryParse(indexText, out var index)) continue;
            if (byIndex.ContainsKey(index)) continue;
            var label = link.Current.Name ?? "";
            if (label.Contains("SHIFT", StringComparison.OrdinalIgnoreCase)) continue; // the player's "next" button
            var (title, channel) = Words(link, label);
            if (title.Length > 0) byIndex[index] = (id, title, channel, link);
        }
        var anyCurrent = current is not null && byIndex.Values.Any(s => s.Id == current);
        return byIndex.Select(pair => new PlaylistSong(pair.Value.Id, pair.Key, pair.Value.Title, pair.Value.Channel,
            anyCurrent ? pair.Value.Id == current : string.Equals(pair.Value.Title, nowTitle, StringComparison.OrdinalIgnoreCase), pair.Value.Link)).ToList();
    }

    /// <summary>The title and the channel of a list entry: its first two texts that are not a number or a duration.</summary>
    private static (string Title, string Channel) Words(AutomationElement link, string label)
    {
        var words = new List<string>();
        foreach (AutomationElement text in link.FindAll(TreeScope.Descendants, Texts))
        {
            var word = (text.Current.Name ?? "").Trim();
            if (word.Length == 0 || Counter().IsMatch(word) || words.Contains(word)) continue;
            words.Add(word);
            if (words.Count == 2) break;
        }
        return words.Count switch
        {
            0 => (label.Trim(), ""),
            1 => (words[0], ""),
            _ => (words[0], words[1])
        };
    }

    /// <summary>The video in the address bar of the window, the one playing.</summary>
    private static string? CurrentVideo(AutomationElement window)
    {
        var field = window.FindFirst(TreeScope.Descendants, Fields);
        if (field is null || !field.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) return null;
        return Query(((ValuePattern)value).Current.Value).GetValueOrDefault("v");
    }

    /// <summary>A YouTube tab of the window, the one playing this song if it can be told.</summary>
    private static AutomationElement? YouTubeTab(AutomationElement window, string nowTitle)
    {
        AutomationElement? any = null;
        foreach (AutomationElement tab in window.FindAll(TreeScope.Descendants, Tabs))
        {
            var name = tab.Current.Name ?? "";
            if (!name.Contains("YouTube", StringComparison.OrdinalIgnoreCase)) continue;
            if (nowTitle.Length > 0 && name.Contains(nowTitle, StringComparison.OrdinalIgnoreCase)) return tab;
            any ??= tab;
        }
        return any;
    }

    private static Dictionary<string, string> Query(string url)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var start = url.IndexOf('?');
        if (start < 0) return result;
        var end = url.IndexOf('#', start);
        foreach (var part in url[(start + 1)..(end < 0 ? url.Length : end)].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            if (equals > 0) result.TryAdd(part[..equals], Uri.UnescapeDataString(part[(equals + 1)..]));
        }
        return result;
    }

    [GeneratedRegex(@"^[\d\s:.,•▶►|/-]+$")]
    private static partial Regex Counter();

    // The browser can close a tab or change the page at any moment; then there is simply nothing to read.
    private static T? Try<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException or TimeoutException)
        {
            return default;
        }
    }

    private static void Try<T>(Func<T?> read, out T? result) where T : class => result = Try(read);
}
