using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZapretTester.Core;
using ZapretTester.Helper;
using ZapretTester.Windows;

if (args.Length != 4) return 2;
var folder = Path.GetFullPath(args[0]);
var pipeName = args[1];
var nonce = args[2];
var sid = WindowsIdentity.GetCurrent().User!.Value;
if (args[3] != sid || !Regex.IsMatch(nonce, "^[A-Fa-f0-9]{64}$")
    || !pipeName.StartsWith("ZapretTester.", StringComparison.Ordinal) || !Regex.IsMatch(pipeName, "^[A-Za-z0-9.\\-]+$")) return 2;
if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 740;
using var singleton = new Mutex(true, @"Local\ZapretTester.Helper." + sid, out var created);
if (!created) return 3;
var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZapretTester");
Directory.CreateDirectory(data);
FlowsealSnapshot.RejectReparse(data);
var protectedRoot = ProtectedWorkspace.Create(sid);
try
{
    await using var runtime = new TesterRuntime(folder, protectedRoot, data);
    if (runtime.Settings.ManagedEnabled && runtime.Settings.MainProfileId != null)
    {
        try { await runtime.StartManagedAsync(runtime.Settings.MainProfileId, runtime.Settings.MainSuite, runtime.Settings.MainRegion); }
        catch (Exception error) { runtime.ReportStartupFailure(error); }
        // Keep IPC available so the GUI can select another profile or restore the service.
    }
    var stopping = false;
    while (!stopping)
    {
        // CurrentUserOnly uses the elevated token's owner on Windows (Administrators),
        // which denies a medium-integrity GUI. Pin both owner and DACL to the user SID.
        var pipeSecurity = new PipeSecurity();
        var user = new SecurityIdentifier(sid);
        pipeSecurity.SetOwner(user);
        pipeSecurity.SetAccessRuleProtection(true, false);
        pipeSecurity.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        await using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 4096, pipeSecurity);
        using var idle = new CancellationTokenSource();
        if (!runtime.HasMain) idle.CancelAfter(TimeSpan.FromSeconds(60));
        try { await pipe.WaitForConnectionAsync(idle.Token); }
        catch (OperationCanceledException) { break; }
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        using var writes = new SemaphoreSlim(1);
        using var connection = new CancellationTokenSource();
        var authenticated = false;
        async Task Send(HelperMessage message, CancellationToken token = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, connection.Token);
            await writes.WaitAsync(linked.Token);
            try { await writer.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), linked.Token); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            finally { writes.Release(); }
        }
        var updates = new StateMessagePump((state, token) => Send(new("State", state), token));
        void Changed(HelperState state) { if (authenticated) updates.Enqueue(state); }
        runtime.Changed += Changed;
        try
        {
            while (pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;
                if (line.Length > 32768) throw new IOException("Request too large.");
                var request = JsonSerializer.Deserialize<HelperRequest>(line) ?? throw new IOException("Invalid request.");
                if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(request.Nonce ?? ""), Encoding.ASCII.GetBytes(nonce)))
                    throw new UnauthorizedAccessException("Invalid session nonce.");
                authenticated = true;
                try
                {
                    switch (request.Command)
                    {
                        case "State": break;
                        case "SelectSuite": runtime.SelectSuite(request.Suite, request.Region); break;
                        case "SelectVariant": runtime.SelectVariant(request.Variant ?? throw new ArgumentException("Variant required.")); break;
                        case "Refresh": runtime.Refresh(); break;
                        case "Settings": runtime.UpdateSettings(request.Settings ?? throw new ArgumentException("Settings required.")); break;
                        case "StartManaged": await runtime.ApplyManagedVariantAsync(request.ProfileId, request.Variant ?? throw new ArgumentException("Variant required.")); break;
                        case "ResumeManaged": await runtime.ResumeManagedAsync(); break;
                        case "PrepareFlowseal": await runtime.PrepareFlowsealAsync(request.ProfileId); break;
                        case "ExpandPortPool": await runtime.ExpandPortPoolAsync(request.Settings ?? throw new ArgumentException("Settings required.")); break;
                        case "StopMain": await runtime.StopMainAsync(); break;
                        case "RestoreExternal": await runtime.RestoreExternalAsync(); break;
                        case "TestBatch": runtime.StartBatch(request.ProfileIds ?? throw new ArgumentException("Profiles required."), request.Variant?.Selections ?? request.TestSuites ?? throw new ArgumentException("Suites required.")); break;
                        case "Test": runtime.StartTests(request.ProfileId, request.Suite, request.Region); break;
                        case "Cancel": await runtime.CancelAsync(); break;
#if DEBUG
                        case "FailSharedProcessForAcceptance": runtime.FailSharedProcessForAcceptance = true; break;
                        case "FailNextMainForAcceptance": runtime.FailNextMainForAcceptance = true; break;
                        case "FailMainPersistenceForAcceptance": runtime.FailMainPersistenceForAcceptance = true; break;
                        case "FailFinalSaveForAcceptance": runtime.FailFinalSaveForAcceptance = true; break;
                        case "DelayFinalSaveForAcceptance": runtime.DelayFinalSaveForAcceptance = true; break;
                        case "CrashForAcceptance": Environment.Exit(77); break;
#endif
                        case "Shutdown":
                            if (runtime.HasMain) throw new InvalidOperationException("Stop the managed main before shutting down the helper.");
                            stopping = true; break;
                        default: throw new ArgumentException("Unknown command.");
                    }
                    await Send(new("Reply", runtime.State(), RequestId: request.RequestId));
                }
                catch (Exception e) { await Send(new("Error", runtime.State(), e.Message, request.RequestId)); }
                if (stopping) break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        finally
        {
            runtime.Changed -= Changed;
            authenticated = false;
            await runtime.CancelAsync();
            connection.Cancel();
            await updates.DisposeAsync();
            try { writer.Dispose(); } catch (IOException) { }
        }
        if (!runtime.HasMain) stopping = true;
    }
    return 0;
}
catch (Exception e)
{
    JsonFiles.Save(Path.Combine(data, "helper-error.json"), new { Time = DateTimeOffset.Now, Error = e.ToString() });
    return 1;
}
