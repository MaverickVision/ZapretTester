namespace ZapretTester.Core;

public sealed record HelperRequest(string Command, string Nonce, string? ProfileId = null, ApplicationSettings? Settings = null, string? RequestId = null,
    TestSuiteId Suite = TestSuiteId.Discord, string? Region = null, IReadOnlyList<string>? ProfileIds = null, IReadOnlyList<SuiteSelection>? TestSuites = null,
    VariantDescriptor? Variant = null);
public sealed record ProfileView(string Id, string Name, string? Error, string? Fingerprint,
    RunState RunState, ProfileTestResult? LastResult, bool IsStale, bool IsMain, string? RunError = null, string? VariantId = null);
public sealed record HelperState(string FlowsealFolder, string Mode, string? MainProfileId, bool IsTesting,
    ApplicationSettings Settings, IReadOnlyList<ProfileView> Profiles, string? Error, string Diagnostics,
    IReadOnlyList<string>? ExternalProfileMatches = null, TestSuiteId Suite = TestSuiteId.Discord, string Region = "",
    TestSuiteId? ActiveSuite = null, string? ActiveRegion = null, IReadOnlyList<TestTarget>? Targets = null,
    IReadOnlyList<string>? LeagueRegions = null, int ProtocolVersion = 1, IReadOnlyList<SuiteView>? Suites = null, IReadOnlyList<string>? ActiveProfileIds = null, SeriesProgress? Series = null,
    VariantDescriptor? ViewVariant = null, VariantDescriptor? ActiveVariant = null, VariantDescriptor? MainVariant = null,
    long StateRevision = 0, bool MainVariantVerified = false);
public sealed record HelperMessage(string Kind, HelperState? State = null, string? Error = null, string? RequestId = null);
public sealed record HelperSession(int Pid, long StartTicks, string Pipe, string Nonce, string Folder);

public sealed record SuiteView(TestSuiteId Suite, string Region, IReadOnlyList<TestTarget> Targets, IReadOnlyList<ProfileView> Profiles, string? Error = null, string? VariantId = null);

public sealed record SuiteSelection(TestSuiteId Suite, string? Region = null);
public sealed record RunParticipant(TestSuiteId Suite, string Region, string ProfileId, string? VariantId = null)
{
    public string Key => ResultKeys.For(VariantId, Suite, Region, ProfileId);
}
public sealed record SeriesProgress(string RunId, IReadOnlyList<RunParticipant> Participants, int Completed, int Total,
    int ProcessesStarted, int ActiveProcesses, int PeakProcesses, int ActiveRequests, int PeakRequests, double ElapsedSeconds,
    SeriesPhase Phase = SeriesPhase.Completed, SeriesTimings? Timings = null, int ExportProcessesStarted = 0, int SnapshotsPrepared = 0,
    VariantDescriptor? Variant = null);
public enum SeriesPhase { Preparing, Testing, Finalizing, Completed }
// Cumulative operation durations; parallel operations overlap and do not sum to elapsed wall time.
public sealed record SeriesTimings(double DiscoverySeconds = 0, double SnapshotSeconds = 0, double FilterExportSeconds = 0,
    double ReadinessSeconds = 0, double NetworkSeconds = 0, double SaveSeconds = 0, double CleanupSeconds = 0);
