using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZapretTester.Core;

public enum TestSuiteId { Discord, LeagueOfLegends, YouTube }
public sealed record SuiteDefinition(TestSuiteId Id, string Region, IReadOnlyList<TestTarget> Targets, IReadOnlyList<string> Hosts)
{
    public string Key(string profileId) => ResultKeys.For(Id, Region, profileId);
    public string Fingerprint(string original) => Id == TestSuiteId.Discord ? original
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original + (Id == TestSuiteId.LeagueOfLegends ? "\nLoLAdapter1\n" : "\nYouTube1\n") + FingerprintJson())));
    // Keep existing Discord/LoL fingerprints stable when extending target policy.
    private string FingerprintJson() => Id == TestSuiteId.LeagueOfLegends
        ? JsonSerializer.Serialize(new { Id, Region, Targets = Targets.Select(t => new { t.Name, t.Url, t.ServiceRoot, t.Require200 }), Hosts })
        : JsonSerializer.Serialize(this);
    public static SuiteDefinition YouTube { get; } = new(TestSuiteId.YouTube, "", [
        new("Сайт", new("https://www.youtube.com/"), false, StrictPublic: true),
        new("Короткие ссылки", new("https://youtu.be/"), false, StrictPublic: true),
        new("Изображения", new("https://i.ytimg.com/"), true),
        new("Перенаправление видео", new("https://redirector.googlevideo.com/"), true)], []);
    public static SuiteDefinition Discord { get; } = new(TestSuiteId.Discord, "", TestTarget.Defaults, []);
}
public static class ResultKeys
{
    public static string For(TestSuiteId suite, string? region, string profileId) => $"{suite}|{(suite != TestSuiteId.LeagueOfLegends ? "" : region)}|{profileId}";
    public static Dictionary<string, ProfileTestResult> Migrate(IReadOnlyDictionary<string, ProfileTestResult> saved) =>
        saved.Values.ToDictionary(r => For(r.Suite, r.Region, r.ProfileId), r => r);
}

