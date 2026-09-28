using System.Net;
using System.Net.Sockets;

namespace ZapretTester.Core;

public static class IsolationFilter
{
    public static string LocalPorts(IEnumerable<int> ports)
    {
        var values = ports.Distinct().Order().ToArray();
        if (values.Length == 0 || values.Any(p => p is < 1024 or > 65535)) throw new ArgumentOutOfRangeException(nameof(ports));
        return "ip and tcp and ((outbound and (" + string.Join(" or ", values.Select(p => $"tcp.SrcPort == {p}"))
            + ")) or (inbound and (" + string.Join(" or ", values.Select(p => $"tcp.DstPort == {p}")) + ")))";
    }
    public static string Pool(int start, int end)
    {
        if (start < 1024 || end > 65535 || end < start) throw new ArgumentOutOfRangeException(nameof(start));
        return $"ip and tcp and ((outbound and tcp.SrcPort >= {start} and tcp.SrcPort <= {end}) or (inbound and tcp.DstPort >= {start} and tcp.DstPort <= {end}))";
    }
    public static string Candidate(string original, IEnumerable<int> ports) => $"({Require(original)}) and ({LocalPorts(ports)})";
    public static string Main(string original, int start, int end)
    {
        _ = Pool(start, end); // Validate the range.
        // WinDivert 2.x cannot negate a parenthesized expression. Apply De Morgan
        // to L(pool); preserve all non-IPv4/TCP traffic from the original filter.
        return $"({Require(original)}) and (!ip or !tcp or (outbound and (tcp.SrcPort < {start} or tcp.SrcPort > {end})) or (inbound and (tcp.DstPort < {start} or tcp.DstPort > {end})))";
    }
    private static string Require(string filter) => string.IsNullOrWhiteSpace(filter) ? throw new ArgumentException("Empty original filter.") : filter;
}

// One allocator is one run. Even released leases cannot be reused in this run.
public sealed class PortAllocator(int first, int last) : IDisposable
{
    private readonly object gate = new();
    private readonly HashSet<int> used = [];
    private readonly List<PortLease> leases = [];
    private bool disposed;
    public PortLease Allocate()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (first < 1024 || last > 65535 || first > last) throw new ArgumentOutOfRangeException(nameof(first));
            for (var port = first; port <= last; port++)
            {
                if (!used.Add(port)) continue;
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true, NoDelay = true };
                try
                {
                    socket.Bind(new IPEndPoint(IPAddress.Any, port));
                    var lease = new PortLease(port, socket);
                    leases.Add(lease);
                    return lease;
                }
                catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
                { socket.Dispose(); }
                catch { socket.Dispose(); throw; }
            }
            throw new InvalidOperationException("No unused bindable port remains in the test pool; start a new run after TIME_WAIT clears.");
        }
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; foreach (var lease in leases) lease.Dispose(); }
    }
}

public sealed class PortLease(int port, Socket socket) : IDisposable
{
    public int Port { get; } = port;
    public Socket Socket { get; } = socket;
    public void Dispose() => Socket.Dispose();
}
