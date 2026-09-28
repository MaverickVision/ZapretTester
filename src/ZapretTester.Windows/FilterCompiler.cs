using ZapretTester.Core;

namespace ZapretTester.Windows;

public static class FilterCompiler
{
    private static readonly HashSet<string> UnsafeOptions = new(StringComparer.Ordinal)
    { "--wf-save", "--wf-raw", "--wf-raw-part", "--pidfile", "--hostlist-auto", "--hostlist-auto-debug", "--debug", "--daemon", "--chdir", "--user" };
    public static void ValidateSourceArguments(IReadOnlyList<string> arguments)
    {
        ProfileParser.ValidateArguments(arguments);
        foreach (var arg in arguments)
        {
            var option = arg.Split('=', 2)[0];
            if (UnsafeOptions.Contains(option) || !arg.StartsWith("--", StringComparison.Ordinal))
                throw new UnsupportedProfileException("Unsupported launch/capture/output argument: " + option);
        }
    }
    public static async Task<string> ExportAsync(SnapshotProfile profile, string output, CancellationToken token)
    {
        ValidateSourceArguments(profile.Arguments);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using var process = OwnedProcess.Start(profile.Executable, profile.WorkingDirectory,
            profile.Arguments.Append("--wf-save=" + Path.GetFullPath(output)));
        var code = await process.WaitExitAsync(timeout.Token);
        if (code != 0 || !File.Exists(output)) throw new IOException($"winws filter export failed ({code}): {process.Output}");
        var filter = (await File.ReadAllTextAsync(output, token)).Trim();
        WinDivert.Compile(filter);
        return filter;
    }
    public static IReadOnlyList<string> IsolatedArguments(SnapshotProfile profile, string filterPath) =>
        profile.Arguments.Where(a => !a.StartsWith("--wf-", StringComparison.Ordinal))
            .Prepend("--wf-raw=@" + Path.GetFullPath(filterPath)).ToArray();
}
