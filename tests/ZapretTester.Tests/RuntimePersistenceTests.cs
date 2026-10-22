using System.Collections.Concurrent;
using System.Text.Json;
using ZapretTester.Core;
using ZapretTester.Helper;
using Xunit;

namespace ZapretTester.Tests;

public sealed class RuntimePersistenceTests
{
    private static Dictionary<string, ProfileTestResult> Results(bool pending) => new()
    { ["test"] = new("test", "hash", true, [], DateTimeOffset.UtcNow, null, PendingValidation: pending) };

    [Fact]
    public async Task FinalRevisionCannotBeOverwrittenByAnOlderQueuedCheckpoint()
    {
        var root = Path.Combine(Path.GetTempPath(), "zapret-writer-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "results.json");
        try
        {
            await using (var writer = new SeriesResultWriter(path))
            {
                writer.Checkpoint(1, Results(true));
                Assert.True(await writer.FinalizeAsync(3, Results(false), temporary => { File.Move(temporary, path, true); return true; }));
                writer.Checkpoint(2, Results(true));
                Assert.False(await writer.FinalizeAsync(2, Results(true), temporary => { File.Move(temporary, path, true); return true; }));
            }
            var saved = JsonSerializer.Deserialize<Dictionary<string, ProfileTestResult>>(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), JsonFiles.Options)!;
            Assert.False(saved["test"].PendingValidation);
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CancellationAtPublicationLeavesProvisionalFileUntilInvalidationIsSaved()
    {
        var root = Path.Combine(Path.GetTempPath(), "zapret-writer-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "results.json");
        try
        {
            JsonFiles.Save(path, Results(true));
            await using var writer = new SeriesResultWriter(path);
            Assert.False(await writer.FinalizeAsync(2, Results(false), _ => false));
            var provisional = JsonSerializer.Deserialize<Dictionary<string, ProfileTestResult>>(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), JsonFiles.Options)!;
            Assert.True(provisional["test"].PendingValidation);
            var invalid = Results(false);
            invalid["test"] = invalid["test"] with { IsReliable = false, Error = "Cancelled" };
            Assert.True(await writer.FinalizeAsync(3, invalid, temporary => { File.Move(temporary, path, true); return true; }));
            var saved = JsonSerializer.Deserialize<Dictionary<string, ProfileTestResult>>(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), JsonFiles.Options)!;
            Assert.False(saved["test"].IsReliable);
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FailedFinalPublicationPreservesExistingFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "zapret-writer-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "results.json");
        try
        {
            JsonFiles.Save(path, Results(true));
            var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            await using var writer = new SeriesResultWriter(path);
            await Assert.ThrowsAsync<IOException>(() => writer.FinalizeAsync(2, Results(false), _ => throw new IOException("Disk unavailable")));
            Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task StateMailboxCoalescesUpdatesAndPublishesCompletionWithoutWaitingForTheThrottle()
    {
        var revisions = new ConcurrentQueue<long>();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pump = new StateMessagePump((state, _) =>
        {
            revisions.Enqueue(state.StateRevision);
            if (!state.IsTesting) completion.TrySetResult();
            return Task.CompletedTask;
        });
        var running = new HelperState("folder", "Compatibility", null, true, new(), [], null, "", StateRevision: 1);
        for (var i = 1; i <= 100; i++) pump.Enqueue(running with { StateRevision = i });
        pump.Enqueue(running with { IsTesting = false, StateRevision = 101 });
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(101, revisions.Last());
        Assert.True(revisions.Count <= 3);
    }

    [Fact]
    public async Task LateRunningStateCannotReplaceAnUnsentNewerCompletion()
    {
        var revisions = new ConcurrentQueue<long>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pump = new StateMessagePump(async (state, token) =>
        {
            if (state.StateRevision == 99)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            revisions.Enqueue(state.StateRevision);
            if (state.StateRevision == 101 && !state.IsTesting) completion.TrySetResult();
        });
        var running = new HelperState("folder", "Compatibility", null, true, new(), [], null, "", StateRevision: 99);
        pump.Enqueue(running);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        pump.Enqueue(running with { StateRevision = 101, IsTesting = false });
        pump.Enqueue(running with { StateRevision = 100 });
        release.TrySetResult();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(new long[] { 99, 101 }, revisions.ToArray());
    }

    [Fact]
    public async Task LateCheckpointCannotReplaceNewerAcceptedCheckpoint()
    {
        var root = Path.Combine(Path.GetTempPath(), "zapret-writer-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "results.json");
        try
        {
            var newer = Results(true);
            newer["test"] = newer["test"] with { Fingerprint = "newer" };
            await using (var writer = new SeriesResultWriter(path))
            {
                writer.Checkpoint(101, newer);
                writer.Checkpoint(100, Results(true));
                Assert.False(await writer.FinalizeAsync(100, Results(false), _ => throw new InvalidOperationException("Stale final revision must not publish")));
            }
            var saved = JsonSerializer.Deserialize<Dictionary<string, ProfileTestResult>>(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), JsonFiles.Options)!;
            Assert.Equal("newer", saved["test"].Fingerprint);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
