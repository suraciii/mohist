using Mohist.Server.Workflow.Services.Prompts;

namespace Mohist.Server.Issue.Services.IssueTemplates;

internal sealed record BuiltinTemplateEntry(string Name, string Description, string Body);

internal sealed class IssueTemplateFileLoader
{
    private readonly string _directory;

    public IssueTemplateFileLoader(string directory)
    {
        _directory = directory;
    }

    public Dictionary<string, BuiltinTemplateEntry> Load()
    {
        var result = new Dictionary<string, BuiltinTemplateEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in Directory.EnumerateFiles(_directory, "*.md"))
        {
            var id = Path.GetFileNameWithoutExtension(filePath);
            if (string.Equals(id, "README", StringComparison.OrdinalIgnoreCase))
                continue;

            var content = File.ReadAllText(filePath);
            var (frontmatter, body) = PromptFrontmatterParser.Parse(content, id);

            if (string.IsNullOrWhiteSpace(frontmatter.Name))
                continue;

            result[id] = new BuiltinTemplateEntry(
                frontmatter.Name,
                frontmatter.Description ?? string.Empty,
                body);
        }

        return result;
    }
}
