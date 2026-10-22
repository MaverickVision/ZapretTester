using System.Text.RegularExpressions;

namespace ZapretTester.App;

public sealed record UserFacingMessage(string Text, bool OfferPrepareFlowseal = false, string? TechnicalDetails = null)
{
    public static UserFacingMessage Create(string original)
    {
        // Keep the wire protocol unchanged: older Helpers report this diagnostic in English.
        var conflict = Regex.Match(original, @"Unowned modifying WinDivert handle: PID (\d+), layer ([0-4])\. Isolation is not verified\.");
        if (!conflict.Success || !uint.TryParse(conflict.Groups[1].Value, out var pid) || pid == 0)
            return new(original);

        return new($"Уже запущен другой Zapret или другая программа, перехватывающая трафик (PID {pid}). " +
            "Проверка остановлена, чтобы не смешивать результаты. Нажмите «Подготовить Flowseal к проверке». " +
            "Если это другая программа, закройте её перед проверкой.", true, original);
    }
}
