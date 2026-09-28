using ZapretTester.Core;
using Xunit;

namespace ZapretTester.Tests;

public sealed class Flowseal1103Tests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Zapret1103-" + Guid.NewGuid().ToString("N"));
    public Flowseal1103Tests()
    {
        Directory.CreateDirectory(root);
        foreach (var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures1103"), "*.bat"))
            File.Copy(path, Path.Combine(root, Path.GetFileName(path)));
        Directory.CreateDirectory(Path.Combine(root, "utils"));
    }
    [Theory]
    [InlineData("mode=all\ntcp=1024-1934,1936-65535\nudp=50000-50100", "1024-1934,1936-65535", "50000-50100")]
    [InlineData("mode=tcp\ntcp=443, 1024-2048\nudp=50000", "443,1024-2048", "12")]
    [InlineData("MODE=UDP\nTCP=443\nUDP=1935", "12", "1935")]
    [InlineData("mode=disabled\ntcp=443\nudp=443", "12", "12")]
    [InlineData("all", "1024-65535", "1024-65535")]
    [InlineData("tcp", "1024-65535", "12")]
    [InlineData("udp", "12", "1024-65535")]
    [InlineData("", "12", "12")]
    [InlineData("mode=all\ntcp=0,65536\nudp=200-100", "1024-65535", "1024-65535")]
    [InlineData("mode=all\ntcp=0443\nudp=443,,500", "1024-65535", "1024-65535")]
    [InlineData("mode=all\ntcp=443&whoami\nudp=65535", "1024-65535", "65535")]
    [InlineData("mode=tcp\ntcp=443\ntcp=80,443", "80,443", "12")]
    public void NewAndLegacyGameFilterSettingsArePreservedInAllProfiles(string content, string tcp, string udp)
    {
        File.WriteAllText(Path.Combine(root, "utils", "game_filter.enabled"), content);
        Assert.Equal((tcp, udp), ProfileParser.ReadGameFilter(root));
        var entries = new ProfileScanner().Scan(root);
        Assert.Equal(22, entries.Count);
        Assert.All(entries, e => {
            Assert.Null(e.Error);
            Assert.Contains("--filter-tcp=" + tcp, e.Profile!.Arguments);
            Assert.Contains("--filter-udp=" + udp, e.Profile.Arguments);
            Assert.DoesNotContain(e.Profile.Arguments, a => a.Contains('%'));
        });
    }
    [Fact]
    public void DisabledDefaultParses22ProfilesAndRetainsGoogleGroups()
    {
        Assert.Equal(("12", "12"), ProfileParser.ReadGameFilter(root));
        var entries = new ProfileScanner().Scan(root);
        Assert.Equal(22, entries.Count);
        Assert.All(entries, e => {
            Assert.Null(e.Error);
            Assert.Equal(e.Profile!.DisplayName == "ALT5" ? 5 : 8, e.Profile.Arguments.Count(a => a == "--new"));
            if (e.Profile.DisplayName != "ALT5") Assert.Contains(e.Profile.Arguments, a => a.Contains("list-google.txt"));
        });
    }
    public void Dispose() => Directory.Delete(root, true);
}
