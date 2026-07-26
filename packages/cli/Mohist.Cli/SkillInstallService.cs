using System.Text;

namespace Mohist.Cli;

internal sealed class SkillInstallService
{
    public const string HermesHomeEnvironmentVariable = "HERMES_HOME";

    private readonly SkillAssetService _assets;
    private readonly IFileSystem _fileSystem;
    private readonly IEnvironmentVariableProvider _environment;
    private readonly TextWriter _output;
    private readonly TextWriter _error;

    public SkillInstallService(SkillAssetService assets, IFileSystem fileSystem, TextWriter output, TextWriter error)
        : this(assets, fileSystem, SystemEnvironmentVariableProvider.Instance, output, error)
    {
    }

    public SkillInstallService(
        SkillAssetService assets,
        IFileSystem fileSystem,
        IEnvironmentVariableProvider environment,
        TextWriter output,
        TextWriter error)
    {
        _assets = assets;
        _fileSystem = fileSystem;
        _environment = environment;
        _output = output;
        _error = error;
    }

    private const string EntrySkillName = "mohist";

    public async Task<int> InstallAsync(SkillInstallOptions options)
    {
        var validationError = Validate(options);
        if (validationError is not null)
        {
            await _error.WriteLineAsync(validationError);
            return 1;
        }

        var targetRoot = ResolveTargetRoot(options);
        var skills = _assets.ListVisibleSkills();
        var entrySkill = skills.FirstOrDefault(s => s.Name == EntrySkillName);
        if (entrySkill is null || string.IsNullOrWhiteSpace(entrySkill.Name))
        {
            await _error.WriteLineAsync($"Built-in entry skill '{EntrySkillName}' is missing.");
            return 1;
        }

        var result = await InstallDiscoveryStubAsync(targetRoot, entrySkill);
        await WriteSummaryAsync(options, targetRoot, [result]);
        return 0;
    }

    private string ResolveTargetRoot(SkillInstallOptions options)
    {
        if (options.Hermes)
        {
            var hermesHome = _environment.GetEnvironmentVariable(HermesHomeEnvironmentVariable);
            var hermesBasePath = string.IsNullOrWhiteSpace(hermesHome)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hermes")
                : Path.GetFullPath(hermesHome);
            return Path.Combine(hermesBasePath, "skills");
        }

        var basePath = options.TargetPath is null
            ? _fileSystem.CurrentDirectory
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
        builder.Append("Run `mo skill view ").Append(skill.Name).AppendLine("` to view the full guidance packaged with this Mohist CLI.");
        return builder.ToString().Replace("\r\n", "\n");
    }
}

internal sealed record SkillInstallOptions(string? TargetPath, bool Claude, bool Hermes);

internal sealed record SkillInstallResult(string Name, string Status);
