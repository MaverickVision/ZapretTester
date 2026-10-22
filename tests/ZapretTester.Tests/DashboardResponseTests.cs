using System.Text.Json;
using ZapretTester.Core;
using Xunit;

namespace ZapretTester.Tests;

public sealed class DashboardResponseTests
{
    private static ProfileView Row(int successes, params double[] durations)
    {
        var endpoints = TestTarget.Defaults.Select((t, i) => new EndpointResult(t.Name,
            i < successes ? EndpointStatus.Available : EndpointStatus.ServerWarning,
            41000 + i, "1.1.1.1", TimeSpan.FromMilliseconds(durations.Length == 1 ? durations[0] : durations[i]),
            TimeSpan.FromSeconds(20), i < successes ? 200 : 503, null, null)).ToArray();
        return new("GENERAL.BAT", "General", null, "hash", RunState.Idle,
            new("GENERAL.BAT", "hash", true, endpoints, DateTimeOffset.UtcNow, null), false, false);
    }

    private static DashboardHalf Half(ProfileView row, bool active = false) =>
        DashboardProjection.Half(new(TestSuiteId.Discord, "", TestTarget.Defaults, [row]), row, active);

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void CountsSuccessfulEndpointsIndependentlyOfLatency(int successes)
    {
        var half = Half(Row(successes, 200));
        var metrics = DashboardScoring.Summarize([half]);
        Assert.Equal(successes, half.SuccessfulEndpoints);
        Assert.Equal(4, half.TotalEndpoints);
        Assert.Equal(successes, metrics.SuccessfulEndpoints);
        Assert.Equal($"{successes}/4", DashboardScoring.FormatAvailability(metrics));
        Assert.Equal(successes * 2.5m, metrics.AvailabilityScore);
        Assert.Equal(successes == 0 ? null : 200m, metrics.MeanHttpsMilliseconds);
        Assert.Equal(successes == 0 ? null : 80m / 9, metrics.ResponseScore);
    }

    [Theory]
    [InlineData(0, "10 / 10")] [InlineData(99, "10 / 10")] [InlineData(100, "10 / 10")]
    [InlineData(200, "8,9 / 10")] [InlineData(500, "5,6 / 10")]
    [InlineData(1000, "0 / 10")] [InlineData(2000, "0 / 10")]
    public void ResponseUsesFixedScaleWithoutDns(double milliseconds, string displayed)
    {
        var half = Half(Row(4, milliseconds));
        Assert.Equal((decimal)milliseconds, half.HttpsMedianMilliseconds);
        Assert.Equal(displayed, DashboardScoring.Format(half.ResponseScore));
        Assert.Equal(half.ResponseScore, DashboardScoring.Summarize([half]).ResponseScore);
    }

    [Fact]
    public void SelectedServicesHaveEqualWeightDespiteUnequalSuccessfulSamples()
    {
        var oneSuccess = Half(Row(1, 100, 5000, 5000, 5000));
        var fourSuccesses = Half(Row(4, 600, 800, 1000, 1200));
        var third = Half(Row(4, 100));
        Assert.Equal(100m, oneSuccess.HttpsMedianMilliseconds);
        Assert.Equal(900m, fourSuccesses.HttpsMedianMilliseconds);
        var two = DashboardScoring.Summarize([oneSuccess, fourSuccesses]);
        Assert.Equal("5/8", DashboardScoring.FormatAvailability(two));
        Assert.Equal(500m, two.MeanHttpsMilliseconds);
        Assert.Equal(50m / 9, two.ResponseScore);
        var three = DashboardScoring.Summarize([oneSuccess, fourSuccesses, third]);
        Assert.Equal("9/12", DashboardScoring.FormatAvailability(three));
        Assert.Equal(1100m / 3, three.MeanHttpsMilliseconds);
        Assert.Equal((1000m - 1100m / 3) / 90, three.ResponseScore);
    }

