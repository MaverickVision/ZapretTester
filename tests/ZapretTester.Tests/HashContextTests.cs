using ZapretTester.Core;
using Xunit;

namespace ZapretTester.Tests;

public sealed class HashContextTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Zapret-hashes-" + Guid.NewGuid().ToString("N"));
    private readonly ZapretProfile profile;
    public HashContextTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        Directory.CreateDirectory(Path.Combine(root, "lists"));
        foreach (var name in new[] { "winws.exe", "WinDivert.dll", "WinDivert64.sys", "cygwin1.dll" })
            File.WriteAllText(Path.Combine(root, "bin", name), name);
        File.WriteAllText(Path.Combine(root, "lists", "list-general.txt"), "example.com");
        profile = new("test", "test", Path.Combine(root, "general.bat"), Path.Combine(root, "bin", "winws.exe"),
            Path.Combine(root, "bin"), ["--hostlist=" + Path.Combine(root, "lists", "list-general.txt")], "bat-hash");
    }

    [Fact]
    public void PassReadsEachResourceOnceAndPreservesFingerprint()
    {
        var before = FlowsealSnapshot.Fingerprint(profile);
        using var context = new FingerprintContext();
        Assert.Equal(before, FlowsealSnapshot.Fingerprint(profile, context));
        Assert.Equal(before, FlowsealSnapshot.Fingerprint(profile, context));
        Assert.Equal(5, context.FileHashCount);
        Assert.Equal(context.HashFile(profile.WinwsPath), context.HashFile(Path.Combine(root, "bin", ".", "WINWS.EXE")));
        Assert.Equal(5, context.FileHashCount);
    }

    [Fact]
    public void OpenPassDeniesMutationAndNextPassSeesChangedContent()
    {
        string before;
        using (var context = new FingerprintContext())
        {
            before = FlowsealSnapshot.Fingerprint(profile, context);
            Assert.Throws<IOException>(() => File.WriteAllText(profile.WinwsPath, "replacement"));
            Assert.Throws<IOException>(() => File.Delete(profile.WinwsPath));
        }
        File.WriteAllText(profile.WinwsPath, "replacement");
        using var next = new FingerprintContext();
        Assert.NotEqual(before, FlowsealSnapshot.Fingerprint(profile, next));
    }

    [Fact]
    public void MissingUserListDefaultsStayEquivalentAndDisposedContextCannotBeReused()
    {
        var withDefault = profile with { Arguments = ["--hostlist=" + Path.Combine(root, "lists", "list-general-user.txt")] };
        var context = new FingerprintContext();
        Assert.Equal(FlowsealSnapshot.Fingerprint(withDefault), FlowsealSnapshot.Fingerprint(withDefault, context));
        Assert.False(File.Exists(Path.Combine(root, "lists", "list-general-user.txt")));
        context.Dispose();
        Assert.Throws<ObjectDisposedException>(() => context.HashFile(profile.WinwsPath));
    }

    public void Dispose() => Directory.Delete(root, true);
}
