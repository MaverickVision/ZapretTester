using ZapretTester.App;
using ZapretTester.Core;
using Xunit;

namespace ZapretTester.Tests;

public sealed class LiveDashboardTests
{
    private static (SuiteView Suite, SeriesProgress Series) Example()
    {
        var variant = VariantDescriptor.Create([new(TestSuiteId.Discord)]);
        var result = new ProfileTestResult("ALT.BAT", "hash", true,
            TestTarget.Defaults.Select((t, i) => new EndpointResult(t.Name, i == 3 ? EndpointStatus.Failed : EndpointStatus.Available,
                41000 + i, "203.0.113.1", TimeSpan.FromMilliseconds(150), TimeSpan.Zero, i == 3 ? null : 200, null, null)).ToArray(),
            DateTimeOffset.UtcNow, null, PendingValidation: true, VariantId: variant.Id, VariantFingerprint: "hash", SeriesId: "live");
        var row = new ProfileView(result.ProfileId, "ALT", null, "hash", RunState.Idle, result, false, false, VariantId: variant.Id);
        return (new(TestSuiteId.Discord, "", TestTarget.Defaults, [row], VariantId: variant.Id),
            new("live", [new(TestSuiteId.Discord, "", row.Id, variant.Id)], 1, 2, 1, 0, 1, 0, 2, 1, SeriesPhase.Testing, Variant: variant));
    }

    [Fact]
    public void CompletedStrategyAppearsBeforeTheSeriesFinishesWithoutFinalColors()
    {
        var (suite, series) = Example();
        var update = LiveDashboardProjection.Project([suite], series).Single();
        var service = update.Card.Discord;
        Assert.True(update.IsPreliminary);
        Assert.Equal(3, service.SuccessfulEndpoints);
        Assert.Equal("150 мс", service.Latency);
        Assert.Equal("#8994A3", service.Status.Color);
        Assert.Contains("Предварительно", service.Details);
        Assert.Equal("×", service.Endpoints[3].Status.Glyph);
        Assert.All(service.Endpoints, e => Assert.Equal("#8994A3", e.Status.Color));
        Assert.Null(service.Score);
        Assert.Null(service.ResponseScore);
        Assert.True(suite.Profiles[0].LastResult!.PendingValidation);
    }

    [Theory]
    [InlineData("old-series")]
    [InlineData("queued")]
    [InlineData("testing")]
    [InlineData("cancelled")]
    [InlineData("blocked")]
    [InlineData("stale")]
    [InlineData("fingerprint")]
    [InlineData("invalid")]
    [InlineData("partial")]
    [InlineData("wrong-region")]
    [InlineData("conflict")]
    public void UnfinishedOrUntrustedResultsCannotBecomeLiveEvidence(string scenario)
    {
        var (suite, series) = Example(); var row = suite.Profiles[0]; var result = row.LastResult!;
        row = scenario switch {
            "old-series" => row with { LastResult = result with { SeriesId = "old" } },
            "queued" => row with { RunState = RunState.Queued }, "testing" => row with { RunState = RunState.Testing },
            "cancelled" => row with { RunState = RunState.Cancelled }, "blocked" => row with { RunState = RunState.Blocked },
            "stale" => row with { IsStale = true }, "fingerprint" => row with { Fingerprint = "changed" },
            "invalid" => row with { LastResult = result with { IsReliable = false } },
            "partial" => row with { LastResult = result with { Endpoints = result.Endpoints.Take(3).ToArray() } }, _ => row };
        if (scenario == "wrong-region") series = series with { Participants = [series.Participants[0] with { Region = "RU" }] };
        var update = LiveDashboardProjection.Project([suite with { Profiles = [row] }], series, scenario != "conflict").Single();
        Assert.False(update.IsPreliminary);
        Assert.Null(update.Card.Discord.SuccessfulEndpoints);
        Assert.Equal("—", update.Card.Discord.Latency);
    }

    [Fact]
    public void FinalValidationPromotesTheResultAndOfflinePendingDoesNot()
    {
        var (suite, _) = Example();
        Assert.False(LiveDashboardProjection.Project([suite], null).Single().IsPreliminary);
        Assert.Null(LiveDashboardProjection.Project([suite], null).Single().Card.Discord.Score);
        var row = suite.Profiles[0];
        suite = suite with { Profiles = [row with { LastResult = row.LastResult! with { PendingValidation = false } }] };
        var final = LiveDashboardProjection.Project([suite], null).Single();
        Assert.False(final.IsPreliminary);
        Assert.Equal(7.5m, final.Card.Discord.Score);
        Assert.Equal("#B77916", final.Card.Discord.Status.Color);
    }
}
