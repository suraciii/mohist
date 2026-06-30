using Mohist.Server.Workflow.Services.Prompts;

namespace Mohist.Server.Issue.Services.IssueTemplates;

internal sealed record BuiltinTemplateEntry(
    string Name,
    string Description,
    string FilePath,
    Func<string>? ReadContent = null)
{
    public string LoadContent() => ReadContent?.Invoke() ?? File.ReadAllText(FilePath);
}

internal sealed class IssueTemplateFileLoader
{
    private readonly string _directory;
    private readonly Func<string, string, IEnumerable<string>> _enumerateFiles;
    private readonly Func<string, TextReader> _openText;
    private readonly Func<string, string> _readAllText;

    public IssueTemplateFileLoader(string directory)
        : this(directory, Directory.EnumerateFiles, File.OpenText, File.ReadAllText)
    {
    }

    internal IssueTemplateFileLoader(
        string directory,
        Func<string, string, IEnumerable<string>> enumerateFiles,
        Func<string, TextReader> openText,
        Func<string, string> readAllText)
    {
        _directory = directory;
        _enumerateFiles = enumerateFiles;
        _openText = openText;
        _readAllText = readAllText;
    }

    public Dictionary<string, BuiltinTemplateEntry> Discover()
    {
        var result = new Dictionary<string, BuiltinTemplateEntry>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var filePath in _enumerateFiles(_directory, "*.md"))
            {
                var id = Path.GetFileNameWithoutExtension(filePath);
                if (string.Equals(id, "README", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var content = ReadFrontmatterOnly(filePath);
                    var (frontmatter, _) = PromptFrontmatterParser.Parse(content, id);

                    if (string.IsNullOrWhiteSpace(frontmatter.Name))
                        continue;

                    result[id] = new BuiltinTemplateEntry(
                        frontmatter.Name,
                        frontmatter.Description ?? string.Empty,
                        filePath,
                        () => _readAllText(filePath));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Failed to discover issue template '{id}' from '{filePath}'.", ex);
                }
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Failed to discover issue templates from '{_directory}'.", ex);
        }

        return result;
    }

    private string ReadFrontmatterOnly(string filePath)
    {
        using var reader = _openText(filePath);
        var firstLine = reader.ReadLine();
        if (firstLine is null)
            return string.Empty;

        var lines = new List<string> { firstLine };
        if (firstLine != "---")
            return string.Join('\n', lines);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lines.Add(line);
            if (line == "---")
                break;
        }

        return string.Join('\n', lines);
    }
}
