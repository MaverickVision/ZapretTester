namespace ZapretTester.Core;

public sealed record DashboardIndicator(string Glyph, string Color, string Description);
public sealed record DashboardEndpoint(string ServiceGlyph, DashboardIndicator Status, string Details);
public sealed record DashboardHalf(TestSuiteId Suite, DashboardIndicator Status, string Details,
    IReadOnlyList<DashboardEndpoint> Endpoints, string Latency = "—", decimal? Score = null,
    int? SuccessfulEndpoints = null, int TotalEndpoints = 4, decimal? HttpsMedianMilliseconds = null,
    decimal? ResponseScore = null)
{
    public bool IsDiscord => Suite == TestSuiteId.Discord;
    public bool IsLeague => Suite == TestSuiteId.LeagueOfLegends;
    public bool IsYouTube => Suite == TestSuiteId.YouTube;
    public bool IsSteam => Suite == TestSuiteId.Steam;
    public bool IsTwitch => Suite == TestSuiteId.Twitch;
    public bool IsCloudflare => Suite == TestSuiteId.Cloudflare;
}
public sealed record DashboardCard(string Id, string Name, IReadOnlyList<DashboardHalf> Services)
{
    public DashboardHalf Discord => Services.Single(s => s.Suite == TestSuiteId.Discord);
    public DashboardHalf League => Services.Single(s => s.Suite == TestSuiteId.LeagueOfLegends);
}

public static class DashboardProjection
{
    private const string Gray = "#8994A3", Green = "#218559", Amber = "#B77916", Red = "#C34343";
    private static DashboardIndicator Neutral(string glyph, string text) => new(glyph, Gray, text);
    public static DashboardHalf Half(SuiteView suite, ProfileView row, bool active)
    {
        var result = row.LastResult;
        decimal? score = null;
        int? successfulEndpoints = null;
        var totalEndpoints = suite.Targets.Count == 0 ? 4 : suite.Targets.Count;
        decimal? httpsMedianMilliseconds = null;
        DashboardIndicator status;
        if ((row.Error ?? suite.Error) is { } error) status = Neutral("⚠", error);
        else if (active || row.RunState is RunState.Testing or RunState.Queued)
            status = Neutral("◷", "Серия выполняется; итог ещё не подтверждён");
        else if (row.RunState == RunState.Cancelled) status = Neutral("⊘", "Проверка отменена");
        else if (row.RunState == RunState.Blocked) status = Neutral("⊟", "Проверка заблокирована");
        else if (result == null) status = Neutral("?", "Ещё не проверено");
        else if (row.IsStale) status = Neutral("◷", "Результат устарел");
        else if (result.PendingValidation || !result.IsReliable || result.Error != null)
            status = Neutral("⚠", result.Error ?? "Достоверность серии не подтверждена");
        else if (suite.Targets.Count != 4 || suite.Targets.Select(t => t.Name).Distinct().Count() != 4
            || result.ExpectedEndpoints != 4 || result.Endpoints.Count != 4 || result.Endpoints.Select(e => e.Name).Distinct().Count() != 4
            || suite.Targets.Any(t => !result.Endpoints.Any(e => e.Name == t.Name)))
            status = Neutral("⚠", "Проверка неполная");
        else
        {
            successfulEndpoints = result.Endpoints.Count(e => e.Status == EndpointStatus.Available);
            score = 10m * successfulEndpoints.Value / totalEndpoints;
            if (result.MedianMilliseconds is { } milliseconds) httpsMedianMilliseconds = (decimal)milliseconds;
            if (score == 10) status = new("✓", Green, "Все службы доступны");
            else if (result.Endpoints.All(e => e.Status == EndpointStatus.Failed)) status = new("×", Red, "Службы недоступны по результатам проверки");
            else status = new("!", Amber, "Частичная доступность или предупреждение сервера");
        }
        var metadata = ServiceCatalog.Get(suite.Suite);
        var app = metadata.Name + (suite.Suite == TestSuiteId.LeagueOfLegends ? " • " + suite.Region : "");
        var limitation = metadata.Limitation;
        var summary = result == null ? "Выберите сервисы и запустите проверку этого общего варианта."
            : $"{result.Endpoints.Count(e => e.Status == EndpointStatus.Available)}/{result.ExpectedEndpoints} • {result.TestedAt.LocalDateTime:g}\nОтклик HTTPS (медиана): {result.MedianMilliseconds?.ToString("F0") ?? "—"} мс";
        var details = $"{row.Name} • {app}\n{status.Description}\n{summary}\n{row.RunError ?? result?.Error}\n{limitation}\n"
            + "Отклик HTTPS включает установление TCP/TLS-соединения и ответ сервера; DNS и предшествующие неудачные попытки не входят. Это не ICMP-пинг.";
        var glyphs = metadata.EndpointGlyphs;
        var names = metadata.Definition?.Targets.Select(t => t.Name).ToArray() ?? ["Авторизация Riot", "Конфигурация клиента", "Платформа", "CDN обновлений"];
        var endpoints = Enumerable.Range(0, 4).Select(i =>
        {
            var name = suite.Targets.ElementAtOrDefault(i)?.Name ?? names[i];
            var endpoint = result?.Endpoints.FirstOrDefault(e => e.Name == name);
            var indicator = !score.HasValue || endpoint == null ? Neutral(status.Glyph, status.Description)
                : endpoint.Status switch {
                    EndpointStatus.Available => new DashboardIndicator("✓", Green, "Доступно"),
                    EndpointStatus.ServerWarning => new("!", Amber, "Предупреждение сервера"),
                    _ => new("×", Red, "Ошибка проверки") };
            var text = $"{app} • {name}\n{indicator.Description}\n" + (endpoint == null ? "Ещё не проверено" :
                $"HTTP {endpoint.HttpCode?.ToString() ?? "—"} • HTTPS {endpoint.Duration.TotalMilliseconds:F0} мс • DNS {endpoint.DnsDuration.TotalMilliseconds:F0} мс\nIP {endpoint.RemoteIp ?? "—"}\n{result!.TestedAt.LocalDateTime:g}\n{endpoint.Error}");
            return new DashboardEndpoint(glyphs[i], indicator, text);
        }).ToArray();
        return new(suite.Suite, status, details, endpoints, httpsMedianMilliseconds is { } ms ? $"{ms:F0} мс" : "—",
            score, successfulEndpoints, totalEndpoints, httpsMedianMilliseconds,
            httpsMedianMilliseconds is { } median ? DashboardScoring.ResponseScoreFor(median) : null);
    }

