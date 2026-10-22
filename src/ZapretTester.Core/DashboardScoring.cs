using System.Globalization;

namespace ZapretTester.Core;

public sealed record DashboardMetrics(int? SuccessfulEndpoints, int TotalEndpoints, decimal? AvailabilityScore,
    decimal? MeanHttpsMilliseconds, decimal? ResponseScore);

public static class DashboardScoring
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    public static decimal? Average(IReadOnlyList<DashboardHalf> services) =>
        services.Count == 0 || services.Any(s => !s.Score.HasValue) ? null : services.Average(s => s.Score!.Value);

    public static DashboardMetrics Summarize(IReadOnlyList<DashboardHalf> services)
    {
        var total = services.Sum(s => s.TotalEndpoints);
        if (services.Count == 0 || total == 0 || services.Any(s => !s.SuccessfulEndpoints.HasValue))
            return new(null, total, null, null, null);

        var successful = services.Sum(s => s.SuccessfulEndpoints!.Value);
        decimal? mean = services.Any(s => s.SuccessfulEndpoints == 0 || !s.HttpsMedianMilliseconds.HasValue)
            ? null : services.Average(s => s.HttpsMedianMilliseconds!.Value);
        return new(successful, total, 10m * successful / total, mean,
            mean is { } milliseconds ? ResponseScoreFor(milliseconds) : null);
    }

    public static decimal ResponseScoreFor(decimal milliseconds) => Math.Clamp((1000m - milliseconds) / 90m, 0m, 10m);

    public static string FormatAvailability(DashboardMetrics metrics) => metrics.TotalEndpoints == 0 ? "—"
        : $"{metrics.SuccessfulEndpoints?.ToString() ?? "—"}/{metrics.TotalEndpoints}";

    private static string Number(decimal score) => Math.Round(score, 1, MidpointRounding.AwayFromZero).ToString("0.#", Russian);
    public static string Format(decimal? score) => score is { } value ? Number(value) + " / 10" : "—";
    public static string Color(decimal? score) => score switch {
        null => "#8994A3", < 5 => "#C34343", < 8 => "#B77916", _ => "#218559" };

    public static string Details(IReadOnlyList<DashboardHalf> services)
    {
        var metrics = Summarize(services);
        var lines = services.Select(s => $"{ServiceName(s)}: {s.SuccessfulEndpoints?.ToString() ?? "—"}/{s.TotalEndpoints} • {s.Status.Description}");
        var formula = metrics.SuccessfulEndpoints is { } successes
            ? $"Успешно {successes} из {metrics.TotalEndpoints} проверок выбранных сервисов."
            : "Итог появится, когда все выбранные сервисы будут иметь полные, актуальные и достоверные результаты.";
        return $"Доступность: {FormatAvailability(metrics)}\n{string.Join("\n", lines)}\n{formula}\n\n"
            + "Учитываются только успешные HTTPS-проверки. Ошибки и предупреждения сервера, включая HTTP 429/5xx, не считаются успехом.\n"
            + "Рамка: менее 50% — красная, от 50% до менее 80% — янтарная, от 80% — зелёная. Отклик не меняет цвет рамки.\n"
            + string.Join("\n", services.Select(s => ServiceCatalog.Get(s.Suite).Limitation));
    }

    public static string ResponseDetails(IReadOnlyList<DashboardHalf> services)
    {
        var metrics = Summarize(services);
        var lines = services.Select(s => $"{ServiceName(s)}: "
            + (s.HttpsMedianMilliseconds is { } ms ? $"медиана {Number(ms)} мс, успешных {s.SuccessfulEndpoints}/{s.TotalEndpoints}"
                : $"— • {s.Status.Description}; успешных {s.SuccessfulEndpoints?.ToString() ?? "—"}/{s.TotalEndpoints}"));
        var formula = metrics.MeanHttpsMilliseconds is { } mean
            ? $"Среднее медиан: ({string.Join(" + ", services.Select(s => Number(s.HttpsMedianMilliseconds!.Value)))}) ÷ {services.Count} = {Number(mean)} мс.\n"
                + $"Оценка: ограничить((1000 − {Number(mean)}) ÷ 90, 0…10) = {Format(metrics.ResponseScore)}."
            : "Оценка появится, когда все выбранные сервисы будут иметь подтверждённый результат и хотя бы одну успешную проверку.";
        return $"Отклик: {Format(metrics.ResponseScore)}\n{string.Join("\n", lines)}\n{formula}\n\n"
            + "Все выбранные сервисы имеют одинаковый вес. ≤100 мс — 10 баллов; ≥1000 мс — 0.\n"
            + "Отклик HTTPS включает установление TCP/TLS-соединения и ответ сервера. DNS и предшествующие неудачные попытки не входят.\n"
            + "Это не ICMP-пинг, игровой пинг или скорость загрузки. Частичный успех означает меньшую выборку; доступность оценивается отдельно.";
    }

    private static string ServiceName(DashboardHalf service) => ServiceCatalog.Get(service.Suite).ShortName;
}
