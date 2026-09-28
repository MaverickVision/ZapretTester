using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ZapretTester.Windows;

public sealed record ServiceSnapshot(string ImagePath, int StartMode, bool WasRunning);

// Intentionally restricted to the one explicitly selected Flowseal service.
public static class ZapretService
{
    [StructLayout(LayoutKind.Sequential)] private struct Status
    { public uint Type, State, Accepted, Win32Exit, ServiceExit, Checkpoint, WaitHint; }
    public static ServiceSnapshot Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\zapret")
            ?? throw new InvalidOperationException("The zapret service does not exist.");
        return new((string?)key.GetValue("ImagePath") ?? throw new InvalidOperationException("Missing service image path."),
            (int?)key.GetValue("Start") ?? 3, GetState() == 4);
    }
    public static uint GetState() => WithService(service =>
    {
        if (!QueryServiceStatus(service, out var status)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return status.State;
    });
    public static async Task StopAsync(CancellationToken token)
    {
        if (GetState() == 1) return;
        WithService(service =>
        {
            if (!ControlService(service, 1, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return true;
        });
        await WaitAsync(1, token);
    }
    public static async Task StartAsync(CancellationToken token)
    {
        if (GetState() == 4) return;
        WithService(service =>
        {
            if (!StartService(service, 0, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return true;
        });
        await WaitAsync(4, token);
    }
    public static void SetStartMode(int mode)
    {
        if (mode is not (2 or 3 or 4)) throw new ArgumentOutOfRangeException(nameof(mode));
        WithService(service =>
        {
            if (!ChangeServiceConfig(service, uint.MaxValue, (uint)mode, uint.MaxValue, null, null, 0, null, null, null, null))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return true;
        });
    }
    private static async Task WaitAsync(uint target, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (GetState() != target) await Task.Delay(100, timeout.Token);
    }
    private static T WithService<T>(Func<nint, T> action)
    {
        var manager = OpenSCManager(null, null, 1);
        if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenService(manager, "zapret", 2 | 4 | 16 | 32);
            if (service == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { return action(service); } finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenService(nint manager, string name, uint access);
    [DllImport("advapi32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatus(nint service, out Status status);
    [DllImport("advapi32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ControlService(nint service, uint control, out Status status);
    [DllImport("advapi32", EntryPoint = "StartServiceW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StartService(nint service, uint argc, nint argv);
    [DllImport("advapi32")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(nint service);
    [DllImport("advapi32", EntryPoint = "ChangeServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfig(nint service, uint type, uint start, uint error,
        string? binary, string? group, nint tag, string? dependencies, string? account, string? password, string? display);
}
