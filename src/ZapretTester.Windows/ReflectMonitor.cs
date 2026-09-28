using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ZapretTester.Windows;

public sealed record ReflectedHandle(uint Pid, long OpenedAt, uint Layer, ulong Flags, short Priority, string Filter);

public sealed class ReflectMonitor : IAsyncDisposable
{
    private readonly DivertHandle handle;
    private readonly Task receiver;
    private readonly object gate = new();
    private readonly Dictionary<(uint, long), ReflectedHandle> active = [];
    private readonly HashSet<uint> owned = [(uint)Environment.ProcessId];
    private readonly TaskCompletionSource marker = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource conflict = new();
    private bool enforcing;
    private bool disposing;
    private int disposed;
    public string? Error { get; private set; }
    public CancellationToken ConflictToken => conflict.Token;
    public IReadOnlyList<ReflectedHandle> Active { get { lock (gate) return active.Values.ToArray(); } }
    public ReflectMonitor()
    {
        // REFLECT must bootstrap the driver on a clean machine. NO_INSTALL (16)
        // fails with ERROR_SERVICE_DOES_NOT_EXIST when no previous Zapret loaded it.
        // This receive-only monitor observes handles; it does not modify traffic.
        handle = WinDivert.Open("true", 4, 0, 1 | 4);
        receiver = Task.Run(Receive);
    }
    public async Task SynchronizeAsync(CancellationToken token)
    {
        // A marker OPEN event is queued behind the initial reflected handles.
        using var sentinel = WinDivert.Open("false", 0, 29999, 1 | 4 | 16);
        await marker.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
    }
    public OwnedProcess StartOwned(Func<OwnedProcess> factory)
    {
        lock (gate)
        {
            var process = factory();
            owned.Add((uint)process.Id);
            return process;
        }
    }
    public void RegisterOwned(OwnedProcess process)
    {
        lock (gate)
        {
            if (process.HasExited) throw new InvalidOperationException("Owned process has exited.");
            owned.Add((uint)process.Id);
        }
    }
    public void Enforce()
    {
        lock (gate)
        {
            enforcing = true;
            foreach (var item in active.Values) Check(item);
            if (Error != null) throw new InvalidOperationException(Error);
        }
    }
    public async Task WaitForNoForeignAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (Error != null) throw new InvalidOperationException(Error);
                if (!active.Values.Any(h => !owned.Contains(h.Pid) && (h.Flags & 1) == 0 && h.Layer is 0 or 1 or 3)) return;
            }
            await Task.Delay(25, timeout.Token);
        }
    }
    public async Task WaitCaptureAsync(int pid, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, conflict.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            lock (gate) { if (active.Values.Any(h => h.Pid == pid && h.Layer == 0 && (h.Flags & 1) == 0)) return; }
            await Task.Delay(25, timeout.Token);
        }
    }
    private void Receive()
    {
        try
        {
            var buffer = new byte[65536];
            while (WinDivert.Receive(handle, buffer, (uint)buffer.Length, out _, out var address))
            {
                lock (gate)
                {
                    var key = (address.ProcessId, address.OpenTimestamp);
                    if (address.Event == 9)
                    {
                        active.Remove(key);
                        if (address.ProcessId != Environment.ProcessId && !active.Values.Any(h => h.Pid == address.ProcessId)) owned.Remove(address.ProcessId);
                        continue;
                    }
                    if (address.Event != 8) continue;
                    var item = new ReflectedHandle(address.ProcessId, address.OpenTimestamp, address.ReflectLayer,
                        address.ReflectFlags, address.Priority, WinDivert.Format(buffer, address.ReflectLayer));
                    active[key] = item;
                    if (item.Pid == Environment.ProcessId && item.Priority == 29999 && item.Layer == 0) marker.TrySetResult();
                    if (enforcing) Check(item);
                }
            }
            if (!disposing) throw new Win32Exception(Marshal.GetLastWin32Error(), "REFLECT observation stopped.");
        }
        catch (Exception e)
        {
            lock (gate) { Error = e.Message; marker.TrySetException(e); conflict.Cancel(); }
        }
    }
    private void Check(ReflectedHandle item)
    {
        if (owned.Contains(item.Pid) || (item.Flags & 1) != 0 || item.Layer is not (0 or 1 or 3)) return;
        Error = $"Unowned modifying WinDivert handle: PID {item.Pid}, layer {item.Layer}. Isolation is not verified.";
        conflict.Cancel();
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        disposing = true;
        WinDivert.Shutdown(handle, 1);
        await receiver;
        handle.Dispose(); conflict.Dispose();
    }
}
