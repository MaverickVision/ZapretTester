using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace ZapretTester.Windows;

public static class NetworkEnvironment
{
    public static bool IsApplicationConnection(TcpState state) => state is not
        (TcpState.TimeWait or TcpState.FinWait1 or TcpState.FinWait2 or TcpState.Closing or TcpState.LastAck or TcpState.Closed or TcpState.DeleteTcb);
    public static async Task<string> ValidatePoolAsync(int first, int last, CancellationToken token = default)
    {
        var dynamicPorts = await Netsh(token, "interface", "ipv4", "show", "dynamicport", "tcp");
        var values = Regex.Matches(dynamicPorts, @":\s*(\d+)\s*$", RegexOptions.Multiline).Select(m => int.Parse(m.Groups[1].Value)).ToArray();
        if (values.Length != 2) throw new InvalidOperationException("Cannot determine the Windows dynamic TCP range.");
        var dynamicEnd = values[0] + values[1] - 1;
        if (first <= dynamicEnd && last >= values[0]) throw new InvalidOperationException($"Test pool overlaps Windows dynamic TCP ports {values[0]}–{dynamicEnd}.");
        var excluded = await Netsh(token, "interface", "ipv4", "show", "excludedportrange", "protocol=tcp");
        foreach (Match m in Regex.Matches(excluded, @"^\s*(\d+)\s+(\d+)\s*\*?\s*$", RegexOptions.Multiline))
            if (first <= int.Parse(m.Groups[2].Value) && last >= int.Parse(m.Groups[1].Value))
                throw new InvalidOperationException("Test pool overlaps a Windows excluded TCP range.");
        // FIN/CLOSING/TIME_WAIT are kernel cleanup after the application closed its
        // socket. Exclusive bind still skips their ports; they must not block the entire pool.
        var ip = IPGlobalProperties.GetIPGlobalProperties();
        if (ip.GetActiveTcpConnections().Any(c => IsApplicationConnection(c.State) && c.LocalEndPoint.Port >= first && c.LocalEndPoint.Port <= last)
            || ip.GetActiveTcpListeners().Any(p => p.Port >= first && p.Port <= last))
            throw new InvalidOperationException("The reserved test pool contains an active connection/listener. Choose an unused pool.");
        var global = await Netsh(token, "interface", "tcp", "show", "global");
        return $"Windows dynamic TCP: {values[0]}–{dynamicEnd}\n{global.Trim()}";
    }
    private static async Task<string> Netsh(CancellationToken token, params string[] arguments)
    {
        token.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Cannot start netsh diagnostics.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        if (process.ExitCode != 0) throw new IOException("netsh diagnostic failed: " + await error);
        return await output;
    }
}
