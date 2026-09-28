using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ZapretTester.Core;

public sealed record ZapretProfile(string Id, string DisplayName, string BatFilePath,
    string WinwsPath, string WorkingDirectory, IReadOnlyList<string> Arguments, string FileHash);
public sealed record ProfileEntry(string FilePath, ZapretProfile? Profile, string? Error);
public sealed class UnsupportedProfileException(string message) : Exception(message);

public sealed class ProfileParser
{
    private static readonly HashSet<string> ServiceCalls = new(StringComparer.OrdinalIgnoreCase)
    { "status_zapret", "check_updates", "load_game_filter", "load_user_lists" };

    public ZapretProfile Parse(string path)
    {
        path = Path.GetFullPath(path);
        var root = Path.GetDirectoryName(path)!;
        var bytes = File.ReadAllBytes(path);
        string source;
        try { source = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException e) { throw new UnsupportedProfileException("BAT must use UTF-8: " + e.Message); }
        var (tcp, udp) = ReadGameFilter(root);
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { ["GameFilterTCP"] = tcp, ["GameFilterUDP"] = udp };
        string Expand(string value)
        {
            value = Regex.Replace(value, "%~dp0", _ => root + Path.DirectorySeparatorChar, RegexOptions.IgnoreCase);
            value = Regex.Replace(value, "%~n0", _ => Path.GetFileNameWithoutExtension(path), RegexOptions.IgnoreCase);
            value = Regex.Replace(value, "%([^%]+)%", m => vars.TryGetValue(m.Groups[1].Value, out var v)
                ? v : throw new UnsupportedProfileException("Unknown BAT variable: " + m.Value));
            if (value.Contains('%')) throw new UnsupportedProfileException("Unsupported BAT expansion: " + value);
            return value;
        }

        string cwd = root;
        string? executable = null;
        List<string>? arguments = null;
        var joined = Regex.Replace(source, @"\^\r?\n", " ");
        foreach (var raw in joined.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("::") || line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Equals("@echo off", StringComparison.OrdinalIgnoreCase) || line.Equals("echo:", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(line, @"^chcp\s+65001\s*>\s*nul$", RegexOptions.IgnoreCase)) continue;
            RejectShellOperators(line);
            if (line.StartsWith("set ", StringComparison.OrdinalIgnoreCase))
            {
                var assignment = line[4..].Trim();
                if (assignment.StartsWith('"') && assignment.EndsWith('"')) assignment = assignment[1..^1];
                var split = assignment.IndexOf('=');
                if (split <= 0 || !Regex.IsMatch(assignment[..split], @"^[A-Za-z_][A-Za-z0-9_]*$"))
                    throw new UnsupportedProfileException("Unsupported SET: " + line);
                vars[assignment[..split]] = Expand(assignment[(split + 1)..]);
                continue;
            }
            // CMD's CD accepts an unquoted path containing spaces as its entire tail.
            var cd = Regex.Match(line, @"^cd\s+/d\s+(.+)$", RegexOptions.IgnoreCase);
            if (cd.Success)
            {
                cwd = Path.GetFullPath(Expand(cd.Groups[1].Value).Trim('"'), root);
                EnsureUnder(root, cwd);
                continue;
            }
            var tokens = Tokenize(Expand(line));
            if (tokens.Count == 3 && tokens[0].Equals("call", StringComparison.OrdinalIgnoreCase)
                && tokens[1].Equals("service.bat", StringComparison.OrdinalIgnoreCase) && ServiceCalls.Contains(tokens[2])) continue;
            if (tokens.Count == 3 && tokens[0].Equals("cd", StringComparison.OrdinalIgnoreCase) && tokens[1].Equals("/d", StringComparison.OrdinalIgnoreCase))
            {
                cwd = Path.GetFullPath(tokens[2], root);
                EnsureUnder(root, cwd);
                continue;
            }
            if (tokens.Count >= 4 && tokens[0].Equals("start", StringComparison.OrdinalIgnoreCase)
                && tokens[1].StartsWith("zapret: ", StringComparison.OrdinalIgnoreCase) && tokens[2].Equals("/min", StringComparison.OrdinalIgnoreCase))
            {
                if (executable != null) throw new UnsupportedProfileException("Multiple winws launches are unsupported.");
                executable = Path.GetFullPath(tokens[3], cwd);
                EnsureUnder(root, executable);
                if (!Path.GetFileName(executable).Equals("winws.exe", StringComparison.OrdinalIgnoreCase))
                    throw new UnsupportedProfileException("Only winws.exe is supported.");
                arguments = tokens.Skip(4).Select(a => NormalizeFileArgument(a, cwd, root)).ToList();
                ValidateArguments(arguments);
                continue;
            }
            throw new UnsupportedProfileException("Unsupported BAT statement: " + line[..Math.Min(line.Length, 160)]);
        }
        if (executable == null || arguments is not { Count: > 0 }) throw new UnsupportedProfileException("No supported winws launch found.");
        var stem = Path.GetFileNameWithoutExtension(path);
        var match = Regex.Match(stem, @"^general\s*\((.+)\)$", RegexOptions.IgnoreCase);
        var name = match.Success ? match.Groups[1].Value : stem.Equals("general", StringComparison.OrdinalIgnoreCase) ? "General" : stem;
        return new ZapretProfile(Path.GetFileName(path).ToUpperInvariant(), name, path, executable, cwd,
            arguments.AsReadOnly(), Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public static (string Tcp, string Udp) ReadGameFilter(string root)
    {
        var path = Path.Combine(root, "utils", "game_filter.enabled");
        if (!File.Exists(path)) return ("12", "12");
        FlowsealSnapshot.RejectReparse(path);
        var service = Path.Combine(root, "service.bat");
        var rangeFormat = false;
        if (File.Exists(service))
        {
            FlowsealSnapshot.RejectReparse(service);
            // Inspect the format capability as data; never execute service.bat.
            rangeFormat = File.ReadLines(service).Any(l => l.Equals(":validate_game_filter_range", StringComparison.OrdinalIgnoreCase));
        }
        if (!rangeFormat) return File.ReadLines(path).FirstOrDefault() switch
        {
            "all" => ("1024-65535", "1024-65535"), "tcp" => ("1024-65535", "12"), _ => ("12", "1024-65535")
        };
        var mode = "disabled";
        string? tcp = null, udp = null;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(';')) continue;
            var parts = line.TrimStart('=').Split('=', 2);
            var key = parts[0].ToLowerInvariant();
            var value = parts.Length > 1 ? parts[1].TrimStart('=') : "";
            if (key == "mode") mode = value.ToLowerInvariant();
            else if (key == "all") mode = "all";
            else if (key is "tcp" or "udp")
            {
                if (value.Length == 0) mode = key;
                else if (key == "tcp") tcp = value;
                else udp = value;
            }
        }
        return (mode is "all" or "tcp" ? GameFilterRange(tcp) : "12",
            mode is "all" or "udp" ? GameFilterRange(udp) : "12");
    }

