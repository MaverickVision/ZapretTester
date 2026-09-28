using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ZapretTester.Windows;

public sealed class ProcessJob : SafeHandleZeroOrMinusOneIsInvalid
{
    private static ProcessJob? lifetimeRoot;
    private static readonly object lifetimeGate = new();
    public static void EnsureLifetimeRoot()
    {
        lock (lifetimeGate)
        {
            if (lifetimeRoot != null) return;
            var root = new ProcessJob();
            try { using var current = Process.GetCurrentProcess(); root.Assign(current); lifetimeRoot = root; }
            catch { root.Dispose(); throw; }
            // Intentionally held until OS process teardown. Child processes inherit
            // this job atomically, covering the Process.Start -> per-child-job gap.
        }
    }
    public ProcessJob() : base(true)
    {
        SetHandle(CreateJobObject(0, null));
        if (IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        // JOBOBJECT_EXTENDED_LIMIT_INFORMATION, x64 ABI, LimitFlags at offset 16.
        var info = new byte[144];
        BitConverter.GetBytes(0x2000u).CopyTo(info, 16); // KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(handle, 9, info, (uint)info.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    protected override bool ReleaseHandle() => CloseHandle(handle);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(nint job, int informationClass, byte[] info, uint length);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}

public sealed class OwnedProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly ProcessJob job;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringBuilder output = new();
    public int Id => process.Id;
    public bool HasExited => process.HasExited;
    public string Output { get { lock (output) return output.ToString(); } }
    private OwnedProcess(Process process, ProcessJob job) { this.process = process; this.job = job; }
    public static OwnedProcess Start(string executable, string workingDirectory, IEnumerable<string> arguments)
    {
        ProcessJob.EnsureLifetimeRoot();
        var info = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var job = new ProcessJob();
        var owner = new OwnedProcess(process, job);
        process.OutputDataReceived += (_, e) => owner.OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => owner.OnLine(e.Data);
        process.Exited += (_, _) => owner.ready.TrySetException(new IOException("winws exited before capture was ready."));
        try
        {
            process.Start();
            job.Assign(process);
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            return owner;
        }
        catch
        {
            try { if (process.Id > 0 && !process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            job.Dispose(); process.Dispose(); throw;
        }
    }
    private void OnLine(string? line)
    {
        if (line == null) return;
        lock (output) { if (output.Length < 2_000_000) output.AppendLine(line); }
        if (line.Contains("windivert initialized. capture is started.", StringComparison.Ordinal)) ready.TrySetResult();
    }
    public async Task WaitReadyAsync(CancellationToken token)
    {
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        if (HasExited) throw new IOException("winws exited after initialization.");
    }
    public async Task<int> WaitExitAsync(CancellationToken token)
    {
        await process.WaitForExitAsync(token);
        process.WaitForExit(); // flush asynchronous output callbacks
        return process.ExitCode;
    }
    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
        job.Dispose(); process.Dispose();
    }
}
