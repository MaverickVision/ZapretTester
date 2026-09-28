using System.Text.Json;

namespace ZapretTester.Core;

public sealed record ResolvedSuite(TestSuiteId Id, string Region, SuiteDefinition? Definition, string? Error);
public sealed record BatchJob(ZapretProfile Profile, IReadOnlyList<SuiteDefinition> Suites);
public sealed record BlockedParticipant(RunParticipant Participant, string Error);
public sealed record BatchPlan(IReadOnlyList<BatchJob> Jobs, IReadOnlyList<RunParticipant> Participants,
    IReadOnlyList<BlockedParticipant> Blocked, int RequiredPorts)
{
    public static BatchPlan Create(IReadOnlyList<ProfileEntry> profiles, IReadOnlyList<string> ids,
        IReadOnlyList<ResolvedSuite> suites, int attempts, int availablePorts)
    {
        if (ids.Count == 0 || suites.Count == 0) throw new ArgumentException("Выберите стратегии и сервисы.");
        if (ids.Distinct().Count() != ids.Count || suites.Select(s => s.Id).Distinct().Count() != suites.Count
            || suites.Any(s => !Enum.IsDefined(s.Id))) throw new ArgumentException("Некорректный выбор проверки.");
        var known = profiles.ToDictionary(p => p.Profile?.Id ?? Path.GetFileName(p.FilePath).ToUpperInvariant());
        if (ids.Any(id => !known.ContainsKey(id))) throw new ArgumentException("Неизвестная стратегия.");
        var jobs = new List<BatchJob>(); var participants = new List<RunParticipant>(); var blocked = new List<BlockedParticipant>();
        foreach (var id in ids)
        {
            var entry = known[id];
            var groups = new Dictionary<string, List<SuiteDefinition>>();
            foreach (var suite in suites.OrderBy(s => s.Id))
            {
                var participant = new RunParticipant(suite.Id, suite.Region, id); participants.Add(participant);
                var error = entry.Error ?? suite.Error;
                if (error == null && entry.Profile != null && suite.Definition != null)
                {
                    try
                    {
                        // Same source profile/resources, and exactly the same ordered effective arguments.
                        var key = JsonSerializer.Serialize(LeagueAdapter.Adapt(entry.Profile.Arguments, suite.Definition));
                        if (!groups.TryGetValue(key, out var group)) groups[key] = group = [];
                        group.Add(suite.Definition);
                    }
                    catch (UnsupportedProfileException e) { error = e.Message; }
                }
                else error ??= "Конфигурация недоступна.";
                if (error != null) blocked.Add(new(participant, error));
            }
            foreach (var group in groups.Values) jobs.Add(new(entry.Profile!, group.ToArray()));
        }
        var budget = checked(jobs.Sum(j => j.Suites.Sum(s => s.Targets.Count)) * attempts);
        if (budget > availablePorts) throw new InvalidOperationException($"Недостаточный пул: требуется {budget} неповторяющихся портов.");
        return new(jobs.ToArray(), participants.ToArray(), blocked.ToArray(), budget);
    }

    public static IEnumerable<(SuiteDefinition Suite, TestTarget Target, int Index)> InterleavedTargets(BatchJob job)
    {
        for (var i = 0; i < job.Suites.Max(s => s.Targets.Count); i++)
            foreach (var suite in job.Suites)
                if (i < suite.Targets.Count) yield return (suite, suite.Targets[i], i);
    }
}
