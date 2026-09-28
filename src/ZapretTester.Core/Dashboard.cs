namespace ZapretTester.Core;

public sealed record DashboardIndicator(string Glyph, string Color, string Description);
public sealed record DashboardEndpoint(string ServiceGlyph, DashboardIndicator Status, string Details);
public sealed record DashboardHalf(TestSuiteId Suite, DashboardIndicator Status, string Details,
    IReadOnlyList<DashboardEndpoint> Endpoints, string Latency = "—", decimal? Score = null)
{
    public bool IsDiscord => Suite == TestSuiteId.Discord;
    public bool IsLeague => Suite == TestSuiteId.LeagueOfLegends;
    public bool IsYouTube => Suite == TestSuiteId.YouTube;
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
            score = 2.5m * result.Endpoints.Count(e => e.Status == EndpointStatus.Available);
            if (score == 10) status = new("✓", Green, "Все службы доступны");
            else if (result.Endpoints.All(e => e.Status == EndpointStatus.Failed)) status = new("×", Red, "Службы недоступны по результатам проверки");
            else status = new("!", Amber, "Частичная доступность или предупреждение сервера");
        }
        var app = suite.Suite switch { TestSuiteId.Discord => "Discord", TestSuiteId.YouTube => "YouTube", _ => "League of Legends • " + suite.Region };
        var limitation = suite.Suite == TestSuiteId.YouTube ? "Проверена доступность HTTPS-служб YouTube. Воспроизведение и скорость видео не проверялись." : suite.Suite == TestSuiteId.Discord ? "Голос и WebSocket не проверялись."
            : "Проверена доступность служб клиента. Вход в аккаунт и подключение к матчу не проверялись.";
        var summary = result == null ? "Запустите проверку на вкладке сервиса или дашборде."
            : $"{result.Endpoints.Count(e => e.Status == EndpointStatus.Available)}/{result.ExpectedEndpoints} • {result.TestedAt.LocalDateTime:g}\nМедиана: {result.MedianMilliseconds?.ToString("F0") ?? "—"} мс";
        var details = $"{row.Name} • {app}\n{status.Description}\n{summary}\n{row.RunError ?? result?.Error}\n{limitation}";
        string[] glyphs = suite.Suite == TestSuiteId.YouTube ? ["\uE774", "\uE71B", "\uEB9F", "\uE8AB"] : suite.Suite == TestSuiteId.Discord ? ["\uE774", "\uE71B", "\uE753", "\uE895"] : ["\uE72E", "\uE713", "\uE968", "\uE895"];
        string[] names = suite.Suite == TestSuiteId.YouTube ? ["Сайт", "Короткие ссылки", "Изображения", "Перенаправление видео"] : suite.Suite == TestSuiteId.Discord ? ["Website", "Gateway HTTPS", "CDN", "Update"] : ["Авторизация Riot", "Конфигурация клиента", "Платформа", "CDN обновлений"];
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
                $"HTTP {endpoint.HttpCode?.ToString() ?? "—"} • {endpoint.Duration.TotalMilliseconds:F0} мс\n{result!.TestedAt.LocalDateTime:g}\n{endpoint.Error}");
            return new DashboardEndpoint(glyphs[i], indicator, text);
        }).ToArray();
        return new(suite.Suite, status, details, endpoints, score.HasValue && result?.MedianMilliseconds is { } ms ? $"{ms:F0} мс" : "—", score);
    }

    public static IReadOnlyList<DashboardCard> Cards(IReadOnlyList<SuiteView> suites,
        TestSuiteId? activeSuite = null, string? activeRegion = null, IReadOnlyList<string>? activeIds = null,
        IReadOnlyList<RunParticipant>? participants = null)
    {
        if (suites.Count == 0) return [];
        bool Active(SuiteView s, string id) => participants != null
            ? participants.Any(p => p.Suite == s.Suite && p.Region == s.Region && p.ProfileId == id)
            : s.Suite == activeSuite && s.Region == activeRegion && activeIds?.Contains(id) == true;
        return suites[0].Profiles.Select(row => new DashboardCard(row.Id, row.Name,
            suites.OrderBy(s => s.Suite).Select(s => Half(s, s.Profiles.Single(p => p.Id == row.Id), Active(s, row.Id))).ToArray())).ToArray();
    }
}
