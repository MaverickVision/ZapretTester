using System.Text.Json;
using ZapretTester.Core;
using Xunit;

namespace ZapretTester.Tests;

public sealed class LeagueTests
{
    [Fact]
    public void ClosingSocketsDoNotBlockPoolButLiveConnectionsStillDo()
    {
        Assert.False(ZapretTester.Windows.NetworkEnvironment.IsApplicationConnection(System.Net.NetworkInformation.TcpState.FinWait2));
        Assert.False(ZapretTester.Windows.NetworkEnvironment.IsApplicationConnection(System.Net.NetworkInformation.TcpState.TimeWait));
        Assert.True(ZapretTester.Windows.NetworkEnvironment.IsApplicationConnection(System.Net.NetworkInformation.TcpState.Established));
        Assert.True(ZapretTester.Windows.NetworkEnvironment.IsApplicationConnection(System.Net.NetworkInformation.TcpState.CloseWait));
        Assert.True(ZapretTester.Windows.NetworkEnvironment.IsApplicationConnection(System.Net.NetworkInformation.TcpState.SynSent));
    }
    private static LeagueInstallation Installation(string region = "RU") => LeagueConfiguration.Parse("test", new[]
    {
        "patcher:", "  channel: https://lol.secure.dyn.riotcdn.net/channels/public", "region_data:",
        "  RU:", "    rso:", "      token: SHOULD_NEVER_APPEAR",
        "        client_config_url: https://clientconfig.rpg.riotgames.com",
        "        league_edge_url: https://ru-red.lol.sgp.pvp.net",
        "        rms_url: wss://eu.edge.rms.si.riotgames.com:443",
        "        entitlements_url: https://entitlements.auth.riotgames.com/api/token/v1",
        "  EUW:", "        client_config_url: https://clientconfig.rpg.riotgames.com",
        "        league_edge_url: https://euw1-red.lol.sgp.pvp.net"
    }, region);

    [Fact]
    public void ReadsOnlyEndpointFieldsAndUsesStrictPublicEndpointPolicy()
    {
        var install = Installation();
        Assert.Equal("RU", install.SelectedRegion);
        Assert.Equal(2, install.Regions.Count);
        var suite = install.Regions["RU"];
        Assert.Equal(4, suite.Targets.Count);
        Assert.Contains("eu.edge.rms.si.riotgames.com", suite.Hosts);
        Assert.DoesNotContain("SHOULD_NEVER_APPEAR", JsonSerializer.Serialize(install));
        Assert.True(suite.Targets[0].Accepts(200));
        Assert.False(suite.Targets[0].Accepts(403));
        Assert.False(suite.Targets[1].Accepts(404));
        Assert.True(suite.Targets[2].Accepts(404));
        Assert.True(suite.Targets[3].Accepts(404));
        Assert.False(suite.Targets[3].Accepts(503));
    }

    [Fact]
    public void All22ProfilesAdaptWithoutChangingOriginalGroupsOrDesyncOptions()
    {
        var suite = Installation().Regions["RU"];
        var profiles = new ProfileScanner().Scan(Path.Combine(AppContext.BaseDirectory, "Fixtures"));
        Assert.Equal(22, profiles.Count);
        foreach (var entry in profiles)
        {
            var original = entry.Profile!.Arguments;
            var adapted = LeagueAdapter.Adapt(original, suite).ToArray();
            var globals = original.TakeWhile(a => a.StartsWith("--wf-", StringComparison.Ordinal)).ToArray();
            Assert.Equal(globals, adapted.Take(globals.Length));
            var firstEnd = Array.IndexOf(adapted, "--new");
            Assert.Equal(original.Skip(globals.Length), adapted.Skip(firstEnd + 1));
            Assert.Contains("--filter-tcp=443", adapted.Take(firstEnd));
            Assert.Contains("--hostlist-domains=" + string.Join(',', suite.Hosts), adapted.Take(firstEnd));
            Assert.DoesNotContain(adapted.Take(firstEnd), a => a.StartsWith("--hostlist-exclude") || a.StartsWith("--ipset"));
            Assert.Equal(original.Count(a => a == "--new") + 1, adapted.Count(a => a == "--new"));
            ProfileParser.ValidateArguments(adapted);
        }
    }

    [Fact]
    public void AmbiguousAdapterCannotSilentlyChooseStrategy()
    {
        var args = new[] { "--wf-tcp=443", "--filter-tcp=443", "--hostlist=list-general.txt", "--dpi-desync=fake",
            "--new", "--filter-tcp=443", "--hostlist=list-general.txt", "--dpi-desync=multisplit" };
        Assert.Throws<UnsupportedProfileException>(() => LeagueAdapter.Adapt(args, Installation().Regions["RU"]));
    }

    [Fact]
    public void LegacyResultsMigrateToDiscordAndDoNotCollideWithLeagueRegions()
    {
        var old = JsonSerializer.Deserialize<ProfileTestResult>("""
            {"ProfileId":"GENERAL.BAT","Fingerprint":"hash","IsReliable":true,"Endpoints":[],"TestedAt":"2026-09-28T00:00:00Z","Error":null}
            """)!;
        var migrated = ResultKeys.Migrate(new Dictionary<string, ProfileTestResult> { [old.ProfileId] = old });
        Assert.Same(old, migrated[ResultKeys.Legacy(TestSuiteId.Discord, "RU", old.ProfileId)]);
        Assert.Equal(4, old.ExpectedEndpoints);
        var ru = Installation().Regions["RU"];
        var euw = Installation().Regions["EUW"];
        Assert.NotEqual(ru.Key(old.ProfileId), euw.Key(old.ProfileId));
        Assert.NotEqual(ru.Key(old.ProfileId), SuiteDefinition.Discord.Key(old.ProfileId));
        Assert.Equal("hash", SuiteDefinition.Discord.Fingerprint("hash"));
        Assert.NotEqual(ru.Fingerprint("hash"), euw.Fingerprint("hash"));
        Assert.NotEqual(ru.Fingerprint("hash"), ru.Fingerprint("changed-resource"));
    }

    [Fact]
    public void LeagueConfigurationRejectsNonRiotAndCredentialUrls()
    {
        Assert.Throws<IOException>(() => LeagueConfiguration.Parse("test", ["patcher:", "  channel: https://localhost/private"], "RU"));
        Assert.Throws<IOException>(() => LeagueConfiguration.Parse("test", ["patcher:", "  channel: https://user:pass@riotcdn.net/a"], "RU"));
    }
}
