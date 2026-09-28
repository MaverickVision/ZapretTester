using ZapretTester.Core;
using Xunit;

namespace ZapretTester.Tests;

public sealed class ParserTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ZapretTester.Tests", Guid.NewGuid().ToString("N"), "путь с пробелами");
    public ParserTests()
    {
        Directory.CreateDirectory(root);
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "general*.bat"))
            File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
    }

    [Fact]
    public void All22RealProfilesParseWithPreservedGroups()
    {
        var entries = new ProfileScanner().Scan(root);
        Assert.Equal(22, entries.Count);
        Assert.All(entries, e => Assert.Null(e.Error));
        Assert.All(entries, e => Assert.Equal(e.Profile!.DisplayName == "ALT5" ? 5 : 8,
            e.Profile.Arguments.Count(a => a == "--new")));
        Assert.Equal("General", entries[0].Profile!.DisplayName);
        Assert.True(entries.ToList().FindIndex(e => e.Profile!.DisplayName == "ALT9") < entries.ToList().FindIndex(e => e.Profile!.DisplayName == "ALT10"));
    }

    [Fact]
    public void ExpansionPreservesEscapesRepeatedArgumentsAndAbsolutePaths()
    {
        var p = new ProfileParser().Parse(Path.Combine(root, "general (FAKE TLS AUTO).bat"));
        Assert.Equal(Path.Combine(root, "bin", "winws.exe"), p.WinwsPath);
        Assert.Equal(5, p.Arguments.Count(a => a == "--dpi-desync-fake-tls=!"));
        Assert.Contains("--dpi-desync-fake-tls=0x00000000", p.Arguments);
        Assert.Contains("--dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com", p.Arguments);
        Assert.Contains("--hostlist=" + Path.Combine(root, "lists", "list-general.txt"), p.Arguments);
        Assert.Contains("--filter-tcp=12", p.Arguments);
        Assert.DoesNotContain(p.Arguments, a => a.Contains('%') || a.Contains('^'));
    }

    [Theory]
    [InlineData("all", "1024-65535", "1024-65535")]
    [InlineData("tcp", "1024-65535", "12")]
    [InlineData("udp", "12", "1024-65535")]
    [InlineData("", "12", "1024-65535")]
    public void GameFilterMatchesServiceSemantics(string value, string tcp, string udp)
    {
        Directory.CreateDirectory(Path.Combine(root, "utils"));
        File.WriteAllText(Path.Combine(root, "utils", "game_filter.enabled"), value);
        var p = new ProfileParser().Parse(Path.Combine(root, "general.bat"));
        Assert.Contains("--filter-tcp=" + tcp, p.Arguments);
        Assert.Contains("--filter-udp=" + udp, p.Arguments);
    }

    [Fact]
    public void AddedProfileIsFoundWithoutCodeChangesAndBadProfileDoesNotStopScan()
    {
        File.Copy(Path.Combine(root, "general.bat"), Path.Combine(root, "general (ALT99).bat"));
        File.WriteAllText(Path.Combine(root, "general (BAD).bat"), "call :unknown\r\n");
        var entries = new ProfileScanner().Scan(root);
        Assert.Equal(24, entries.Count);
        Assert.Contains(entries, e => e.Profile?.DisplayName == "ALT99");
        Assert.Contains(entries, e => e.Error != null);
    }

    [Theory]
    [InlineData("call :custom_function")]
    [InlineData("set BIN=%UNSUPPORTED%")]
    [InlineData("set BIN=x & echo should-not-run")]
    public void UnknownShellConstructsAreRejected(string prefix)
    {
        var path = Path.Combine(root, "general.bat");
        File.WriteAllText(path, prefix + "\r\n" + File.ReadAllText(path));
        Assert.Throws<UnsupportedProfileException>(() => new ProfileParser().Parse(path));
    }

    [Fact]
    public void ServiceMatchingPreservesRepeatedOptionsAndReportsAmbiguity()
    {
        var profile = new ProfileParser().Parse(Path.Combine(root, "general.bat"));
        var command = "\"" + profile.WinwsPath + "\" " + string.Join(" ", profile.Arguments.Select(a =>
        {
            var eq = a.IndexOf('=');
            return eq < 0 ? a : a[..eq] + " \"" + a[(eq + 1)..] + "\"";
        }));
        Assert.Single(ProfileParser.MatchService(command, [profile]));
        Assert.Equal(2, ProfileParser.MatchService(command, [profile, profile with { Id = "duplicate" }]).Count);
        Assert.Empty(ProfileParser.MatchService(command + " --new", [profile]));
    }

    [Theory]
    [InlineData("--debug=@C:\\output.txt")]
    [InlineData("--wf-save=C:\\output.txt")]
    [InlineData("--hostlist-auto=C:\\output.txt")]
    [InlineData("--future-unreviewed-option=1")]
    public void UnreviewedEngineOptionsCannotReachElevatedEngine(string argument) =>
        Assert.Throws<UnsupportedProfileException>(() => ProfileParser.ValidateArguments([argument]));

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(root)!, true);
}
