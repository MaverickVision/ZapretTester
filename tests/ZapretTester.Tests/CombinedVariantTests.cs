using System.Text.Json;
using ZapretTester.Core;
using Xunit;

namespace ZapretTester.Tests;

public sealed class CombinedVariantTests
{
    private static IReadOnlyList<ProfileEntry> Profiles(string fixture = "Fixtures1103") => new ProfileScanner().Scan(Path.Combine(AppContext.BaseDirectory, fixture));
    private static SuiteDefinition League(string region = "RU") => new(TestSuiteId.LeagueOfLegends, region,
        [new("Авторизация Riot", new("https://auth.riotgames.com/.well-known/openid-configuration"), false, Require200: true),
        new("Конфигурация клиента", new("https://clientconfig.rpg.riotgames.com/api/v1/config/public"), false, Require200: true),
        new("Платформа " + region, new("https://" + region.ToLowerInvariant() + "-red.lol.sgp.pvp.net/"), true),
        new("CDN обновлений", new("https://lol.secure.dyn.riotcdn.net/channels/public"), true)],
        ["auth.riotgames.com", "clientconfig.rpg.riotgames.com", "lol.secure.dyn.riotcdn.net", region.ToLowerInvariant() + "-red.lol.sgp.pvp.net"]);
    private static SuiteDefinition[] Definitions() => Enum.GetValues<TestSuiteId>().Select(id => id == TestSuiteId.LeagueOfLegends ? League() : ServiceCatalog.Resolve(id)).ToArray();
    private static ResolvedSuite[] Resolve(IEnumerable<SuiteDefinition> suites) => suites.Select(s => new ResolvedSuite(s.Id, s.Region, s, null)).ToArray();

    [Theory]
    [InlineData("Fixtures1103")]
    [InlineData("Fixtures1104")]
    public void All63CombinationsUseOneWorkerPerStrategyAndStrictUniquePortBudget(string fixture)
    {
        var profiles = Profiles(fixture);
        Assert.Equal(22, profiles.Count);
        Assert.All(profiles, p => Assert.Null(p.Error));
        var definitions = Definitions();
        for (var mask = 1; mask < 64; mask++)
        {
            var selected = definitions.Where(s => (mask & (1 << (int)s.Id)) != 0).ToArray();
            var plan = BatchPlan.Create(profiles, profiles.Select(p => p.Profile!.Id).ToArray(), Resolve(selected), 2, 2000);
            Assert.Equal(22, plan.Jobs.Count);
            Assert.Empty(plan.Blocked);
            Assert.Equal(22 * selected.Length, plan.Participants.Count);
            Assert.Equal(22 * selected.Length * 8, plan.RequiredPorts);
            Assert.Equal(plan.Participants.Count, plan.Participants.Select(p => p.Key).Distinct().Count());
            Assert.All(plan.Participants, p => Assert.Equal(plan.Variant!.Id, p.VariantId));
            Assert.All(plan.Jobs, j => Assert.Equal(plan.Variant!.Id, j.Variant!.Id));
        }
        Assert.Throws<InvalidOperationException>(() => BatchPlan.Create(profiles, profiles.Select(p => p.Profile!.Id).ToArray(), Resolve(definitions), 2, 1000));
        Assert.Equal(42999, new ApplicationSettings().LastPort);
        Assert.Equal(41999, JsonSerializer.Deserialize<ApplicationSettings>("{\"LastPort\":41999}")!.LastPort);
    }

    [Theory]
    [InlineData("Fixtures")]
    [InlineData("Fixtures1103")]
    [InlineData("Fixtures1104")]
    public void AdapterPreservesEveryOriginalGroupIncludingGoogleAndAddsOnlySelectedExtraHosts(string fixture)
    {
        var definitions = Definitions();
        foreach (var entry in Profiles(fixture))
        {
            var original = entry.Profile!.Arguments;
            var adapted = VariantAdapter.Adapt(original, definitions).ToArray();
            var newEnd = Array.IndexOf(adapted, "--new");
            Assert.True(newEnd > 0);
            var originalBody = original.Where(a => !a.StartsWith("--wf-", StringComparison.Ordinal)).ToArray();
            Assert.Equal(originalBody, adapted.Skip(newEnd + 1));
            Assert.Equal(original.Count(a => a == "--new") + 1, adapted.Count(a => a == "--new"));
            var group = adapted.Take(newEnd).ToArray();
            Assert.Contains("--filter-tcp=443", group);
            Assert.DoesNotContain(group, a => a.StartsWith("--hostlist-exclude") || a.StartsWith("--ipset"));
            var hosts = group.Single(a => a.StartsWith("--hostlist-domains=")).Split('=', 2)[1].Split(',');
            Assert.Equal(VariantAdapter.ExtraHosts(definitions), hosts);
            Assert.DoesNotContain("discord.com", hosts);
            Assert.DoesNotContain("www.youtube.com", hosts);
            Assert.Contains("www.twitch.tv", hosts);
            Assert.Contains("community.fastly.steamstatic.com", hosts);
            Assert.Contains("speed.cloudflare.com", hosts);
            ProfileParser.ValidateArguments(adapted);
            Assert.Equal(original, VariantAdapter.Adapt(original, [SuiteDefinition.Discord, SuiteDefinition.YouTube]));
        }
    }

