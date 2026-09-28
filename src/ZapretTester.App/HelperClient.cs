using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ZapretTester.Core;

namespace ZapretTester.App;

public sealed class HelperClient : IAsyncDisposable
{
    private NamedPipeClientStream? pipe;
    private StreamWriter? writer;
    private Task? receive;
    private readonly SemaphoreSlim writes = new(1);
    private readonly SemaphoreSlim connects = new(1);
    private readonly CancellationTokenSource stop = new();
    private string nonce = "";
    private readonly ConcurrentDictionary<string, TaskCompletionSource> replies = new();
    public event Action<HelperMessage>? Message;
    public bool Connected => pipe?.IsConnected == true;
    public async Task ConnectAsync(string folder)
    {
        await connects.WaitAsync(stop.Token);
        try { await ConnectCoreAsync(folder); }
        finally { connects.Release(); }
    }
    private async Task ConnectCoreAsync(string folder)
    {
        if (Connected) return;
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZapretTester");
        Directory.CreateDirectory(data);
        var sessionPath = Path.Combine(data, "session.json");
        HelperSession? session = null;
        try
        {
            if (File.Exists(sessionPath))
            {
                var candidate = JsonSerializer.Deserialize<HelperSession>(File.ReadAllText(sessionPath));
                if (candidate != null)
                {
                    using var p = Process.GetProcessById(candidate.Pid);
                    if (!p.HasExited && candidate.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)) session = candidate;
                }
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or JsonException or Win32Exception) { }
        if (session != null)
        {
            try { await OpenAsync(session.Pipe, session.Nonce, session.Pid, 2500); return; }
            catch (Exception e) when (e is TimeoutException or IOException) { pipe?.Dispose(); }
        }
        var helper = Path.Combine(AppContext.BaseDirectory, "ZapretTester.Helper.exe");
        if (!File.Exists(helper)) throw new FileNotFoundException("ZapretTester.Helper.exe отсутствует рядом с приложением.");
        var name = "ZapretTester." + Guid.NewGuid().ToString("N");
        nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var info = new ProcessStartInfo(helper) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { Path.GetFullPath(folder), name, nonce, sid }) info.ArgumentList.Add(arg);
        using var process = await Task.Run(() => Process.Start(info)) ?? throw new IOException("Не удалось запустить Helper.");
        JsonFiles.Save(sessionPath, new HelperSession(process.Id, 0, name, nonce, folder));
        try { await OpenAsync(name, nonce, process.Id, 30000); }
        catch
        {
            var errorPath = Path.Combine(data, "helper-error.json");
            if (File.Exists(errorPath)) throw new IOException("Helper не подключился. " + File.ReadAllText(errorPath));
            throw;
        }
    }
    private async Task OpenAsync(string name, string secret, int expectedProcessId, int timeout)
    {
        var connection = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await connection.ConnectAsync(timeout, stop.Token);
            HelperPipeIdentity.Validate(connection, expectedProcessId);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
        pipe = connection;
        nonce = secret;
        writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        receive = ReceiveAsync(pipe);
        await SendAsync("State");
    }
    private async Task ReceiveAsync(NamedPipeClientStream connectedPipe)
    {
        using var reader = new StreamReader(connectedPipe, new UTF8Encoding(false), false, 4096, true);
        try
        {
            while (!stop.IsCancellationRequested && await reader.ReadLineAsync(stop.Token) is { } line)
            {
                var value = JsonSerializer.Deserialize<HelperMessage>(line);
                if (value != null)
                {
                    if (value.State is { ProtocolVersion: < 4 })
                    {
                        if (value.RequestId != null && replies.TryRemove(value.RequestId, out var incompatible))
                            incompatible.TrySetException(new InvalidOperationException("Работает устаревший Helper. Закройте старую версию и перезапустите приложение."));
                        connectedPipe.Dispose();
                        return;
                    }
                    Message?.Invoke(value);
                    if (value.RequestId != null && replies.TryRemove(value.RequestId, out var reply))
                    {
                        if (value.Kind == "Error") reply.TrySetException(new IOException(value.Error)); else reply.TrySetResult();
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            foreach (var reply in replies.Values) reply.TrySetException(new IOException("Соединение с Helper закрыто."));
            replies.Clear();
            Message?.Invoke(new("Disconnected", Error: "Соединение с Helper закрыто."));
        }
    }
    public async Task SendAsync(string command, string? profileId = null, ApplicationSettings? settings = null, TestSuiteId suite = TestSuiteId.Discord, string? region = null, IReadOnlyList<string>? profileIds = null, IReadOnlyList<SuiteSelection>? testSuites = null)
    {
        if (!Connected || writer == null) throw new IOException("Helper не подключён.");
        var id = Guid.NewGuid().ToString("N");
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        replies[id] = reply;
        try
        {
            await writes.WaitAsync();
            try { await writer.WriteLineAsync(JsonSerializer.Serialize(new HelperRequest(command, nonce, profileId, settings, id, suite, region, profileIds, testSuites))); }
            finally { writes.Release(); }
            await reply.Task.WaitAsync(TimeSpan.FromSeconds(60), stop.Token);
        }
        finally { replies.TryRemove(id, out _); }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); pipe?.Dispose();
        if (receive != null) await receive;
        try { writer?.Dispose(); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        writes.Dispose(); stop.Dispose();
    }
}

