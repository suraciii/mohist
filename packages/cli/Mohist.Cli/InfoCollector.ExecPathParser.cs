using System.Text;

namespace Mohist.Cli;

internal sealed partial class InfoCollector
{
    internal static string? ResolveSourcePath(SystemdUnitParser.SystemdUnitFields unit)
    {
        if (!string.IsNullOrWhiteSpace(unit.WorkingDirectory))
            return unit.WorkingDirectory;

        if (!string.IsNullOrWhiteSpace(unit.ExecStart))
        {
            var fromProject = ExtractProjectPath(unit.ExecStart!);
            if (!string.IsNullOrWhiteSpace(fromProject))
                return fromProject;

            var fromBinary = ExtractBinaryDirectory(unit.ExecStart!);
            if (!string.IsNullOrWhiteSpace(fromBinary))
                return fromBinary;
        }
        return null;
    }

    internal static string? ExtractProjectPath(string execStart)
    {
        var tokens = TokenizeExecStart(execStart);
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if (tokens[i] == "--project" && i + 1 < tokens.Count)
            {
                var path = StripQuotes(tokens[i + 1]);
                if (IsLikelyPath(path))
                {
                    var fullPath = Path.GetFullPath(path).Replace('\\', '/');
                    if (fullPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                        || fullPath.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
                    {
                        var dir = Path.GetDirectoryName(fullPath);
                        if (!string.IsNullOrWhiteSpace(dir))
                            return dir.Replace('\\', '/');
                    }
                    return fullPath;
                }
            }
        }
        return null;
    }

    internal static string? ExtractBinaryDirectory(string execStart)
    {
        var tokens = TokenizeExecStart(execStart);
        if (tokens.Count == 0)
            return null;
        var first = tokens[0];
        if (string.IsNullOrWhiteSpace(first) || first.StartsWith('-'))
            return null;
        if (IsRuntimeWrapper(first))
        {
            for (var i = 1; i < tokens.Count; i++)
            {
                if (tokens[i].StartsWith('-'))
                {
                    if (tokens[i] == "--" && i + 1 < tokens.Count)
                        return ExtractBinaryDirectoryFromCandidate(tokens[i + 1]);
                    continue;
                }
                if (IsRuntimeSubcommand(tokens[i]))
                    return null;
                return ExtractBinaryDirectoryFromCandidate(tokens[i]);
            }
            return null;
        }
        return ExtractBinaryDirectoryFromCandidate(first);
    }

    private static bool IsRuntimeWrapper(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var basename = Path.GetFileName(token);
        return basename == "dotnet" || basename == "node" || token == "/usr/bin/env";
    }

    private static bool IsRuntimeSubcommand(string token)
    {
        return token is "run" or "exec" or "start" or "serve" or "dev";
    }

    private static string? ExtractBinaryDirectoryFromCandidate(string candidate)
    {
        var path = StripQuotes(candidate);
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (!IsAbsoluteOrProjectOrScript(path))
            return null;
        try
        {
            if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrWhiteSpace(dir))
                    return dir.Replace('\\', '/');
            }
            var full = Path.GetFullPath(path);
            if (Directory.Exists(full))
                return full.Replace('\\', '/');
            var dir2 = Path.GetDirectoryName(full);
            if (!string.IsNullOrWhiteSpace(dir2))
                return dir2.Replace('\\', '/');
        }
        catch
        {
        }
        return null;
    }

    private static bool IsAbsoluteOrProjectOrScript(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith('/')) return true;
        if (path.StartsWith("./") || path.StartsWith("../")) return true;
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':') return true;
        if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static List<string> TokenizeExecStart(string execStart)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inSingle = false;
        var inDouble = false;
        foreach (var c in execStart)
        {
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == ' ' && !inSingle && !inDouble)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }

    private static string StripQuotes(string value)
    {
        if (value.Length >= 2 && ((value[0] == '\'' && value[^1] == '\'') || (value[0] == '"' && value[^1] == '"')))
            return value[1..^1];
        return value;
    }

    private static bool IsLikelyPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.StartsWith('-')) return false;
        return value.StartsWith('/')
            || value.StartsWith("./")
            || value.StartsWith("../")
            || (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':');
    }
}