    [Fact]
    public void CompletedZeroSuccessIsKnownAvailabilityButUnknownResponse()
    {
        var good = Half(Row(4, 100));
        var failed = Half(Row(0, 5000));
        var metrics = DashboardScoring.Summarize([good, good, failed]);
        Assert.Equal("8/12", DashboardScoring.FormatAvailability(metrics));
        Assert.Equal(20m / 3, metrics.AvailabilityScore);
        Assert.Equal("#B77916", DashboardScoring.Color(metrics.AvailabilityScore));
        Assert.Null(metrics.MeanHttpsMilliseconds);
        Assert.Null(metrics.ResponseScore);
    }

    [Fact]
    public void CountsDoNotComeFromLegacyScoreAndResponseRetainsPrecision()
    {
        var faster = Half(Row(4, 200)) with { Score = 0 };
        var slower = Half(Row(4, 201));
        Assert.Equal("4/4", DashboardScoring.FormatAvailability(DashboardScoring.Summarize([faster])));
        Assert.Equal(10m, DashboardScoring.Summarize([faster]).AvailabilityScore);
        Assert.Equal(DashboardScoring.Format(faster.ResponseScore), DashboardScoring.Format(slower.ResponseScore));
        Assert.True(faster.ResponseScore > slower.ResponseScore);
    }

    [Fact]
    public void MissingSelectedServiceSuppressesAggregateButKeepsExpectedCount()
    {
        var good = Half(Row(4, 100));
        var missing = Half(Row(4, 100) with { LastResult = null });
        var metrics = DashboardScoring.Summarize([good, missing, good]);
        Assert.Equal("—/12", DashboardScoring.FormatAvailability(metrics));
        Assert.Null(metrics.SuccessfulEndpoints);
        Assert.Null(metrics.AvailabilityScore);
        Assert.Null(metrics.MeanHttpsMilliseconds);
        Assert.Null(metrics.ResponseScore);
        Assert.Equal("—", DashboardScoring.FormatAvailability(DashboardScoring.Summarize([])));
    }

    [Fact]
    public void MissingLeagueConfigurationRetainsFourExpectedEndpoints()
    {
        var row = Row(4, 100) with { LastResult = null, Error = "Клиент не найден" };
        var half = DashboardProjection.Half(new(TestSuiteId.LeagueOfLegends, "RU", [], [row], "Клиент не найден"), row, false);
        Assert.Equal(4, half.TotalEndpoints);
        Assert.Equal("—/4", DashboardScoring.FormatAvailability(DashboardScoring.Summarize([half])));
    }

    [Fact]
    public void ProjectionUsesActualTargetCountForUnknownAvailability()
    {
        var row = Row(4, 100) with { LastResult = null };
        var half = DashboardProjection.Half(new(TestSuiteId.Discord, "", TestTarget.Defaults.Take(3).ToArray(), [row]), row, false);
        Assert.Equal(3, half.TotalEndpoints);
        Assert.Equal("—/3", DashboardScoring.FormatAvailability(DashboardScoring.Summarize([half])));
    }

    [Fact]
    public void ScoresAreRecomputedFromExistingSavedResultsWithoutMigration()
    {
        var row = Row(2, 100, 300, 5000, 5000);
        var json = JsonSerializer.Serialize(row.LastResult);
        Assert.DoesNotContain("ResponseScore", json);
        var half = Half(row with { LastResult = JsonSerializer.Deserialize<ProfileTestResult>(json) });
        Assert.Equal(2, half.SuccessfulEndpoints);
        Assert.Equal(200m, half.HttpsMedianMilliseconds);
        Assert.Equal(80m / 9, half.ResponseScore);
        Assert.Equal("2/4", DashboardScoring.FormatAvailability(DashboardScoring.Summarize([half])));
    }

    [Fact]
    public void TooltipsExplainIndependentCountsAndHttpsNotGamePing()
    {
        var services = new[] { Half(Row(2, 100, 300, 5000, 5000)), Half(Row(4, 300)) };
        Assert.Contains("6/8", DashboardScoring.Details(services));
        var response = DashboardScoring.ResponseDetails(services);
        Assert.Contains("2/4", response);
        Assert.Contains("200", response);
        Assert.Contains("300", response);
        Assert.Contains("DNS", response);
        Assert.Contains("TCP/TLS", response);
        Assert.Contains("1000", response);
    }
}
