using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ZapretTester.Core;
using ZapretTester.Windows;

namespace TestIsolation;

public sealed class PacketObserver : IAsyncDisposable
{
    private readonly DivertHandle handle;
    private readonly Task worker;
    private readonly Dictionary<string, string> filters;
    private readonly HashSet<int> aPorts, bPorts;
    private readonly object gate = new();
    private bool disposing;
    public Dictionary<string, int> Counts { get; } = [];
    public string? Error { get; private set; }
    public PacketObserver(int[] a, int[] b, int[] control, Dictionary<string, string> filters)
    {
        aPorts = a.ToHashSet(); bPorts = b.ToHashSet(); this.filters = filters;
        handle = WinDivert.Open(IsolationFilter.LocalPorts(a.Concat(b).Concat(control)), 0, 30000, 1 | 4 | 16);
        worker = Task.Run(Receive);
    }
    private void Receive()
    {
        var buffer = new byte[65536];
        try
        {
            while (WinDivert.Receive(handle, buffer, (uint)buffer.Length, out var length, out var address))
            {
                if (length < 40) continue;
                var packet = buffer.AsSpan(0, (int)length).ToArray();
                var offset = (packet[0] & 15) * 4;
                if (offset < 20 || offset + 20 > packet.Length) continue;
                var outbound = (address.Bits & (1u << 17)) != 0;
                var port = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset + (outbound ? 0 : 2)));
                var flow = aPorts.Contains(port) ? "A" : bPorts.Contains(port) ? "B" : "Control";
                lock (gate)
                {
                    foreach (var pair in filters)
                        if (WinDivert.EvaluatePacket(pair.Value, packet, ref address))
                        {
                            var key = $"{flow}->{pair.Key}:{(outbound ? "out" : "in")}";
                            Counts[key] = Counts.GetValueOrDefault(key) + 1;
                            if (flow != pair.Key && !(flow == "Control" && pair.Key == "Main"))
                                Error = "Cross-capture detected: " + key;
                        }
                }
            }
            if (!disposing) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception e) { Error = e.Message; }
    }
    public void Verify()
    {
        lock (gate)
        {
            if (Error != null) throw new InvalidOperationException(Error);
            foreach (var key in new[] { "A->A:out", "A->A:in", "B->B:out", "B->B:in", "Control->Main:out", "Control->Main:in" })
                if (Counts.GetValueOrDefault(key) == 0) throw new InvalidOperationException("Missing positive packet evidence: " + key);
        }
    }
    public async ValueTask DisposeAsync()
    {
        disposing = true; WinDivert.Shutdown(handle, 1); await worker; handle.Dispose();
    }
}
