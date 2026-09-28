using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ZapretTester.Core;

namespace ZapretTester.Windows;

// WinDivert 2.x ABI: 16-byte common prefix followed by a 64-byte union.
[StructLayout(LayoutKind.Explicit, Size = 80)]
public struct DivertAddress
{
    [FieldOffset(0)] public long Timestamp;
    [FieldOffset(8)] public uint Bits;
    [FieldOffset(16)] public long OpenTimestamp;
    [FieldOffset(24)] public uint ProcessId;
    [FieldOffset(28)] public uint ReflectLayer;
    [FieldOffset(32)] public ulong ReflectFlags;
    [FieldOffset(40)] public short Priority;
    public readonly uint Event => (Bits >> 8) & 255;
    public static DivertAddress Packet(bool outbound, bool ipv6 = false) => new() { Bits = (outbound ? 1u << 17 : 0) | (ipv6 ? 1u << 20 : 0) };
}

public sealed class DivertHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public DivertHandle() : base(true) { }
    protected override bool ReleaseHandle() => WinDivert.Close(handle);
}

public static class WinDivert
{
    private static nint library;
    private static string? loadedPath;
    public static void Load(string path)
    {
        path = Path.GetFullPath(path);
        FlowsealSnapshot.RejectReparse(path);
        if (library != 0)
        {
            if (!string.Equals(path, loadedPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("One WinDivert library per process.");
            return;
        }
        library = NativeLibrary.Load(path);
        loadedPath = path;
        NativeLibrary.SetDllImportResolver(typeof(WinDivert).Assembly, (name, _, _) => name == "WinDivert.dll" ? library : 0);
    }
    public static DivertHandle Open(string filter, uint layer, short priority, ulong flags)
    {
        var handle = OpenNative(filter, layer, priority, flags);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, $"WinDivertOpen(layer={layer}): " + new Win32Exception(error).Message); }
        return handle;
    }
    public static void Compile(string filter)
    {
        if (!CompileNative(filter, 0, null, 0, out var error, out var position))
            throw new ArgumentException($"WinDivert filter error at {position}: {Marshal.PtrToStringAnsi(error)}");
    }
    public static bool Evaluate(string filter, byte[] packet, bool outbound, bool ipv6 = false)
    {
        var address = DivertAddress.Packet(outbound, ipv6);
        return EvalNative(filter, packet, (uint)packet.Length, ref address);
    }
    public static bool EvaluatePacket(string filter, byte[] packet, ref DivertAddress address) => EvalNative(filter, packet, (uint)packet.Length, ref address);
    public static string Format(byte[] compiled, uint layer)
    {
        var buffer = new byte[65536];
        if (!FormatNative(compiled, layer, buffer, (uint)buffer.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return System.Text.Encoding.ASCII.GetString(buffer).TrimEnd('\0');
    }
    [DllImport("WinDivert.dll", EntryPoint = "WinDivertOpen", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern DivertHandle OpenNative(string filter, uint layer, short priority, ulong flags);
    [DllImport("WinDivert.dll", EntryPoint = "WinDivertClose", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Close(nint handle);
    [DllImport("WinDivert.dll", EntryPoint = "WinDivertShutdown", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] public static extern bool Shutdown(DivertHandle handle, uint how);
    [DllImport("WinDivert.dll", EntryPoint = "WinDivertRecv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] public static extern bool Receive(DivertHandle handle, [Out] byte[] packet, uint size, out uint received, out DivertAddress address);
    [DllImport("WinDivert.dll", EntryPoint = "WinDivertHelperCompileFilter", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CompileNative(string filter, uint layer, byte[]? buffer, uint size, out nint error, out uint position);
    [DllImport("WinDivert.dll", EntryPoint = "WinDivertHelperEvalFilter", CharSet = CharSet.Ansi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EvalNative(string filter, byte[] packet, uint size, ref DivertAddress address);
    [DllImport("WinDivert.dll", EntryPoint = "WinDivertHelperFormatFilter", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FormatNative(byte[] filter, uint layer, [Out] byte[] buffer, uint size);
}
