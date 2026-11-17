using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace QiDock.Services.Widgets;

/// <summary>
/// Reports master volume and mute changes of the default playback device as they happen (volume
/// keys, the Windows mixer, other apps), with no polling. Changes made by QiDock's own slider
/// carry <see cref="AudioEndpoint.OwnContext"/> and are not reported. Follows default device changes.
/// </summary>
public sealed class VolumeWatcher : IDisposable
{
    private static readonly Guid AudioEndpointVolumeId = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private readonly Dispatcher _dispatcher;
    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _volume;
    private VolumeCallback? _callback;
    private nint _callbackPointer;
    private DeviceCallback? _deviceCallback;
    private nint _deviceCallbackPointer;
    private bool _disposed;

    public VolumeWatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Level 0..1 and mute state, raised on the UI thread.</summary>
    public event Action<float, bool>? Changed;

    public void Start()
    {
        try
        {
            _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), throwOnError: true)!)!;
            _deviceCallback = new DeviceCallback(this);
            _deviceCallbackPointer = Marshal.GetComInterfaceForObject<DeviceCallback, IMMNotificationClient>(_deviceCallback);
            _enumerator.RegisterEndpointNotificationCallback(_deviceCallbackPointer);
            Attach();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            System.Diagnostics.Trace.TraceWarning("Volume watcher unavailable: {0}", ex.Message);
        }
    }

    private void Attach()
    {
        Detach();
        if (_disposed || _enumerator is null) return;
        try
        {
            if (_enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device) != 0 || device is null) return;
            try
            {
                var iid = AudioEndpointVolumeId;
                if (device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, 0, out var instance) != 0) return;
                _volume = (IAudioEndpointVolume)instance;
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
            _callback ??= new VolumeCallback(this);
            _callbackPointer = Marshal.GetComInterfaceForObject<VolumeCallback, IAudioEndpointVolumeCallback>(_callback);
            if (_volume.RegisterControlChangeNotify(_callbackPointer) != 0) Detach();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Detach();
        }
    }

    private void Detach()
    {
        if (_volume is not null)
        {
            if (_callbackPointer != 0) _volume.UnregisterControlChangeNotify(_callbackPointer);
            Marshal.ReleaseComObject(_volume);
            _volume = null;
        }
        if (_callbackPointer != 0)
        {
            Marshal.Release(_callbackPointer);
            _callbackPointer = 0;
        }
    }

    private void OnNotify(nint data)
    {
        if (data == 0 || _disposed) return;
        // AUDIO_VOLUME_NOTIFICATION_DATA: GUID guidEventContext; BOOL bMuted; float fMasterVolume; ...
        var context = Marshal.PtrToStructure<Guid>(data);
        if (context == AudioEndpoint.OwnContext) return;
        var muted = Marshal.ReadInt32(data, 16) != 0;
        var level = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, 20));
        _dispatcher.InvokeAsync(() => Changed?.Invoke(level, muted));
    }

    private void OnDefaultDeviceChanged(int flow, int role)
    {
        if (flow != 0 || role != 1 || _disposed) return;
        _dispatcher.InvokeAsync(Attach);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
        if (_enumerator is not null)
        {
            if (_deviceCallbackPointer != 0)
            {
                _enumerator.UnregisterEndpointNotificationCallback(_deviceCallbackPointer);
                Marshal.Release(_deviceCallbackPointer);
                _deviceCallbackPointer = 0;
            }
            Marshal.ReleaseComObject(_enumerator);
            _enumerator = null;
        }
    }

    [ComVisible(true)]
    public sealed class VolumeCallback(VolumeWatcher owner) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(nint data)
        {
            owner.OnNotify(data);
            return 0;
        }
    }

    [ComVisible(true)]
    public sealed class DeviceCallback(VolumeWatcher owner) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(nint deviceId, int newState) => 0;
        public int OnDeviceAdded(nint deviceId) => 0;
        public int OnDeviceRemoved(nint deviceId) => 0;
        public int OnDefaultDeviceChanged(int flow, int role, nint defaultDeviceId)
        {
            owner.OnDefaultDeviceChanged(flow, role);
            return 0;
        }
        public int OnPropertyValueChanged(nint deviceId, PropertyKey key) => 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

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
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint notify);
        [PreserveSig] int UnregisterControlChangeNotify(nint notify);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(nint data);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged(nint deviceId, int newState);
        [PreserveSig] int OnDeviceAdded(nint deviceId);
        [PreserveSig] int OnDeviceRemoved(nint deviceId);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, nint defaultDeviceId);
        [PreserveSig] int OnPropertyValueChanged(nint deviceId, PropertyKey key);
    }
}
