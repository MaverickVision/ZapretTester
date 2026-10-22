using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZapretTester.Core;
using Xunit;

namespace ZapretTester.Tests;

public sealed class BatchTests
{
    private static IReadOnlyList<ProfileEntry> Profiles() => new ProfileScanner().Scan(Path.Combine(AppContext.BaseDirectory, "Fixtures"));
    private static SuiteDefinition League() => LeagueConfiguration.Parse("fixture", ["patcher:",
        "  channel: https://lol.secure.dyn.riotcdn.net/channels/public", "region_data:", "  RU:",
        "        league_edge_url: https://ru-red.lol.sgp.pvp.net", "        client_config_url: https://clientconfig.rpg.riotgames.com"], "RU").Regions["RU"];
    private static ResolvedSuite Resolved(SuiteDefinition suite) => new(suite.Id, suite.Region, suite, null);

    [Fact]
    public void FullPlanUsesOneCommonVariantPerStrategyAndFitsExistingPool()
    {
        var profiles = Profiles();
        var plan = BatchPlan.Create(profiles, profiles.Select(p => p.Profile!.Id).ToArray(),
            [Resolved(SuiteDefinition.Discord), Resolved(League()), Resolved(SuiteDefinition.YouTube)], 2, 1000);
        Assert.Equal(22, plan.Jobs.Count); Assert.Equal(66, plan.Participants.Count); Assert.Equal(528, plan.RequiredPorts);
        Assert.Empty(plan.Blocked);
        Assert.Equal(22, plan.Jobs.Count(j => j.Suites.Count == 3));
        foreach (var job in plan.Jobs)
        {
            var original = job.Profile.Arguments;
            Assert.Equal(new[] { TestSuiteId.Discord, TestSuiteId.LeagueOfLegends, TestSuiteId.YouTube }, job.Suites.Select(s => s.Id));
            Assert.Equal(new[] { TestSuiteId.Discord, TestSuiteId.LeagueOfLegends, TestSuiteId.YouTube, TestSuiteId.Discord },
                BatchPlan.InterleavedTargets(job).Take(4).Select(t => t.Suite.Id));
            Assert.NotEqual(original, VariantAdapter.Adapt(original, job.Suites));
        }
    }

    [Theory]
    [InlineData(1, 1)] [InlineData(2, 1)] [InlineData(3, 1)] [InlineData(4, 1)]
    [InlineData(5, 1)] [InlineData(6, 1)] [InlineData(7, 1)]
    public void EveryServiceCombinationHasExpectedProcessCount(int mask, int jobs)
    {
        var profiles = Profiles();
        var suites = new[] { SuiteDefinition.Discord, League(), SuiteDefinition.YouTube }.Where(s => (mask & (1 << (int)s.Id)) != 0).ToArray();
        var plan = BatchPlan.Create(profiles, [profiles[0].Profile!.Id], suites.Select(Resolved).ToArray(), 2, 1000);
        Assert.Equal(jobs, plan.Jobs.Count); Assert.Equal(8 * suites.Length, plan.RequiredPorts);
        Assert.Equal(suites.Length, plan.Participants.Count);
    }

    [Fact]
    public void MissingLeagueBlocksTheWholeVariantAndInvalidRequestsFailBeforeRun()
    {
        var profiles = Profiles(); var ids = profiles.Select(p => p.Profile!.Id).ToArray();
        var suites = new[] { Resolved(SuiteDefinition.Discord), new ResolvedSuite(TestSuiteId.LeagueOfLegends, "RU", null, "Missing LoL"), Resolved(SuiteDefinition.YouTube) };
        var plan = BatchPlan.Create(profiles, ids, suites, 2, 1000);
        Assert.Empty(plan.Jobs); Assert.Equal(66, plan.Blocked.Count); Assert.Equal(0, plan.RequiredPorts);
        Assert.Throws<InvalidOperationException>(() => BatchPlan.Create(profiles, ids, [Resolved(SuiteDefinition.Discord)], 2, 100));
        Assert.Throws<ArgumentException>(() => BatchPlan.Create(profiles, [], suites, 2, 1000));
        Assert.Throws<ArgumentException>(() => BatchPlan.Create(profiles, ids, [], 2, 1000));
        Assert.Throws<ArgumentException>(() => BatchPlan.Create(profiles, ["unknown"], suites, 2, 1000));
    }

