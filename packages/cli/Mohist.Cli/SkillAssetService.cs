using System.Text;

namespace Mohist.Cli;

internal sealed class SkillAssetService
{
    private static readonly IReadOnlyList<BuiltInSkillDefinition> BuiltIns =
    [
        new("mohist", "执行 Mohist 当前 .NET 后端/API/Web 相关操作。当用户要求创建、查看、启动、审批、关闭 issue，查看项目状态或日志，或任何涉及 Mohist issue/workflow 的操作时使用。旧 Node CLI 已移除。"),
        new("mohist-explore", "把模糊的产品想法提炼成清晰的、有边界的 Mohist issue 需求文档。当用户带着一句话、一个模糊念头或未沉淀的改进意图，需要探索当前产品形态和技术实现，最终产出一份用户视角、产品视角、领域视角三段协作的 PRD 时使用。触发词包括 \"提炼需求\"、\"写 PRD\"、\"沉淀 issue\"、\"需求文档\"、\"探索\"、\"完善 issue\"。"),
    ];

    internal static IReadOnlyList<string> BuiltInSkillNames =>
        BuiltIns.Select(skill => skill.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();

    private readonly SkillAssetRootResolution _resolution;
    private readonly IFileSystem _fileSystem;
    private readonly string? _assetRoot;

    public SkillAssetService()
        : this(RealFileSystem.Instance, SystemEnvironmentVariableProvider.Instance, SkillAssetRootResolver.CreateDefault(RealFileSystem.Instance, SystemEnvironmentVariableProvider.Instance))
    {
    }

    public SkillAssetService(IFileSystem fileSystem)
        : this(fileSystem, SystemEnvironmentVariableProvider.Instance, SkillAssetRootResolver.CreateDefault(fileSystem, SystemEnvironmentVariableProvider.Instance))
    {
    }

    public SkillAssetService(IFileSystem fileSystem, IEnvironmentVariableProvider environment)
        : this(fileSystem, environment, SkillAssetRootResolver.CreateDefault(fileSystem, environment))
    {
    }

    internal SkillAssetService(string? overrideAssetRoot)
        : this(RealFileSystem.Instance, overrideAssetRoot)
    {
    }

    internal SkillAssetService(IFileSystem fileSystem, IEnvironmentVariableProvider environment, SkillAssetRootResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(resolver);
        _fileSystem = fileSystem;
        _resolution = resolver.Resolve(BuiltInSkillNames);
        _assetRoot = _resolution.AssetRoot;
    }

    internal SkillAssetService(IFileSystem fileSystem, SkillAssetRootResolver resolver)
        : this(fileSystem, SystemEnvironmentVariableProvider.Instance, resolver)
    {
    }

    internal SkillAssetService(IFileSystem fileSystem, SkillAssetRootResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(resolution);
        _fileSystem = fileSystem;
        _resolution = resolution;
        _assetRoot = resolution.AssetRoot;
    }

    internal SkillAssetService(IFileSystem fileSystem, string? overrideAssetRoot)
        : this(fileSystem, BuildTestOnlyResolution(fileSystem, overrideAssetRoot))
    {
    }

    private static SkillAssetRootResolution BuildTestOnlyResolution(IFileSystem fileSystem, string? overrideAssetRoot)
    {
        var resolvedRoot = string.IsNullOrWhiteSpace(overrideAssetRoot)
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "skill-data"))
            : Path.GetFullPath(overrideAssetRoot);

        return SkillAssetRootResolution.Selected(
            resolvedRoot,
            SkillAssetRootSource.Override,
            SkillAssetManifestValidation.Valid());
    }

    public string? AssetRoot => _assetRoot;

    public SkillAssetRootSource AssetRootSource => _resolution.Source;

    public string? ResolverDiagnostic => _resolution.DiagnosticSummary;

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

        if (_assetRoot is null)
            return SkillAssetReadResult.Fail(BuildUnresolvedDiagnostic(definition.Name));

        var skillDirectory = Path.Combine(_assetRoot, definition.Name);
        var skillFile = Path.Combine(skillDirectory, "SKILL.md");

        if (!_fileSystem.Exists(skillFile))
            return SkillAssetReadResult.Fail(BuildMissingAssetDiagnostic(definition.Name, skillFile));

        var content = _fileSystem.ReadAllText(skillFile);
        if (!TryReadFrontmatter(content, out var frontmatterName, out var frontmatterDescription))
            return SkillAssetReadResult.Fail($"Built-in skill asset '{definition.Name}' has invalid AgentSkills frontmatter.");

        if (!string.Equals(frontmatterName, definition.Name, StringComparison.Ordinal))
            return SkillAssetReadResult.Fail($"Built-in skill asset '{definition.Name}' has mismatched frontmatter name '{frontmatterName}'.");

        if (!string.Equals(frontmatterDescription, definition.Description, StringComparison.Ordinal))
            return SkillAssetReadResult.Fail($"Built-in skill asset '{definition.Name}' has mismatched frontmatter description.");

        var supplementaryFiles = includeSupplementaryFiles
            ? EnumerateSupplementaryFiles(skillDirectory).ToArray()
            : Array.Empty<SkillSupplementaryFile>();

        return SkillAssetReadResult.Success(new BuiltInSkillContent(
            definition.Name,
            definition.Description,
            skillDirectory,
            content,
            supplementaryFiles));
    }

    private string BuildMissingAssetDiagnostic(string skillName, string skillFile)
    {
        const string repairGuidance = "Repair by running 'mo update' or 'scripts/install-mo.sh'.";
        var message = $"Built-in skill asset '{skillName}' is missing SKILL.md at '{skillFile}'.";
        var resolverMessage = _resolution.DiagnosticSummary;
        if (string.IsNullOrWhiteSpace(resolverMessage))
            return $"{message} {repairGuidance}";

        if (resolverMessage.Contains("mo update", StringComparison.Ordinal))
            return $"{message} {resolverMessage}";

        return $"{message} {resolverMessage} {repairGuidance}";
    }

    private string BuildUnresolvedDiagnostic(string skillName)
    {
        const string repairGuidance = "Repair by running 'mo update' or 'scripts/install-mo.sh'.";
        var message = $"Built-in skill asset '{skillName}' could not be resolved from any packaged asset root.";
        var resolverMessage = _resolution.DiagnosticSummary;
        if (string.IsNullOrWhiteSpace(resolverMessage))
            return $"{message} {repairGuidance}";

        return $"{message} {resolverMessage}";
    }

    private IEnumerable<SkillSupplementaryFile> EnumerateSupplementaryFiles(string skillDirectory)
    {
        var files = new List<SkillSupplementaryFile>();
        foreach (var folderName in new[] { "references", "templates" })
        {
            var folderPath = Path.Combine(skillDirectory, folderName);
            if (!_fileSystem.DirectoryExists(folderPath))
                continue;

            foreach (var file in _fileSystem.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories)
                         .OrderBy(path => Path.GetRelativePath(skillDirectory, path), StringComparer.Ordinal))
            {
                files.Add(new SkillSupplementaryFile(
                    Path.GetRelativePath(skillDirectory, file).Replace(Path.DirectorySeparatorChar, '/'),
                    _fileSystem.ReadAllText(file)));
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
