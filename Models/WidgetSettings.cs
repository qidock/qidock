namespace QiDock.Models;

public static class WidgetIds
{
    public const string Media = "media";
    public const string Weather = "weather";
    public const string Clock = "clock";
    public const string AiLimits = "ai";
    public const string QuickControls = "quick";
    public const string System = "system";
    public const string Folder = "folder";
    public const string Search = "search";
    public const string Colors = "colors";
    public const string AppVolume = "appvolume";
    public const string Focus = "focus";
    public const string WorldClock = "worldclock";
    public const string Todo = "todo";
    public const string Tools = "tools";
    public const string Notifications = "notifications";
    public const string ControlCenter = "controlcenter";
    public const string Battery = "battery";
    public const string Profile = "profile";
    public const string Currency = "currency";
    public const string Stopwatch = "stopwatch";
    public const string Note = "note";
    public const string AppGroup = "appgroup";
    public const string Progress = "progress";
    public const string Reminder = "reminder";
    public const string VoiceMemo = "voicememo";
    public const string Tray = "tray";

    public static readonly IReadOnlyList<string> All =
        [Media, Weather, Clock, Battery, AiLimits, System, QuickControls, Folder, Search, Colors, AppVolume, Focus, WorldClock, Todo, Tools,
         Profile, Currency, Stopwatch, Note, AppGroup, Progress, Reminder, VoiceMemo, Tray, Notifications, ControlCenter];

    /// <summary>Widgets of the second batch: they start hidden (in the add list), so the dock does not overflow.</summary>
    public static readonly IReadOnlyList<string> SecondBatch = [Currency, Stopwatch, Note, AppGroup, Progress, Reminder, VoiceMemo];

    /// <summary>Widgets added after the first release; they start hidden in the default profile.</summary>
    public static readonly IReadOnlyList<string> Added = [Folder, Search, Colors, AppVolume, Focus, WorldClock, Todo, Tools, Notifications, ControlCenter, .. SecondBatch];

    /// <summary>
    /// Tiles that repeat something always at hand (the dock bell; Quick controls opens the Control Center).
    /// They start hidden and can still be shown from the widget list.
    /// </summary>
    public static readonly IReadOnlyList<string> Duplicates = [Notifications, ControlCenter];

    /// <summary>Always the last tiles of the row, in this order: the background apps, the battery, then the bell in the corner.</summary>
    public static readonly IReadOnlyList<string> Corner = [Tray, Battery, Notifications];
}

/// <summary>A named widget layout ("Varsayılan", "İş"…): which widgets show, in what order.</summary>
public sealed record WidgetProfile(string Name, IReadOnlyList<string>? Order = null, IReadOnlyList<string>? Hidden = null);

public sealed record TodoItem(string Text, bool Done = false);

/// <summary>An app in the app group: its name and its shell id (as the Start menu's "All apps" lists it).</summary>
public sealed record AppLink(string Name, string Id);

