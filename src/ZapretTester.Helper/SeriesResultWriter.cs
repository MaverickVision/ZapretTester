using System.Text.Json;
using System.Diagnostics;
using ZapretTester.Core;

namespace ZapretTester.Helper;

// One writer owns results.json. Checkpoints are immutable copies and may be coalesced;
// final publication is serialized after them and supersedes every older revision.
public sealed class SeriesResultWriter(string path) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim serial = new(1);
    private (long Version, IReadOnlyDictionary<string, ProfileTestResult> Results)? pending;
    private Task pump = Task.CompletedTask;
    private long finalVersion;
    private long highestAcceptedVersion;
    private long previousCheckpoint;
    private long checkpointTicks;
    public double CheckpointSeconds => (double)Interlocked.Read(ref checkpointTicks) / Stopwatch.Frequency;
    public Exception? CheckpointError { get; private set; }

    public void Checkpoint(long version, IReadOnlyDictionary<string, ProfileTestResult> snapshot)
    {
        lock (gate)
        {
            if (version <= highestAcceptedVersion) return;
            highestAcceptedVersion = version;
            pending = (version, snapshot);
            if (pump.IsCompleted) pump = Task.Run(PumpAsync);
        }
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            TimeSpan delay;
            lock (gate) delay = previousCheckpoint == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(1) - Stopwatch.GetElapsedTime(previousCheckpoint);
            if (delay > TimeSpan.Zero) await Task.Delay(delay);
            await serial.WaitAsync();
            try
            {
                (long Version, IReadOnlyDictionary<string, ProfileTestResult> Results)? item;
                lock (gate) { item = pending; pending = null; }
                if (item != null && item.Value.Version > Volatile.Read(ref finalVersion))
                {
                    var started = Stopwatch.GetTimestamp();
                    string? temporary = null;
                    try
                    {
                        temporary = await WriteTemporaryAsync(item.Value.Results);
                        lock (gate)
                            if (item.Value.Version > finalVersion) File.Move(temporary, path, true);
                        CheckpointError = null;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { CheckpointError = e; }
                    finally
                    {
                        DeleteTemporary(temporary);
                        Interlocked.Add(ref checkpointTicks, Stopwatch.GetTimestamp() - started);
                    }
                }
                lock (gate) previousCheckpoint = Stopwatch.GetTimestamp();
            }
            finally { serial.Release(); }
            lock (gate)
            {
                if (pending != null) continue;
                // Mark the pump finished while holding the same lock used by Checkpoint.
                pump = Task.CompletedTask;
                return;
            }
        }
    }

    public async Task<bool> FinalizeAsync(long version, IReadOnlyDictionary<string, ProfileTestResult> snapshot,
        Func<string, bool> publishAndCommit)
    {
        lock (gate)
        {
            if (version < highestAcceptedVersion) return false;
            highestAcceptedVersion = version;
            finalVersion = version; pending = null;
        }
        await serial.WaitAsync();
        string? temporary = null;
        try
        {
            if (version < Volatile.Read(ref finalVersion)) return false;
            temporary = await WriteTemporaryAsync(snapshot);
            if (version < Volatile.Read(ref finalVersion)) return false;
            // The caller performs its last cancellation check, atomic rename and memory
            // commit together under the short runtime gate. Serialization is already done.
            return publishAndCommit(temporary);
        }
        finally
        {
            DeleteTemporary(temporary);
            serial.Release();
        }
    }

    private static void DeleteTemporary(string? temporary)
    {
        if (temporary == null) return;
        try { if (File.Exists(temporary)) File.Delete(temporary); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private async Task<string> WriteTemporaryAsync(IReadOnlyDictionary<string, ProfileTestResult> snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await JsonSerializer.SerializeAsync(stream, snapshot, JsonFiles.Options);
            await stream.FlushAsync();
            return temporary;
        }
        catch { DeleteTemporary(temporary); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        Task remaining;
        lock (gate) remaining = pump;
        await remaining;
        serial.Dispose();
    }
}
