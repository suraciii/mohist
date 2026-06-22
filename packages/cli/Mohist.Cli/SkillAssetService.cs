namespace Mohist.Cli;

internal sealed class SkillAssetService
{
    private static readonly IReadOnlyList<BuiltInSkillDefinition> BuiltIns =
    [
        new("mohist", "执行 Mohist 当前 .NET 后端/API/Web 相关操作。当用户要求创建、查看、启动、审批、关闭 issue 或 epic，查看项目状态或日志，或任何涉及 Mohist issue/epic/workflow 的操作时使用。旧 Node CLI 已移除。"),
        new("mohist-explore", "把模糊的产品想法提炼成清晰的、有边界的 Mohist issue 需求文档。当用户带着一句话、一个模糊念头或未沉淀的改进意图，需要探索当前产品形态和技术实现，最终产出一份用户视角、产品视角、领域视角三段协作的 PRD 时使用。触发词包括 \"提炼需求\"、\"写 PRD\"、\"沉淀 issue\"、\"需求文档\"、\"探索\"、\"完善 issue\"。"),
    ];

    internal static IReadOnlyList<string> BuiltInSkillNames =>
        BuiltIns.Select(skill => skill.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
    private readonly IFileSystem _fileSystem;
    private readonly SkillAssetRootResolution _resolution;
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
        _resolution = resolver.Resolve();
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
        : this(fileSystem, BuildResolution(fileSystem, overrideAssetRoot))
    {
    }

    private static SkillAssetRootResolution BuildResolution(IFileSystem fileSystem, string? overrideAssetRoot)
    {
        var resolvedRoot = string.IsNullOrWhiteSpace(overrideAssetRoot)
            ? Path.Combine(AppContext.BaseDirectory, "skill-data")
            : NormalizePath(overrideAssetRoot);

        if (!fileSystem.DirectoryExists(resolvedRoot))
        {
            return SkillAssetRootResolution.Failed(
                resolvedRoot,
                SkillAssetRootSource.Override,
                $"Skill asset root '{resolvedRoot}' does not exist.");
        }

        return SkillAssetRootResolution.Selected(resolvedRoot, SkillAssetRootSource.Override);
    }

    private static string NormalizePath(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    public string? AssetRoot => _assetRoot;

    public SkillAssetRootSource AssetRootSource => _resolution.Source;

    public string? ResolverDiagnostic => _resolution.DiagnosticSummary;

    public IReadOnlyList<BuiltInSkillMetadata> ListVisibleSkills() =>
        DiscoverSkills()
            .OrderBy(skill => skill.Name, StringComparer.Ordinal)
            .Select(skill => new BuiltInSkillMetadata(skill.Name, skill.Description))
            .ToArray();

    public SkillAssetReadResult GetSkill(string name, bool includeSupplementaryFiles)
    {
        if (string.IsNullOrWhiteSpace(name))
            return SkillAssetReadResult.Fail("A built-in skill name is required.");

        if (_assetRoot is null)
            return SkillAssetReadResult.Fail(BuildUnresolvedDiagnostic(name));

        var skill = DiscoverSkills().FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
        if (skill is null)
            return SkillAssetReadResult.Fail($"Unknown Mohist built-in skill '{name}'.");

        var skillFile = Path.Combine(skill.Directory, "SKILL.md");
        if (!_fileSystem.Exists(skillFile))
            return SkillAssetReadResult.Fail(BuildMissingAssetDiagnostic(skill.Name, skillFile));

        var content = _fileSystem.ReadAllText(skillFile);

        var supplementaryFiles = includeSupplementaryFiles
            ? EnumerateSupplementaryFiles(skill.Directory).ToArray()
            : Array.Empty<SkillSupplementaryFile>();

        return SkillAssetReadResult.Success(new BuiltInSkillContent(
            skill.Name,
            skill.Description,
            skill.Directory,
            content,
            supplementaryFiles));
    }

    private List<DiscoveredSkill> DiscoverSkills()
    {
        var skills = new List<DiscoveredSkill>();

        if (string.IsNullOrWhiteSpace(_assetRoot) || !_fileSystem.DirectoryExists(_assetRoot))
            return skills;

        var rootFull = NormalizePath(_assetRoot);

        IEnumerable<string> skillFiles;
        try
        {
            skillFiles = _fileSystem.EnumerateFiles(rootFull, "SKILL.md", SearchOption.AllDirectories);
        }
        catch
        {
            return skills;
        }

        foreach (var file in skillFiles)
        {
            var parentDir = Path.GetDirectoryName(file);
            if (parentDir is null)
                continue;

            // Only accept SKILL.md that is a direct child of a skill directory under root:
            //   <root>/<skillName>/SKILL.md
            var grandparent = NormalizePath(Path.GetDirectoryName(parentDir)!);
            if (!string.Equals(grandparent, rootFull, StringComparison.OrdinalIgnoreCase))
                continue;

            var content = _fileSystem.ReadAllText(file);
            if (TryReadFrontmatter(content, out var frontmatterName, out var frontmatterDescription)
                && !string.IsNullOrWhiteSpace(frontmatterName))
            {
                skills.Add(new DiscoveredSkill(frontmatterName!, frontmatterDescription ?? string.Empty, parentDir));
            }
        }

        return skills;
    }

    private string BuildMissingAssetDiagnostic(string skillName, string skillFile)
    {
        const string repairGuidance = "Repair by running 'mo update' or 'scripts/install-mo.sh'.";
        return $"Built-in skill asset '{skillName}' is missing SKILL.md at '{skillFile}'. {repairGuidance}";
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

    private sealed record DiscoveredSkill(string Name, string Description, string Directory);
}

internal sealed record BuiltInSkillDefinition(string Name, string Description);

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
