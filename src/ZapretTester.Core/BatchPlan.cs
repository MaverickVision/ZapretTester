namespace ZapretTester.Core;

public sealed record ResolvedSuite(TestSuiteId Id, string Region, SuiteDefinition? Definition, string? Error);
public sealed record BatchJob(ZapretProfile Profile, IReadOnlyList<SuiteDefinition> Suites, VariantDescriptor? Variant = null);
public sealed record BlockedParticipant(RunParticipant Participant, string Error);
public sealed record BatchPlan(IReadOnlyList<BatchJob> Jobs, IReadOnlyList<RunParticipant> Participants,
    IReadOnlyList<BlockedParticipant> Blocked, int RequiredPorts, VariantDescriptor? Variant = null)
{
    public static BatchPlan Create(IReadOnlyList<ProfileEntry> profiles, IReadOnlyList<string> ids,
        IReadOnlyList<ResolvedSuite> suites, int attempts, int availablePorts)
    {
        if (ids.Count == 0 || suites.Count == 0 || attempts is < 1 or > 5) throw new ArgumentException("Выберите стратегии и сервисы.");
        if (ids.Distinct().Count() != ids.Count || suites.Select(s => s.Id).Distinct().Count() != suites.Count
            || suites.Any(s => !Enum.IsDefined(s.Id))) throw new ArgumentException("Некорректный выбор проверки.");
        var variant = VariantDescriptor.Create(suites.Select(s => new SuiteSelection(s.Id, s.Region))).Validate();
        var known = profiles.ToDictionary(p => p.Profile?.Id ?? Path.GetFileName(p.FilePath).ToUpperInvariant());
        if (ids.Any(id => !known.ContainsKey(id))) throw new ArgumentException("Неизвестная стратегия.");
        var jobs = new List<BatchJob>(); var participants = new List<RunParticipant>(); var blocked = new List<BlockedParticipant>();
        var definitions = suites.Where(s => s.Definition != null).OrderBy(s => s.Id).Select(s => s.Definition!).ToArray();
        foreach (var id in ids)
        {
            var entry = known[id];
            var members = suites.OrderBy(s => s.Id).Select(s => new RunParticipant(s.Id, s.Region, id, variant.Id)).ToArray();
            participants.AddRange(members);
            var error = entry.Error ?? suites.FirstOrDefault(s => s.Error != null)?.Error;
            if (entry.Profile == null || definitions.Length != suites.Count) error ??= "Конфигурация недоступна. Исправьте настройки или снимите выбор сервиса.";
            if (error == null)
                try { VariantAdapter.ValidateDefinitions(variant, definitions); _ = VariantAdapter.Adapt(entry.Profile!.Arguments, definitions); }
                catch (UnsupportedProfileException e) { error = e.Message; }
            if (error != null) blocked.AddRange(members.Select(p => new BlockedParticipant(p, error)));
            else jobs.Add(new(entry.Profile!, definitions, variant));
        }
        var budget = checked(jobs.Sum(j => j.Suites.Sum(s => s.Targets.Count)) * attempts);
        if (budget > availablePorts) throw new InvalidOperationException($"Недостаточный пул: требуется {budget} неповторяющихся портов, доступно {availablePorts}. Увеличьте пул портов в настройках.");
        return new(jobs.ToArray(), participants.ToArray(), blocked.ToArray(), budget, variant);
    }

    public static IEnumerable<(SuiteDefinition Suite, TestTarget Target, int Index)> InterleavedTargets(BatchJob job)
    {
        for (var i = 0; i < job.Suites.Max(s => s.Targets.Count); i++)
            foreach (var suite in job.Suites)
                if (i < suite.Targets.Count) yield return (suite, suite.Targets[i], i);
    }
}
