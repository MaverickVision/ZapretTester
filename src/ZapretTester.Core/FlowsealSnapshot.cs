using System.Security.Cryptography;
using System.Text;

namespace ZapretTester.Core;

public sealed record SnapshotProfile(ZapretProfile Source, string Executable, string WorkingDirectory,
    IReadOnlyList<string> Arguments, string Fingerprint, string? SourceFingerprint = null);

// A snapshot freezes the actual selected bundle, including mutable ACTIVE_*.bin.
public sealed class FlowsealSnapshot
{
    private static readonly Dictionary<string, string> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ipset-exclude-user.txt"] = "203.0.113.113/32\r\n",
        ["list-general-user.txt"] = "# Never leave this file empty\r\ndomain.example.abc\r\n",
        ["list-exclude-user.txt"] = "domain.example.abc\r\n"
    };
    public static SnapshotProfile Create(ZapretProfile profile, string destination)
    {
        var root = Path.GetDirectoryName(profile.BatFilePath)!;
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination)) throw new IOException("Snapshot destination must be new.");
        RejectReparse(root);
        Directory.CreateDirectory(destination);
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string Copy(string source)
        {
            ProfileParser.EnsureUnder(root, source);
            var relative = Path.GetRelativePath(root, source);
            var target = Path.Combine(destination, relative);
            if (hashes.ContainsKey(relative)) return target;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(source))
            {
                RejectReparse(source);
                // Holding this handle denies concurrent write/delete while copying.
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }
            else if (Path.GetDirectoryName(source)!.Equals(Path.Combine(root, "lists"), StringComparison.OrdinalIgnoreCase)
                && Defaults.TryGetValue(Path.GetFileName(source), out var value)) File.WriteAllText(target, value);
            else throw new FileNotFoundException("Required Flowseal resource is missing.", source);
            using var stream = File.OpenRead(target);
            hashes[relative] = Convert.ToHexString(SHA256.HashData(stream));
            return target;
        }
        var executable = Copy(profile.WinwsPath);
        foreach (var file in new[] { "WinDivert.dll", "WinDivert64.sys", "cygwin1.dll" }) Copy(Path.Combine(Path.GetDirectoryName(profile.WinwsPath)!, file));
        var arguments = profile.Arguments.Select(arg =>
        {
            var eq = arg.IndexOf('=');
            if (eq < 0 || !ProfileParser.FileOptions.Contains(arg[..eq])) return arg;
            var value = arg[(eq + 1)..];
            return value == "!" || value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? arg : arg[..(eq + 1)] + Copy(value);
        }).ToArray();
        var fingerprint = CombineFingerprint(profile, hashes);
        JsonFiles.Save(Path.Combine(destination, "manifest.json"), new { profile.Id, profile.FileHash, fingerprint, files = hashes });
        return new(profile, executable, Path.GetDirectoryName(executable)!, arguments, fingerprint, CombineFingerprint(profile, hashes, false));
    }
    public static string Fingerprint(ZapretProfile profile, FingerprintContext? context = null)
        => ComputeFingerprint(profile, context, true);

    // Common variants add their own selected endpoint policies. Legacy results
    // retain the original target-bearing hash above for migration/history.
    public static string SourceFingerprint(ZapretProfile profile, FingerprintContext? context = null)
        => ComputeFingerprint(profile, context, false);

    private static string ComputeFingerprint(ZapretProfile profile, FingerprintContext? context, bool legacyTargets)
    {
        var root = Path.GetDirectoryName(profile.BatFilePath)!;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { profile.WinwsPath };
        foreach (var name in new[] { "WinDivert.dll", "WinDivert64.sys", "cygwin1.dll" }) files.Add(Path.Combine(Path.GetDirectoryName(profile.WinwsPath)!, name));
        foreach (var arg in profile.Arguments)
        {
            var eq = arg.IndexOf('=');
            if (eq < 0 || !ProfileParser.FileOptions.Contains(arg[..eq])) continue;
            var value = arg[(eq + 1)..];
            if (value != "!" && !value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) files.Add(value);
        }
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            ProfileParser.EnsureUnder(root, file);
            string hash;
            if (File.Exists(file))
            {
                if (context != null) hash = context.HashFile(file);
                else
                {
                    RejectReparse(file);
                    using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                    hash = Convert.ToHexString(SHA256.HashData(input));
                }
            }
            else if (Path.GetDirectoryName(file)!.Equals(Path.Combine(root, "lists"), StringComparison.OrdinalIgnoreCase) && Defaults.TryGetValue(Path.GetFileName(file), out var value))
                hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
            else throw new FileNotFoundException("Required Flowseal resource is missing.", file);
            hashes[Path.GetRelativePath(root, file)] = hash;
        }
        return CombineFingerprint(profile, hashes, legacyTargets);
    }
    private static string CombineFingerprint(ZapretProfile profile, SortedDictionary<string, string> hashes, bool legacyTargets = true)
    {
        var evidence = profile.FileHash + "\n" + string.Join("\n", profile.Arguments) + "\n"
            + string.Join("\n", hashes.Select(p => p.Key + "=" + p.Value));
        if (legacyTargets) evidence += "\nHEAD;IPv4;HTTP1.1;Policy1;" + string.Join(";", TestTarget.Defaults.Select(t => t.Name + ":" + t.Url + ":" + t.ServiceRoot));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence)));
    }
    public static void RejectReparse(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse paths are unsupported: " + current);
    }
}
