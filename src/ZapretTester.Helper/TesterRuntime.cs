using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ZapretTester.Core;
using ZapretTester.Windows;

namespace ZapretTester.Helper;

public sealed partial class TesterRuntime : IAsyncDisposable
{
    private readonly string folder, protectedRoot, dataRoot;
    private readonly Dictionary<string, string> trustedBinaries;
    private readonly object gate = new();
    private readonly SeriesResultWriter resultWriter;
    private long resultVersion, stateRevision;
    private double checkpointSecondsAtStart;
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
    private VariantDescriptor viewVariant = VariantDescriptor.Create([new(TestSuiteId.Discord)]);
    private VariantDescriptor? activeVariant;
    private IReadOnlyList<ResolvedSuite> viewDefinitions = [];
    private SuiteDefinition? dashboardLeague;
    private string? dashboardLeagueError;
    private string[] activeProfileIds = [];
    private string? runId;
    private IReadOnlyList<RunParticipant> runParticipants = [];
    private int runCompleted, processesStarted, activeProcesses, peakProcesses, activeRequests, peakRequests;
    private int exportProcessesStarted, snapshotsPrepared;
    private SeriesPhase runPhase = SeriesPhase.Completed;
    private SeriesTimings timings = new();
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
    public void SelectVariant(VariantDescriptor variant)
    {
        var selected = NormalizeVariant(variant);
        var definitions = ServiceCatalog.Resolve(selected.Selections, settings);
        selected = VariantDescriptor.Create(definitions.Select(s => new SuiteSelection(s.Id, s.Region)));
        lock (gate) { viewVariant = selected; viewDefinitions = definitions; }
        Publish();
    }
    private VariantDescriptor NormalizeVariant(VariantDescriptor variant) =>
        VariantDescriptor.Create(variant.Selections.Select(s => s.Suite == TestSuiteId.LeagueOfLegends && string.IsNullOrWhiteSpace(s.Region)
            ? s with { Region = settings.LeagueRegion } : s), settings.LeagueRegion).Validate(allowEmpty: true);
    private void RefreshViewVariant()
    {
        viewDefinitions = ServiceCatalog.Resolve(viewVariant.Selections, settings);
        viewVariant = VariantDescriptor.Create(viewDefinitions.Select(s => new SuiteSelection(s.Id, s.Region)));
    }
    private SuiteDefinition ResolveSuite(TestSuiteId id, string? region = null)
    {
        if (id != TestSuiteId.LeagueOfLegends) return ServiceCatalog.Resolve(id);
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
    public bool FailMainPersistenceForAcceptance { get; set; }
    private int failSharedProcessForAcceptance;
    public bool FailSharedProcessForAcceptance
    {
        get => Volatile.Read(ref failSharedProcessForAcceptance) != 0;
        set => Interlocked.Exchange(ref failSharedProcessForAcceptance, value ? 1 : 0);
    }
    public bool FailFinalSaveForAcceptance { get; set; }
    public bool DelayFinalSaveForAcceptance { get; set; }
#endif

    public TesterRuntime(string folder, string protectedRoot, string dataRoot)
    {
        this.folder = Path.GetFullPath(folder); this.protectedRoot = protectedRoot; this.dataRoot = dataRoot;
        resultWriter = new(Path.Combine(dataRoot, "results.json"));
        var certificate = JsonSerializer.Deserialize<Certificate>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "isolation-certificate.json")), JsonFiles.Options)
            ?? throw new InvalidOperationException("Missing isolation certificate.");
        trustedBinaries = certificate.Binaries;
        ValidateBinaries(Path.Combine(folder, "bin"));
        Directory.CreateDirectory(dataRoot);
        settings = ReadJson<ApplicationSettings>(Path.Combine(dataRoot, "settings.json")) ?? new ApplicationSettings();
        settings = settings with { FlowsealFolder = this.folder };
        settings.Validate();
        var resultsPath = Path.Combine(dataRoot, "results.json");
        if (File.Exists(resultsPath)) FlowsealSnapshot.RejectReparse(resultsPath);
        var saved = ReadJson<Dictionary<string, ProfileTestResult>>(resultsPath);
        if (saved != null)
        {
            if (saved.Values.Any(r => string.IsNullOrEmpty(r.VariantId)))
            {
                var backup = Path.Combine(dataRoot, "results.before-combined.json");
                if (!File.Exists(backup)) File.Copy(Path.Combine(dataRoot, "results.json"), backup);
            }
            foreach (var pair in ResultKeys.Migrate(saved)) results[pair.Key] = pair.Value;
        }
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
    private SnapshotProfile PrepareBase(ZapretProfile profile) => PrepareBase(profile,
        Path.Combine(protectedRoot, "runs", Guid.NewGuid().ToString("N")), CancellationToken.None);
    private SnapshotProfile PrepareBase(ZapretProfile profile, string destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateBinaries(Path.GetDirectoryName(profile.WinwsPath)!);
        token.ThrowIfCancellationRequested();
        var snapshot = FlowsealSnapshot.Create(profile, destination);
        token.ThrowIfCancellationRequested();
        ValidateBinaries(snapshot.WorkingDirectory);
        return snapshot;
    }
    private sealed record ProfileDiscovery(List<ProfileEntry> Profiles, Dictionary<string, string> Fingerprints, string[] ExternalMatches);
    private ProfileDiscovery DiscoverProfiles(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var discovered = new ProfileScanner().Scan(folder).ToList();
        var hashes = new Dictionary<string, string>();
        using var hashContext = new FingerprintContext();
        for (var i = 0; i < discovered.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (discovered[i].Profile is not { } profile) continue;
            try { hashes[profile.Id] = FlowsealSnapshot.SourceFingerprint(profile, hashContext); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { discovered[i] = discovered[i] with { Error = e.Message }; }
        }
        token.ThrowIfCancellationRequested();
        string[] matches;
        try { matches = ProfileParser.MatchService(ZapretService.Read().ImagePath,
            discovered.Where(p => p.Profile != null && p.Error == null).Select(p => p.Profile!)).Select(p => p.Id).ToArray(); }
        catch (InvalidOperationException) { matches = []; }
        return new(discovered, hashes, matches);
    }
    // Apply only finished discovery, never expose partially replaced profile/fingerprint collections.
    private void ApplyDiscovery(ProfileDiscovery discovery)
    {
        profiles = discovery.Profiles;
        fingerprints.Clear();
        foreach (var pair in discovery.Fingerprints) fingerprints[pair.Key] = pair.Value;
        externalMatches = discovery.ExternalMatches;
    }
    public void Refresh()
    {
        lock (gate) if (testing) throw new InvalidOperationException("Stop the current run before refreshing profiles.");
        var discovery = DiscoverProfiles(CancellationToken.None);
        lock (gate)
        {
            if (testing) throw new InvalidOperationException("Stop the current run before refreshing profiles.");
            ApplyDiscovery(discovery);
            RefreshDashboardLeague();
            RefreshViewVariant();
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
        settings = value with { ManagedEnabled = settings.ManagedEnabled, MainProfileId = settings.MainProfileId, MainSuite = settings.MainSuite, MainRegion = settings.MainRegion,
            MainVariant = settings.MainVariant, MainVariantFingerprint = settings.MainVariantFingerprint, MainVariantVerified = settings.MainVariantVerified };
        RefreshDashboardLeague();
        viewVariant = VariantDescriptor.Create(viewVariant.Suites.Select(id => new SuiteSelection(id,
            id == TestSuiteId.LeagueOfLegends ? settings.LeagueRegion : "")));
        RefreshViewVariant(); SaveSettings(); Publish();
    }
    // Startup compatibility for pre-combined settings. Never called by Apply from IPC.
    public async Task ResumeManagedAsync()
    {
        // The stored descriptor is authoritative; dashboard selection and request
        // arguments never choose a replacement for this explicit resume command.
        if (HasMain) return;
        if (string.IsNullOrWhiteSpace(settings.MainProfileId))
            throw new InvalidOperationException("Сохранённый основной профиль не выбран.");
        await CancelAsync();
        var discovery = DiscoverProfiles(CancellationToken.None);
        lock (gate) ApplyDiscovery(discovery);
        await StartManagedAsync(settings.MainProfileId, settings.MainSuite, settings.MainRegion);
    }

    public async Task StartManagedAsync(string? id, TestSuiteId suiteId = TestSuiteId.Discord, string? region = null)
    {
        var profile = Profile(id);
        if (settings.MainVariant is { Mode: "combined" } persisted)
        {
            var definitions = ServiceCatalog.Resolve(persisted.Selections, settings);
            var suites = RequireDefinitions(definitions);
            var snapshot = VariantAdapter.Apply(PrepareBase(profile), persisted, suites);
            var fingerprint = snapshot.Fingerprint;
            if (settings.MainVariantVerified && fingerprint != settings.MainVariantFingerprint)
                throw new InvalidOperationException("Основной вариант изменился. Повторите проверку перед запуском.");
            await StartManagedSnapshotAsync(profile, snapshot, persisted, fingerprint, settings.MainVariantVerified);
        }
        else
        {
            var suite = ResolveSuite(suiteId, region);
            var legacy = new VariantDescriptor([suiteId], suite.Region, Mode: suiteId == TestSuiteId.LeagueOfLegends ? "legacy-lol" : "legacy-original");
            await StartManagedSnapshotAsync(profile, Prepare(profile, suite), legacy, null, false);
        }
    }
    private static IReadOnlyList<SuiteDefinition> RequireDefinitions(IReadOnlyList<ResolvedSuite> definitions)
    {
        var error = definitions.FirstOrDefault(d => d.Error != null || d.Definition == null);
        if (error != null) throw new InvalidOperationException(error.Error ?? "Конфигурация сервиса недоступна. Исправьте настройки или снимите его выбор.");
        return definitions.Select(d => d.Definition!).ToArray();
    }
    public async Task ApplyManagedVariantAsync(string? id, VariantDescriptor variant)
    {
        lock (gate) if (testing) throw new InvalidOperationException("Дождитесь завершения проверки.");
        // Parsing again is essential: an old Profile object retains the previous BAT hash/arguments.
        var discovery = DiscoverProfiles(CancellationToken.None);
        lock (gate) ApplyDiscovery(discovery);
        variant = NormalizeVariant(variant);
        var profile = Profile(id);
        var suites = RequireDefinitions(ServiceCatalog.Resolve(variant.Selections, settings));
        variant = VariantDescriptor.Create(suites.Select(s => new SuiteSelection(s.Id, s.Region))).Validate();
        var original = PrepareBase(profile);
        var fingerprint = VariantAdapter.Fingerprint(profile, original.SourceFingerprint ?? original.Fingerprint, variant, suites);
        ProfileTestResult[] evidence;
        lock (gate)
        {
            evidence = suites.Select(s => results.GetValueOrDefault(ResultKeys.For(variant.Id, s.Id, s.Region, profile.Id)))
                .Where(r => r != null).Select(r => r!).ToArray();
        }
        if (evidence.Length != suites.Count || evidence.Any(r => !r.IsReliable || r.PendingValidation || r.Error != null
            || r.Endpoints.Count != r.ExpectedEndpoints || r.VariantId != variant.Id
            || r.Fingerprint != fingerprint || r.VariantFingerprint != fingerprint || string.IsNullOrEmpty(r.SeriesId))
            || suites.Any(s => !evidence.Single(r => r.Suite == s.Id).Endpoints.Select(e => e.Name).SequenceEqual(s.Targets.Select(t => t.Name)))
            || evidence.Select(r => r.SeriesId).Distinct().Count() != 1)
            throw new InvalidOperationException("Сначала завершите достоверную проверку этой стратегии с выбранным набором сервисов. Файлы и конфигурация должны совпадать с проверенными.");
        var snapshot = VariantAdapter.Apply(original, variant, suites);
        await StartManagedSnapshotAsync(profile, snapshot, variant, fingerprint, true);
    }
    public async Task PrepareFlowsealAsync(string? id)
    {
        lock (gate) if (testing) throw new InvalidOperationException("Остановите текущую проверку.");
        var profile = Profile(id);
        await StartManagedSnapshotAsync(profile, PrepareBase(profile),
            new VariantDescriptor([TestSuiteId.Discord], Mode: "bootstrap-original"), null, false);
    }
    public async Task ExpandPortPoolAsync(ApplicationSettings requested)
    {
        lock (gate) if (testing) throw new InvalidOperationException("Остановите текущую проверку.");
        var next = settings with { FirstPort = requested.FirstPort, LastPort = requested.LastPort };
        next.Validate();
        if (!HasMain)
        {
            networkDiagnostics = await NetworkEnvironment.ValidatePoolAsync(next.FirstPort, next.LastPort);
            settings = next; SaveSettings(); Publish(); return;
        }
        await StartManagedSnapshotAsync(Profile(settings.MainProfileId), mainSnapshot!, settings.MainVariant,
            settings.MainVariantFingerprint, settings.MainVariantVerified, next);
    }
    private async Task StartManagedSnapshotAsync(ZapretProfile profile, SnapshotProfile snapshot,
        VariantDescriptor? variant, string? fingerprint, bool verified, ApplicationSettings? poolSettings = null)
    {
        await CancelAsync();
        var nextSettings = poolSettings ?? settings;
        networkDiagnostics = await NetworkEnvironment.ValidatePoolAsync(nextSettings.FirstPort, nextSettings.LastPort);
        var raw = await FilterCompiler.ExportAsync(snapshot, Path.Combine(snapshot.WorkingDirectory, "main-original-" + Guid.NewGuid().ToString("N") + ".filter"), CancellationToken.None);
        var filterPath = Path.Combine(snapshot.WorkingDirectory, "main-" + Guid.NewGuid().ToString("N") + ".filter");
        File.WriteAllText(filterPath, IsolationFilter.Main(raw, nextSettings.FirstPort, nextSettings.LastPort));
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
            settings = nextSettings with { ManagedEnabled = true, MainProfileId = profile.Id, MainSuite = variant?.Suites.FirstOrDefault() ?? TestSuiteId.Discord, MainRegion = variant?.Region ?? "", MainVariant = variant, MainVariantFingerprint = fingerprint, MainVariantVerified = verified };
            SaveSettings();
#if DEBUG
            if (FailMainPersistenceForAcceptance)
            { FailMainPersistenceForAcceptance = false; throw new IOException("Injected main persistence failure after settings publication."); }
#endif
            JsonFiles.Save(Path.Combine(protectedRoot, "transition.json"), new { State = "Managed", Profile = profile.Id });
            Log("Main started: " + profile.DisplayName + ", PID " + main.Id);
            lastError = null;
        }
        catch (Exception failure)
        {
            // Restore ownership metadata before any fallible rollback work, including
            // the first managed startup where there was no previous main process.
            mainSnapshot = previous; mainFilter = previousFilter; settings = previousSettings;
            var rollbackErrors = new List<Exception>();
            try
            {
                var failedMain = main; main = null;
                if (failedMain != null) await failedMain.DisposeAsync();
                if (previous != null && previousFilter != null)
                {
                    await using var rollbackMonitor = new ReflectMonitor();
                    await rollbackMonitor.SynchronizeAsync(CancellationToken.None);
                    await rollbackMonitor.WaitForNoForeignAsync(CancellationToken.None);
                    rollbackMonitor.Enforce();
                    main = await LaunchAsync(previous, previousFilter, rollbackMonitor, CancellationToken.None);
                }
                else if (changedExternal && originalService != null)
                {
                    ZapretService.SetStartMode(originalService.StartMode);
                    if (originalService.WasRunning) await ZapretService.StartAsync(CancellationToken.None);
                }
            }
            catch (Exception rollback) { rollbackErrors.Add(rollback); }
            // A transition/log write can fail after settings.json was replaced. Persist
            // the prior descriptor and pool even when restarting its process also failed.
            try
            {
                SaveSettings();
                JsonFiles.Save(Path.Combine(protectedRoot, "transition.json"), new
                { State = HasMain ? "Managed" : changedExternal ? "Restored" : "Compatibility", Profile = previousSettings.MainProfileId });
            }
            catch (Exception rollback) { rollbackErrors.Add(rollback); }
            if (rollbackErrors.Count != 0)
                throw new AggregateException("Не удалось полностью восстановить прежний основной Zapret: " + failure.Message,
                    new[] { failure }.Concat(rollbackErrors));
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
        lock (gate)
        {
            if (testing) throw new InvalidOperationException("A run is already active.");
            var request = SeriesRequest.Capture(ids, selections,
                profiles.Select(p => p.Profile?.Id ?? Path.GetFileName(p.FilePath).ToUpperInvariant()), settings);
            runCancellation?.Dispose(); runCancellation = new CancellationTokenSource();
            var token = runCancellation.Token;
            testing = true; activeSuite = null; activeVariant = request.Variant;
            activeProfileIds = request.ProfileIds.ToArray(); lastError = null; runId = Guid.NewGuid().ToString("N");
            runParticipants = request.Participants; runCompleted = 0;
            processesStarted = activeProcesses = peakProcesses = activeRequests = peakRequests = 0;
            exportProcessesStarted = snapshotsPrepared = 0; timings = new(); runPhase = SeriesPhase.Preparing;
            checkpointSecondsAtStart = resultWriter.CheckpointSeconds;
            runClock.Restart();
            foreach (var p in request.Participants) { states[p.Key] = RunState.Queued; runErrors.Remove(p.Key); }
            // No scheduling cancellation token: even an immediate Cancel must enter finally and release the reservation.
            runTask = Task.Run(() => RunBatchAsync(request, token));
        }
        Publish();
    }

    private async Task RunBatchAsync(SeriesRequest request, CancellationToken token)
    {
        var completed = new List<string>();
        ReflectMonitor? observer = null;
        Exception? jobFailure = null;
        SeriesWorkspace? workspace = null;
        var validated = false;
        var frozenSettings = request.Settings;
        try
        {
            token.ThrowIfCancellationRequested();
            ProfileDiscovery discovery;
            ResolvedSuite[] suites;
            var discoveryStart = Stopwatch.GetTimestamp();
            try
            {
                discovery = DiscoverProfiles(token);
                suites = ServiceCatalog.Resolve(request.Selections, frozenSettings).ToArray();
            }
            finally { AddTiming(t => t with { DiscoverySeconds = t.DiscoverySeconds + SecondsSince(discoveryStart) }); }
            token.ThrowIfCancellationRequested();
            var plan = BatchPlan.Create(discovery.Profiles, request.ProfileIds, suites, frozenSettings.Attempts,
                frozenSettings.LastPort - frozenSettings.FirstPort + 1);
            lock (gate)
            {
                ApplyDiscovery(discovery);
                activeSuite = suites.Length == 1 ? suites[0].Definition : null;
                activeVariant = plan.Variant;
                // Resolving a run never changes the selected tab or the region chosen while it was preparing.
                var league = suites.FirstOrDefault(s => s.Id == TestSuiteId.LeagueOfLegends);
                if (league != null && settings.LeagueRegion == frozenSettings.LeagueRegion && settings.LeagueFolder == frozenSettings.LeagueFolder
                    && (string.IsNullOrEmpty(frozenSettings.LeagueRegion) || league.Region == frozenSettings.LeagueRegion))
                { dashboardLeague = league.Definition; dashboardLeagueError = league.Error; }
                foreach (var p in runParticipants) states.Remove(p.Key);
                runParticipants = plan.Participants; runCompleted = plan.Blocked.Count;
                foreach (var p in plan.Participants) { states[p.Key] = RunState.Queued; runErrors.Remove(p.Key); }
                foreach (var p in plan.Blocked) { states[p.Participant.Key] = RunState.Blocked; runErrors[p.Participant.Key] = p.Error; }
            }
            Publish();
            if (plan.Jobs.Count == 0) return;
            var environmentStart = Stopwatch.GetTimestamp();
            try { networkDiagnostics = await NetworkEnvironment.ValidatePoolAsync(frozenSettings.FirstPort, frozenSettings.LastPort, token); }
            finally { AddTiming(t => t with { DiscoverySeconds = t.DiscoverySeconds + SecondsSince(environmentStart) }); }
            await using var monitor = new ReflectMonitor(); observer = monitor;
            if (HasMain) monitor.RegisterOwned(main!);
            var synchronizationStart = Stopwatch.GetTimestamp();
            try { await monitor.SynchronizeAsync(token); monitor.Enforce(); }
            finally { AddTiming(t => t with { ReadinessSeconds = t.ReadinessSeconds + SecondsSince(synchronizationStart) }); }
            using var guarded = CancellationTokenSource.CreateLinkedTokenSource(token, monitor.ConflictToken);
            using var allocator = new PortAllocator(frozenSettings.FirstPort, frozenSettings.LastPort);
            using var slots = new SemaphoreSlim(frozenSettings.MaxParallelTests);
            workspace = new SeriesWorkspace(Path.Combine(protectedRoot, "runs", "series-" + runId));
            lock (gate) runPhase = SeriesPhase.Testing;
            Publish();
            await Task.WhenAll(plan.Jobs.Select(async job =>
            {
                await slots.WaitAsync(guarded.Token);
                try
                {
                    lock (gate) foreach (var suite in job.Suites) states[ResultKeys.For(job.Variant!.Id, suite.Id, suite.Region, job.Profile.Id)] = RunState.Testing;
                    Publish();
                    var jobResults = await TestJobAsync(job, frozenSettings, workspace, allocator, monitor, guarded.Token);
                    lock (gate)
                    {
                        foreach (var result in jobResults)
                        {
                            var key = ResultKeys.For(result);
                            results[key] = result with { PendingValidation = true }; completed.Add(key);
                            states[key] = RunState.Idle; runCompleted++;
                        }
                        QueueResultsCheckpoint();
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
            lock (gate) runPhase = SeriesPhase.Finalizing;
            Publish();
            await monitor.DisposeAsync();
            token.ThrowIfCancellationRequested();
            if (monitor.Error != null) throw new InvalidOperationException(monitor.Error);
            validated = true;
        }
        catch (Exception e)
        {
            lock (gate)
            {
                lastError = token.IsCancellationRequested ? "Проверка отменена." : observer?.Error ?? jobFailure?.Message ?? e.Message;
                foreach (var id in completed) results[id] = results[id] with { IsReliable = false, Error = lastError, PendingValidation = false };
                foreach (var p in runParticipants)
                    if (states.GetValueOrDefault(p.Key) is RunState.Queued or RunState.Testing)
                    { states[p.Key] = RunFailure.State(token.IsCancellationRequested); runErrors[p.Key] = lastError; runCompleted++; }
                QueueResultsCheckpoint();
            }
            Log("Run stopped: " + lastError);
        }
        finally
        {
            lock (gate) runPhase = SeriesPhase.Finalizing;
            Publish();
            var cleanupStart = Stopwatch.GetTimestamp();
            try { workspace?.Dispose(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log("Series cleanup deferred: " + e.Message); }
            finally { AddTiming(t => t with { CleanupSeconds = t.CleanupSeconds + SecondsSince(cleanupStart) }); }
            await CommitSeriesAsync(completed, validated, token);
            Log($"Series {runId}: {runCompleted}/{runParticipants.Count}; workers {processesStarted}; exports {exportProcessesStarted}; snapshots {snapshotsPrepared}; peak workers {peakProcesses}; peak requests {peakRequests}; {runClock.Elapsed.TotalSeconds:F1}s; timings {JsonSerializer.Serialize(timings)}");
            Publish();
        }
    }

    private sealed class PortPoolException(string message, Exception inner) : Exception(message, inner);
    private async Task<IReadOnlyList<ProfileTestResult>> TestJobAsync(BatchJob job, ApplicationSettings frozenSettings,
        SeriesWorkspace workspace, PortAllocator allocator, ReflectMonitor monitor, CancellationToken token)
    {
        var endpoints = job.Suites.ToDictionary(s => s.Id, s => new EndpointResult?[s.Targets.Count]);
        var leases = new List<PortLease>();
        OwnedProcess? process = null;
        var baseFingerprint = fingerprints[job.Profile.Id];
        var variant = job.Variant ?? throw new InvalidOperationException("Missing combined variant.");
        var effectiveFingerprint = VariantAdapter.Fingerprint(job.Profile, baseFingerprint, variant, job.Suites);
        bool countedProcess = false;
        IReadOnlyList<ProfileTestResult> Results(bool reliable, string? error) => job.Suites.Select(s =>
            new ProfileTestResult(job.Profile.Id, effectiveFingerprint, reliable,
                endpoints[s.Id].Where(e => e != null).Select(e => e!).ToArray(), DateTimeOffset.UtcNow, error, s.Id, s.Region, s.Targets.Count,
                    VariantId: variant.Id, VariantFingerprint: effectiveFingerprint, SeriesId: runId)).ToArray();
        try
        {
            var snapshot = workspace.Snapshot(job.Profile, (profile, destination, ct) =>
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    var prepared = PrepareBase(profile, destination, ct);
                    lock (gate) snapshotsPrepared++;
                    return prepared;
                }
                finally { AddTiming(t => t with { SnapshotSeconds = t.SnapshotSeconds + SecondsSince(started) }); }
            }, token);
            baseFingerprint = snapshot.SourceFingerprint ?? snapshot.Fingerprint;
            effectiveFingerprint = VariantAdapter.Fingerprint(job.Profile, baseFingerprint, variant, job.Suites);
            var effective = VariantAdapter.Apply(snapshot, variant, job.Suites);
            var work = new List<(SuiteDefinition Suite, TestTarget Target, int Index, PortLease[] Attempts)>();
            foreach (var item in BatchPlan.InterleavedTargets(job))
            {
                var attempts = new PortLease[frozenSettings.Attempts];
                for (var i = 0; i < attempts.Length; i++)
                {
                    try { attempts[i] = allocator.Allocate(); leases.Add(attempts[i]); }
                    catch (InvalidOperationException e) { throw new PortPoolException(e.Message, e); }
                }
                work.Add((item.Suite, item.Target, item.Index, attempts));
            }
            var jobDirectory = workspace.CreateJobDirectory();
            string original;
            var exportStart = Stopwatch.GetTimestamp();
            try { original = await FilterCompiler.ExportAsync(effective, Path.Combine(jobDirectory, "original.filter"), token,
                () => { lock (gate) exportProcessesStarted++; }); }
            finally { AddTiming(t => t with { FilterExportSeconds = t.FilterExportSeconds + SecondsSince(exportStart) }); }
            var filter = Path.Combine(jobDirectory, "test.filter");
            File.WriteAllText(filter, IsolationFilter.Candidate(original, leases.Select(l => l.Port)));
            WinDivert.Compile(File.ReadAllText(filter));
            lock (gate) { processesStarted++; activeProcesses++; peakProcesses = Math.Max(peakProcesses, activeProcesses); countedProcess = true; }
            var readinessStart = Stopwatch.GetTimestamp();
            try { process = await LaunchAsync(effective, filter, monitor, token); }
            finally { AddTiming(t => t with { ReadinessSeconds = t.ReadinessSeconds + SecondsSince(readinessStart) }); }
#if DEBUG
            if (job.Suites.Count > 1 && Interlocked.Exchange(ref failSharedProcessForAcceptance, 0) != 0)
            {
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
                        EndpointResult endpoint;
                        var networkStart = Stopwatch.GetTimestamp();
                        try { endpoint = await tester.TestAsync(item.Target, lease, TimeSpan.FromSeconds(frozenSettings.TimeoutSeconds), token); }
                        finally { AddTiming(t => t with { NetworkSeconds = t.NetworkSeconds + SecondsSince(networkStart) }); }
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
            var cleanupStart = Stopwatch.GetTimestamp();
            try
            {
                if (process != null) { var pid = process.Id; await process.DisposeAsync(); Log($"Stopped temporary PID {pid}\n{process.Output}"); }
            }
            finally
            {
                if (countedProcess) { lock (gate) activeProcesses--; }
                foreach (var lease in leases) lease.Dispose();
                AddTiming(t => t with { CleanupSeconds = t.CleanupSeconds + SecondsSince(cleanupStart) });
            }
        }
    }

    public async Task CancelAsync()
    {
        CancellationTokenSource? cancellation;
        Task pending;
        bool notify;
        lock (gate)
        {
            cancellation = runCancellation; pending = runTask; cancellation?.Cancel(); notify = testing;
            if (notify) lastError = "Проверка отменяется.";
        }
        if (notify) Publish();
        await pending;
        lock (gate)
            if (ReferenceEquals(cancellation, runCancellation))
            { cancellation?.Dispose(); runCancellation = null; }
    }
    private void SetRunState(string id, RunState state) { lock (gate) states[id] = state; Publish(); }
    public HelperState State()
    {
        lock (gate)
        {
            var mainKey = HasMain && settings.MainVariant != null
                ? ResultKeys.For(settings.MainVariant.Id, settings.MainSuite, settings.MainRegion, settings.MainProfileId!) : null;
            var suites = SuiteProjection.BuildVariant(profiles, fingerprints, results, viewVariant, viewDefinitions,
                states, runErrors, mainKey);
            var selected = suites.FirstOrDefault();
            var rows = selected?.Profiles ?? [];
            return new(folder, HasMain ? "Managed" : "Compatibility", HasMain ? settings.MainProfileId : null, testing, settings, rows, lastError,
                $"Профили: {rows.Count}\nДвижок: проверенный набор бинарников (контроль SHA-256)\nПул: {settings.FirstPort}–{settings.LastPort}\nMain PID: {(HasMain ? main!.Id : 0)}\nСовпадения исходной службы: {string.Join(", ", externalMatches)}\nIPv4 / HEAD (Twitch: GET только заголовки) / HTTP 1.1; proxy отключён\nЛоги: {Path.Combine(dataRoot, "logs")}\nСерия: {runPhase}; рабочие процессы {processesStarted}; экспорты фильтров {exportProcessesStarted}; снимки {snapshotsPrepared}\n{networkDiagnostics}",
                externalMatches, selected?.Suite ?? TestSuiteId.Discord, selected?.Region ?? "", activeSuite?.Id, activeSuite?.Region,
                selected?.Targets ?? [], leagueRegions, 6, suites, activeProfileIds,
                runId == null ? null : new SeriesProgress(runId, runParticipants, runCompleted, runParticipants.Count, processesStarted, activeProcesses,
                    peakProcesses, activeRequests, peakRequests, runClock.Elapsed.TotalSeconds, runPhase, timings, exportProcessesStarted, snapshotsPrepared, activeVariant),
                ViewVariant: viewVariant, ActiveVariant: activeVariant, MainVariant: HasMain ? settings.MainVariant : null,
                StateRevision: ++stateRevision, MainVariantVerified: HasMain && settings.MainVariantVerified);
        }
    }
    public void Publish() => Changed?.Invoke(State());
    private void SaveSettings() => JsonFiles.Save(Path.Combine(dataRoot, "settings.json"), settings);
    private static double SecondsSince(long started) => Stopwatch.GetElapsedTime(started).TotalSeconds;
    private void AddTiming(Func<SeriesTimings, SeriesTimings> update) { lock (gate) timings = update(timings); }
    // Caller holds gate; records and endpoint arrays are immutable once inserted.
    private void QueueResultsCheckpoint() => resultWriter.Checkpoint(++resultVersion, new Dictionary<string, ProfileTestResult>(results));
    private async Task CommitSeriesAsync(IReadOnlyList<string> completed, bool validated, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                Dictionary<string, ProfileTestResult> snapshot;
                bool cancellationObserved;
                long version;
                lock (gate)
                {
                    cancellationObserved = token.IsCancellationRequested;
                    if (cancellationObserved) lastError = "Проверка отменена.";
                    snapshot = new(results);
                    foreach (var key in completed)
                    {
                        var previous = snapshot[key];
                        snapshot[key] = validated && !cancellationObserved
                            ? previous with { PendingValidation = false }
                            : previous with { IsReliable = false, Error = lastError ?? "Достоверность серии не подтверждена.", PendingValidation = false };
                    }
                    version = ++resultVersion;
                }
#if DEBUG
                if (DelayFinalSaveForAcceptance)
                {
                    DelayFinalSaveForAcceptance = false;
                    // The runtime gate remains free so acceptance can cancel while a
                    // final immutable snapshot is awaiting publication.
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
#endif
                var published = await resultWriter.FinalizeAsync(version, snapshot, temporary =>
                {
                    lock (gate)
                    {
                        // Cancellation can arrive while the immutable final snapshot is serialized.
                        // Rebuild an invalidated snapshot instead of publishing any successful results.
                        if (!cancellationObserved && token.IsCancellationRequested) return false;
#if DEBUG
                        if (FailFinalSaveForAcceptance)
                        { FailFinalSaveForAcceptance = false; throw new IOException("Injected final result save failure."); }
#endif
                        File.Move(temporary, Path.Combine(dataRoot, "results.json"), true);
                        foreach (var key in completed)
                        {
                            results[key] = snapshot[key];
                            if (cancellationObserved) { states[key] = RunState.Cancelled; runErrors[key] = lastError!; }
                        }
                        FinishSeries(SecondsSince(started));
                        return true;
                    }
                });
                if (published) break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Dictionary<string, ProfileTestResult> invalid;
            long version;
            lock (gate)
            {
                lastError = "Не удалось сохранить результаты: " + e.Message;
                foreach (var key in completed) results[key] = results[key] with { IsReliable = false, Error = lastError, PendingValidation = false };
                invalid = new(results); version = ++resultVersion;
            }
            try
            {
                await resultWriter.FinalizeAsync(version, invalid, temporary =>
                { lock (gate) { File.Move(temporary, Path.Combine(dataRoot, "results.json"), true); return true; } });
            }
            catch (Exception retry) when (retry is IOException or UnauthorizedAccessException) { Log("Result invalidation could not be persisted: " + retry.Message); }
            lock (gate) FinishSeries(SecondsSince(started));
        }
    }
    private void FinishSeries(double saveSeconds)
    {
        timings = timings with { SaveSeconds = timings.SaveSeconds + saveSeconds + resultWriter.CheckpointSeconds - checkpointSecondsAtStart };
        testing = false; activeSuite = null; activeProfileIds = []; runPhase = SeriesPhase.Completed; runClock.Stop();
    }
    private void Log(string text)
    {
        lock (gate)
        {
            var dir = Path.Combine(dataRoot, "logs"); Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, DateTime.Today.ToString("yyyy-MM-dd") + ".log"), DateTimeOffset.Now.ToString("O") + " " + text + Environment.NewLine);
        }
    }
    public async ValueTask DisposeAsync() { await CancelAsync(); await resultWriter.DisposeAsync(); if (main != null) await main.DisposeAsync(); }
    private sealed record Certificate(Dictionary<string, string> Binaries);
}

