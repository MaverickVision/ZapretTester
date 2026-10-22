using System.Text.Json;

namespace ZapretTester.Core;

public static class SuiteProjection
{
    public static SuiteView Build(IEnumerable<ProfileEntry> profiles, IReadOnlyDictionary<string, string> fingerprints,
        IReadOnlyDictionary<string, ProfileTestResult> results, TestSuiteId id, string region, SuiteDefinition? suite,
        string? error = null, IReadOnlyDictionary<string, RunState>? states = null,
        IReadOnlyDictionary<string, string>? runErrors = null, string? mainKey = null)
    {
        var rows = profiles.Select(entry =>
        {
            var profileId = entry.Profile?.Id ?? Path.GetFileName(entry.FilePath).ToUpperInvariant();
            var key = ResultKeys.For(id, region, profileId);
            results.TryGetValue(key, out var result);
            result ??= results.GetValueOrDefault(ResultKeys.Legacy(id, region, profileId));
            fingerprints.TryGetValue(profileId, out var original);
            var fingerprint = original == null || suite == null ? null : suite.Fingerprint(original);
            var profileError = entry.Error ?? error;
            if (profileError == null && entry.Profile != null && suite != null)
                try { _ = LeagueAdapter.Adapt(entry.Profile.Arguments, suite); }
                catch (UnsupportedProfileException e) { profileError = e.Message; }
            return new ProfileView(profileId, entry.Profile?.DisplayName ?? Path.GetFileNameWithoutExtension(entry.FilePath),
                profileError, fingerprint, states?.GetValueOrDefault(key) ?? RunState.Idle, result,
                result != null && result.Fingerprint != fingerprint, key == mainKey, runErrors?.GetValueOrDefault(key));
        }).ToArray();
        return new(id, region, suite?.Targets ?? [], rows, error);
    }

    public static IReadOnlyList<SuiteView> BuildVariant(IEnumerable<ProfileEntry> profiles,
        IReadOnlyDictionary<string, string> fingerprints, IReadOnlyDictionary<string, ProfileTestResult> results,
        VariantDescriptor variant, IReadOnlyList<ResolvedSuite> resolved,
        IReadOnlyDictionary<string, RunState>? states = null, IReadOnlyDictionary<string, string>? runErrors = null,
        string? mainKey = null)
    {
        variant = variant.Validate(allowEmpty: true);
        if (variant.Suites.Count == 0) return [];
        var entries = profiles.ToArray();
        var suites = resolved.Where(s => variant.Suites.Contains(s.Id)).OrderBy(s => s.Id).ToArray();
        var configurationError = suites.FirstOrDefault(s => s.Error != null)?.Error;
        if (suites.Length != variant.Suites.Count || suites.Any(s => s.Definition == null))
            configurationError ??= "Конфигурация выбранного набора недоступна.";
        if (configurationError != null)
            configurationError += " Исправьте настройки или снимите выбор недоступного сервиса: общий вариант не проверен.";
        var definitions = suites.Where(s => s.Definition != null).Select(s => s.Definition!).ToArray();
        var effective = new Dictionary<string, (string? Fingerprint, string? Error)>();
        foreach (var entry in entries)
        {
            var id = entry.Profile?.Id ?? Path.GetFileName(entry.FilePath).ToUpperInvariant();
            var error = entry.Error ?? configurationError;
            string? fingerprint = null;
            if (error == null && entry.Profile != null && fingerprints.TryGetValue(id, out var original))
                try { fingerprint = VariantAdapter.Fingerprint(entry.Profile, original, variant, definitions); }
                catch (Exception e) when (e is UnsupportedProfileException or ArgumentException) { error = e.Message; }
            effective[id] = (fingerprint, error);
        }
        return variant.Suites.Select(id =>
        {
            var suite = suites.SingleOrDefault(s => s.Id == id);
            var region = id == TestSuiteId.LeagueOfLegends ? variant.Region : "";
            var rows = entries.Select(entry =>
            {
                var profileId = entry.Profile?.Id ?? Path.GetFileName(entry.FilePath).ToUpperInvariant();
                var key = ResultKeys.For(variant.Id, id, region, profileId);
                var result = results.GetValueOrDefault(key);
                var (fingerprint, error) = effective[profileId];
                var stale = result != null && (result.VariantId != variant.Id || result.VariantFingerprint != fingerprint || result.Fingerprint != fingerprint);
                return new ProfileView(profileId, entry.Profile?.DisplayName ?? Path.GetFileNameWithoutExtension(entry.FilePath),
                    error, fingerprint, states?.GetValueOrDefault(key) ?? RunState.Idle, result, stale,
                    key == mainKey, runErrors?.GetValueOrDefault(key), variant.Id);
            }).ToArray();
            return new SuiteView(id, region, suite?.Definition?.Targets ?? [], rows, configurationError, variant.Id);
        }).ToArray();
    }

    // Read-only: no snapshot creation, Helper, driver or networking is needed.
    public static IReadOnlyList<SuiteView> Load(string folder, ApplicationSettings settings, string resultsPath, VariantDescriptor? variant = null)
    {
        var profiles = new ProfileScanner().Scan(folder).ToArray();
        var fingerprints = new Dictionary<string, string>();
        using var hashes = new FingerprintContext();
        for (var i = 0; i < profiles.Length; i++)
        {
            if (profiles[i].Profile is not { } profile) continue;
            try { fingerprints[profile.Id] = variant == null ? FlowsealSnapshot.Fingerprint(profile, hashes) : FlowsealSnapshot.SourceFingerprint(profile, hashes); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { profiles[i] = profiles[i] with { Error = e.Message }; }
        }
        Dictionary<string, ProfileTestResult> results = [];
        if (File.Exists(resultsPath))
            results = ResultKeys.Migrate(JsonSerializer.Deserialize<Dictionary<string, ProfileTestResult>>(File.ReadAllText(resultsPath), JsonFiles.Options) ?? []);
        if (variant != null)
        {
            var selected = ServiceCatalog.Resolve(variant.Selections, settings);
            // Resolve an unspecified region once from the installation; all resulting keys use that region.
            var canonical = VariantDescriptor.Create(selected.Select(s => new SuiteSelection(s.Id, s.Region)));
            return BuildVariant(profiles, fingerprints, results, canonical, selected);
        }
        SuiteDefinition? league = null;
        string? leagueError = null;
        var region = settings.LeagueRegion ?? "";
        try
        {
            var installation = LeagueConfiguration.Load(settings.LeagueFolder, region);
            region = installation.SelectedRegion;
            league = installation.Regions[region];
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        { leagueError = e.Message; }
        return [Build(profiles, fingerprints, results, TestSuiteId.Discord, "", SuiteDefinition.Discord),
            Build(profiles, fingerprints, results, TestSuiteId.LeagueOfLegends, region, league, leagueError),
            Build(profiles, fingerprints, results, TestSuiteId.YouTube, "", SuiteDefinition.YouTube)];
    }
}
