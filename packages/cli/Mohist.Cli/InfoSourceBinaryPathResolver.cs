namespace Mohist.Cli;

internal static class InfoSourceBinaryPathResolver
{
    internal static string? ExtractBinaryDirectory(IReadOnlyList<string> tokens)
    {
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
        var path = InfoExecStartTokenizer.StripQuotes(candidate);
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
}
