using System.Text;

namespace Mohist.Cli;

internal sealed class SkillAssetService
{
    private static readonly IReadOnlyList<BuiltInSkillDefinition> BuiltIns =
    [
        new("mohist", "执行 Mohist 当前 .NET 后端/API/Web 相关操作。当用户要求创建、查看、启动、审批、关闭 issue，查看项目状态或日志，或任何涉及 Mohist issue/workflow 的操作时使用。旧 Node CLI 已移除。"),
        new("mohist-explore", "从产品和用户视角探索 mohist 项目，发现功能缺陷、体验问题、设计机会和价值增长点。当用户想要探索代码库、发现改进点、审查用户体验、思考功能设计、或无目标地巡检产品时使用。触发词包括 \"explore\"、\"探索\"、\"巡检\"、\"找问题\"、\"体验审查\"、\"功能设计\"、\"产品思考\"。"),
    ];

    private readonly string _assetRoot;

    public SkillAssetService()
        : this(Environment.GetEnvironmentVariable("MOHIST_SKILLS_DIR"))
    {
    }

    internal SkillAssetService(string? overrideAssetRoot)
    {
        _assetRoot = ResolveAssetRoot(overrideAssetRoot);
    }

    public IReadOnlyList<BuiltInSkillMetadata> ListVisibleSkills() =>
        BuiltIns
            .OrderBy(skill => skill.Name, StringComparer.Ordinal)
            .Select(skill => new BuiltInSkillMetadata(skill.Name, skill.Description))
            .ToArray();

    public SkillAssetReadResult GetSkill(string name, bool includeSupplementaryFiles)
    {
        var definition = BuiltIns.FirstOrDefault(skill => string.Equals(skill.Name, name, StringComparison.Ordinal));
        if (definition is null)
            return SkillAssetReadResult.Fail($"Unknown Mohist built-in skill '{name}'.");

        var skillDirectory = Path.Combine(_assetRoot, definition.Name);
        var skillFile = Path.Combine(skillDirectory, "SKILL.md");

        if (!File.Exists(skillFile))
            return SkillAssetReadResult.Fail($"Built-in skill asset '{definition.Name}' is missing SKILL.md at '{skillFile}'.");

        var content = File.ReadAllText(skillFile, Encoding.UTF8);
        if (!TryReadFrontmatter(content, out var frontmatterName, out var frontmatterDescription))
            return SkillAssetReadResult.Fail($"Built-in skill asset '{definition.Name}' has invalid AgentSkills frontmatter.");

        if (!string.Equals(frontmatterName, definition.Name, StringComparison.Ordinal))
            return SkillAssetReadResult.Fail($"Built-in skill asset '{definition.Name}' has mismatched frontmatter name '{frontmatterName}'.");

        if (!string.Equals(frontmatterDescription, definition.Description, StringComparison.Ordinal))
            return SkillAssetReadResult.Fail($"Built-in skill asset '{definition.Name}' has mismatched frontmatter description.");

        var supplementaryFiles = includeSupplementaryFiles
            ? EnumerateSupplementaryFiles(skillDirectory)
            : [];

        return SkillAssetReadResult.Success(new BuiltInSkillContent(
            definition.Name,
            definition.Description,
            skillDirectory,
            content,
            supplementaryFiles));
    }

    private static string ResolveAssetRoot(string? overrideAssetRoot)
    {
        if (!string.IsNullOrWhiteSpace(overrideAssetRoot))
        {
            var candidate = Path.GetFullPath(overrideAssetRoot);
            if (Directory.Exists(candidate))
                return candidate;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "skill-data"));
    }

    private static IReadOnlyList<SkillSupplementaryFile> EnumerateSupplementaryFiles(string skillDirectory)
    {
        var files = new List<SkillSupplementaryFile>();
        foreach (var folderName in new[] { "references", "templates" })
        {
            var folderPath = Path.Combine(skillDirectory, folderName);
            if (!Directory.Exists(folderPath))
                continue;

            foreach (var file in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories)
                         .OrderBy(path => Path.GetRelativePath(skillDirectory, path), StringComparer.Ordinal))
            {
                files.Add(new SkillSupplementaryFile(
                    Path.GetRelativePath(skillDirectory, file).Replace(Path.DirectorySeparatorChar, '/'),
                    File.ReadAllText(file, Encoding.UTF8)));
            }
        }

        return files;
    }

    private static bool TryReadFrontmatter(string content, out string? name, out string? description)
    {
        name = null;
        description = null;

        using var reader = new StringReader(content);
        if (!string.Equals(reader.ReadLine(), "---", StringComparison.Ordinal))
            return false;

        for (var line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            if (string.Equals(line, "---", StringComparison.Ordinal))
                return !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(description);

            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
                continue;

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (string.Equals(key, "name", StringComparison.Ordinal))
                name = value;
            else if (string.Equals(key, "description", StringComparison.Ordinal))
                description = value;
        }

        return false;
    }
}

internal sealed record BuiltInSkillMetadata(string Name, string Description);

internal sealed record BuiltInSkillContent(
    string Name,
    string Description,
    string DirectoryPath,
    string SkillMarkdown,
    IReadOnlyList<SkillSupplementaryFile> SupplementaryFiles);

internal sealed record SkillSupplementaryFile(string RelativePath, string Content);

internal sealed record SkillAssetReadResult(bool Found, string? Error, BuiltInSkillContent? Skill)
{
    public static SkillAssetReadResult Success(BuiltInSkillContent skill) => new(true, null, skill);

    public static SkillAssetReadResult Fail(string error) => new(false, error, null);
}

internal sealed record BuiltInSkillDefinition(string Name, string Description);
