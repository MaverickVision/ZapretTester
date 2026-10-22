using System.Collections.Concurrent;

namespace ZapretTester.Core;

// Owned by one series. Base files remain alive until every job has stopped.
public sealed class SeriesWorkspace : IDisposable
{
    private readonly string root;
    private readonly ConcurrentDictionary<string, Lazy<SnapshotProfile>> snapshots = new(StringComparer.Ordinal);
    public SeriesWorkspace(string root)
    {
        this.root = Path.GetFullPath(root);
        if (Directory.Exists(this.root)) throw new IOException("Series workspace must be new.");
        var parent = Path.GetDirectoryName(this.root) ?? throw new ArgumentException("Series workspace needs a parent.");
        Directory.CreateDirectory(parent);
        FlowsealSnapshot.RejectReparse(parent);
        Directory.CreateDirectory(this.root);
    }
    public SnapshotProfile Snapshot(ZapretProfile profile,
        Func<ZapretProfile, string, CancellationToken, SnapshotProfile> prepare, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var snapshot = snapshots.GetOrAdd(profile.Id, _ => new(() =>
        {
            token.ThrowIfCancellationRequested();
            return prepare(profile, Path.Combine(root, "snapshots", Guid.NewGuid().ToString("N")), token);
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        token.ThrowIfCancellationRequested();
        return snapshot;
    }
    public string CreateJobDirectory()
    {
        var path = Path.Combine(root, "jobs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        // The immutable absolute path was created by this instance; never delete a caller's existing tree.
        FlowsealSnapshot.RejectReparse(root);
        Directory.Delete(root, recursive: true);
    }
}
