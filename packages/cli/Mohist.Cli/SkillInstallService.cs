using System.Text;

namespace Mohist.Cli;

internal sealed class SkillInstallService
{
    private readonly SkillAssetService _assets;
    private readonly IFileSystem _fileSystem;
    private readonly TextWriter _output;
    private readonly TextWriter _error;

    public SkillInstallService(SkillAssetService assets, IFileSystem fileSystem, TextWriter output, TextWriter error)
    {
        _assets = assets;
        _fileSystem = fileSystem;
        _output = output;
        _error = error;
    }

    public async Task<int> InstallAsync(SkillInstallOptions options)
    {
        var validationError = Validate(options);
        if (validationError is not null)
        {
            await _error.WriteLineAsync(validationError);
            return 1;
        }

        var targetRoot = ResolveTargetRoot(options);
        var results = new List<SkillInstallResult>();
        foreach (var skill in _assets.ListVisibleSkills())
        {
            SkillInstallResult result;
            if (options.Hermes)
            {
                var resolved = await TryInstallHermesSkillAsync(targetRoot, skill.Name);
                if (resolved.Error is not null)
                {
                    await _error.WriteLineAsync(resolved.Error);
                    return 1;
                }
                result = resolved.Result;
            }
            else
            {
                result = await InstallDiscoveryStubAsync(targetRoot, skill);
            }
            results.Add(result);
        }

        await WriteSummaryAsync(options, targetRoot, results);
        return 0;
    }

    private static string ResolveTargetRoot(SkillInstallOptions options)
    {
        if (options.Hermes)
        {
            var hermesHome = Environment.GetEnvironmentVariable("HERMES_HOME");
            var hermesBasePath = string.IsNullOrWhiteSpace(hermesHome)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hermes")
                : Path.GetFullPath(hermesHome);
            return Path.Combine(hermesBasePath, "skills");
        }

        var basePath = options.TargetPath is null
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(options.TargetPath);
        var installFolder = options.Claude ? Path.Combine(".claude", "skills") : Path.Combine(".agents", "skills");
        return Path.Combine(basePath, installFolder);
    }

    private async Task<SkillInstallResult> InstallDiscoveryStubAsync(string targetRoot, BuiltInSkillMetadata skill)
    {
        var skillPath = Path.Combine(targetRoot, skill.Name, "SKILL.md");
        var existed = _fileSystem.Exists(skillPath);
        await _fileSystem.WriteAllTextAsync(skillPath, BuildDiscoveryStub(skill));
        return new SkillInstallResult(skill.Name, existed ? "updated" : "created");
    }

    private async Task<SkillInstallResult> InstallHermesSkillAsync(string targetRoot, string skillName)
    {
        var asset = _assets.GetSkill(skillName, includeSupplementaryFiles: true);
        if (!asset.Found || asset.Skill is null)
            throw new InvalidOperationException(asset.Error ?? $"Unable to resolve built-in skill '{skillName}'.");

        var sourceRoot = asset.Skill.DirectoryPath;
        var targetSkillRoot = Path.Combine(targetRoot, skillName);
        var existed = _fileSystem.DirectoryExists(targetSkillRoot);

        foreach (var file in _fileSystem.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, file);
            var destinationPath = Path.Combine(targetSkillRoot, relativePath);
            var contents = await _fileSystem.ReadAllTextAsync(file);
            await _fileSystem.WriteAllTextAsync(destinationPath, contents);
        }

        return new SkillInstallResult(skillName, existed ? "updated" : "created");
    }

    private async Task<(SkillInstallResult Result, string? Error)> TryInstallHermesSkillAsync(string targetRoot, string skillName)
    {
        try
        {
            var result = await InstallHermesSkillAsync(targetRoot, skillName);
            return (result, null);
        }
        catch (InvalidOperationException ex)
        {
            return (new SkillInstallResult(skillName, "error"), ex.Message);
        }
    }

    private async Task WriteSummaryAsync(SkillInstallOptions options, string targetRoot, IReadOnlyList<SkillInstallResult> results)
    {
        await _output.WriteLineAsync($"Installed Mohist built-in skills to {targetRoot}");
        foreach (var result in results)
            await _output.WriteLineAsync($"- {result.Name}: {result.Status}");

        if (!options.Hermes)
            return;

        await _output.WriteLineAsync();
        await _output.WriteLineAsync("Hermes usage:");
        await _output.WriteLineAsync("- Use /mohist for current Mohist .NET backend, API, and workflow operations.");
        await _output.WriteLineAsync("- Use /mohist-explore for product and UX exploration in the Mohist codebase.");
        await _output.WriteLineAsync("- If Hermes is already running, reload/reset the session or start a new session to pick up installed skills.");
    }

    private static string? Validate(SkillInstallOptions options)
    {
        if (!options.Hermes)
            return null;

        if (options.Claude && !string.IsNullOrWhiteSpace(options.TargetPath))
            return "The --hermes option cannot be combined with --claude or --path.";
        if (options.Claude)
            return "The --hermes option cannot be combined with --claude.";
        if (!string.IsNullOrWhiteSpace(options.TargetPath))
            return "The --hermes option cannot be combined with --path.";

        return null;
    }

    private static string BuildDiscoveryStub(BuiltInSkillMetadata skill)
    {
        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.Append("name: ").AppendLine(skill.Name);
        builder.Append("description: ").AppendLine(skill.Description);
        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine("This Mohist-managed discovery stub keeps local agent skill installs lightweight and version-matched.");
        builder.AppendLine();
        builder.Append("Run `mo skills get ").Append(skill.Name).AppendLine("` to view the full guidance packaged with this Mohist CLI.");
        return builder.ToString().Replace("\r\n", "\n");
    }
}

internal sealed record SkillInstallOptions(string? TargetPath, bool Claude, bool Hermes);

internal sealed record SkillInstallResult(string Name, string Status);
