using System.Security.Principal;
using System.Text.Json;
using TestIsolation;
using ZapretTester.Core;
using ZapretTester.Windows;

if (args.Length == 3 && args[0] == "--foreign-probe")
{
    if (!int.TryParse(args[2], out var probePort) || probePort is < 41000 or > 41999) return 2;
    WinDivert.Load(Path.Combine(Path.GetFullPath(args[1]), "bin", "WinDivert.dll"));
    using var foreign = WinDivert.Open(IsolationFilter.LocalPorts([probePort]), 0, 100, 16);
    // No receive/reinject: the parent terminates this bounded negative-control process.
    await Task.Delay(TimeSpan.FromSeconds(15));
    return 0;
}

if (args.Length != 3 || args[0] is not ("--inspect" or "--inspect-lol" or "--live-with-service-restart"))
{
    Console.WriteLine("TestIsolation --inspect <Flowseal folder> <new report directory>");
    Console.WriteLine("Exports native filters and evaluates the packet matrix. Does not stop services or claim live isolation.");
    Console.WriteLine("TestIsolation --live-with-service-restart <Flowseal folder> <new report directory>");
    Console.WriteLine("Live mode TEMPORARILY stops the matching zapret service and restores it in finally.");
    return 2;
}
var folder = Path.GetFullPath(args[1]);
var output = Path.GetFullPath(args[2]);
if (Directory.Exists(output)) { Console.Error.WriteLine("Output directory must be new."); return 2; }
if (output.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
{ Console.Error.WriteLine("Reports must not be written inside Flowseal."); return 2; }
Directory.CreateDirectory(output);
var report = new InspectionReport { FlowsealFolder = folder, StartedAt = DateTimeOffset.UtcNow,
    Administrator = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator) };
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
if (args[0] == "--live-with-service-restart") return await LiveExperiment.RunAsync(folder, output, cancel.Token);
try
{
    var suite = SuiteDefinition.Discord;
    if (args[0] == "--inspect-lol")
    {
        var installation = LeagueConfiguration.Load(null);
        suite = installation.Regions[installation.SelectedRegion];
    }
    var entries = new ProfileScanner().Scan(folder);
    report.ProfilesFound = entries.Count;
    report.ParseErrors = entries.Where(e => e.Error != null).Select(e => e.FilePath + ": " + e.Error).ToArray();
    if (report.ParseErrors.Length > 0) throw new InvalidOperationException("Profile parser errors.");
    WinDivert.Load(Path.Combine(folder, "bin", "WinDivert.dll"));
    foreach (var entry in entries)
    {
        var profile = entry.Profile!;
        var snapshot = LeagueAdapter.Apply(FlowsealSnapshot.Create(profile, Path.Combine(output, "snapshots", Path.GetFileNameWithoutExtension(profile.BatFilePath))), suite);
        var directory = Path.Combine(output, "filters", Path.GetFileNameWithoutExtension(profile.BatFilePath));
        Directory.CreateDirectory(directory);
        JsonFiles.Save(Path.Combine(directory, "effective-arguments.json"), new { Suite = suite.Id, suite.Region, suite.Hosts, snapshot.Arguments });
        var original = await FilterCompiler.ExportAsync(snapshot, Path.Combine(directory, "original.filter"), cancel.Token);
        if (!report.RawPartOrConfirmed)
        {
            var probePath = Path.Combine(output, "raw-part.filter");
            await using var probe = OwnedProcess.Start(snapshot.Executable, snapshot.WorkingDirectory,
                ["--wf-tcp=443", "--wf-raw-part=false", "--wf-save=" + probePath]);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            if (await probe.WaitExitAsync(timeout.Token) != 0) throw new IOException("Raw-part behavior probe failed: " + probe.Output);
            report.RawPartOrConfirmed = PacketMatrix.VerifyRawPartOr(File.ReadAllText(probePath));
            if (!report.RawPartOrConfirmed) throw new InvalidOperationException("Local wf-raw-part behavior differs from expected OR composition.");
        }
        var candidate = IsolationFilter.Candidate(original, [41001]);
        var main = IsolationFilter.Main(original, 41000, 41999);
        File.WriteAllText(Path.Combine(directory, "candidate.filter"), candidate);
        File.WriteAllText(Path.Combine(directory, "main.filter"), main);
        var assertions = PacketMatrix.Verify(original, candidate, main);
        report.Profiles.Add(new(profile.DisplayName, snapshot.Fingerprint, assertions));
        Console.WriteLine($"{profile.DisplayName}: filter exported, {assertions} matrix cases passed");
    }
    // A successful static inspection is never a live isolation certificate.
    report.Status = "StaticPassed_LiveNotVerified";
    return 0;
}
catch (Exception e)
{
    report.Status = "Blocked"; report.Error = e.ToString();
    Console.Error.WriteLine(e.Message);
    return 1;
}
finally
{
    report.FinishedAt = DateTimeOffset.UtcNow;
    JsonFiles.Save(Path.Combine(output, "inspection.json"), report);
    Console.WriteLine(JsonSerializer.Serialize(new { report.Status, report.ProfilesFound, Verified = report.Profiles.Count, report.Administrator }));
}

public sealed record InspectedProfile(string Name, string Fingerprint, int MatrixCases);
public sealed class InspectionReport
{
    public string FlowsealFolder { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public bool Administrator { get; set; }
    public int ProfilesFound { get; set; }
    public bool RawPartOrConfirmed { get; set; }
    public string[] ParseErrors { get; set; } = [];
    public List<InspectedProfile> Profiles { get; set; } = [];
    public string Status { get; set; } = "NotVerified";
    public string? Error { get; set; }
}
