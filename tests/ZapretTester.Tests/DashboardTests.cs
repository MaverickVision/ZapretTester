using System.Text.Json;
using ZapretTester.Core;
using Xunit;

namespace ZapretTester.Tests;

public sealed class DashboardTests
{
    private static ProfileView Row(string id = "GENERAL.BAT") => new(id, "General", null, "hash", RunState.Idle,
        new(id, "hash", true, TestTarget.Defaults.Select(t => new EndpointResult(t.Name, EndpointStatus.Available,
            41000, "1.1.1.1", TimeSpan.FromMilliseconds(50), TimeSpan.Zero, 200, null, null)).ToArray(), DateTimeOffset.UtcNow, null), false, false);
    private static SuiteView Suite(ProfileView row) => new(TestSuiteId.Discord, "", TestTarget.Defaults, [row]);

    [Theory]
    [InlineData("success", "✓")]
    [InlineData("partial", "!")]
    [InlineData("warning", "!")]
    [InlineData("failed", "×")]
    [InlineData("missing", "?")]
    [InlineData("stale", "◷")]
    [InlineData("invalid", "⚠")]
    [InlineData("unsupported", "⚠")]
    [InlineData("pending", "⚠")]
    [InlineData("cancelled", "⊘")]
    [InlineData("blocked", "⊟")]
    [InlineData("active", "◷")]
    public void DistinguishesAvailabilityFromRunAndTrustState(string scenario, string expected)
    {
        var row = Row();
        row = scenario switch {
            "partial" => row with { LastResult = row.LastResult! with { Endpoints = row.LastResult.Endpoints.Select((e, i) => i == 0 ? e with { Status = EndpointStatus.Failed } : e).ToArray() } },
            "warning" => row with { LastResult = row.LastResult! with { Endpoints = row.LastResult.Endpoints.Select(e => e with { Status = EndpointStatus.ServerWarning, HttpCode = 503 }).ToArray() } },
            "failed" => row with { LastResult = row.LastResult! with { Endpoints = row.LastResult.Endpoints.Select(e => e with { Status = EndpointStatus.Failed }).ToArray() } },
            "missing" => row with { LastResult = null },
            "stale" => row with { IsStale = true },
            "invalid" => row with { LastResult = row.LastResult! with { IsReliable = false } },
            "unsupported" => row with { Error = "Адаптация не поддерживается" },
            "pending" => row with { LastResult = row.LastResult! with { PendingValidation = true } },
            "cancelled" => row with { RunState = RunState.Cancelled },
            "blocked" => row with { RunState = RunState.Blocked },
            _ => row };
        var half = DashboardProjection.Half(Suite(row), row, scenario == "active");
        Assert.Equal(expected, half.Status.Glyph);
        Assert.Equal(4, half.Endpoints.Count);
        if (scenario is not ("success" or "partial" or "warning" or "failed"))
            Assert.All(half.Endpoints, e => Assert.Equal("#8994A3", e.Status.Color));
    }

    [Fact]
    public void OnlyParticipantsAndMatchingSuiteArePending()
    {
        var rows = new[] { Row(), Row("ALT.BAT") };
        var discord = Suite(rows[0]) with { Profiles = rows };
        var league = discord with { Suite = TestSuiteId.LeagueOfLegends, Region = "RU" };
        var cards = DashboardProjection.Cards([discord, league], TestSuiteId.Discord, "", [rows[0].Id]);
        Assert.Equal("◷", cards[0].Discord.Status.Glyph);
        Assert.Equal("✓", cards[0].League.Status.Glyph);
        Assert.Equal("✓", cards[1].Discord.Status.Glyph);
        league = league with { Profiles = rows.Select(r => r with { LastResult = null }).ToArray() };
        Assert.Equal("?", DashboardProjection.Cards([discord, league])[0].League.Status.Glyph);
    }

    [Fact]
    public void CrashCannotPromoteSavedPreliminaryResultsToGreen()
    {
        var row = Row();
        var persisted = JsonSerializer.Serialize(row.LastResult! with { PendingValidation = true });
        row = row with { LastResult = JsonSerializer.Deserialize<ProfileTestResult>(persisted) };
        Assert.Equal("⚠", DashboardProjection.Half(Suite(row), row, false).Status.Glyph);
        row = row with { LastResult = row.LastResult! with { PendingValidation = false } };
        Assert.Equal("✓", DashboardProjection.Half(Suite(row), row, false).Status.Glyph);
    }

