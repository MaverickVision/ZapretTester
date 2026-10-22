using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZapretTester.Core;

public sealed record VariantDescriptor(IReadOnlyList<TestSuiteId> Suites, string Region = "", int AdapterVersion = 1, string Mode = "combined")
{
    [JsonIgnore] public string Id => Hash(JsonSerializer.Serialize(new
    { Suites = Suites.Order().ToArray(), Region = Suites.Contains(TestSuiteId.LeagueOfLegends) ? (Region ?? "").ToUpperInvariant() : "", AdapterVersion, Mode }));
    [JsonIgnore] public IReadOnlyList<SuiteSelection> Selections => Suites.Order().Select(s => new SuiteSelection(s, s == TestSuiteId.LeagueOfLegends ? Region : "")).ToArray();
    public static VariantDescriptor Create(IEnumerable<SuiteSelection> selections, string? fallbackRegion = null)
    {
        var items = selections.ToArray();
        if (items.Length > 6 || items.Any(s => !Enum.IsDefined(s.Suite)) || items.Select(s => s.Suite).Distinct().Count() != items.Length)
            throw new ArgumentException("Некорректный набор сервисов.");
        var league = items.SingleOrDefault(s => s.Suite == TestSuiteId.LeagueOfLegends);
        return new(Array.AsReadOnly(items.Select(s => s.Suite).Order().ToArray()),
            league == null ? "" : (league.Region ?? fallbackRegion ?? "").ToUpperInvariant());
    }
    public VariantDescriptor Validate(bool allowEmpty = false)
    {
        if (Mode != "combined" || AdapterVersion != 1 || Suites == null || (!allowEmpty && Suites.Count == 0))
            throw new ArgumentException("Неподдерживаемый вариант проверки.");
        var canonical = Create(Selections);
        if (canonical.Region.Any(c => !char.IsAsciiLetterOrDigit(c))) throw new ArgumentException("Некорректный регион LoL.");
        return canonical;
    }
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

public static class VariantAdapter
{
    public static IReadOnlyList<string> ExtraHosts(IEnumerable<SuiteDefinition> suites) => suites
        .Where(s => s.Id is TestSuiteId.LeagueOfLegends or TestSuiteId.Steam or TestSuiteId.Twitch or TestSuiteId.Cloudflare)
        .SelectMany(s => s.Hosts).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();

    public static IReadOnlyList<string> Adapt(IReadOnlyList<string> original, IReadOnlyList<SuiteDefinition> suites)
    {
        var hosts = ExtraHosts(suites);
        return hosts.Count == 0 ? original : LeagueAdapter.AdaptHosts(original, hosts);
    }

    public static string Fingerprint(ZapretProfile profile, string baseFingerprint, VariantDescriptor variant,
        IReadOnlyList<SuiteDefinition> suites)
    {
        ValidateDefinitions(variant, suites);
        var root = Path.GetDirectoryName(Path.GetFullPath(profile.BatFilePath))!;
        string Canonical(string argument)
        {
            var eq = argument.IndexOf('=');
            if (eq < 0 || !ProfileParser.FileOptions.Contains(argument[..eq])) return argument;
            var path = argument[(eq + 1)..];
            if (path == "!" || path.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return argument;
            return argument[..(eq + 1)] + "{bundle}/" + Path.GetRelativePath(root, path).Replace('\\', '/');
        }
        return VariantDescriptor.Hash(JsonSerializer.Serialize(new
        {
            Base = baseFingerprint, Variant = variant.Id,
            Arguments = Adapt(profile.Arguments, suites).Select(Canonical).ToArray(), Hosts = ExtraHosts(suites),
            Services = suites.OrderBy(s => s.Id).Select(s => new
            { s.Id, s.Region, Targets = s.Targets.Select(t => new { t.Name, t.Url, t.Method, t.ServiceRoot, t.Require200, t.StrictPublic }) })
        }));
    }

    public static SnapshotProfile Apply(SnapshotProfile snapshot, VariantDescriptor variant, IReadOnlyList<SuiteDefinition> suites) => snapshot with
    { Arguments = Adapt(snapshot.Arguments, suites), Fingerprint = Fingerprint(snapshot.Source, snapshot.SourceFingerprint ?? snapshot.Fingerprint, variant, suites) };

    public static void ValidateDefinitions(VariantDescriptor variant, IReadOnlyList<SuiteDefinition> suites)
    {
        variant.Validate();
        if (!variant.Suites.Order().SequenceEqual(suites.Select(s => s.Id).Order())
            || suites.Any(s => s.Id == TestSuiteId.LeagueOfLegends && s.Region != variant.Region))
            throw new ArgumentException("Конфигурация не соответствует выбранному варианту.");
    }
}