/// <summary>
/// Widget preferences. <see cref="Order"/> and <see cref="Hidden"/> describe the active profile;
/// <see cref="Profiles"/> keeps every profile's layout (the active one is synced on switch).
/// </summary>
public sealed record WidgetSettings(
    IReadOnlyList<string>? Order = null,
    IReadOnlyList<string>? Hidden = null,
    string WeatherCity = "İstanbul",
    string? CalendarFeedUrl = null,
    bool ShowSeconds = true,
    IReadOnlyList<WidgetProfile>? Profiles = null,
    int ActiveProfile = 0,
    string? FolderPath = null,
    IReadOnlyList<string>? Colors = null,
    IReadOnlyList<TodoItem>? Todos = null,
    IReadOnlyList<string>? WorldClocks = null,
    int FocusMinutes = 25,
    bool NotificationBanners = true,
    bool NotificationSound = false,
    bool HideWindowsToasts = true,
    int LayoutRevision = 0,
    string? Note = null,
    IReadOnlyList<AppLink>? AppGroup = null,
    string CurrencyFrom = "USD",
    string CurrencyTo = "TRY",
    string ProgressKind = "month",
    string ReminderKind = "water",
    int ReminderMinutes = 0)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> EffectiveOrder
    {
        get
        {
            var known = (Order ?? []).Where(id => WidgetIds.All.Contains(id) && !WidgetIds.Corner.Contains(id)).Distinct().ToList();
            known.AddRange(WidgetIds.All.Where(id => !known.Contains(id) && !WidgetIds.Corner.Contains(id)));
            known.AddRange(WidgetIds.Corner);
            return known;
        }
    }

    public bool IsVisible(string id) => Hidden?.Contains(id) != true;

    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<string> VisibleWidgets => EffectiveOrder.Where(IsVisible);

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<WidgetProfile> EffectiveProfiles =>
        Profiles is { Count: > 0 } ? Profiles : [new WidgetProfile("Varsayılan", Order, Hidden)];

    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectiveActiveProfile => Math.Clamp(ActiveProfile, 0, EffectiveProfiles.Count - 1);

    /// <summary>The profile list with the active profile's current layout written back into it.</summary>
    private List<WidgetProfile> SyncedProfiles()
    {
        var profiles = EffectiveProfiles.ToList();
        var active = EffectiveActiveProfile;
        profiles[active] = profiles[active] with { Order = Order, Hidden = Hidden };
        return profiles;
    }

    public WidgetSettings SwitchProfile(int index)
    {
        var profiles = SyncedProfiles();
        if (index < 0 || index >= profiles.Count) return this;
        var target = profiles[index];
        return this with { Profiles = profiles, ActiveProfile = index, Order = target.Order, Hidden = target.Hidden };
    }

    public WidgetSettings AddProfile(string name)
    {
        var profiles = SyncedProfiles();
        profiles.Add(new WidgetProfile(name, Order, Hidden));
        return this with { Profiles = profiles, ActiveProfile = profiles.Count - 1 };
    }

    public WidgetSettings RenameProfile(int index, string name)
    {
        var profiles = SyncedProfiles();
        if (index < 0 || index >= profiles.Count) return this;
        profiles[index] = profiles[index] with { Name = name };
        return this with { Profiles = profiles };
    }

    public WidgetSettings RemoveProfile(int index)
    {
        var profiles = SyncedProfiles();
        if (profiles.Count <= 1 || index < 0 || index >= profiles.Count) return this;
        var active = EffectiveActiveProfile;
        profiles.RemoveAt(index);
        var next = active == index ? Math.Max(0, index - 1) : active > index ? active - 1 : active;
        var target = profiles[next];
        return this with { Profiles = profiles, ActiveProfile = next, Order = target.Order, Hidden = target.Hidden };
    }

    // Lists compare by content so the settings window can detect real edits.
    public bool Equals(WidgetSettings? other) => other is not null
        && EffectiveOrder.SequenceEqual(other.EffectiveOrder)
        && (Hidden ?? []).OrderBy(x => x).SequenceEqual((other.Hidden ?? []).OrderBy(x => x))
        && string.Equals(WeatherCity, other.WeatherCity, StringComparison.Ordinal)
        && string.Equals(CalendarFeedUrl ?? "", other.CalendarFeedUrl ?? "", StringComparison.Ordinal)
        && ShowSeconds == other.ShowSeconds
        && ActiveProfile == other.ActiveProfile
        && EffectiveProfiles.Select(p => p.Name).SequenceEqual(other.EffectiveProfiles.Select(p => p.Name))
        && string.Equals(FolderPath ?? "", other.FolderPath ?? "", StringComparison.Ordinal)
        && (Colors ?? []).SequenceEqual(other.Colors ?? [])
        && (Todos ?? []).SequenceEqual(other.Todos ?? [])
        && (WorldClocks ?? []).SequenceEqual(other.WorldClocks ?? [])
        && FocusMinutes == other.FocusMinutes
        && NotificationBanners == other.NotificationBanners
        && NotificationSound == other.NotificationSound
        && HideWindowsToasts == other.HideWindowsToasts
        && LayoutRevision == other.LayoutRevision
        && string.Equals(Note ?? "", other.Note ?? "", StringComparison.Ordinal)
        && (AppGroup ?? []).SequenceEqual(other.AppGroup ?? [])
        && CurrencyFrom == other.CurrencyFrom && CurrencyTo == other.CurrencyTo
        && ProgressKind == other.ProgressKind
        && ReminderKind == other.ReminderKind && ReminderMinutes == other.ReminderMinutes;

    public override int GetHashCode() => HashCode.Combine(string.Join(",", EffectiveOrder), WeatherCity, CalendarFeedUrl ?? "", ShowSeconds, ActiveProfile);
}
