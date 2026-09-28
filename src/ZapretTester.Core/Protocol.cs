namespace ZapretTester.Core;

public sealed record HelperRequest(string Command, string Nonce, string? ProfileId = null, ApplicationSettings? Settings = null, string? RequestId = null,
    TestSuiteId Suite = TestSuiteId.Discord, string? Region = null, IReadOnlyList<string>? ProfileIds = null, IReadOnlyList<SuiteSelection>? TestSuites = null);
public sealed record ProfileView(string Id, string Name, string? Error, string? Fingerprint,
    RunState RunState, ProfileTestResult? LastResult, bool IsStale, bool IsMain, string? RunError = null);
public sealed record HelperState(string FlowsealFolder, string Mode, string? MainProfileId, bool IsTesting,
    ApplicationSettings Settings, IReadOnlyList<ProfileView> Profiles, string? Error, string Diagnostics,
    IReadOnlyList<string>? ExternalProfileMatches = null, TestSuiteId Suite = TestSuiteId.Discord, string Region = "",
    TestSuiteId? ActiveSuite = null, string? ActiveRegion = null, IReadOnlyList<TestTarget>? Targets = null,
    IReadOnlyList<string>? LeagueRegions = null, int ProtocolVersion = 1, IReadOnlyList<SuiteView>? Suites = null, IReadOnlyList<string>? ActiveProfileIds = null, SeriesProgress? Series = null);
public sealed record HelperMessage(string Kind, HelperState? State = null, string? Error = null, string? RequestId = null);
public sealed record HelperSession(int Pid, long StartTicks, string Pipe, string Nonce, string Folder);

public sealed record SuiteView(TestSuiteId Suite, string Region, IReadOnlyList<TestTarget> Targets, IReadOnlyList<ProfileView> Profiles, string? Error = null);

public sealed record SuiteSelection(TestSuiteId Suite, string? Region = null);
public sealed record RunParticipant(TestSuiteId Suite, string Region, string ProfileId)
{
    public string Key => ResultKeys.For(Suite, Region, ProfileId);
}
public sealed record SeriesProgress(string RunId, IReadOnlyList<RunParticipant> Participants, int Completed, int Total,
    int ProcessesStarted, int ActiveProcesses, int PeakProcesses, int ActiveRequests, int PeakRequests, double ElapsedSeconds);
