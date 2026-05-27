using System.Text.Json;

namespace Mohist.Server.Workflow.Prompts;

public sealed class FilePromptLoader : IPromptLoader
{
    private readonly string _promptsDirectory;
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);

    public FilePromptLoader(string? promptsDirectory = null)
    {
        _promptsDirectory = promptsDirectory ?? ResolveDefaultPromptsDirectory();
    }

    public string Load(string name)
    {
        if (_cache.TryGetValue(name, out var cached))
            return cached;

        var filePath = Path.Combine(_promptsDirectory, $"{name}.md");
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Prompt file not found: {filePath}", filePath);

        var content = File.ReadAllText(filePath);
        _cache[name] = content;
        return content;
    }

    public Dictionary<string, string> LoadAll()
    {
        if (_cache.Count > 0)
            return new Dictionary<string, string>(_cache, StringComparer.Ordinal);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(_promptsDirectory))
            return result;

        foreach (var filePath in Directory.EnumerateFiles(_promptsDirectory, "*.md"))
        {
            var name = Path.GetFileNameWithoutExtension(filePath);
            var content = File.ReadAllText(filePath);
            result[name] = content;
            _cache[name] = content;
        }

        return result;
    }

    private static string ResolveDefaultPromptsDirectory()
    {
        // Try to find the Prompts directory relative to the executing assembly
        var assemblyLocation = typeof(FilePromptLoader).Assembly.Location;
        var assemblyDir = Path.GetDirectoryName(assemblyLocation);
        if (assemblyDir is not null)
        {
            var promptsDir = Path.Combine(assemblyDir, "Workflow", "Prompts");
            if (Directory.Exists(promptsDir))
                return promptsDir;

            // Try parent directories for development scenarios
            var current = assemblyDir;
            for (var i = 0; i < 5; i++)
            {
                var candidate = Path.Combine(current, "Workflow", "Prompts");
                if (Directory.Exists(candidate))
                    return candidate;

                var srcCandidate = Path.Combine(current, "src", "Mohist.Server", "Workflow", "Prompts");
                if (Directory.Exists(srcCandidate))
                    return srcCandidate;

                var parent = Directory.GetParent(current);
                if (parent is null) break;
                current = parent.FullName;
            }
        }

        // Fallback: use the known source location
        var baseDir = AppContext.BaseDirectory;
        var fallback = Path.Combine(baseDir, "..", "..", "..", "..", "src", "Mohist.Server", "Workflow", "Prompts");
        return Path.GetFullPath(fallback);
    }
}
