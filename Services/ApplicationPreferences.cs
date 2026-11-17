using QiDock.Models;

namespace QiDock.Services;

public sealed class ApplicationPreferences
{
    private readonly DockSettingsStore _store;
    public DockSettings Current { get; private set; }
    public string? LoadError { get; private set; }
    public event EventHandler? Changed;

    public ApplicationPreferences(DockSettingsStore store)
    {
        _store = store;
        (Current, LoadError) = store.Load();
    }

    public string? Save(DockSettings settings)
    {
        var error = _store.Save(settings);
        if (error is not null) return error;
        Current = settings;
        LoadError = null;
        UiText.Current.SetLanguage(settings.Language);
        ThemeManager.Apply(settings.Theme, settings.SurfaceOpacity, settings.Style);
        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }
}
