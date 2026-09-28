using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ZapretTester.Core;
using Xunit;

namespace ZapretTester.Tests;

public sealed class ConnectivityTests
{
    private static PortLease Lease()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return new(((IPEndPoint)socket.LocalEndPoint!).Port, socket);
    }

    [Fact]
    public async Task DnsFailureIsSeparateFromTransportFailure()
    {
        using var lease = Lease();
        var result = await new ConnectivityTester().TestAsync(new("DNS", new("https://zapret-tester.invalid"), false), lease, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(FailureStage.Dns, result.FailureStage);
        Assert.Equal(EndpointStatus.Failed, result.Status);
    }

    [Fact]
    public async Task SelfSignedCertificateCannotBecomeSuccess()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var tls = new SslStream(peer.GetStream());
            try { await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert }, deadline.Token); }
            catch (Exception e) when (e is IOException or System.Security.Authentication.AuthenticationException) { }
        }, deadline.Token);
        using var lease = Lease();
        var target = new TestTarget("TLS", new($"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"), false);
        var result = await new ConnectivityTester().TestAsync(target, lease, TimeSpan.FromSeconds(3), deadline.Token);
        await server;
        Assert.Equal(FailureStage.Tls, result.FailureStage);
        Assert.Equal(EndpointStatus.Failed, result.Status);
    }

    [Fact]
    public async Task StalledTlsTimesOutAndCancellationPropagates()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var target = new TestTarget("Timeout", new($"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"), false);
        using var lease = Lease();
        var result = await new ConnectivityTester().TestAsync(target, lease, TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken);
        Assert.Equal(FailureStage.Timeout, result.FailureStage);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        using var another = Lease();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ConnectivityTester().TestAsync(target, another, TimeSpan.FromSeconds(1), cancel.Token));
    }
}
