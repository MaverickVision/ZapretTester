using System.Text.Json;

namespace ZapretTester.Core;

public enum RunState { Idle, Queued, Testing, Cancelled, Blocked }
public static class RunFailure
{
    public static RunState State(bool userCancelled) => userCancelled ? RunState.Cancelled : RunState.Blocked;
}
public enum EndpointStatus { Available, Failed, ServerWarning }
public enum ProfileStatus { Unknown, Success, Partial, Failed, Error }
public enum FailureStage { Dns, Tcp, Tls, Http, Timeout, Process, Isolation, Port }
public sealed record TestTarget(string Name, Uri Url, bool ServiceRoot, bool Require200 = false, bool StrictPublic = false, string Method = "HEAD")
{
    public bool Accepts(int code) => Require200 ? code == 200 : StrictPublic ? code is >= 200 and < 400 : code is >= 200 and < 400 or 403 || ServiceRoot && code is 401 or 404 or 405;
    public static IReadOnlyList<TestTarget> Defaults { get; } = [
        new("Website", new("https://discord.com"), false),
        new("Gateway HTTPS", new("https://gateway.discord.gg"), true),
        new("CDN", new("https://cdn.discordapp.com"), true),
        new("Update", new("https://updates.discord.com"), true)];
}
public sealed record EndpointResult(string Name, EndpointStatus Status, int LocalPort,
    string? RemoteIp, TimeSpan Duration, TimeSpan DnsDuration, int? HttpCode,
    FailureStage? FailureStage, string? Error);
public sealed record ProfileTestResult(string ProfileId, string Fingerprint, bool IsReliable,
    IReadOnlyList<EndpointResult> Endpoints, DateTimeOffset TestedAt, string? Error,
    TestSuiteId Suite = TestSuiteId.Discord, string Region = "", int ExpectedEndpoints = 4, bool PendingValidation = false,
    string? VariantId = null, string? VariantFingerprint = null, string? SeriesId = null)
{
    public ProfileStatus Status => PendingValidation ? ProfileStatus.Unknown : !IsReliable || Error != null ? ProfileStatus.Error
        : Endpoints.Count == 0 ? ProfileStatus.Unknown
        : Endpoints.Count == ExpectedEndpoints && Endpoints.All(e => e.Status == EndpointStatus.Available) ? ProfileStatus.Success
        : Endpoints.Any(e => e.Status != EndpointStatus.Failed) ? ProfileStatus.Partial : ProfileStatus.Failed;
    public double? MedianMilliseconds
    {
        get
        {
            var values = Endpoints.Where(e => e.Status == EndpointStatus.Available).Select(e => e.Duration.TotalMilliseconds).Order().ToArray();
            return values.Length == 0 ? null : (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2;
        }
    }
}
public sealed record ApplicationSettings
{
    public string FlowsealFolder { get; init; } = "";
    public int MaxParallelTests { get; init; } = 4;
    public int Attempts { get; init; } = 2;
    public int TimeoutSeconds { get; init; } = 5;
    public int FirstPort { get; init; } = 41000;
    public int LastPort { get; init; } = 42999;
    public bool AutoTest { get; init; }
    public bool KeepMainRunning { get; init; } = true;
    public string? MainProfileId { get; init; }
    public bool ManagedEnabled { get; init; }
    public string LeagueFolder { get; init; } = "";
    public string? LeagueRegion { get; init; }
    public TestSuiteId MainSuite { get; init; }
    public string MainRegion { get; init; } = "";
    public VariantDescriptor? MainVariant { get; init; }
    public string? MainVariantFingerprint { get; init; }
    public bool MainVariantVerified { get; init; }
    public void Validate()
    {
        if (MaxParallelTests is < 1 or > 8 || Attempts is < 1 or > 5 || TimeoutSeconds is < 1 or > 60
            || FirstPort < 1024 || LastPort > 65535 || LastPort - FirstPort + 1 < MaxParallelTests * Attempts)
            throw new ArgumentException("Invalid settings or insufficient port pool.");
    }
}
public static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
