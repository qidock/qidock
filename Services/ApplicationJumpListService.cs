using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using QiDock.Models;

namespace QiDock.Services;

public sealed class ApplicationJumpListService : IDisposable
{
    private readonly object _gate = new();
    private Task<Dispatcher>? _worker;
    private NativeJumpListSession? _session;
    private long _sessionId;
    private volatile bool _disposed;

    public async Task<ApplicationJumpList> ReadAsync(nint window, string? shortcut, CancellationToken cancellation)
    {
        var dispatcher = await GetWorker().WaitAsync(cancellation);
        return await dispatcher.InvokeAsync(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            _session?.Dispose();
            _session = new NativeJumpListSession();
            var id = ++_sessionId;
            try
            {
                var result = _session.Read(id, window, shortcut, cancellation);
                cancellation.ThrowIfCancellationRequested();
                return result;
            }
            catch (OperationCanceledException)
            {
                _session.Dispose();
                _session = null;
                throw;
            }
            catch (COMException ex)
            {
                _session.Dispose();
                _session = null;
                Trace.TraceWarning("Jump List read failed: 0x{0:X8}", ex.HResult);
                var unsupported = ex.HResult is unchecked((int)0x80004002) or unchecked((int)0x80040154);
                return new ApplicationJumpList(id, unsupported ? JumpListStatus.Unsupported : JumpListStatus.Unavailable, []);
            }
            catch (InvalidOperationException)
            {
                _session.Dispose();
                _session = null;
                return new ApplicationJumpList(id, JumpListStatus.Unavailable, []);
            }
        }, DispatcherPriority.Normal, cancellation).Task.WaitAsync(cancellation);
    }

    public async Task<bool> InvokeAsync(long sessionId, int commandId, nint owner)
    {
        try
        {
            var dispatcher = await GetWorker();
            return await dispatcher.InvokeAsync(() =>
            {
                if (_disposed || _session is null || _sessionId != sessionId) return false;
                try { _session.Invoke(commandId, owner); return true; }
                catch (COMException ex) { Trace.TraceWarning("Jump List invocation failed: 0x{0:X8}", ex.HResult); return false; }
                catch (InvalidOperationException) { return false; }
                catch (Win32Exception ex) { Trace.TraceWarning("Jump List invocation failed: {0}", ex.NativeErrorCode); return false; }
            }).Task;
        }
        catch (OperationCanceledException) when (_disposed) { return false; }
        catch (ObjectDisposedException) when (_disposed) { return false; }
    }

    public async Task ReleaseAsync(long sessionId)
    {
        if (_disposed) return;
        try
        {
            var dispatcher = await GetWorker();
            await dispatcher.InvokeAsync(() =>
            {
                if (_sessionId != sessionId) return;
                _session?.Dispose();
                _session = null;
            }).Task;
        }
        catch (OperationCanceledException) when (_disposed) { /* The worker's finally releases the session. */ }
        catch (ObjectDisposedException) when (_disposed) { /* Shutdown owns cleanup. */ }
    }

    private Task<Dispatcher> GetWorker()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is not null) return _worker;
            var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
            _worker = ready.Task;
            var thread = new Thread(() =>
            {
                var result = CoInitializeEx(0, 2);
                if (result < 0) { ready.SetException(new COMException("Shell STA initialization failed.", result)); return; }
                try
                {
                    ready.SetResult(Dispatcher.CurrentDispatcher);
                    if (_disposed) Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    Dispatcher.Run();
                }
                finally
                {
                    _session?.Dispose();
                    _session = null;
                    CoUninitialize();
                }
            }) { IsBackground = true, Name = "QiDock Jump Lists" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return _worker;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_worker?.IsCompletedSuccessfully == true)
                _worker.Result.BeginInvokeShutdown(DispatcherPriority.Background);
        }
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}