    [Fact]
    public void YoutubePoliciesDoNotChangeExistingResultsOrLeagueFingerprint()
    {
        Assert.Equal(0, (int)TestSuiteId.Discord); Assert.Equal(1, (int)TestSuiteId.LeagueOfLegends); Assert.Equal(2, (int)TestSuiteId.YouTube);
        var youtube = SuiteDefinition.YouTube;
        Assert.True(youtube.Targets[0].Accepts(200)); Assert.True(youtube.Targets[1].Accepts(303));
        Assert.False(youtube.Targets[0].Accepts(403)); Assert.False(youtube.Targets[1].Accepts(404));
        foreach (var code in new[] { 401, 403, 404, 405 }) { Assert.True(youtube.Targets[2].Accepts(code)); Assert.True(youtube.Targets[3].Accepts(code)); }
        Assert.All(youtube.Targets, t => { Assert.False(t.Accepts(429)); Assert.False(t.Accepts(503)); });
        var league = League();
        var legacy = JsonSerializer.Serialize(new { league.Id, league.Region,
            Targets = league.Targets.Select(t => new { t.Name, t.Url, t.ServiceRoot, t.Require200 }), league.Hosts });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("base\nLoLAdapter1\n" + legacy)));
        Assert.Equal(hash, league.Fingerprint("base")); Assert.Equal("base", SuiteDefinition.Discord.Fingerprint("base"));
        Assert.NotEqual("base", youtube.Fingerprint("base"));
        Assert.NotEqual(youtube.Fingerprint("base"), (youtube with { Targets = youtube.Targets.Select(t => t with { StrictPublic = false }).ToArray() }).Fingerprint("base"));
    }

    [Fact]
    public void DashboardLatencySelectionAndParticipantsStayIndependent()
    {
        var views = new[] { SuiteDefinition.Discord, League(), SuiteDefinition.YouTube }.Select(s =>
        {
            var endpoints = s.Targets.Select((t, i) => new EndpointResult(t.Name, i == 0 ? EndpointStatus.Failed : EndpointStatus.Available,
                41000 + i, "1.1.1.1", TimeSpan.FromMilliseconds(10 + i * 10), TimeSpan.Zero, 200, null, null)).ToArray();
            var result = new ProfileTestResult("A", "hash", true, endpoints, DateTimeOffset.UtcNow, null, s.Id, s.Region);
            return new SuiteView(s.Id, s.Region, s.Targets, [new("A", "A", null, "hash", RunState.Idle, result, false, false)]);
        }).ToArray();
        var cards = DashboardProjection.Cards(views, participants: [new(TestSuiteId.YouTube, "", "A")]);
        Assert.Equal("30 мс", cards[0].Discord.Latency);
        Assert.Equal("—", cards[0].Services.Single(s => s.IsYouTube).Latency);
        Assert.Equal("!", cards[0].League.Status.Glyph);
        var preferences = new DashboardPreferences(); var folder = Path.GetTempPath();
        preferences.Select(folder, "A", false); preferences.VisibleSuites = [TestSuiteId.YouTube];
        var restored = JsonSerializer.Deserialize<DashboardPreferences>(JsonSerializer.Serialize(preferences))!;
        Assert.False(restored.IsSelected(folder, "A")); Assert.True(restored.IsSelected(folder, "NEW"));
        Assert.Equal(new[] { TestSuiteId.YouTube }, restored.VisibleSuites);
    }
}

