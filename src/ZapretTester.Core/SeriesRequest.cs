namespace ZapretTester.Core;

// Captured synchronously before scheduling preparation. UI selection cannot alter this run.
public sealed record SeriesRequest(ApplicationSettings Settings, IReadOnlyList<string> ProfileIds,
    IReadOnlyList<SuiteSelection> Selections, IReadOnlyList<RunParticipant> Participants, VariantDescriptor? Variant = null)
{
    public static SeriesRequest Capture(IReadOnlyList<string> ids, IReadOnlyList<SuiteSelection> selections,
        IEnumerable<string> knownIds, ApplicationSettings settings)
    {
        settings.Validate();
        var frozenIds = ids.ToArray();
        var frozenSuites = selections.Select(s => s with
        { Region = s.Suite == TestSuiteId.LeagueOfLegends ? s.Region ?? settings.LeagueRegion ?? "" : "" }).ToArray();
        var known = knownIds.ToHashSet(StringComparer.Ordinal);
        if (frozenIds.Length == 0 || frozenSuites.Length is < 1 or > 6
            || frozenIds.Distinct().Count() != frozenIds.Length || frozenIds.Any(id => !known.Contains(id))
            || frozenSuites.Any(s => !Enum.IsDefined(s.Suite))
            || frozenSuites.Select(s => s.Suite).Distinct().Count() != frozenSuites.Length)
            throw new ArgumentException("Выберите известные стратегии и сервисы без повторений.");
        var variant = VariantDescriptor.Create(frozenSuites).Validate();
        var participants = frozenIds.SelectMany(id => frozenSuites.OrderBy(s => s.Suite)
            .Select(s => new RunParticipant(s.Suite, s.Region!, id, variant.Id))).ToArray();
        return new(settings with { }, Array.AsReadOnly(frozenIds), Array.AsReadOnly(frozenSuites), Array.AsReadOnly(participants), variant);
    }
}