    [Fact]
    public void ProjectionUsesRegionAndFingerprintWithoutBorrowingResults()
    {
        var entries = new ProfileScanner().Scan(Path.Combine(AppContext.BaseDirectory, "Fixtures"));
        var profile = entries[0].Profile!;
        var fingerprints = new Dictionary<string, string> { [profile.Id] = "hash" };
        var saved = Row(profile.Id).LastResult!;
        var results = new Dictionary<string, ProfileTestResult> { [SuiteDefinition.Discord.Key(profile.Id)] = saved };
        var current = SuiteProjection.Build(entries, fingerprints, results, TestSuiteId.Discord, "", SuiteDefinition.Discord);
        Assert.False(current.Profiles[0].IsStale);
        fingerprints[profile.Id] = "changed";
        Assert.True(SuiteProjection.Build(entries, fingerprints, results, TestSuiteId.Discord, "", SuiteDefinition.Discord).Profiles[0].IsStale);
        var missingLeague = SuiteProjection.Build(entries, fingerprints, results, TestSuiteId.LeagueOfLegends, "EUW", null, "Клиент не найден");
        Assert.All(missingLeague.Profiles, r => { Assert.Null(r.LastResult); Assert.NotNull(r.Error); });
        Assert.Equal(22, current.Profiles.Count);
    }

    [Fact]
    public void OfflineReloadPreservesResultsAndDetectsChangedResourcesWithoutWritingSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "ZapretDashboard-" + Guid.NewGuid().ToString("N"));
        var flowseal = Path.Combine(root, "Flowseal");
        Directory.CreateDirectory(flowseal);
        try
        {
            var bat = Path.Combine(flowseal, "general.bat");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "general.bat"), bat);
            var profile = new ProfileParser().Parse(bat);
            var resources = profile.Arguments.Where(a => a.Contains('='))
                .Where(a => ProfileParser.FileOptions.Contains(a[..a.IndexOf('=')]))
                .Select(a => a[(a.IndexOf('=') + 1)..]).Where(p => p != "!" && !p.StartsWith("0x"))
                .Concat(new[] { "winws.exe", "WinDivert.dll", "WinDivert64.sys", "cygwin1.dll" }.Select(f => Path.Combine(flowseal, "bin", f)))
                .Distinct().ToArray();
            foreach (var file in resources) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "fixture"); }
            var fingerprint = FlowsealSnapshot.Fingerprint(profile);
            var resultPath = Path.Combine(root, "results.json");
            JsonFiles.Save(resultPath, new Dictionary<string, ProfileTestResult> { [profile.Id] = Row(profile.Id).LastResult! with { Fingerprint = fingerprint } });
            var settings = new ApplicationSettings { FlowsealFolder = flowseal, LeagueFolder = Path.Combine(root, "MissingLeague"), LeagueRegion = "RU" };
            var before = Directory.GetFiles(flowseal, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllText);
            var loaded = SuiteProjection.Load(flowseal, settings, resultPath);
            Assert.False(loaded[0].Profiles[0].IsStale);
            Assert.Equal("✓", DashboardProjection.Cards(loaded)[0].Discord.Status.Glyph);
            Assert.Equal("⚠", DashboardProjection.Cards(loaded)[0].League.Status.Glyph);
            var variant = VariantDescriptor.Create([new(TestSuiteId.Discord)]);
            var newView = SuiteProjection.Load(flowseal, settings, resultPath, variant);
            Assert.Null(newView[0].Profiles[0].LastResult); // Independent historical results are never common-variant proof.
            var combinedFingerprint = VariantAdapter.Fingerprint(profile, FlowsealSnapshot.SourceFingerprint(profile), variant, [SuiteDefinition.Discord]);
            var combined = Row(profile.Id).LastResult! with
            { Fingerprint = combinedFingerprint, VariantId = variant.Id, VariantFingerprint = combinedFingerprint, SeriesId = "completed" };
            JsonFiles.Save(resultPath, new Dictionary<string, ProfileTestResult>
            { [profile.Id] = Row(profile.Id).LastResult! with { Fingerprint = fingerprint }, [ResultKeys.For(combined)] = combined });
            Assert.Equal("✓", DashboardProjection.Cards(SuiteProjection.Load(flowseal, settings, resultPath, variant))[0].Discord.Status.Glyph);
            Assert.Equal("?", DashboardProjection.Cards(SuiteProjection.Load(flowseal, settings, resultPath,
                VariantDescriptor.Create([new(TestSuiteId.Discord), new(TestSuiteId.Steam)])))[0].Discord.Status.Glyph);
            Assert.Equal(before.Count, Directory.GetFiles(flowseal, "*", SearchOption.AllDirectories).Length);
            foreach (var pair in before) Assert.Equal(pair.Value, File.ReadAllText(pair.Key));
            File.AppendAllText(Path.Combine(flowseal, "lists", "list-general.txt"), "\nchanged.example");
            Assert.True(SuiteProjection.Load(flowseal, settings, resultPath)[0].Profiles[0].IsStale);
            Assert.True(SuiteProjection.Load(flowseal, settings, resultPath, variant)[0].Profiles[0].IsStale);
            File.WriteAllText(Path.Combine(flowseal, "lists", "list-general.txt"), "fixture");
            File.AppendAllText(bat, "\r\nrem changed profile\r\n");
            Assert.True(SuiteProjection.Load(flowseal, settings, resultPath)[0].Profiles[0].IsStale);
        }
        finally { Directory.Delete(root, true); }
    }
}
