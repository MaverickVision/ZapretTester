using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ZapretTester.Core;
using ZapretTester.Windows;

namespace ZapretTester.Helper;

public sealed class TesterRuntime : IAsyncDisposable
{
    private readonly string folder, protectedRoot, dataRoot;
    private readonly Dictionary<string, string> trustedBinaries;
    private readonly object gate = new();
    private List<ProfileEntry> profiles = [];
    private readonly Dictionary<string, string> fingerprints = [];
    private readonly Dictionary<string, ProfileTestResult> results = [];
    private readonly Dictionary<string, RunState> states = [];
    private ApplicationSettings settings;
    private OwnedProcess? main;
    private SnapshotProfile? mainSnapshot;
    private string? mainFilter;
    private ServiceSnapshot? originalService;
    private CancellationTokenSource? runCancellation;
    private Task runTask = Task.CompletedTask;
    private string? lastError;
    private bool testing;
    private TestSuiteId viewId;
    private SuiteDefinition? viewSuite = SuiteDefinition.Discord;
    private SuiteDefinition? activeSuite;
    private string? viewError;
    private SuiteDefinition? dashboardLeague;
    private string? dashboardLeagueError;
    private string[] activeProfileIds = [];
    private string? runId;
    private IReadOnlyList<RunParticipant> runParticipants = [];
    private int runCompleted, processesStarted, activeProcesses, peakProcesses, activeRequests, peakRequests;
    private readonly Stopwatch runClock = new();
    private void RefreshDashboardLeague()
    {
        try { dashboardLeague = ResolveSuite(TestSuiteId.LeagueOfLegends); dashboardLeagueError = null; }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        { dashboardLeague = null; dashboardLeagueError = e.Message; }
    }
    private readonly Dictionary<string, string> runErrors = [];
    private string[] leagueRegions = [];
    public void SelectSuite(TestSuiteId id, string? region = null)
    {
        lock (gate)
        {
            viewId = id;
            try { viewSuite = ResolveSuite(id, region); viewError = null; }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
            { viewSuite = null; viewError = e.Message; }
        }
        Publish();
    }
    private SuiteDefinition ResolveSuite(TestSuiteId id, string? region = null)
    {
        if (id == TestSuiteId.Discord) return SuiteDefinition.Discord;
        if (id == TestSuiteId.YouTube) return SuiteDefinition.YouTube;
        if (id != TestSuiteId.LeagueOfLegends) throw new ArgumentException("Unknown test suite.");
        var installation = LeagueConfiguration.Load(settings.LeagueFolder, region ?? settings.LeagueRegion);
        leagueRegions = installation.Regions.Keys.Order().ToArray();
        settings = settings with { LeagueFolder = installation.Folder, LeagueRegion = installation.SelectedRegion };
        dashboardLeagueError = null;
        return dashboardLeague = installation.Regions[installation.SelectedRegion];
    }
    private string networkDiagnostics = "Пул ещё не проверен.";
    private string[] externalMatches = [];
    public event Action<HelperState>? Changed;
#if DEBUG
    public bool FailNextMainForAcceptance { get; set; }
    public bool FailSharedProcessForAcceptance { get; set; }
#endif

