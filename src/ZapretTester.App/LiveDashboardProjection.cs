using ZapretTester.Core;

namespace ZapretTester.App;

public sealed record DashboardCardUpdate(DashboardCard Card, bool IsPreliminary);

public static class LiveDashboardProjection
{
    public static IReadOnlyList<DashboardCardUpdate> Project(IReadOnlyList<SuiteView> suites, SeriesProgress? series, bool allowPreviews = true)
    {
        var cards = DashboardProjection.Cards(suites, participants: series?.Participants);
        return cards.Select(card =>
        {
            var preliminary = false;
            var services = card.Services.Select(service =>
            {
                if (!allowPreviews || series == null) return service;
                var suite = suites.Single(s => s.Suite == service.Suite);
                var row = suite.Profiles.Single(p => p.Id == card.Id);
                var result = row.LastResult;
                if (suite.Error != null || row.Error != null || row.RunError != null || row.IsStale || row.RunState != RunState.Idle
                    || result is not { PendingValidation: true, IsReliable: true, Error: null }
                    || result.SeriesId != series.RunId || result.VariantId != suite.VariantId
                    || result.ProfileId != row.Id || result.Suite != suite.Suite || result.Region != suite.Region
                    || result.Fingerprint != row.Fingerprint || result.VariantFingerprint != row.Fingerprint
                    || !series.Participants.Any(p => p.ProfileId == row.Id && p.Suite == suite.Suite && p.Region == suite.Region && p.VariantId == suite.VariantId))
                    return service;

                // Project a copy for display only. Stored evidence remains pending until the isolation monitor approves the series.
                var preview = DashboardProjection.Half(suite, row with { LastResult = result with { PendingValidation = false } }, false);
                if (!preview.Score.HasValue) return service; // Includes the normal completeness and endpoint-name checks.
                preliminary = true;
                const string note = "Предварительно — стратегия проверена, достоверность всей серии ещё подтверждается.";
                return preview with {
                    Status = preview.Status with { Color = "#8994A3", Description = note + " " + preview.Status.Description },
                    Details = note + "\n" + preview.Details,
                    Endpoints = preview.Endpoints.Select(e => e with {
                        Status = e.Status with { Color = "#8994A3", Description = "Предварительно: " + e.Status.Description },
                        Details = note + "\n" + e.Details }).ToArray(),
                    Score = null, ResponseScore = null
                };
            }).ToArray();
            return new DashboardCardUpdate(card with { Services = services }, preliminary);
        }).ToArray();
    }
}
