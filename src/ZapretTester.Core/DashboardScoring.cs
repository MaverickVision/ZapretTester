using System.Globalization;

namespace ZapretTester.Core;

public static class DashboardScoring
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    public static decimal? Average(IReadOnlyList<DashboardHalf> services) =>
        services.Count == 0 || services.Any(s => !s.Score.HasValue) ? null : services.Average(s => s.Score!.Value);

    private static string Number(decimal score) => Math.Round(score, 1, MidpointRounding.AwayFromZero).ToString("0.#", Russian);
    public static string Format(decimal? score) => score is { } value ? Number(value) + " / 10" : "—";
    public static string Color(decimal? score) => score switch {
        null => "#8994A3", < 5 => "#C34343", < 8 => "#B77916", _ => "#218559" };

    public static string Details(IReadOnlyList<DashboardHalf> services)
    {
        var score = Average(services);
        var lines = services.Select(s => $"{s.Suite switch { TestSuiteId.Discord => "Discord", TestSuiteId.LeagueOfLegends => "LoL", _ => "YouTube" }}: "
            + (s.Score is { } value ? $"{Number(value)} / 10 ({value / 2.5m:0}/4)" : $"— • {s.Status.Description}"));
        var formula = score is { } average
            ? $"({string.Join(" + ", services.Select(s => Number(s.Score!.Value)))}) ÷ {services.Count} = {Number(average)} / 10"
            : "Оценка появится, когда все выбранные сервисы будут иметь актуальные и достоверные результаты.";
        return $"Доступность: {Format(score)}\n{string.Join("\n", lines)}\n{formula}\n\n"
            + "Каждая успешная HTTPS-проверка даёт 2,5 балла. Ошибки и предупреждения сервера — 0. Скорость не влияет на баллы.\n"
            + "Рамка: менее 5 — красная, от 5 до менее 8 — янтарная, от 8 — зелёная. Цвет отражает среднее, отдельные ошибки остаются видны.\n"
            + "Голос/WebSocket Discord, вход в аккаунт и матч LoL, воспроизведение и скорость видео YouTube не проверялись.";
    }
}
