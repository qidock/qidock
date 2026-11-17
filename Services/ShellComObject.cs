using System.Runtime.InteropServices;

namespace QiDock.Services;

// Native objects stay on the Shell worker's STA; only immutable menu data crosses threads.
internal sealed class ShellComObject(nint handle) : IDisposable
{
    public nint Handle { get; private set; } = handle;

    public static ShellComObject Create(Guid classId, Guid interfaceId)
    {
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref classId, 0, 1, ref interfaceId, out var pointer));
        return new ShellComObject(pointer);
    }

    public ShellComObject? Query(Guid interfaceId)
    {
        var result = Marshal.QueryInterface(Handle, in interfaceId, out var pointer);
        if (result == unchecked((int)0x80004002)) return null;
        Marshal.ThrowExceptionForHR(result);
        return new ShellComObject(pointer);
    }

    public T Method<T>(int slot) where T : Delegate
    {
        ObjectDisposedException.ThrowIf(Handle == 0, this);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(Handle), slot * IntPtr.Size));
    }

    public void Dispose()
    {
        if (Handle == 0) return;
        Marshal.Release(Handle);
        Handle = 0;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid classId, nint outer, uint context, ref Guid interfaceId, out nint pointer);
}
