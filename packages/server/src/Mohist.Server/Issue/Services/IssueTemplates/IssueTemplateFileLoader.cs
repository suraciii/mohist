using Mohist.Server.Infrastructure;
using Mohist.Server.Workflow.Services.Prompts;

namespace Mohist.Server.Issue.Services.IssueTemplates;

internal sealed record BuiltinTemplateEntry(
    string Name,
    string Description,
    string FilePath,
    Func<string> ReadContent)
{
    public string LoadContent() => ReadContent();
}

internal sealed class IssueTemplateFileLoader
{
    private const int MaxFrontmatterLines = 100;

    private readonly string _directory;
    private readonly Func<string, string, IEnumerable<string>> _enumerateFiles;
    private readonly Func<string, TextReader> _openText;
    private readonly Func<string, string> _readAllText;

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

    public static IssueTemplateFileLoader FromEmbeddedResources()
    {
        const string root = "embedded://mohist-issue-templates";
        const string prefix = "Mohist.Server.IssueTemplates.";
        var assembly = typeof(IssueTemplateFileLoader).Assembly;
        var files = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal)
                && name.EndsWith(".md", StringComparison.Ordinal))
            .ToDictionary(
                name => $"{root}/{name[prefix.Length..]}",
                name => AssemblyTextResources.Read(assembly, name),
                StringComparer.Ordinal);

        if (files.Count == 0)
            throw new InvalidOperationException("No embedded Mohist issue templates were found.");

        return new IssueTemplateFileLoader(
            root,
            (_, _) => files.Keys.OrderBy(path => path, StringComparer.Ordinal),
            path => new StringReader(files[path]),
            path => files[path]);
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
        for (var lineCount = 1; lineCount < MaxFrontmatterLines && (line = reader.ReadLine()) is not null; lineCount++)
        {
            lines.Add(line);
            if (line == "---")
                return string.Join('\n', lines);
        }

        throw new InvalidDataException($"Issue template '{filePath}' has unterminated frontmatter.");
    }
}
