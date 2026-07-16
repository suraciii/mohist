
namespace Mohist.Cli;

internal enum SkillAssetRootSource
{
    None,
    Override,
    ManagedCache,
    SiblingFallback,
}

internal sealed class SkillAssetRootResolver
{
    public const string OverrideEnvironmentVariable = "MOHIST_SKILLS_DIR";
    public const string HomeEnvironmentVariable = "HOME";

    private readonly IFileSystem _fileSystem;
    private readonly IEnvironmentVariableProvider _environment;
    private readonly Func<string?>? _getOverrideAssetRoot;
    private readonly Func<string?>? _getManagedAssetRoot;
    private readonly Func<string?>? _getUserHome;
    private readonly Func<string> _getSiblingAssetRoot;

    public static SkillAssetRootResolver CreateDefault(IFileSystem fileSystem, IEnvironmentVariableProvider environment) =>
        new(fileSystem, environment,
            getOverrideAssetRoot: null,
            getManagedAssetRoot: null,
            getUserHome: null);

    internal SkillAssetRootResolver(
        IFileSystem fileSystem,
        IEnvironmentVariableProvider environment,
        Func<string?>? getOverrideAssetRoot = null,
        Func<string?>? getManagedAssetRoot = null,
        Func<string?>? getUserHome = null,
        Func<string>? getSiblingAssetRoot = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _getOverrideAssetRoot = getOverrideAssetRoot ?? (() => _environment.GetEnvironmentVariable(OverrideEnvironmentVariable));
        _getManagedAssetRoot = getManagedAssetRoot;
        _getUserHome = getUserHome ?? (() => DefaultUserHome(_environment));
        _getSiblingAssetRoot = getSiblingAssetRoot ?? (() => Path.Combine(AppContext.BaseDirectory, "skill-data"));
    }

    public string DefaultManagedAssetRoot()
    {
        var home = _userHome();
        return string.IsNullOrWhiteSpace(home)
            ? Path.Combine(AppContext.BaseDirectory, "skill-data")
            : Path.Combine(home, ".mohist", "cli", "skill-data");
    }

    private string? _userHome() => _getUserHome?.Invoke();

    private static string? DefaultUserHome(IEnvironmentVariableProvider environment)
    {
        var home = environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(home))
            return home;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public SkillAssetRootResolution Resolve()
    {
        var overrideRoot = _getOverrideAssetRoot?.Invoke();
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            var candidate = NormalizeRoot(overrideRoot);
            if (_fileSystem.DirectoryExists(candidate))
                return SkillAssetRootResolution.Selected(candidate, SkillAssetRootSource.Override);

            return SkillAssetRootResolution.Failed(
                candidate,
                SkillAssetRootSource.Override,
                BuildOverrideMissingDiagnostic(candidate));
        }

        var managedRoot = ResolveManagedRoot();
        if (!string.IsNullOrWhiteSpace(managedRoot))
        {
            var normalized = NormalizeRoot(managedRoot);
            if (_fileSystem.DirectoryExists(normalized))
                return SkillAssetRootResolution.Selected(normalized, SkillAssetRootSource.ManagedCache);
        }

        var siblingRoot = NormalizeRoot(_getSiblingAssetRoot());
        if (_fileSystem.DirectoryExists(siblingRoot))
            return SkillAssetRootResolution.Selected(siblingRoot, SkillAssetRootSource.SiblingFallback);

        return SkillAssetRootResolution.Failed(
            null,
            SkillAssetRootSource.None,
            BuildNoRootDiagnostic(siblingRoot));
    }

    private string? ResolveManagedRoot()
    {
        if (_getManagedAssetRoot is null)
        {
            var home = _getUserHome?.Invoke();
            if (string.IsNullOrWhiteSpace(home))
                return null;
            return Path.Combine(home, ".mohist", "cli", "skill-data");
        }

        return _getManagedAssetRoot();
    }

    private static string NormalizeRoot(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private static string BuildOverrideMissingDiagnostic(string candidate) =>
        $"MOHIST_SKILLS_DIR points to '{candidate}' which does not exist. " +
        "Repair by running 'mo update' or 'scripts/install-mo.sh'.";

    private static string BuildNoRootDiagnostic(string siblingRoot) =>
        $"No packaged skill assets found. MOHIST_SKILLS_DIR is not set, " +
        "the managed cache at '~/.mohist/cli/skill-data' is absent, " +
        $"and no sibling assets exist at '{siblingRoot}'. " +
        "Repair by running 'mo update' or 'scripts/install-mo.sh'.";
}

internal sealed record SkillAssetRootResolution(
    string? AssetRoot,
    string? AttemptedRoot,
    SkillAssetRootSource Source,
    string? DiagnosticSummary)
{
    public bool IsSelected => AssetRoot is not null;

    public static SkillAssetRootResolution Selected(string assetRoot, SkillAssetRootSource source) =>
        new(assetRoot, assetRoot, source, null);

    public static SkillAssetRootResolution Failed(
        string? attemptedRoot,
        SkillAssetRootSource source,
        string diagnosticSummary) =>
        new(null, attemptedRoot, source, diagnosticSummary);
}