    public static IReadOnlyList<DashboardCard> Cards(IReadOnlyList<SuiteView> suites,
        TestSuiteId? activeSuite = null, string? activeRegion = null, IReadOnlyList<string>? activeIds = null,
        IReadOnlyList<RunParticipant>? participants = null)
    {
        if (suites.Count == 0) return [];
        bool Active(SuiteView s, string id) => participants != null
            ? participants.Any(p => p.Suite == s.Suite && p.Region == s.Region && p.ProfileId == id && p.VariantId == s.VariantId)
            : s.Suite == activeSuite && s.Region == activeRegion && activeIds?.Contains(id) == true;
        return suites[0].Profiles.Select(row =>
        {
            var rows = suites.Select(s => s.Profiles.Single(p => p.Id == row.Id)).ToArray();
            var combined = suites.Any(s => s.VariantId != null);
            var validSeries = !combined || (suites.All(s => s.VariantId == suites[0].VariantId)
                && rows.All(r => r.LastResult?.VariantId == suites[0].VariantId && !string.IsNullOrEmpty(r.LastResult?.SeriesId)
                    && r.LastResult.IsReliable && !r.LastResult.PendingValidation && r.LastResult.Error == null
                    && !r.IsStale && r.LastResult.VariantFingerprint == r.Fingerprint)
                && rows.Select(r => r.LastResult!.SeriesId).Distinct(StringComparer.Ordinal).Count() == 1);
            var services = suites.OrderBy(s => s.Suite).Select(s =>
            {
                var selected = s.Profiles.Single(p => p.Id == row.Id);
                if (!validSeries && selected.LastResult != null && selected.Error == null
                    && selected.RunState == RunState.Idle && !Active(s, row.Id))
                    selected = selected with { LastResult = selected.LastResult with
                        { IsReliable = false, Error = "Нет общей подтверждённой серии для выбранного набора. Выполните проверку повторно." } };
                return Half(s, selected, Active(s, row.Id));
            }).ToArray();
            return new DashboardCard(row.Id, row.Name, services);
        }).ToArray();
    }
}
