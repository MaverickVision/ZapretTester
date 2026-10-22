using System.Security.Cryptography;

namespace ZapretTester.Core;

// A content cache for one discovery pass. Open handles freeze every cached file
// until the whole pass ends; timestamps are never used as evidence of equality.
public sealed class FingerprintContext : IDisposable
{
    private readonly Dictionary<string, (FileStream Stream, string Hash)> files = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;
    public int FileHashCount => files.Count;

    public string HashFile(string path)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var fullPath = Path.GetFullPath(path);
        if (files.TryGetValue(fullPath, out var cached)) return cached.Hash;
        FlowsealSnapshot.RejectReparse(fullPath);
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            files.Add(fullPath, (stream, hash));
            return hash;
        }
        catch { stream.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var file in files.Values) file.Stream.Dispose();
        files.Clear();
    }
}
