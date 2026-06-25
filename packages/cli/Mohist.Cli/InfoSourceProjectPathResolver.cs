namespace Mohist.Cli;

internal static class InfoSourceProjectPathResolver
{
    internal static string? ExtractProjectPath(IReadOnlyList<string> tokens)
    {
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if (tokens[i] == "--project" && i + 1 < tokens.Count)
            {
                var path = InfoExecStartTokenizer.StripQuotes(tokens[i + 1]);
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