    [Fact]
    public void CatalogUsesOnlyOneHeadersOnlyGetAndExplicitAcceptancePolicies()
    {
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, Enum.GetValues<TestSuiteId>().Select(s => (int)s));
        var targets = Definitions().SelectMany(s => s.Targets).ToArray();
        Assert.Equal(24, targets.Length);
        var get = Assert.Single(targets, t => t.Method != "HEAD");
        Assert.Equal("GET", get.Method); Assert.Equal("https://www.twitch.tv/", get.Url.AbsoluteUri);
        Assert.All(targets, t => { Assert.False(t.Accepts(429)); Assert.False(t.Accepts(500)); Assert.False(t.Accepts(503)); });
        var steam = ServiceCatalog.Resolve(TestSuiteId.Steam);
        Assert.False(steam.Targets[0].Accepts(403)); Assert.False(steam.Targets[1].Accepts(404));
        foreach (var s in new[] { steam, ServiceCatalog.Resolve(TestSuiteId.Twitch) })
            foreach (var t in s.Targets.Where(t => t.ServiceRoot))
                foreach (var code in new[] { 200, 302, 401, 403, 404, 405 }) Assert.True(t.Accepts(code));
        var cloudflare = ServiceCatalog.Resolve(TestSuiteId.Cloudflare);
        foreach (var i in new[] { 0, 3 })
        { Assert.True(cloudflare.Targets[i].Accepts(200)); Assert.False(cloudflare.Targets[i].Accepts(204)); Assert.False(cloudflare.Targets[i].Accepts(302)); }
        Assert.False(cloudflare.Targets[1].Accepts(403)); Assert.True(cloudflare.Targets[2].Accepts(404));
        Assert.EndsWith("?bytes=0", cloudflare.Targets[3].Url.AbsoluteUri);
    }

    [Fact]
    public void VariantIdsAreCanonicalAndFingerprintIncludesEffectiveMethodPolicyHostsAndRegion()
    {
        var selected = Definitions();
        var variant = VariantDescriptor.Create(selected.Select(s => new SuiteSelection(s.Id, s.Region)));
        Assert.Equal(variant.Id, VariantDescriptor.Create(variant.Selections.Reverse()).Id);
        var profile = Profiles()[0].Profile!;
        var original = VariantAdapter.Fingerprint(profile, "base", variant, selected);
        var changedMethod = selected.Select(s => s.Id != TestSuiteId.Twitch ? s : s with { Targets = s.Targets.Select(t => t with { Method = "HEAD" }).ToArray() }).ToArray();
        Assert.NotEqual(original, VariantAdapter.Fingerprint(profile, "base", variant, changedMethod));
        Assert.NotEqual(original, VariantAdapter.Fingerprint(profile, "resource changed", variant, selected));
        var snapshot = new SnapshotProfile(profile, "winws.exe", "snapshot", profile.Arguments, "legacy", SourceFingerprint: "base");
        Assert.Equal(original, VariantAdapter.Apply(snapshot, variant, selected).Fingerprint);
        var changedPolicy = selected.Select(s => s.Id != TestSuiteId.Cloudflare ? s : s with { Targets = s.Targets.Select(t => t with { Require200 = false }).ToArray() }).ToArray();
        Assert.NotEqual(original, VariantAdapter.Fingerprint(profile, "base", variant, changedPolicy));
        var changedHosts = selected.Select(s => s.Id != TestSuiteId.Steam ? s : s with { Hosts = s.Hosts.Append("extra.steampowered.com").ToArray() }).ToArray();
        Assert.NotEqual(original, VariantAdapter.Fingerprint(profile, "base", variant, changedHosts));
        Assert.NotEqual(variant.Id, (variant with { Region = "EUW" }).Id);
        var noLeague = VariantDescriptor.Create([new(TestSuiteId.Discord), new(TestSuiteId.Steam)], "RU");
        Assert.Equal(noLeague.Id, VariantDescriptor.Create(noLeague.Selections, "EUW").Id);
        Assert.Throws<ArgumentException>(() => (variant with { Mode = "legacy-original" }).Validate());
        Assert.Throws<ArgumentException>(() => (variant with { AdapterVersion = 99 }).Validate());
        Assert.Throws<ArgumentException>(() => new VariantDescriptor([TestSuiteId.Discord, TestSuiteId.Discord]).Validate());
        Assert.Throws<ArgumentException>(() => new VariantDescriptor([(TestSuiteId)99]).Validate());
    }

    [Fact]
    public void MigrationKeepsAllVariantsAndLegacyHistoryAndIsIdempotent()
    {
        var legacy = new ProfileTestResult("A", "old", true, [], DateTimeOffset.UtcNow, null);
        var first = legacy with { VariantId = "one", VariantFingerprint = "f", SeriesId = "run" };
        var second = first with { VariantId = "two" };
        var saved = new Dictionary<string, ProfileTestResult> { ["old-key"] = legacy, ["duplicate-old-key"] = legacy,
            ["first"] = first, ["second"] = second };
        var migrated = ResultKeys.Migrate(saved);
        Assert.Equal(4, migrated.Count);
        Assert.Same(first, migrated[ResultKeys.For(first)]);
        Assert.Same(second, migrated[ResultKeys.For(second)]);
        Assert.Contains(ResultKeys.Legacy(TestSuiteId.Discord, "", "A"), migrated.Keys);
        Assert.Equal(migrated.Keys.Order(), ResultKeys.Migrate(migrated).Keys.Order());
    }

    [Fact]
    public void CombinedCardsNeverAggregateSeparateRunsOrVariantsAndIgnoreAnotherActiveVariant()
    {
        var selected = new[] { SuiteDefinition.Discord, ServiceCatalog.Resolve(TestSuiteId.Steam) };
        var variant = VariantDescriptor.Create(selected.Select(s => new SuiteSelection(s.Id)));
        var views = selected.Select(s =>
        {
            var endpoints = s.Targets.Select(t => new EndpointResult(t.Name, EndpointStatus.Available, 41000, "1.1.1.1", TimeSpan.FromMilliseconds(80), TimeSpan.Zero, 200, null, null)).ToArray();
            var result = new ProfileTestResult("A", "f", true, endpoints, DateTimeOffset.UtcNow, null, s.Id,
                VariantId: variant.Id, VariantFingerprint: "f", SeriesId: "one");
            return new SuiteView(s.Id, "", s.Targets, [new("A", "A", null, "f", RunState.Idle, result, false, false, VariantId: variant.Id)], VariantId: variant.Id);
        }).ToArray();
        Assert.Equal(8, DashboardScoring.Summarize(DashboardProjection.Cards(views)[0].Services).SuccessfulEndpoints);
        Assert.All(DashboardProjection.Cards(views, participants: [new(TestSuiteId.Discord, "", "A", "another variant")])[0].Services, s => Assert.Equal(10m, s.Score));
        var changed = views[1].Profiles[0];
        views[1] = views[1] with { Profiles = [changed with { LastResult = changed.LastResult! with { SeriesId = "two" } }] };
        Assert.All(DashboardProjection.Cards(views)[0].Services, s => Assert.Null(s.Score));
        views[1] = views[1] with { Profiles = [changed with { LastResult = changed.LastResult! with { VariantId = "other" } }] };
        Assert.All(DashboardProjection.Cards(views)[0].Services, s => Assert.Null(s.Score));
        views[1] = views[1] with { Profiles = [changed with { LastResult = changed.LastResult! with { PendingValidation = true } }] };
        Assert.All(DashboardProjection.Cards(views)[0].Services, s => Assert.Null(s.Score));
        views[1] = views[1] with { Profiles = [changed with { LastResult = changed.LastResult! with { IsReliable = false } }] };
        Assert.All(DashboardProjection.Cards(views)[0].Services, s => Assert.Null(s.Score));
        views[1] = views[1] with { Profiles = [changed with { LastResult = null }] };
        Assert.All(DashboardProjection.Cards(views)[0].Services, s => Assert.Null(s.Score));
    }

    [Fact]
    public void DefaultsEnableNewServicesOnlyForNewInstallAndKeepUpgradeSelection()
    {
        Assert.Equal(6, DashboardPreferences.New(true).VisibleSuites.Length);
        Assert.Equal(5, DashboardPreferences.New(false).VisibleSuites.Length);
        Assert.DoesNotContain(TestSuiteId.LeagueOfLegends, DashboardPreferences.New(false).VisibleSuites);
        Assert.Equal(new[] { TestSuiteId.Discord, TestSuiteId.YouTube }, JsonSerializer.Deserialize<DashboardPreferences>("{\"VisibleSuites\":[0,2]}")!.VisibleSuites);
        Assert.Equal(3, JsonSerializer.Deserialize<DashboardPreferences>("{}")!.VisibleSuites.Length);
    }
}
