using Mohist.Server.Workflow.Prompts.Domain;
using Mohist.Server.Workflow.Prompts.Infrastructure;

namespace Mohist.Server.Workflow.Prompts;

public sealed class FilePromptLoader : IPromptLoader
{
    private readonly string _promptsDirectory;
    private readonly IPromptFileStore _files;
    private readonly Dictionary<string, SystemTemplate> _cache = new(StringComparer.Ordinal);

    public FilePromptLoader(string? promptsDirectory = null, IPromptFileStore? files = null)
    {
        _promptsDirectory = promptsDirectory ?? ResolveDefaultPromptsDirectory();
        _files = files ?? RealPromptFileStore.Instance;
    }

    public Dictionary<string, string> LoadAll()
    {
        var templates = LoadAllTemplates();
        var result = new Dictionary<string, string>(templates.Count, StringComparer.Ordinal);
        foreach (var (key, template) in templates)
            result[key] = template.Body;
        return result;
    }

    public Dictionary<string, SystemTemplate> LoadAllTemplates()
    {
        if (_cache.Count > 0)
            return new Dictionary<string, SystemTemplate>(_cache, StringComparer.Ordinal);

        var result = new Dictionary<string, SystemTemplate>(StringComparer.Ordinal);
        if (!_files.DirectoryExists(_promptsDirectory))
            return result;

        foreach (var filePath in _files.EnumeratePromptFiles(_promptsDirectory))
        {
            var key = Path.GetFileNameWithoutExtension(filePath);
            var content = _files.ReadAllText(filePath);
            var (frontmatter, body) = PromptFrontmatterParser.Parse(content, key);
            var template = new SystemTemplate(
                key,
                frontmatter.Name ?? key,
                frontmatter.Description,
                frontmatter.Tags,
                frontmatter.Stage,
                body);
            result[key] = template;
            _cache[key] = template;
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

public interface IPromptFileStore
{
    bool DirectoryExists(string path);
    IEnumerable<string> EnumeratePromptFiles(string path);
    string ReadAllText(string path);
}

internal sealed class RealPromptFileStore : IPromptFileStore
{
    public static readonly RealPromptFileStore Instance = new();

    private RealPromptFileStore()
    {
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IEnumerable<string> EnumeratePromptFiles(string path) => Directory.EnumerateFiles(path, "*.prompt");

    public string ReadAllText(string path) => File.ReadAllText(path);
}
