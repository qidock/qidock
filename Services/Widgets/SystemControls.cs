using System.Runtime.InteropServices;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;

namespace QiDock.Services.Widgets;

/// <summary>Default playback device volume through Windows Core Audio.</summary>
public sealed class AudioEndpoint : IDisposable
{
    private static readonly Guid AudioEndpointVolumeId = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private IMMDeviceEnumerator? _enumerator;
    // Shared by every QiDock audio change so the volume HUD can tell them from the volume keys.
    private static Guid _eventContext = Guid.NewGuid();
    public static Guid OwnContext => _eventContext;

    public sealed record State(float Volume, bool Muted, string DeviceName);

    /// <summary>Why the last read failed (for the log), or null.</summary>
    public string? LastProblem { get; private set; }

    /// <summary>Reads the volume on a worker thread with its own connection (the window thread never waits).</summary>
    public static Task<State?> ReadInBackgroundAsync() => Task.Run(() =>
    {
        using var endpoint = new AudioEndpoint();
        return endpoint.Read();
    });

    public State? Read()
    {
        try
        {
            var (volume, name) = Open();
            if (volume is null) return null;
            LastProblem = null;
            try
            {
                Marshal.ThrowExceptionForHR(volume.GetMasterVolumeLevelScalar(out var level));
                Marshal.ThrowExceptionForHR(volume.GetMute(out var muted));
                return new State(level, muted, name);
            }
            finally
            {
                Marshal.ReleaseComObject(volume);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException)
        {
            LastProblem = $"{ex.GetType().Name} 0x{ex.HResult:X8}";
            ResetEnumerator();
            return null;
        }
    }

    public void SetVolume(float level)
    {
        Change(v =>
        {
            Marshal.ThrowExceptionForHR(v.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref _eventContext));
            if (level > 0) v.SetMute(false, ref _eventContext);
        });
    }

    public void SetMute(bool muted) => Change(v => Marshal.ThrowExceptionForHR(v.SetMute(muted, ref _eventContext)));

    private void Change(Action<IAudioEndpointVolume> action)
    {
        try
        {
            var (volume, _) = Open(withName: false);
            if (volume is null) return;
            try { action(volume); }
            finally { Marshal.ReleaseComObject(volume); }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException)
        {
            ResetEnumerator();
        }
    }

    /// <summary>
    /// Drops the cached device enumerator. Its connection to the audio service can break (sleep and
    /// resume, a restarted audio service, devices coming and going); a dead one fails every call, so
    /// the slider could no longer change the volume and showed it as zero.
    /// </summary>
    private void ResetEnumerator()
    {
        if (_enumerator is null) return;
        try { Marshal.ReleaseComObject(_enumerator); }
        catch (InvalidComObjectException) { }
        _enumerator = null;
    }

