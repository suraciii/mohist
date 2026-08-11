using Mohist.Server.Workflow.Domain.Prompts;
using Mohist.Server.Workflow.Services.Prompts;

namespace Mohist.Server.Workflow.Services.Prompts;

public sealed class FilePromptLoader : IPromptLoader
{
    private readonly string _promptsDirectory;
    private readonly IPromptFileStore _files;
    private readonly Dictionary<string, SystemTemplate> _cache = new(StringComparer.Ordinal);

    public FilePromptLoader(
        string? promptsDirectory = null,
        IPromptFileStore? files = null,
        string? applicationRoot = null)
    {
        _promptsDirectory = promptsDirectory ?? ResolveDefaultPromptsDirectory(applicationRoot ?? AppContext.BaseDirectory);
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
            throw new DirectoryNotFoundException($"Built-in prompt directory not found: '{_promptsDirectory}'.");

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

    private static string ResolveDefaultPromptsDirectory(string applicationRoot) =>
        Path.Combine(applicationRoot, "Workflow", "Services", "Prompts", "builtins");
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