    private static string GameFilterRange(string? value)
    {
        value = value?.Replace(" ", "");
        if (value == null || !Regex.IsMatch(value, @"\A[1-9][0-9]{0,4}(-[1-9][0-9]{0,4})?(,[1-9][0-9]{0,4}(-[1-9][0-9]{0,4})?)*\z"))
            return "1024-65535";
        foreach (var item in value.Split(','))
        {
            var bounds = item.Split('-');
            var first = int.Parse(bounds[0]);
            var last = int.Parse(bounds[^1]);
            if (last > 65535 || first > last) return "1024-65535";
        }
        return value;
    }

    internal static void EnsureUnder(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new UnsupportedProfileException("Path escapes Flowseal directory: " + path);
    }

    private static void RejectShellOperators(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '^') { i++; continue; }
            if (line[i] == '"') quoted = !quoted;
            if (!quoted && "&|<>".Contains(line[i])) throw new UnsupportedProfileException("Shell operators are unsupported.");
        }
    }

    public static IReadOnlyList<string> Tokenize(string line)
    {
        var result = new List<string>();
        var token = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '^' && !quoted)
            {
                if (++i >= line.Length) throw new UnsupportedProfileException("Dangling CMD escape.");
                token.Append(line[i]);
            }
            else if (c == '"') quoted = !quoted;
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (token.Length > 0) { result.Add(token.ToString()); token.Clear(); }
            }
            else token.Append(c);
        }
        if (quoted) throw new UnsupportedProfileException("Unclosed quote.");
        if (token.Length > 0) result.Add(token.ToString());
        return result;
    }

    public static readonly IReadOnlySet<string> FileOptions = new HashSet<string>(StringComparer.Ordinal)
    {
        "--hostlist", "--hostlist-exclude", "--ipset", "--ipset-exclude",
        "--dpi-desync-fake-http", "--dpi-desync-fake-tls", "--dpi-desync-fake-quic",
        "--dpi-desync-fake-discord", "--dpi-desync-fake-stun", "--dpi-desync-fake-unknown",
        "--dpi-desync-fake-unknown-udp", "--dpi-desync-split-seqovl-pattern", "--dpi-desync-fakedsplit-pattern"
    };

    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "--wf-tcp", "--wf-udp", "--filter-l3", "--filter-l7", "--filter-tcp", "--filter-udp",
        "--hostlist-domains", "--hostlist-exclude-domains", "--ip-id", "--dpi-desync",
        "--dpi-desync-any-protocol", "--dpi-desync-badseq-increment", "--dpi-desync-cutoff",
        "--dpi-desync-fake-tls-mod", "--dpi-desync-fooling", "--dpi-desync-hostfakesplit-mod",
        "--dpi-desync-repeats", "--dpi-desync-split-pos", "--dpi-desync-split-seqovl"
    };
    public static void ValidateArguments(IReadOnlyList<string> arguments)
    {
        foreach (var arg in arguments)
        {
            if (arg == "--new") continue;
            var eq = arg.IndexOf('=');
            if (eq < 0 || (!ValueOptions.Contains(arg[..eq]) && !FileOptions.Contains(arg[..eq])) || eq == arg.Length - 1)
                throw new UnsupportedProfileException("Unsupported engine option: " + arg.Split('=', 2)[0]);
        }
    }

    // Service installer changes --option=value to --option value. Preserve order/repeats.
    public static IReadOnlyList<ZapretProfile> MatchService(string commandLine, IEnumerable<ZapretProfile> profiles)
    {
        var tokens = Tokenize(commandLine);
        if (tokens.Count == 0) return [];
        var args = new List<string>();
        for (var i = 1; i < tokens.Count; i++)
        {
            var arg = tokens[i];
            if (arg.StartsWith("--", StringComparison.Ordinal) && !arg.Contains('=') && i + 1 < tokens.Count && !tokens[i + 1].StartsWith("--", StringComparison.Ordinal))
                arg += "=" + tokens[++i];
            args.Add(arg);
        }
        return profiles.Where(p => p.WinwsPath.Equals(tokens[0], StringComparison.OrdinalIgnoreCase)
            && p.Arguments.SequenceEqual(args, StringComparer.Ordinal)).ToArray();
    }

    private static string NormalizeFileArgument(string arg, string cwd, string root)
    {
        var split = arg.IndexOf('=');
        if (split < 0 || !FileOptions.Contains(arg[..split])) return arg;
        var value = arg[(split + 1)..];
        if (value == "!" || value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return arg;
        var path = Path.GetFullPath(value, cwd);
        EnsureUnder(root, path);
        return arg[..(split + 1)] + path;
    }
}
public sealed class ProfileScanner
{
    public IReadOnlyList<ProfileEntry> Scan(string folder)
    {
        var parser = new ProfileParser();
        return Directory.EnumerateFiles(folder, "general*.bat", SearchOption.TopDirectoryOnly)
            .OrderBy(p => Path.GetFileNameWithoutExtension(p).Equals("general", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => Path.GetFileNameWithoutExtension(p).TrimEnd(')'), NaturalComparer.Instance)
            .Select(p =>
            {
                try { return new ProfileEntry(p, parser.Parse(p), null); }
                catch (Exception e) when (e is UnsupportedProfileException or IOException or UnauthorizedAccessException or ArgumentException)
                { return new ProfileEntry(p, null, e.Message); }
            }).ToArray();
    }
}

public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();
    public int Compare(string? x, string? y)
    {
        var a = Regex.Split(x ?? "", "([0-9]+)");
        var b = Regex.Split(y ?? "", "([0-9]+)");
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var comparison = i % 2 == 1
                ? CompareDigits(a[i], b[i]) : StringComparer.OrdinalIgnoreCase.Compare(a[i], b[i]);
            if (comparison != 0) return comparison;
        }
        return a.Length.CompareTo(b.Length);
    }
    private static int CompareDigits(string a, string b)
    {
        a = a.TrimStart('0'); b = b.TrimStart('0');
        return a.Length != b.Length ? a.Length.CompareTo(b.Length) : StringComparer.Ordinal.Compare(a, b);
    }
}
