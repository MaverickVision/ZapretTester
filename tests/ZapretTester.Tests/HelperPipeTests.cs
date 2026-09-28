using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using ZapretTester.App;
using Xunit;

namespace ZapretTester.Tests;

public sealed class HelperPipeTests
{
    [Fact]
    public async Task OlderHelperIsDisconnectedBeforeAnyTestCanBeSent()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity(); security.SetOwner(identity.User!);
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        var name = "ZapretTester.Old." + Guid.NewGuid().ToString("N");
        using var server = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 4096, security);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accept = server.WaitForConnectionAsync(timeout.Token);
        await using var client = new HelperClient();
        var method = typeof(HelperClient).GetMethod("OpenAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var opening = (Task)method.Invoke(client, [name, "test-secret", Environment.ProcessId, 5000])!;
        await accept;
        using var reader = new StreamReader(server, leaveOpen: true);
        using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
        var request = System.Text.Json.JsonSerializer.Deserialize<ZapretTester.Core.HelperRequest>((await reader.ReadLineAsync(timeout.Token))!)!;
        Assert.Equal("State", request.Command);
        var state = new ZapretTester.Core.HelperState("", "Compatibility", null, false, new(), [], null, "", ProtocolVersion: 3);
        await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new ZapretTester.Core.HelperMessage("Reply", state, RequestId: request.RequestId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => opening);
        Assert.False(client.Connected);
        await Assert.ThrowsAsync<IOException>(() => client.SendAsync("Test"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserOwnedPipeRequiresExpectedServer(bool wrongProcess)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.SetOwner(identity.User!);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        var name = "ZapretTester.Test." + Guid.NewGuid().ToString("N");
        using var server = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accept = server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        await accept;
        if (wrongProcess)
            Assert.Throws<UnauthorizedAccessException>(() => HelperPipeIdentity.Validate(client, -1));
        else
            HelperPipeIdentity.Validate(client, Environment.ProcessId);
    }
}
