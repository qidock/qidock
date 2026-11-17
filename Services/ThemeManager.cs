using System.Windows;
using System.Windows.Media;
using QiDock.Models;

namespace QiDock.Services;

public static class ThemeManager
{
    private static ResourceDictionary? _palette;
    private static ResourceDictionary? _style;
    private static string _styleId = ThemeStyles.Glass;
    private static readonly ResourceDictionary Materials = new();

    /// <summary>The style last applied ("glass", "black"…).</summary>
    public static string CurrentStyle => _styleId;

    /// <summary>
    /// Loads the dark or light palette, lays the chosen style over it (a style brings its own base: the light
    /// styles always sit on the light palette) and sets the materials' opacity.
    /// </summary>
    public static void Apply(AppTheme theme, double opacity = 0.9, string? style = null)
    {
        var look = ThemeStyles.Find(style ?? _styleId);
        if (look.Id != ThemeStyles.Glass) theme = look.Base;
        var source = $"/QiDock;component/Design/Palettes/{theme}.xaml";
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        if (_palette?.Source?.OriginalString != source)
        {
            var next = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };
            if (_palette is not null) dictionaries.Remove(_palette);
            dictionaries.Add(next);
            _palette = next;
        }
        if (_style is not null && look.Id != _styleId) dictionaries.Remove(_style);
        if (look.Id != _styleId || (_style is null && look.Id != ThemeStyles.Glass))
        {
            _style = ThemeStyles.Overlay(look);
            _styleId = look.Id;
        }
        // Order: palette, then the style over it, then the opacity-adjusted materials on top.
        dictionaries.Remove(Materials);
        if (_style is not null)
        {
            dictionaries.Remove(_style);
            dictionaries.Add(_style);
        }
        dictionaries.Add(Materials);
        SetSurfaceOpacity(opacity);
    }

    public static void SetSurfaceOpacity(double opacity)
    {
        if (!double.IsFinite(opacity) || opacity is < 0.6 or > 1) throw new ArgumentOutOfRangeException(nameof(opacity));
        if (_palette is null) throw new InvalidOperationException("The theme must be initialized before its materials.");
        var alpha = (byte)Math.Round(opacity * 255);
        var settings = ((SolidColorBrush)_palette["SettingsBackground"]).Color;
        settings.A = alpha;
        var settingsBrush = new SolidColorBrush(settings);
        settingsBrush.Freeze();
        Materials["SettingsGlassMaterial"] = settingsBrush;
        foreach (var key in new[] { "DockMaterial", "MenuMaterial" })
        {
            var from = _style is not null && _style.Contains(key) ? _style[key] : _palette[key];
            var material = ((LinearGradientBrush)from).Clone();
            foreach (var stop in material.GradientStops)
            {
                var color = stop.Color;
                color.A = alpha;
                stop.Color = color;
            }
            material.Freeze();
            Materials[key] = material;
        }
    }
}
