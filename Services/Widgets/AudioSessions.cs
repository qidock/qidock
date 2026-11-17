using System.Diagnostics;
using System.Runtime.InteropServices;

namespace QiDock.Services.Widgets;

/// <summary>
/// Per-app volume through Core Audio sessions on the default playback device. Sessions of the
/// same app (browsers open several) are grouped into one entry. Call <see cref="Dispose"/> when
/// the list is no longer shown; nothing here runs in the background.
/// </summary>
public sealed class AudioSessions : IDisposable
{
    private static readonly Guid SessionManagerId = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private readonly List<object> _comObjects = [];

    public sealed class App
    {
        internal readonly List<ISimpleAudioVolume> Volumes = [];
        public required string Name { get; init; }
        public string? ExecutablePath { get; init; }
        public bool Playing { get; internal set; }

        public float Volume
        {
            get
            {
                foreach (var volume in Volumes)
                    if (volume.GetMasterVolume(out var level) == 0) return level;
                return 0;
            }
            set
            {
                var context = AudioEndpoint.OwnContext;
                foreach (var volume in Volumes) volume.SetMasterVolume(Math.Clamp(value, 0f, 1f), ref context);
            }
        }

        public bool Muted
        {
            get => Volumes.Count > 0 && Volumes[0].GetMute(out var muted) == 0 && muted;
            set
            {
                var context = AudioEndpoint.OwnContext;
                foreach (var volume in Volumes) volume.SetMute(value, ref context);
            }
        }
    }

    public IReadOnlyList<App> Read()
    {
        var apps = new Dictionary<string, App>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), true)!)!;
            _comObjects.Add(enumerator);
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out var device) != 0 || device is null) return [];
            _comObjects.Add(device);
            var iid = SessionManagerId;
            if (device.Activate(ref iid, 0x17, 0, out var managerObject) != 0) return [];
            var manager = (IAudioSessionManager2)managerObject;
            _comObjects.Add(manager);
            if (manager.GetSessionEnumerator(out var sessions) != 0 || sessions is null) return [];
            _comObjects.Add(sessions);
            sessions.GetCount(out var count);
            for (var index = 0; index < count; index++)
            {
                if (sessions.GetSession(index, out var control) != 0 || control is null) continue;
                _comObjects.Add(control);
                if (control is not IAudioSessionControl2 control2 || control2.IsSystemSoundsSession() == 0) continue;
                control2.GetState(out var state);
                if (state == 2 /* expired */) continue;
                control2.GetProcessId(out var processId);
                if (processId == 0 || control is not ISimpleAudioVolume volume) continue;
                var (name, path) = Describe((int)processId);
                if (name is null) continue;
                if (!apps.TryGetValue(name, out var app))
                {
                    app = new App { Name = name, ExecutablePath = path };
                    apps[name] = app;
                }
                app.Volumes.Add(volume);
                if (state == 1 /* active */) app.Playing = true;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Trace.TraceWarning("Audio sessions unavailable: {0}", ex.Message);
        }
        return apps.Values.OrderByDescending(a => a.Playing).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static (string? Name, string? Path) Describe(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            string? path = null;
            try { path = process.MainModule?.FileName; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            var name = process.ProcessName;
            if (path is not null)
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description) && description.Length <= 40) name = description.Trim();
            }
            return (name, path);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return (null, null);
        }
    }

    public void Dispose()
    {
        foreach (var item in _comObjects)
            if (Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
        _comObjects.Clear();
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(nint sessionGuid, int flags, out nint control);
        [PreserveSig] int GetSimpleAudioVolume(nint sessionGuid, int flags, out nint volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out nint name);
        [PreserveSig] int SetDisplayName(nint name, nint context);
        [PreserveSig] int GetIconPath(out nint path);
        [PreserveSig] int SetIconPath(nint path, nint context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(nint grouping, nint context);
        [PreserveSig] int RegisterAudioSessionNotification(nint client);
        [PreserveSig] int UnregisterAudioSessionNotification(nint client);
        [PreserveSig] int GetSessionIdentifier(out nint id);
        [PreserveSig] int GetSessionInstanceIdentifier(out nint id);
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
