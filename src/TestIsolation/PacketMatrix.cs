using System.Buffers.Binary;
using ZapretTester.Core;
using ZapretTester.Windows;

namespace TestIsolation;

public static class PacketMatrix
{
    public static bool VerifyRawPartOr(string filter) => WinDivert.Evaluate(filter, Create(false, false, true, 41001, 100, 24), true);
    public static int Verify(string original, string candidate, string main)
    {
        WinDivert.Compile(candidate); WinDivert.Compile(main);
        var count = 0;
        foreach (var ipv6 in new[] { false, true })
        foreach (var udp in new[] { false, true })
        foreach (var outbound in new[] { false, true })
        foreach (var port in new[] { 41001, 41002, 41999, 42000, 50000 })
        foreach (var payload in new[] { 0, 100 })
        foreach (var flags in new byte[] { 2, 18, 24, 17, 4 })
        {
            var packet = Create(ipv6, udp, outbound, port, payload, flags);
            var baseline = WinDivert.Evaluate(original, packet, outbound, ipv6);
            var expectedCandidate = baseline && !ipv6 && !udp && port == 41001;
            var expectedMain = baseline && (ipv6 || udp || port < 41000 || port > 41999);
            if (WinDivert.Evaluate(candidate, packet, outbound, ipv6) != expectedCandidate
                || WinDivert.Evaluate(main, packet, outbound, ipv6) != expectedMain)
                throw new InvalidOperationException($"Filter matrix mismatch: ipv6={ipv6} udp={udp} out={outbound} port={port} payload={payload} flags={flags}");
            count++;
        }
        // Verify that malformed syntax isn't being mistaken for an always-false evaluator.
        var tcp = Create(false, false, true, 41001, 100, 24);
        if (!WinDivert.Evaluate(IsolationFilter.LocalPorts([41001]), tcp, true))
            throw new InvalidOperationException("Native filter evaluator positive control failed.");
        return count;
    }
    private static byte[] Create(bool ipv6, bool udp, bool outbound, int localPort, int payload, byte flags)
    {
        int header = ipv6 ? 40 : 20, transport = udp ? 8 : 20;
        var bytes = new byte[header + transport + payload];
        if (ipv6)
        {
            bytes[0] = 0x60; BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), (ushort)(transport + payload));
            bytes[6] = udp ? (byte)17 : (byte)6; bytes[7] = 64; bytes[8] = 0x20; bytes[24] = 0x20;
        }
        else
        {
            bytes[0] = 0x45; BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), (ushort)bytes.Length);
            bytes[8] = 64; bytes[9] = udp ? (byte)17 : (byte)6;
            bytes[12] = 192; bytes[13] = 0; bytes[14] = 2; bytes[15] = 1;
            bytes[16] = 203; bytes[17] = 0; bytes[18] = 113; bytes[19] = 1;
        }
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(header), (ushort)(outbound ? localPort : 443));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(header + 2), (ushort)(outbound ? 443 : localPort));
        if (udp) BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(header + 4), (ushort)(transport + payload));
        else { bytes[header + 12] = 0x50; bytes[header + 13] = flags; }
        return bytes;
    }
}
