using System.Text.Json;
using ZapretTester.Core;
using Xunit;

namespace ZapretTester.Tests;

public sealed class DashboardScoreTests
{
    private static ProfileView Row(int successes = 4, EndpointStatus other = EndpointStatus.Failed) =>
        new("GENERAL.BAT", "General", null, "hash", RunState.Idle,
            new("GENERAL.BAT", "hash", true, TestTarget.Defaults.Select((t, i) => new EndpointResult(t.Name,
                i < successes ? EndpointStatus.Available : other, 41000 + i, "1.1.1.1",
                TimeSpan.FromMilliseconds(50 + i), TimeSpan.FromSeconds(10), i < successes ? 200 : 503, null, null)).ToArray(),
                DateTimeOffset.UtcNow, null), false, false);
    private static DashboardHalf Half(ProfileView row, bool active = false) =>
        DashboardProjection.Half(new(TestSuiteId.Discord, "", TestTarget.Defaults, [row]), row, active);

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void ScoresOnlySuccessfulEndpoints(int successes)
    {
        Assert.Equal(successes * 2.5m, Half(Row(successes)).Score);
        Assert.Equal(successes * 2.5m, Half(Row(successes, EndpointStatus.ServerWarning)).Score);
    }

    [Theory]
    [InlineData("missing")] [InlineData("stale")] [InlineData("unreliable")] [InlineData("pending")]
    [InlineData("active")] [InlineData("queued")] [InlineData("testing")] [InlineData("cancelled")]
    [InlineData("blocked")] [InlineData("unsupported")] [InlineData("error")] [InlineData("incomplete")]
    [InlineData("duplicate")] [InlineData("wrong-target")]
    public void NeutralResultsHaveNoScore(string scenario)
    {
        var row = Row();
        row = scenario switch {
            "missing" => row with { LastResult = null },
            "stale" => row with { IsStale = true },
            "unreliable" => row with { LastResult = row.LastResult! with { IsReliable = false } },
            "pending" => row with { LastResult = row.LastResult! with { PendingValidation = true } },
            "queued" => row with { RunState = RunState.Queued },
            "testing" => row with { RunState = RunState.Testing },
            "cancelled" => row with { RunState = RunState.Cancelled },
            "blocked" => row with { RunState = RunState.Blocked },
            "unsupported" => row with { Error = "Адаптация не поддерживается" },
            "error" => row with { LastResult = row.LastResult! with { Error = "Конфликт изоляции" } },
            "incomplete" => row with { LastResult = row.LastResult! with { Endpoints = row.LastResult.Endpoints.Take(3).ToArray() } },
            "duplicate" => row with { LastResult = row.LastResult! with { Endpoints = Enumerable.Repeat(row.LastResult.Endpoints[0], 4).ToArray() } },
            "wrong-target" => row with { LastResult = row.LastResult! with { Endpoints = row.LastResult.Endpoints.Select(e => e with { Name = "Other" + e.Name }).ToArray() } },
            _ => row
        };
        Assert.Null(Half(row, scenario == "active").Score);
    }

    [Fact]
    public void AverageUsesAllAndOnlySelectedServicesWithoutRoundingInputs()
    {
        var good = Half(Row()); var failed = Half(Row(0)); var unknown = Half(Row() with { LastResult = null });
        Assert.Equal(10m, DashboardScoring.Average([good]));
        Assert.Equal(5m, DashboardScoring.Average([good, failed]));
        Assert.Equal(20m / 3m, DashboardScoring.Average([good, good, failed]));
        Assert.Equal("6,7 / 10", DashboardScoring.Format(DashboardScoring.Average([good, good, failed])));
        Assert.Null(DashboardScoring.Average([good, unknown]));
        Assert.Null(DashboardScoring.Average([]));
        Assert.Equal("—", DashboardScoring.Format(null));
        Assert.Equal("10 / 10", DashboardScoring.Format(10m));
    }

    [Theory]
    [InlineData("0", "#C34343")] [InlineData("4.99", "#C34343")]
    [InlineData("5", "#B77916")] [InlineData("6.666", "#B77916")]
    [InlineData("7.99", "#B77916")] [InlineData("8", "#218559")] [InlineData("10", "#218559")]
    public void BorderUsesUnroundedScore(string value, string color) =>
        Assert.Equal(color, DashboardScoring.Color(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void ExistingPreferencesDefaultToBestFirstAndNewPreferenceRoundTrips()
    {
        var old = JsonSerializer.Deserialize<DashboardPreferences>("{\"VisibleSuites\":[0,2],\"ExcludedProfiles\":{}}");
        Assert.Equal(DashboardSortMode.BestFirst, old!.SortMode);
        old.SortMode = DashboardSortMode.Name;
        var loaded = JsonSerializer.Deserialize<DashboardPreferences>(JsonSerializer.Serialize(old));
        Assert.Equal(DashboardSortMode.Name, loaded!.SortMode);
        Assert.Equal(old.VisibleSuites, loaded.VisibleSuites);
        Assert.Equal("#8994A3", DashboardScoring.Color(null));
    }
}
