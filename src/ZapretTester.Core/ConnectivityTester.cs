using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ZapretTester.Core;

public sealed class ConnectivityTester
{
    public async Task<EndpointResult> TestAsync(TestTarget target, PortLease lease, TimeSpan timeout, CancellationToken cancellation)
    {
        var dnsWatch = Stopwatch.StartNew();
        IPAddress? address = null;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        attempt.CancelAfter(timeout);
        try
        {
            address = (await Dns.GetHostAddressesAsync(target.Url.DnsSafeHost, AddressFamily.InterNetwork, attempt.Token)).FirstOrDefault()
                ?? throw new SocketException((int)SocketError.HostNotFound);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            cancellation.ThrowIfCancellationRequested();
            return new(target.Name, EndpointStatus.Failed, lease.Port, null, TimeSpan.Zero, dnsWatch.Elapsed, null, FailureStage.Dns, e.Message);
        }
        dnsWatch.Stop();
        var watch = Stopwatch.StartNew();
        var connected = false;
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
            ConnectCallback = async (_, ct) =>
            {
                if (connected) throw new IOException("Connection reuse/reconnect is forbidden for a port lease.");
                connected = true;
                await lease.Socket.ConnectAsync(new IPEndPoint(address, target.Url.Port), ct);
                return new NetworkStream(lease.Socket, ownsSocket: false);
            }
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Head, target.Url)
        { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        request.Headers.ConnectionClose = true;
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
            var code = (int)response.StatusCode;
            var status = target.Accepts(code) ? EndpointStatus.Available : code == 429 || code >= 500 ? EndpointStatus.ServerWarning : EndpointStatus.Failed;
            return new(target.Name, status, lease.Port, address.ToString(), watch.Elapsed, dnsWatch.Elapsed, code,
                status == EndpointStatus.Available ? null : FailureStage.Http, status == EndpointStatus.Available ? null : $"HTTP {code}");
        }
        catch (OperationCanceledException)
        {
            cancellation.ThrowIfCancellationRequested();
            return new(target.Name, EndpointStatus.Failed, lease.Port, address.ToString(), watch.Elapsed, dnsWatch.Elapsed, null, FailureStage.Timeout, "Attempt timed out.");
        }
        catch (HttpRequestException e)
        {
            var stage = e.HttpRequestError switch { HttpRequestError.SecureConnectionError => FailureStage.Tls,
                HttpRequestError.ConnectionError => FailureStage.Tcp, _ => FailureStage.Http };
            return new(target.Name, EndpointStatus.Failed, lease.Port, address.ToString(), watch.Elapsed, dnsWatch.Elapsed, null, stage, e.Message);
        }
    }
}