    public TesterRuntime(string folder, string protectedRoot, string dataRoot)
    {
        this.folder = Path.GetFullPath(folder); this.protectedRoot = protectedRoot; this.dataRoot = dataRoot;
        var certificate = JsonSerializer.Deserialize<Certificate>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "isolation-certificate.json")), JsonFiles.Options)
            ?? throw new InvalidOperationException("Missing isolation certificate.");
        trustedBinaries = certificate.Binaries;
        ValidateBinaries(Path.Combine(folder, "bin"));
        Directory.CreateDirectory(dataRoot);
        settings = ReadJson<ApplicationSettings>(Path.Combine(dataRoot, "settings.json")) ?? new ApplicationSettings();
        settings = settings with { FlowsealFolder = this.folder };
        settings.Validate();
        var saved = ReadJson<Dictionary<string, ProfileTestResult>>(Path.Combine(dataRoot, "results.json"));
        if (saved != null) foreach (var pair in ResultKeys.Migrate(saved)) results[pair.Key] = pair.Value;
        originalService = ReadJson<ServiceSnapshot>(Path.Combine(protectedRoot, "original-service.json"));
        Refresh();
        var first = profiles.FirstOrDefault(p => p.Profile != null && p.Error == null)?.Profile ?? throw new InvalidOperationException("No supported profiles.");
        var bootstrap = Prepare(first);
        WinDivert.Load(Path.Combine(bootstrap.WorkingDirectory, "WinDivert.dll"));
    }
    public ApplicationSettings Settings => settings;
    public void ReportStartupFailure(Exception error) { lastError = "Не удалось возобновить основной Zapret: " + error.Message; Log(lastError); }
    public bool HasMain => main is { HasExited: false };
    private static T? ReadJson<T>(string path)
    {
        if (!File.Exists(path)) return default;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonFiles.Options); }
        catch (JsonException) { return default; }
    }
    private void ValidateBinaries(string directory)
    {
        foreach (var pair in trustedBinaries)
        {
            var path = Path.Combine(directory, pair.Key);
            FlowsealSnapshot.RejectReparse(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(pair.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Engine differs from the validated bundle: " + pair.Key + ". Run TestIsolation before supporting this engine.");
        }
    }
    private SnapshotProfile Prepare(ZapretProfile profile, SuiteDefinition? suite = null) => LeagueAdapter.Apply(PrepareBase(profile), suite ?? SuiteDefinition.Discord);
    private SnapshotProfile PrepareBase(ZapretProfile profile)
    {
        ValidateBinaries(Path.GetDirectoryName(profile.WinwsPath)!);
        var snapshot = FlowsealSnapshot.Create(profile, Path.Combine(protectedRoot, "runs", Guid.NewGuid().ToString("N")));
        ValidateBinaries(snapshot.WorkingDirectory);
        return snapshot;
    }
    public void Refresh()
    {
        lock (gate)
        {
            if (testing) throw new InvalidOperationException("Stop the current run before refreshing profiles.");
            profiles = new ProfileScanner().Scan(folder).ToList();
            fingerprints.Clear();
            RefreshDashboardLeague();
            for (var i = 0; i < profiles.Count; i++)
            {
                if (profiles[i].Profile is not { } p) continue;
                try { fingerprints[p.Id] = FlowsealSnapshot.Fingerprint(p); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { profiles[i] = profiles[i] with { Error = e.Message }; }
            }
            try { externalMatches = ProfileParser.MatchService(ZapretService.Read().ImagePath,
                profiles.Where(p => p.Profile != null && p.Error == null).Select(p => p.Profile!)).Select(p => p.Id).ToArray(); }
            catch (InvalidOperationException) { externalMatches = []; }
        }
        Publish();
    }
    private ZapretProfile Profile(string? id) => profiles.SingleOrDefault(p => p.Profile?.Id == id && p.Error == null)?.Profile
        ?? throw new InvalidOperationException("Select a supported profile.");
    public void UpdateSettings(ApplicationSettings value)
    {
        value.Validate();
        if (testing) throw new InvalidOperationException("Stop testing before changing settings.");
        if (!Path.GetFullPath(value.FlowsealFolder).Equals(folder, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Reconnect the helper to change Flowseal directory.");
        if (HasMain && (value.FirstPort != settings.FirstPort || value.LastPort != settings.LastPort))
            throw new InvalidOperationException("Stop managed Zapret before changing its excluded port pool.");
        // Operational ownership flags are never accepted from an IPC settings payload.
        settings = value with { ManagedEnabled = settings.ManagedEnabled, MainProfileId = settings.MainProfileId, MainSuite = settings.MainSuite, MainRegion = settings.MainRegion };
        RefreshDashboardLeague(); SaveSettings(); SelectSuite(viewId);
    }
    public async Task StartManagedAsync(string? id, TestSuiteId suiteId = TestSuiteId.Discord, string? region = null)
    {
        await CancelAsync();
        networkDiagnostics = await NetworkEnvironment.ValidatePoolAsync(settings.FirstPort, settings.LastPort);
        var profile = Profile(id);
        var suite = ResolveSuite(suiteId, region);
        var snapshot = Prepare(profile, suite);
        var raw = await FilterCompiler.ExportAsync(snapshot, Path.Combine(snapshot.WorkingDirectory, "original.filter"), CancellationToken.None);
        var filterPath = Path.Combine(snapshot.WorkingDirectory, "main.filter");
        File.WriteAllText(filterPath, IsolationFilter.Main(raw, settings.FirstPort, settings.LastPort));
        WinDivert.Compile(File.ReadAllText(filterPath));
        var previous = mainSnapshot;
        var previousFilter = mainFilter;
        var previousSettings = settings;
        var changedExternal = false;
        await using var monitor = new ReflectMonitor();
        if (HasMain) monitor.RegisterOwned(main!);
        await monitor.SynchronizeAsync(CancellationToken.None);
        try
        {
            if (originalService == null)
            {
                try
                {
                    var service = ZapretService.Read();
                    var command = ProfileParser.Tokenize(service.ImagePath);
                    if (!Path.GetFullPath(command[0]).Equals(Path.Combine(folder, "bin", "winws.exe"), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The external zapret service belongs to another bundle.");
                    originalService = service;
                    JsonFiles.Save(Path.Combine(protectedRoot, "original-service.json"), originalService);
                }
                catch (InvalidOperationException e) when (e.Message == "The zapret service does not exist.") { }
            }
            if (originalService != null)
            {
                ServiceSnapshot? current = null;
                try { current = ZapretService.Read(); }
                catch (InvalidOperationException e) when (e.Message == "The zapret service does not exist.") { }
                // A retained backup does not mean the external service still exists.
                // Keep the backup for diagnostics; never recreate a deleted service.
                if (current != null)
                {
                    if (current.ImagePath != originalService.ImagePath) throw new InvalidOperationException("External service configuration changed; refusing to overwrite it.");
                    changedExternal = !settings.ManagedEnabled;
                    JsonFiles.Save(Path.Combine(protectedRoot, "transition.json"), new { State = "TakingOwnership", originalService });
                    await ZapretService.StopAsync(CancellationToken.None);
                    ZapretService.SetStartMode(4);
                }
            }
            if (main != null) { await main.DisposeAsync(); main = null; }
            await monitor.WaitForNoForeignAsync(CancellationToken.None);
            monitor.Enforce();
#if DEBUG
            if (FailNextMainForAcceptance) { FailNextMainForAcceptance = false; throw new IOException("Injected main startup failure."); }
#endif
            main = await LaunchAsync(snapshot, filterPath, monitor, CancellationToken.None);
            mainSnapshot = snapshot; mainFilter = filterPath;
            settings = settings with { ManagedEnabled = true, MainProfileId = profile.Id, MainSuite = suite.Id, MainRegion = suite.Region };
            SaveSettings();
            JsonFiles.Save(Path.Combine(protectedRoot, "transition.json"), new { State = "Managed", Profile = profile.Id });
            Log("Main started: " + profile.DisplayName + ", PID " + main.Id);
            lastError = null;
        }
        catch
        {
            if (main != null) { await main.DisposeAsync(); main = null; }
            if (previous != null && previousFilter != null)
            {
                await using var rollbackMonitor = new ReflectMonitor();
                await rollbackMonitor.SynchronizeAsync(CancellationToken.None);
                await rollbackMonitor.WaitForNoForeignAsync(CancellationToken.None);
                rollbackMonitor.Enforce();
                main = await LaunchAsync(previous, previousFilter, rollbackMonitor, CancellationToken.None);
                mainSnapshot = previous; mainFilter = previousFilter;
                settings = previousSettings; SaveSettings();
            }
            else if (changedExternal && originalService != null)
            {
                ZapretService.SetStartMode(originalService.StartMode);
                if (originalService.WasRunning) await ZapretService.StartAsync(CancellationToken.None);
            }
            throw;
        }
        finally { Publish(); }
    }
    private static async Task<OwnedProcess> LaunchAsync(SnapshotProfile snapshot, string filterPath, ReflectMonitor monitor, CancellationToken token)
    {
        var process = monitor.StartOwned(() => OwnedProcess.Start(snapshot.Executable, snapshot.WorkingDirectory, FilterCompiler.IsolatedArguments(snapshot, filterPath)));
        try { await monitor.WaitCaptureAsync(process.Id, token); await process.WaitReadyAsync(token); return process; }
        catch (Exception e)
        {
            var details = process.Output;
            await process.DisposeAsync();
            throw new IOException("winws startup failed: " + e.Message + "\n" + details[..Math.Min(16000, details.Length)], e);
        }
    }
    public async Task StopMainAsync()
    {
        await CancelAsync();
        if (main != null) { await main.DisposeAsync(); main = null; }
        mainSnapshot = null; mainFilter = null;
        settings = settings with { ManagedEnabled = false };
        SaveSettings(); Publish();
    }
    public async Task RestoreExternalAsync()
    {
        if (originalService == null) throw new InvalidOperationException("No saved external service configuration.");
        if (ZapretService.Read().ImagePath != originalService.ImagePath) throw new InvalidOperationException("External service command changed; automatic restore is refused.");
        await StopMainAsync();
        ZapretService.SetStartMode(originalService.StartMode);
        if (originalService.WasRunning) await ZapretService.StartAsync(CancellationToken.None);
        File.Delete(Path.Combine(protectedRoot, "original-service.json"));
        File.Delete(Path.Combine(protectedRoot, "transition.json"));
        originalService = null;
        Log("Original service restored."); Publish();
    }
    public void StartTests(string? onlyId, TestSuiteId suiteId = TestSuiteId.Discord, string? region = null) =>
        StartBatch(onlyId == null ? profiles.Select(p => p.Profile?.Id ?? Path.GetFileName(p.FilePath).ToUpperInvariant()).ToArray() : [onlyId], [new(suiteId, region)]);

    public void StartBatch(IReadOnlyList<string> ids, IReadOnlyList<SuiteSelection> selections)
    {
        if (testing) throw new InvalidOperationException("A run is already active.");
        if (ids.Count == 0 || selections.Count == 0 || selections.Count > 3 || selections.Any(s => !Enum.IsDefined(s.Suite)))
            throw new ArgumentException("Р’С‹Р±РµСЂРёС‚Рµ СЃС‚СЂР°С‚РµРіРёРё Рё СЃРµСЂРІРёСЃС‹.");
        Refresh();
        var suites = selections.Select(s =>
        {
            try { var definition = ResolveSuite(s.Suite, s.Region); return new ResolvedSuite(s.Suite, definition.Region, definition, null); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            { return new ResolvedSuite(s.Suite, s.Suite == TestSuiteId.LeagueOfLegends ? s.Region ?? settings.LeagueRegion ?? "" : "", null, e.Message); }
        }).ToArray();
        var plan = BatchPlan.Create(profiles, ids, suites, settings.Attempts, settings.LastPort - settings.FirstPort + 1);
        runCancellation = new CancellationTokenSource();
        lock (gate)
        {
            testing = true; activeSuite = suites.Length == 1 ? suites[0].Definition : null;
            activeProfileIds = ids.ToArray(); lastError = null; runId = Guid.NewGuid().ToString("N");
            runParticipants = plan.Participants; runCompleted = plan.Blocked.Count;
            processesStarted = activeProcesses = peakProcesses = activeRequests = peakRequests = 0;
            runClock.Restart();
            foreach (var p in plan.Participants) { states[p.Key] = RunState.Queued; runErrors.Remove(p.Key); }
            foreach (var p in plan.Blocked) { states[p.Participant.Key] = RunState.Blocked; runErrors[p.Participant.Key] = p.Error; }
        }
        Publish();
        runTask = RunBatchAsync(plan, runCancellation.Token);
    }

    private async Task RunBatchAsync(BatchPlan plan, CancellationToken token)
    {
        var completed = new List<string>();
        ReflectMonitor? observer = null;
        Exception? jobFailure = null;
        try
        {
            if (plan.Jobs.Count == 0) return;
            networkDiagnostics = await NetworkEnvironment.ValidatePoolAsync(settings.FirstPort, settings.LastPort);
            await using var monitor = new ReflectMonitor(); observer = monitor;
            if (HasMain) monitor.RegisterOwned(main!);
            await monitor.SynchronizeAsync(token); monitor.Enforce();
            using var guarded = CancellationTokenSource.CreateLinkedTokenSource(token, monitor.ConflictToken);
            using var allocator = new PortAllocator(settings.FirstPort, settings.LastPort);
            using var slots = new SemaphoreSlim(settings.MaxParallelTests);
            await Task.WhenAll(plan.Jobs.Select(async job =>
            {
                await slots.WaitAsync(guarded.Token);
                try
                {
                    lock (gate) foreach (var suite in job.Suites) states[suite.Key(job.Profile.Id)] = RunState.Testing;
                    Publish();
                    var jobResults = await TestJobAsync(job, allocator, monitor, guarded.Token);
                    lock (gate)
                    {
                        foreach (var result in jobResults)
                        {
                            var key = ResultKeys.For(result.Suite, result.Region, result.ProfileId);
                            results[key] = result with { PendingValidation = true }; completed.Add(key);
                            states[key] = RunState.Idle; runCompleted++;
                        }
                        SaveResults();
                    }
                    Publish();
                }
                catch (Exception e)
                {
                    if (e is not OperationCanceledException) { lock (gate) jobFailure ??= e; }
                    guarded.Cancel();
                    throw;
                }
                finally { slots.Release(); }
            }));
            await monitor.DisposeAsync();
            token.ThrowIfCancellationRequested();
            if (monitor.Error != null) throw new InvalidOperationException(monitor.Error);
            lock (gate)
            {
                foreach (var id in completed) results[id] = results[id] with { PendingValidation = false };
                SaveResults();
            }
        }
        catch (Exception e)
        {
            lock (gate)
            {
                lastError = token.IsCancellationRequested ? "РџСЂРѕРІРµСЂРєР° РѕС‚РјРµРЅРµРЅР°." : observer?.Error ?? jobFailure?.Message ?? e.Message;
                foreach (var id in completed) results[id] = results[id] with { IsReliable = false, Error = lastError, PendingValidation = false };
                foreach (var p in plan.Participants)
                    if (states.GetValueOrDefault(p.Key) is RunState.Queued or RunState.Testing)
                    { states[p.Key] = RunFailure.State(token.IsCancellationRequested); runErrors[p.Key] = lastError; runCompleted++; }
                SaveResults();
            }
            Log("Run stopped: " + lastError);
        }
        finally
        {
            lock (gate) { testing = false; activeSuite = null; activeProfileIds = []; runClock.Stop(); }
            Log($"Series {runId}: {runCompleted}/{plan.Participants.Count}; processes {processesStarted}; peak processes {peakProcesses}; peak requests {peakRequests}; {runClock.Elapsed.TotalSeconds:F1}s");
            Publish();
        }
    }

    private sealed class PortPoolException(string message, Exception inner) : Exception(message, inner);
    private async Task<IReadOnlyList<ProfileTestResult>> TestJobAsync(BatchJob job, PortAllocator allocator, ReflectMonitor monitor, CancellationToken token)
    {
        var endpoints = job.Suites.ToDictionary(s => s.Id, s => new EndpointResult?[s.Targets.Count]);
        var leases = new List<PortLease>();
        OwnedProcess? process = null; SnapshotProfile? snapshot = null;
        var baseFingerprint = fingerprints[job.Profile.Id];
        bool countedProcess = false;
        IReadOnlyList<ProfileTestResult> Results(bool reliable, string? error) => job.Suites.Select(s =>
            new ProfileTestResult(job.Profile.Id, s.Fingerprint(baseFingerprint), reliable,
                endpoints[s.Id].Where(e => e != null).Select(e => e!).ToArray(), DateTimeOffset.UtcNow, error, s.Id, s.Region, s.Targets.Count)).ToArray();
        try
        {
            snapshot = PrepareBase(job.Profile); baseFingerprint = snapshot.Fingerprint;
            var effective = LeagueAdapter.Apply(snapshot, job.Suites[0]);
            if (job.Suites.Any(s => !LeagueAdapter.Adapt(snapshot.Arguments, s).SequenceEqual(effective.Arguments)))
                throw new InvalidOperationException("Shared job configurations differ.");
            var work = new List<(SuiteDefinition Suite, TestTarget Target, int Index, PortLease[] Attempts)>();
            foreach (var item in BatchPlan.InterleavedTargets(job))
            {
                var attempts = new PortLease[settings.Attempts];
                for (var i = 0; i < attempts.Length; i++)
                {
                    try { attempts[i] = allocator.Allocate(); leases.Add(attempts[i]); }
                    catch (InvalidOperationException e) { throw new PortPoolException(e.Message, e); }
                }
                work.Add((item.Suite, item.Target, item.Index, attempts));
            }
            var original = await FilterCompiler.ExportAsync(effective, Path.Combine(snapshot.WorkingDirectory, "original.filter"), token);
            var filter = Path.Combine(snapshot.WorkingDirectory, "test.filter");
            File.WriteAllText(filter, IsolationFilter.Candidate(original, leases.Select(l => l.Port)));
            WinDivert.Compile(File.ReadAllText(filter));
            lock (gate) { processesStarted++; activeProcesses++; peakProcesses = Math.Max(peakProcesses, activeProcesses); countedProcess = true; }
            process = await LaunchAsync(effective, filter, monitor, token);
#if DEBUG
            if (FailSharedProcessForAcceptance && job.Suites.Count > 1)
            {
                FailSharedProcessForAcceptance = false;
                // Only the process just launched and held by this job can be terminated.
                using var owned = Process.GetProcessById(process.Id);
                owned.Kill(); await owned.WaitForExitAsync(token);
            }
#endif
            Log($"Test {job.Profile.DisplayName} [{string.Join(',', job.Suites.Select(s => s.Id))}]: PID {process.Id}; ports {string.Join(',', leases.Select(l => l.Port))}");
            using var requests = new SemaphoreSlim(2);
            var tester = new ConnectivityTester();
            await Task.WhenAll(work.Select(async item =>
            {
                await requests.WaitAsync(token);
                try
                {
                    lock (gate) { activeRequests++; peakRequests = Math.Max(peakRequests, activeRequests); }
                    foreach (var lease in item.Attempts)
                    {
                        token.ThrowIfCancellationRequested();
                        if (process.HasExited) throw new IOException("winws exited during the test.");
                        var endpoint = await tester.TestAsync(item.Target, lease, TimeSpan.FromSeconds(settings.TimeoutSeconds), token);
                        endpoints[item.Suite.Id][item.Index] = endpoint;
                        if (endpoint.Status != EndpointStatus.Failed) break;
                    }
                }
                finally { lock (gate) activeRequests--; requests.Release(); }
            }));
            token.ThrowIfCancellationRequested();
            if (process.HasExited || monitor.Error != null) throw new IOException(monitor.Error ?? "winws exited during the test.");
            return Results(true, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (PortPoolException) { throw; }
        catch (Exception e) { return Results(false, e.Message); }
        finally
        {
            if (process != null) { var pid = process.Id; await process.DisposeAsync(); Log($"Stopped temporary PID {pid}\n{process.Output}"); }
            if (countedProcess) { lock (gate) activeProcesses--; }
            foreach (var lease in leases) lease.Dispose();
            if (snapshot != null)
            {
                var directory = Path.GetDirectoryName(snapshot.WorkingDirectory)!;
                var runs = Path.GetFullPath(Path.Combine(protectedRoot, "runs")) + Path.DirectorySeparatorChar;
                if (Path.GetFullPath(directory).StartsWith(runs, StringComparison.OrdinalIgnoreCase))
                    try { Directory.Delete(directory, true); } catch (IOException e) { Log("Snapshot cleanup deferred: " + e.Message); }
            }
        }
    }

    public async Task CancelAsync()
    {
        runCancellation?.Cancel(); await runTask;
        runCancellation?.Dispose(); runCancellation = null;
    }
    private void SetRunState(string id, RunState state) { lock (gate) states[id] = state; Publish(); }
    public HelperState State()
    {
        lock (gate)
        {
            var mainKey = HasMain ? ResultKeys.For(settings.MainSuite, settings.MainRegion, settings.MainProfileId!) : null;
            var suites = new[] {
                SuiteProjection.Build(profiles, fingerprints, results, TestSuiteId.Discord, "", SuiteDefinition.Discord,
                    states: states, runErrors: runErrors, mainKey: mainKey),
                SuiteProjection.Build(profiles, fingerprints, results, TestSuiteId.LeagueOfLegends,
                    dashboardLeague?.Region ?? settings.LeagueRegion ?? "", dashboardLeague, dashboardLeagueError, states, runErrors, mainKey),
                SuiteProjection.Build(profiles, fingerprints, results, TestSuiteId.YouTube, "", SuiteDefinition.YouTube, states: states, runErrors: runErrors, mainKey: mainKey)
            };
            var selected = suites.Single(s => s.Suite == viewId);
            var rows = selected.Profiles;
            return new(folder, HasMain ? "Managed" : "Compatibility", HasMain ? settings.MainProfileId : null, testing, settings, rows, selected.Error ?? lastError,
                $"Профили: {rows.Count}\nДвижок: проверенный набор бинарников (контроль SHA-256)\nПул: {settings.FirstPort}–{settings.LastPort}\nMain PID: {(HasMain ? main!.Id : 0)}\nСовпадения исходной службы: {string.Join(", ", externalMatches)}\nIPv4 / HEAD / HTTP 1.1; proxy отключён\nЛоги: {Path.Combine(dataRoot, "logs")}\n{networkDiagnostics}", externalMatches, viewId, selected.Region, activeSuite?.Id, activeSuite?.Region, selected.Targets, leagueRegions, 4, suites, activeProfileIds,
                runId == null ? null : new SeriesProgress(runId, runParticipants, runCompleted, runParticipants.Count, processesStarted, activeProcesses, peakProcesses, activeRequests, peakRequests, runClock.Elapsed.TotalSeconds));
        }
    }
    public void Publish() => Changed?.Invoke(State());
    private void SaveSettings() => JsonFiles.Save(Path.Combine(dataRoot, "settings.json"), settings);
    private void SaveResults() => JsonFiles.Save(Path.Combine(dataRoot, "results.json"), results);
    private void Log(string text)
    {
        lock (gate)
        {
            var dir = Path.Combine(dataRoot, "logs"); Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, DateTime.Today.ToString("yyyy-MM-dd") + ".log"), DateTimeOffset.Now.ToString("O") + " " + text + Environment.NewLine);
        }
    }
    public async ValueTask DisposeAsync() { await CancelAsync(); if (main != null) await main.DisposeAsync(); }
    private sealed record Certificate(Dictionary<string, string> Binaries);
}

