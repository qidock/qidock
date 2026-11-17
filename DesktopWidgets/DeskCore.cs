using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QiDock.Models;
using QiDock.Services;

namespace QiDock.DesktopWidgets;

/// <summary>Desktop widget sizes on a 164 DIP grid with 16 DIP gaps (two smalls side by side = one medium).</summary>
public enum DeskSize { Mini, Small, Strip, Medium, Large }

/// <summary>One widget placed on the desktop.</summary>
public sealed class DeskItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "";
    public DeskSize Size { get; set; } = DeskSize.Small;
    public double X { get; set; }
    public double Y { get; set; }
    public Dictionary<string, string> Options { get; set; } = new();
}

public sealed class DeskLayout
{
    public bool Initialized { get; set; }
    public List<DeskItem> Items { get; set; } = [];
}

/// <summary>Desktop widgets live in their own file next to settings.json so moving one never rewrites the preferences.</summary>
internal static class DeskStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QiDock", "desktop-widgets.json");

    public static DeskLayout Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new DeskLayout();
            return JsonSerializer.Deserialize<DeskLayout>(File.ReadAllText(FilePath), Json) ?? new DeskLayout();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Desktop widgets could not be read: {0}", ex.Message);
            return new DeskLayout();
        }
    }

    public static void Save(DeskLayout layout)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(layout, Json));
            File.Move(temp, FilePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Desktop widgets could not be saved: {0}", ex.Message);
        }
    }
}

internal static class Desk
{
    public const double Unit = 164;
    public const double Gap = 16;
    public const double StripHeight = 74;

    public static Size Dimensions(DeskSize size) => size switch
    {
        DeskSize.Mini => new Size(Unit, StripHeight),
        DeskSize.Small => new Size(Unit, Unit),
        DeskSize.Strip => new Size(Unit * 2 + Gap, StripHeight),
        DeskSize.Medium => new Size(Unit * 2 + Gap, Unit),
        _ => new Size(Unit * 2 + Gap, Unit * 2 + Gap)
    };

    /// <summary>Turkish or English text, following the app language.</summary>
    public static string L(string turkish, string english) =>
        UiText.Current.Language == AppLanguage.Turkish ? turkish : english;

    public static string SizeName(DeskSize size) => size switch
    {
        DeskSize.Mini => L("Mini", "Mini"),
        DeskSize.Small => L("Küçük", "Small"),
        DeskSize.Strip => L("Şerit", "Strip"),
        DeskSize.Medium => L("Orta", "Medium"),
        _ => L("Büyük", "Large")
    };

    public static System.Globalization.CultureInfo Culture =>
        System.Globalization.CultureInfo.GetCultureInfo(UiText.Current.Language == AppLanguage.Turkish ? "tr-TR" : "en-US");

    public static Thickness Padding(DeskSize size) =>
        size is DeskSize.Mini or DeskSize.Strip ? new Thickness(14, 10, 14, 10) : new Thickness(16);

    /// <summary>A sound and the top-of-screen message (timer done, reminder due).</summary>
    public static void Notify(string iconKey, string text)
    {
        System.Media.SystemSounds.Asterisk.Play();
        QiDock.Widgets.Ui.ShowHud(iconKey, text);
    }

    /// <summary>Windows key shortcuts (Win+V clipboard history, Win+. emoji panel).</summary>
    public static void SendWinKey(byte key)
    {
        keybd_event(0x5B, 0, 0, 0);
        keybd_event(key, 0, 0, 0);
        keybd_event(key, 0, 2, 0);
        keybd_event(0x5B, 0, 2, 0);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, int flags, int extra);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool LockWorkStation();

    /// <summary>Shared access to the widget settings (city, calendar, to-dos, colors) kept in settings.json.</summary>
    public static Func<WidgetSettings> Settings { get; set; } = () => new WidgetSettings();
    public static Action<Func<WidgetSettings, WidgetSettings>> UpdateSettings { get; set; } = _ => { };
    public static Func<IReadOnlyList<string>> DockApps { get; set; } = () => [];
}

/// <summary>Context a widget gets from its desktop window.</summary>
public interface IDeskHost
{
    DeskItem Item { get; }
    bool IsLive { get; }
    void Save();
    void Rebuild();
    void FocusInput(IInputElement element);
}

/// <summary>
/// A desktop widget: builds its view for the chosen size and refreshes it while visible. Data that
/// several widgets share (weather, CPU, media) comes from hubs that only run while someone listens.
/// </summary>
public abstract class DeskWidget : IDisposable
{
    public IDeskHost Host { get; internal set; } = null!;
    public DeskSize Size => Host.Item.Size;
    protected bool Wide => Size is DeskSize.Strip or DeskSize.Medium or DeskSize.Large;

    /// <summary>The view for the current size; called again when the size changes.</summary>
    public abstract FrameworkElement Build();

    /// <summary>How often <see cref="Tick"/> runs while visible (null: only on data events).</summary>
    public virtual TimeSpan? Interval => null;
    public virtual void Tick() { }

    private bool _attached;

    /// <summary>Subscribe to hubs (visible) / unsubscribe (covered by windows or removed). Safe to call twice.</summary>
    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        OnAttach();
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        OnDetach();
    }

    protected virtual void OnAttach() { }
    protected virtual void OnDetach() { }

    /// <summary>The shared widget settings changed (to-dos edited in the bar and so on).</summary>
    public virtual void SettingsChanged() { }

    /// <summary>Widget-specific entries for the right-click menu.</summary>
    public virtual void AddMenuItems(ItemsControl menu) { }

    protected string? Option(string key) => Host.Item.Options.TryGetValue(key, out var value) ? value : null;

    protected int OptionInt(string key, int fallback) => int.TryParse(Option(key), out var value) ? value : fallback;

    protected void SetOption(string key, string? value)
    {
        if (value is null) Host.Item.Options.Remove(key);
        else Host.Item.Options[key] = value;
        Host.Save();
    }

    protected static MenuItem MenuEntry(string header, Action click, bool? isChecked = null)
    {
        var item = new MenuItem { Header = header.Replace("_", "__") };
        if (isChecked is { } value) item.IsChecked = value;
        item.Click += (_, _) => click();
        return item;
    }

    public virtual void Dispose() => Detach();
}

public enum DeskCategory { Time, Weather, System, Media, Productivity, Ai, Tools }

public sealed record DeskKind(string Id, DeskCategory Category, string NameTr, string NameEn, string DescriptionTr, string DescriptionEn,
    DeskSize[] Sizes, DeskSize DefaultSize, Func<DeskWidget> Create)
{
    public string Name => Desk.L(NameTr, NameEn);
    public string Description => Desk.L(DescriptionTr, DescriptionEn);
}
