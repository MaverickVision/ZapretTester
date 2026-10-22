using System.Diagnostics;
using ZapretTester.Core;

namespace ZapretTester.Helper;

// Bounded latest-value mailbox. Replies use the pipe writer directly; state updates
// cannot accumulate one Task per endpoint or retain obsolete full snapshots.
public sealed class StateMessagePump : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly CancellationTokenSource stopping = new();
    private readonly Func<HelperState, CancellationToken, Task> send;
    private readonly Task worker;
    private HelperState? latest, previous;
    private bool urgent;
    private long lastSent;
    private long highestEnqueuedRevision = long.MinValue;

    public StateMessagePump(Func<HelperState, CancellationToken, Task> send)
    { this.send = send; worker = RunAsync(); }

    public void Enqueue(HelperState state)
    {
        lock (gate)
        {
            // State is captured under the runtime gate but callbacks are invoked
            // outside it. A delayed publisher must not replace a newer completion.
            if (stopping.IsCancellationRequested || state.StateRevision <= highestEnqueuedRevision) return;
            highestEnqueuedRevision = state.StateRevision;
            urgent |= previous == null || previous.IsTesting != state.IsTesting
                || previous.Series?.RunId != state.Series?.RunId || previous.Series?.Phase != state.Series?.Phase
                || previous.Error != state.Error;
            previous = state; latest = state;
            if (signal.CurrentCount == 0) signal.Release();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await signal.WaitAsync(stopping.Token);
                while (true)
                {
                    HelperState? value;
                    TimeSpan wait;
                    lock (gate)
                    {
                        wait = urgent || lastSent == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(250) - Stopwatch.GetElapsedTime(lastSent);
                        value = latest;
                        if (wait <= TimeSpan.Zero) { latest = null; urgent = false; }
                    }
                    if (value == null) break;
                    if (wait > TimeSpan.Zero)
                    { await signal.WaitAsync(wait, stopping.Token); continue; }
                    await send(value, stopping.Token);
                    lastSent = Stopwatch.GetTimestamp();
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel(); await worker; signal.Dispose(); stopping.Dispose();
    }
}