    private (IAudioEndpointVolume? Volume, string Name) Open(bool withName = true)
    {
        IMMDevice? device = null;
        for (var attempt = 0; attempt < 2 && device is null; attempt++)
        {
            try
            {
                _enumerator ??= (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(MMDeviceEnumeratorId, throwOnError: true)!)!;
                var hr = _enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out device);
                if (hr != 0)
                {
                    LastProblem = $"varsayılan ses aygıtı yok 0x{hr:X8}";
                    device = null;
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidComObjectException or InvalidCastException)
            {
                LastProblem = $"aygıt listesi açılamadı {ex.GetType().Name} 0x{ex.HResult:X8}";
                device = null;
            }
            // A failure retries once with a fresh enumerator; with no playback device at all both attempts fail.
            if (device is null) ResetEnumerator();
        }
        if (device is null) return (null, "");
        try
        {
            var name = withName ? ReadFriendlyName(device) : "";
            var iid = AudioEndpointVolumeId;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, 0, out var instance));
            return ((IAudioEndpointVolume)instance, name);
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private static string ReadFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0 /* STGM_READ */, out var store) != 0 || store is null) return "";
        try
        {
            // PKEY_Device_DeviceDesc gives the short name ("Hoparlör", "Kulaklık"); fall back to the friendly name.
            foreach (var pid in new[] { 2u, 14u })
            {
                var key = new PropertyKey { FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = pid };
                if (store.GetValue(ref key, out var variant) != 0) continue;
                try
                {
                    if (variant.Type == 31 /* VT_LPWSTR */ && variant.Pointer != 0)
                    {
                        var text = Marshal.PtrToStringUni(variant.Pointer);
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                    }
                }
                finally
                {
                    PropVariantClear(ref variant);
                }
            }
            return "";
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    public void Dispose()
    {
        if (_enumerator is not null) Marshal.ReleaseComObject(_enumerator);
        _enumerator = null;
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant variant);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid FormatId; public uint PropertyId; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint Pointer;
    }

    // Made the same way as the volume watcher's and the app volume list's: Windows hands every caller in the process
    // the same enumerator, and .NET then gives back the wrapper it already made. Made through a [ComImport] class
    // instead, that existing wrapper could not be cast and every read failed (the slider sat at zero).
    private static readonly Guid MMDeviceEnumeratorId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(nint client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(nint client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint notify);
        [PreserveSig] int UnregisterControlChangeNotify(nint notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}

/// <summary>Wi-Fi and Bluetooth radios plus the current Wi-Fi network name.</summary>
public sealed class RadioControls
{
    private Radio? _wifi;
    private Radio? _bluetooth;
    private bool _accessRequested;
    public event EventHandler? Changed;

    public bool HasWifi => _wifi is not null;
    public bool HasBluetooth => _bluetooth is not null;
    public bool WifiOn => _wifi?.State == RadioState.On;
    public bool BluetoothOn => _bluetooth?.State == RadioState.On;

    public async Task InitializeAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            _wifi = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
            _bluetooth = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (_wifi is not null) _wifi.StateChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            if (_bluetooth is not null) _bluetooth.StateChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            _wifi = null;
            _bluetooth = null;
        }
        NetworkInformation.NetworkStatusChanged += _ => Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<bool> ToggleWifiAsync() => ToggleAsync(_wifi);
    public Task<bool> ToggleBluetoothAsync() => ToggleAsync(_bluetooth);

    private async Task<bool> ToggleAsync(Radio? radio)
    {
        if (radio is null) return false;
        try
        {
            if (!_accessRequested)
            {
                _accessRequested = true;
                await Radio.RequestAccessAsync();
            }
            var result = await radio.SetStateAsync(radio.State == RadioState.On ? RadioState.Off : RadioState.On);
            return result == RadioAccessStatus.Allowed;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    public static string? CurrentNetworkName()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) return null;
            if (profile.IsWlanConnectionProfile)
            {
                var ssid = profile.WlanConnectionProfileDetails?.GetConnectedSsid();
                if (!string.IsNullOrWhiteSpace(ssid)) return ssid;
            }
            return profile.ProfileName;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool IsOnWifi()
    {
        try { return NetworkInformation.GetInternetConnectionProfile()?.IsWlanConnectionProfile == true; }
        catch (COMException) { return false; }
    }
}

/// <summary>Windows "power mode" (Settings › System › Power): best efficiency, balanced, best performance.</summary>
public static class PowerMode
{
    public enum Mode { Efficiency, Balanced, Performance }

    private static readonly Guid EfficiencyId = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid BalancedId = Guid.Empty;
    private static readonly Guid PerformanceId = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static Mode? Read()
    {
        try
        {
            if (PowerGetEffectiveOverlayScheme(out var overlay) != 0) return null;
            return overlay == EfficiencyId ? Mode.Efficiency : overlay == PerformanceId ? Mode.Performance : Mode.Balanced;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    public static bool Set(Mode mode)
    {
        try
        {
            var id = mode switch { Mode.Efficiency => EfficiencyId, Mode.Performance => PerformanceId, _ => BalancedId };
            return PowerSetActiveOverlayScheme(id) == 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlay);
}

public static class BatteryStatus
{
    public sealed record State(int Percent, bool Charging, bool OnAc, TimeSpan? Remaining, bool Saver = false);

    /// <summary>Returns null on PCs without a system battery.</summary>
    public static State? Read()
    {
        if (!GetSystemPowerStatus(out var status)) return null;
        if ((status.BatteryFlag & 128) != 0 || status.BatteryFlag == 255 || status.BatteryLifePercent > 100) return null;
        var charging = (status.BatteryFlag & 8) != 0;
        TimeSpan? remaining = status.BatteryLifeTime is > 0 and < int.MaxValue ? TimeSpan.FromSeconds(status.BatteryLifeTime) : null;
        return new State(status.BatteryLifePercent, charging, status.ACLineStatus == 1, remaining, status.SystemStatusFlag == 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
