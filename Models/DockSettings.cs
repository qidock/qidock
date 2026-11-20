namespace QiDock.Models;

public enum DockPosition
{
    Left,
    Center,
    Right
}

public enum AppTheme { Dark, Light }
public enum AppLanguage { Turkish, English }
public enum AutoHideMode { Disabled, Smart, Always }

/// <summary>The screen edge the dock stands on.</summary>
public enum DockEdge { Bottom, Top, Left, Right }

/// <summary>Where the widget tiles live: their own strip at the top, or inside the dock left or right of the apps.</summary>
public enum WidgetPlacement { TopBar, DockLeft, DockRight }

/// <summary>A shortcut the user added to the dock: an app, a file, a folder or a web address.</summary>
public sealed record DockShortcut(string Name, string Target, string? Arguments = null);

public sealed record DockSettings(
    DockPosition Position = DockPosition.Center,
    IReadOnlyList<string>? PinOrder = null,
    AppTheme Theme = AppTheme.Dark,
    AppLanguage Language = AppLanguage.Turkish,
    bool AnimationsEnabled = true,
    bool GenieEffectEnabled = true,
    bool HideDesktopIcons = true,
    bool TopBarPeek = true,
    bool PreviewsEnabled = true,
    bool NotificationBadgesEnabled = true,
    AutoHideMode AutoHide = AutoHideMode.Disabled,
    double SurfaceOpacity = 0.9,
    WidgetSettings? Widgets = null,
    IReadOnlyList<DockShortcut>? Shortcuts = null,
    WidgetPlacement WidgetPlacement = WidgetPlacement.TopBar,
    DockEdge Edge = DockEdge.Bottom,
    string Style = "glass",
    bool DockHud = true,
    bool ClaudeActivity = true,
    bool Magnify = false,
    double MagnifyScale = 1.85,
    bool LowMemory = false,
    bool NotchBrowser = true,
    bool DockGlass = true,
    double DockScale = 1.0)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public WidgetSettings WidgetOptions => Widgets ?? new WidgetSettings();
}
