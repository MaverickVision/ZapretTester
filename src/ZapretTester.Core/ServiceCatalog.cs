namespace ZapretTester.Core;

public sealed record ServiceMetadata(TestSuiteId Id, string Name, string ShortName,
    IReadOnlyList<string> EndpointGlyphs, string Limitation, SuiteDefinition? Definition);

// The catalog is the only source of network targets. IPC only chooses IDs and a validated LoL region.
public static class ServiceCatalog
{
    private static SuiteDefinition Extra(TestSuiteId id, params TestTarget[] targets) => new(id, "", targets,
        targets.Select(t => t.Url.DnsSafeHost).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray());

    public static IReadOnlyList<ServiceMetadata> All { get; } = [
        new(TestSuiteId.Discord, "Discord", "Discord", ["\uE774", "\uE71B", "\uE753", "\uE895"],
            "Проверена доступность HTTPS-служб Discord. Голос и WebSocket не проверялись.", SuiteDefinition.Discord),
        new(TestSuiteId.LeagueOfLegends, "League of Legends", "LoL", ["\uE72E", "\uE713", "\uE968", "\uE895"],
            "Проверена доступность служб клиента. Вход в аккаунт и подключение к матчу не проверялись.", null),
        new(TestSuiteId.YouTube, "YouTube", "YouTube", ["\uE774", "\uE71B", "\uEB9F", "\uE8AB"],
            "Проверена доступность HTTPS-служб YouTube. Воспроизведение и скорость видео не проверялись.", SuiteDefinition.YouTube),
        new(TestSuiteId.Steam, "Steam", "Steam", ["\uE719", "\uE902", "\uE943", "\uE753"],
            "Проверена доступность HTTPS-служб Steam. Вход, загрузка игр и игровые подключения не проверялись.",
            Extra(TestSuiteId.Steam, new("Магазин", new("https://store.steampowered.com/"), false, StrictPublic: true),
                new("Сообщество", new("https://steamcommunity.com/"), false, StrictPublic: true),
                new("API HTTPS", new("https://api.steampowered.com/"), true),
                new("CDN", new("https://community.fastly.steamstatic.com/"), true))),
        new(TestSuiteId.Twitch, "Twitch", "Twitch", ["\uE774", "\uE72E", "\uE943", "\uE753"],
            "Проверена доступность HTTPS-служб Twitch. Вход, чат, просмотр и ведение трансляций не проверялись.",
            Extra(TestSuiteId.Twitch, new("Сайт", new("https://www.twitch.tv/"), false, StrictPublic: true, Method: "GET"),
                new("Авторизация HTTPS", new("https://id.twitch.tv/oauth2/validate"), true),
                new("API HTTPS", new("https://api.twitch.tv/helix/streams"), true),
                new("CDN", new("https://static-cdn.jtvnw.net/"), true))),
        new(TestSuiteId.Cloudflare, "Cloudflare", "Cloudflare", ["\uE9D9", "\uE8A5", "\uE753", "\uE968"],
            "Проверена доступность отдельных HTTPS/CDN-служб Cloudflare. DNS 1.1.1.1, WARP, скорость и все сайты Cloudflare не проверялись.",
            Extra(TestSuiteId.Cloudflare, new("Диагностика", new("https://www.cloudflare.com/cdn-cgi/trace"), false, Require200: true),
                new("Документация", new("https://developers.cloudflare.com/"), false, StrictPublic: true),
                new("CDNJS", new("https://cdnjs.cloudflare.com/"), true),
                new("Тестовый узел", new("https://speed.cloudflare.com/__down?bytes=0"), false, Require200: true)))];

    public static ServiceMetadata Get(TestSuiteId id) => All.SingleOrDefault(s => s.Id == id)
        ?? throw new ArgumentException("Неизвестный сервис.");
    public static SuiteDefinition Resolve(TestSuiteId id) => Get(id).Definition
        ?? throw new ArgumentException("Для LoL требуется конфигурация установленной игры.");
    public static IReadOnlyList<ResolvedSuite> Resolve(IEnumerable<SuiteSelection> selections, ApplicationSettings settings)
    {
        return selections.OrderBy(s => s.Suite).Select(selection =>
        {
            if (selection.Suite != TestSuiteId.LeagueOfLegends)
                return new ResolvedSuite(selection.Suite, "", Resolve(selection.Suite), null);
            var region = selection.Region ?? settings.LeagueRegion ?? "";
            try
            {
                var installation = LeagueConfiguration.Load(settings.LeagueFolder, string.IsNullOrWhiteSpace(region) ? null : region);
                return new ResolvedSuite(selection.Suite, installation.SelectedRegion, installation.Regions[installation.SelectedRegion], null);
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
            { return new ResolvedSuite(selection.Suite, region, null, e.Message); }
        }).ToArray();
    }
}
