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

    // Read-only: no snapshot creation, Helper, driver or networking is needed.
    public static IReadOnlyList<SuiteView> Load(string folder, ApplicationSettings settings, string resultsPath)
    {
        var profiles = new ProfileScanner().Scan(folder).ToArray();
        var fingerprints = new Dictionary<string, string>();
        for (var i = 0; i < profiles.Length; i++)
        {
            if (profiles[i].Profile is not { } profile) continue;
            try { fingerprints[profile.Id] = FlowsealSnapshot.Fingerprint(profile); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { profiles[i] = profiles[i] with { Error = e.Message }; }
        }
        Dictionary<string, ProfileTestResult> results = [];
        if (File.Exists(resultsPath))
            results = ResultKeys.Migrate(JsonSerializer.Deserialize<Dictionary<string, ProfileTestResult>>(File.ReadAllText(resultsPath), JsonFiles.Options) ?? []);
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
