using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ZapretTester.Core;
using ZapretTester.Windows;

namespace TestIsolation;

public static class LiveExperiment
{
    public static async Task<int> RunAsync(string folder, string output, CancellationToken token)
    {
        var report = new LiveReport { StartedAt = DateTimeOffset.UtcNow };
        ServiceSnapshot? service = null;
        var stopped = false;
        var processes = new List<(string Name, OwnedProcess Process)>();
        ReflectMonitor? reflect = null;
        PacketObserver? observer = null;
        using var ports = new PortAllocator(41000, 41999);
        using var controls = new PortAllocator(42000, 42999);
        try
        {
            var profiles = new ProfileScanner().Scan(folder);
            try { service = ZapretService.Read(); }
            catch (InvalidOperationException e) when (e.Message == "The zapret service does not exist.") { }
            report.ServiceBefore = service;
            JsonFiles.Save(Path.Combine(output, "service-before.json"), service);
            var command = service == null ? null : ProfileParser.Tokenize(service.ImagePath);
            var expectedExecutable = Path.Combine(folder, "bin", "winws.exe");
            if (command != null && !Path.GetFullPath(command[0]).Equals(Path.GetFullPath(expectedExecutable), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Service executable does not belong to the selected Flowseal bundle.");
            var originalArgs = command == null ? profiles.First(p => p.Profile != null).Profile!.Arguments : Normalize(command.Skip(1).ToArray());
            report.MainProfile = profiles.FirstOrDefault(p => p.Profile?.Arguments.SequenceEqual(originalArgs) == true)?.Profile?.DisplayName ?? "Service configuration";
            // Preserve the actual running service command even if BAT files have changed.
            var mainDefinition = new ZapretProfile("SERVICE", report.MainProfile, Path.Combine(folder, "service.bat"), expectedExecutable,
                Path.Combine(folder, "bin"), originalArgs, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(service?.ImagePath ?? string.Join("\n", originalArgs)))));
            var definitions = new[] { mainDefinition,
                profiles.Single(p => p.Profile?.DisplayName == "General").Profile!,
                profiles.Single(p => p.Profile?.DisplayName == "ALT").Profile! };
            var names = new[] { "Main", "A", "B" };
            var snapshots = new List<SnapshotProfile>();
            WinDivert.Load(Path.Combine(folder, "bin", "WinDivert.dll"));
            var a = Enumerable.Range(0, 7).Select(_ => ports.Allocate()).ToArray();
            var b = Enumerable.Range(0, 7).Select(_ => ports.Allocate()).ToArray();
            var c = Enumerable.Range(0, 3).Select(_ => controls.Allocate()).ToArray();
            var filters = new Dictionary<string, string>();
            var paths = new Dictionary<string, string>();
            for (var i = 0; i < definitions.Length; i++)
            {
                var snapshot = FlowsealSnapshot.Create(definitions[i], Path.Combine(output, "snapshots", names[i]));
                snapshots.Add(snapshot);
                var original = await FilterCompiler.ExportAsync(snapshot, Path.Combine(output, names[i] + ".original.filter"), token);
                var filter = i == 0 ? IsolationFilter.Main(original, 41000, 41999)
                    : IsolationFilter.Candidate(original, (i == 1 ? a : b).Select(l => l.Port));
                WinDivert.Compile(filter);
                filters[names[i]] = filter;
                paths[names[i]] = Path.Combine(output, names[i] + ".filter");
                File.WriteAllText(paths[names[i]], filter);
            }
            reflect = new ReflectMonitor();
            await reflect.SynchronizeAsync(token);
            report.HandlesBefore = reflect.Active;
            // No process is stopped until all resources and filters have been prepared.
            if (service?.WasRunning == true)
            {
                // Journal before mutation so an interrupted run can be recovered explicitly.
                JsonFiles.Save(Path.Combine(output, "recovery-required.json"), service);
                stopped = true;
                await ZapretService.StopAsync(token);
            }
            await reflect.WaitForNoForeignAsync(token);
            reflect.Enforce();
            using var guarded = CancellationTokenSource.CreateLinkedTokenSource(token, reflect.ConflictToken);
            for (var i = 0; i < snapshots.Count; i++)
            {
                var snapshot = snapshots[i];
                var name = names[i];
                var process = reflect.StartOwned(() => OwnedProcess.Start(snapshot.Executable, snapshot.WorkingDirectory,
                    FilterCompiler.IsolatedArguments(snapshot, paths[name])));
                processes.Add((name, process));
                await reflect.WaitCaptureAsync(process.Id, guarded.Token);
                await process.WaitReadyAsync(guarded.Token);
            }
            report.HandlesDuring = reflect.Active;
            observer = new PacketObserver(a.Select(l => l.Port).ToArray(), b.Select(l => l.Port).ToArray(), c.Select(l => l.Port).ToArray(), filters);
            var tester = new ConnectivityTester();
            var target = new TestTarget("Isolation control", new Uri("https://example.com"), true);
            async Task<EndpointResult[]> Round(int index)
            {
                return await Task.WhenAll(new[] { a[index], b[index], c[index] }.Select(l => tester.TestAsync(target, l, TimeSpan.FromSeconds(5), guarded.Token)));
            }
            var baseline = await Round(0);
            report.ControlRounds["baseline"] = baseline;
            RequireAvailable(baseline, "Baseline");
            // DROP is deliberately limited to an already exclusively bound test socket.
            using (var drop = WinDivert.Open(IsolationFilter.LocalPorts([a[1].Port]), 0, 29000, 2 | 16))
            {
                var negative = await Round(1);
                report.ControlRounds["drop-A"] = negative;
                if (negative[0].Status != EndpointStatus.Failed || negative[0].FailureStage != FailureStage.Timeout)
                    throw new InvalidOperationException("The DROP negative control did not cause a timeout in A.");
                RequireAvailable(negative.Skip(1), "Unaffected B/main");
            }
            var recovery = await Round(2);
            report.ControlRounds["recovery"] = recovery;
            RequireAvailable(recovery, "Recovery");
            // Discord outcomes are measurements, not prerequisites for isolation.
            for (var i = 0; i < TestTarget.Defaults.Count; i++)
            {
                report.Discord.Add(await tester.TestAsync(TestTarget.Defaults[i], a[i + 3], TimeSpan.FromSeconds(5), guarded.Token));
                report.Discord.Add(await tester.TestAsync(TestTarget.Defaults[i], b[i + 3], TimeSpan.FromSeconds(5), guarded.Token));
            }
            guarded.Token.ThrowIfCancellationRequested();
            if (processes.Any(p => p.Process.HasExited)) throw new InvalidOperationException("An owned winws exited during the experiment.");
            await observer.DisposeAsync();
            observer.Verify();
            report.PacketCounts = observer.Counts;
            observer = null;
            // Deliberately do not register this process as owned by the monitor.
            await using (var foreign = OwnedProcess.Start(Environment.ProcessPath!, AppContext.BaseDirectory,
                ["--foreign-probe", folder, a[0].Port.ToString()]))
            {
                try { await Task.Delay(TimeSpan.FromSeconds(5), reflect.ConflictToken); }
                catch (OperationCanceledException) when (reflect.ConflictToken.IsCancellationRequested) { }
                if (!reflect.ConflictToken.IsCancellationRequested || reflect.Error?.Contains($"PID {foreign.Id},", StringComparison.Ordinal) != true)
                    throw new InvalidOperationException("A new unowned WinDivert handle was not correctly detected.");
                report.ForeignHandleRejected = true;
            }
            report.Status = "LiveIsolationPassed";
        }
        catch (Exception e) { report.Status = "Blocked"; report.Error = e.ToString(); }
        finally
        {
            if (observer != null) { await observer.DisposeAsync(); report.PacketCounts = observer.Counts; }
            foreach (var (name, process) in processes.AsEnumerable().Reverse())
            {
                try { await process.DisposeAsync(); File.WriteAllText(Path.Combine(output, name + ".log"), process.Output); }
                catch (Exception e) { report.CleanupErrors.Add(name + ": " + e.Message); }
            }
            if (reflect != null) await reflect.DisposeAsync();
            if (stopped)
            {
                try { await ZapretService.StartAsync(CancellationToken.None); report.ServiceRestored = true; File.Delete(Path.Combine(output, "recovery-required.json")); }
                catch (Exception e) { report.CleanupErrors.Add("Restore zapret: " + e.Message); }
            }
            else report.ServiceRestored = true;
            if (report.CleanupErrors.Count > 0) report.Status = "RecoveryRequired";
            report.FinishedAt = DateTimeOffset.UtcNow;
            JsonFiles.Save(Path.Combine(output, "live.json"), report);
        }
        Console.WriteLine(report.Status + ": " + report.Error);
        return report.Status == "LiveIsolationPassed" ? 0 : 1;
    }
    private static void RequireAvailable(IEnumerable<EndpointResult> values, string stage)
    {
        if (values.Any(v => v.Status != EndpointStatus.Available)) throw new InvalidOperationException(stage + " HTTPS control did not pass; isolation cannot be certified.");
    }
    private static IReadOnlyList<string> Normalize(IReadOnlyList<string> arguments)
    {
        var output = new List<string>();
        for (var i = 0; i < arguments.Count; i++)
            output.Add(!arguments[i].Contains('=') && i + 1 < arguments.Count && !arguments[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? arguments[i] + "=" + arguments[++i] : arguments[i]);
        return output;
    }
}

public sealed class LiveReport
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public string Status { get; set; } = "NotVerified";
    public string? Error { get; set; }
    public string? MainProfile { get; set; }
    public ServiceSnapshot? ServiceBefore { get; set; }
    public bool ServiceRestored { get; set; }
    public bool ForeignHandleRejected { get; set; }
    public IReadOnlyList<ReflectedHandle>? HandlesBefore { get; set; }
    public IReadOnlyList<ReflectedHandle>? HandlesDuring { get; set; }
    public Dictionary<string, EndpointResult[]> ControlRounds { get; set; } = [];
    public Dictionary<string, int> PacketCounts { get; set; } = [];
    public List<EndpointResult> Discord { get; set; } = [];
    public List<string> CleanupErrors { get; set; } = [];
}