public sealed record LeagueInstallation(string Folder, string SelectedRegion, IReadOnlyDictionary<string, SuiteDefinition> Regions);
public static class LeagueConfiguration
{
    private static readonly HashSet<string> AllowedFields = new(StringComparer.Ordinal)
    {
        "client_config_url", "entitlements_url", "league_edge_url", "player_platform_edge_url", "rms_url",
        "payments_host", "auth_token_uri", "external_url"
    };
    public static string DiscoverFolder()
    {
        var manifest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games", "RiotClientInstalls.json");
        if (!File.Exists(manifest)) return "";
        using var json = JsonDocument.Parse(File.ReadAllText(manifest));
        if (json.RootElement.TryGetProperty("associated_client", out var paths))
            foreach (var entry in paths.EnumerateObject())
                if (File.Exists(Path.Combine(entry.Name, "system.yaml"))) return Path.GetFullPath(entry.Name);
        return "";
    }
    public static LeagueInstallation Load(string? folder, string? selectedRegion = null)
    {
        folder = string.IsNullOrWhiteSpace(folder) ? DiscoverFolder() : Path.GetFullPath(folder);
        if (string.IsNullOrWhiteSpace(folder)) throw new IOException("Укажите папку League of Legends в настройках.");
        var system = Path.Combine(folder, "system.yaml");
        FlowsealSnapshot.RejectReparse(system);
        var savedRegion = "RU";
        var local = Path.Combine(folder, "Config", "LeagueClientSettings.yaml");
        if (File.Exists(local))
        {
            FlowsealSnapshot.RejectReparse(local);
            foreach (var line in File.ReadLines(local))
            {
                var match = Regex.Match(line, "^\\s*region:\\s*[\"']?([A-Z0-9]+)[\"']?\\s*$");
                if (match.Success) savedRegion = match.Groups[1].Value;
            }
        }
        return Parse(folder, File.ReadLines(system), selectedRegion ?? savedRegion);
    }
    public static LeagueInstallation Parse(string folder, IEnumerable<string> lines, string selectedRegion)
    {
        var regions = new Dictionary<string, Dictionary<string, Uri>>(StringComparer.Ordinal);
        Dictionary<string, Uri>? current = null;
        bool inRegions = false, patcher = false;
        Uri? cdn = null;
        foreach (var line in lines)
        {
            if (line.Length > 16384) throw new IOException("Некорректная конфигурация League of Legends.");
            if (line == "patcher:") patcher = true;
            else if (line.Length > 0 && !char.IsWhiteSpace(line[0])) patcher = false;
            if (patcher && line.StartsWith("  channel: ", StringComparison.Ordinal)) cdn = ValidUri(line[11..]);
            if (line == "region_data:") { inRegions = true; continue; }
            if (!inRegions) continue;
            if (line.Length > 0 && !char.IsWhiteSpace(line[0])) { inRegions = false; current = null; continue; }
            var region = Regex.Match(line, "^  ([A-Z0-9]+):$");
            if (region.Success) { current = new(StringComparer.Ordinal); regions.Add(region.Groups[1].Value, current); continue; }
            // Only explicit endpoint keys are consumed. Tokens and account data are never parsed or retained.
            var field = Regex.Match(line, "^        ([a-z_]+): (.+)$");
            if (current != null && field.Success && AllowedFields.Contains(field.Groups[1].Value))
                current[field.Groups[1].Value] = ValidUri(field.Groups[2].Value);
        }
        if (cdn == null) throw new IOException("Не найден адрес CDN в system.yaml.");
        var definitions = new Dictionary<string, SuiteDefinition>(StringComparer.Ordinal);
        foreach (var (name, fields) in regions)
        {
            if (!fields.TryGetValue("league_edge_url", out var platform) || !fields.TryGetValue("client_config_url", out var config)) continue;
            var auth = new Uri("https://auth.riotgames.com/.well-known/openid-configuration");
            TestTarget[] targets = [new("Авторизация Riot", auth, false, true),
                new("Конфигурация клиента", new Uri(config, "/api/v1/config/public"), false, true),
                new("Платформа " + name, new Uri(platform, "/"), true), new("CDN обновлений", cdn, true)];
            var hosts = fields.Values.Select(u => u.DnsSafeHost).Concat(targets.Select(t => t.Url.DnsSafeHost))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
            definitions[name] = new(TestSuiteId.LeagueOfLegends, name, targets, hosts);
        }
        if (!definitions.ContainsKey(selectedRegion)) throw new IOException("Регион отсутствует в конфигурации LoL: " + selectedRegion);
        return new(folder, selectedRegion, definitions);
    }
    private static Uri ValidUri(string text)
    {
        text = text.Trim().Trim('"', '\'');
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "wss")
            || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !new[] { "riotgames.com", "riotcdn.net", "pvp.net", "leagueoflegends.com" }.Any(d =>
                uri.DnsSafeHost.Equals(d, StringComparison.OrdinalIgnoreCase) || uri.DnsSafeHost.EndsWith("." + d, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Недопустимый адрес службы в конфигурации LoL.");
        return uri;
    }
}

public static class LeagueAdapter
{
    public static IReadOnlyList<string> Adapt(IReadOnlyList<string> original, SuiteDefinition suite)
    {
        if (suite.Id != TestSuiteId.LeagueOfLegends) return original;
        var globals = original.Where(a => a.StartsWith("--wf-", StringComparison.Ordinal)).ToArray();
        if (!globals.Any(a => a.StartsWith("--wf-tcp=", StringComparison.Ordinal) && Contains443(a[(a.IndexOf('=') + 1)..])))
            throw new UnsupportedProfileException("Адаптация не поддерживается: capture не включает TCP 443.");
        var body = original.Where(a => !a.StartsWith("--wf-", StringComparison.Ordinal)).ToArray();
        var groups = new List<List<string>> { new() };
        foreach (var argument in body) { if (argument == "--new") groups.Add([]); else groups[^1].Add(argument); }
        bool Https(List<string> g) => g.Any(a => a.StartsWith("--filter-tcp=", StringComparison.Ordinal) && Contains443(a[13..]));
        var candidates = groups.Where(g => Https(g) && g.Any(a => a.StartsWith("--hostlist=", StringComparison.Ordinal)
            && Path.GetFileName(a[11..]).Equals("list-general.txt", StringComparison.OrdinalIgnoreCase))).ToArray();
        if (candidates.Length == 0)
            candidates = groups.Where(g => Https(g) && g.Contains("--filter-l3=ipv4")
                && g.All(a => !a.StartsWith("--hostlist", StringComparison.Ordinal) && !a.StartsWith("--ipset=", StringComparison.Ordinal))).ToArray();
        if (candidates.Length != 1) throw new UnsupportedProfileException("Адаптация не поддерживается: неоднозначная HTTPS-группа.");
        var cloned = candidates[0].Where(a => !a.StartsWith("--hostlist", StringComparison.Ordinal)
            && !a.StartsWith("--ipset", StringComparison.Ordinal) && !a.StartsWith("--filter-tcp=", StringComparison.Ordinal)).ToArray();
        return globals.Concat(new[] { "--filter-tcp=443", "--hostlist-domains=" + string.Join(',', suite.Hosts) })
            .Concat(cloned).Append("--new").Concat(body).ToArray();
    }
    private static bool Contains443(string ports) => ports.Split(',').Any(p => p == "443" ||
        (p.Split('-') is [var first, var last] && int.TryParse(first, out var from) && int.TryParse(last, out var to) && from <= 443 && to >= 443));
    public static SnapshotProfile Apply(SnapshotProfile snapshot, SuiteDefinition suite) => snapshot with
    { Arguments = Adapt(snapshot.Arguments, suite), Fingerprint = suite.Fingerprint(snapshot.Fingerprint) };
}
