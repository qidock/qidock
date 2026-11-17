using System.Text.Json.Serialization;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace QiDock.Models;

public sealed class DockApp(string name, string executablePath) : INotifyPropertyChanged
{
    public string Name { get; } = name;
    public string ExecutablePath { get; } = executablePath;
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool _hasNotification;
    private bool _isForeground;
    private int _windowCount;
    public string? LaunchArguments { get; init; }

    [JsonIgnore]
    public string? ShortcutPath { get; init; }

    /// <summary>Added by the user from the dock (app, file, folder or web link), kept in settings.</summary>
    [JsonIgnore]
    public bool IsCustomShortcut { get; init; }

    [JsonIgnore]
    public BitmapSource? Icon { get; set; }

    [JsonIgnore]
    public bool HasNotification { get => _hasNotification; set => SetField(ref _hasNotification, value); }

    [JsonIgnore]
    public bool IsRunning => WindowCount > 0;

    [JsonIgnore]
    public bool IsForeground { get => _isForeground; set => SetField(ref _isForeground, value); }

    [JsonIgnore]
    public int WindowCount
    {
        get => _windowCount;
        set
        {
            if (SetField(ref _windowCount, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
