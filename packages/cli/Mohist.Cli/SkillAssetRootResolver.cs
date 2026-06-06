
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
    private readonly Func<SkillAssetBuildIdentity> _getBuildIdentity;

    public static SkillAssetRootResolver CreateDefault(IFileSystem fileSystem, IEnvironmentVariableProvider environment) =>
        new(fileSystem, environment,
            getOverrideAssetRoot: null,
            getManagedAssetRoot: null,
            getUserHome: null,
            getBuildIdentity: null);

    internal SkillAssetRootResolver(
        IFileSystem fileSystem,
        IEnvironmentVariableProvider environment,
        Func<string?>? getOverrideAssetRoot = null,
        Func<string?>? getManagedAssetRoot = null,
        Func<string?>? getUserHome = null,
        Func<SkillAssetBuildIdentity>? getBuildIdentity = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _getOverrideAssetRoot = getOverrideAssetRoot ?? (() => _environment.GetEnvironmentVariable(OverrideEnvironmentVariable));
        _getManagedAssetRoot = getManagedAssetRoot;
        _getUserHome = getUserHome ?? (() => DefaultUserHome(_environment));
        _getBuildIdentity = getBuildIdentity ?? SkillAssetManifest.ResolveCurrentBuildIdentity;
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

    public SkillAssetRootResolution Resolve(IReadOnlyList<string> expectedSkillNames)
    {
        ArgumentNullException.ThrowIfNull(expectedSkillNames);

        var identity = _getBuildIdentity();

        var overrideRoot = _getOverrideAssetRoot?.Invoke();
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            var candidate = NormalizeRoot(overrideRoot);
            if (!_fileSystem.DirectoryExists(candidate))
            {
                return SkillAssetRootResolution.Failed(
                    candidate,
                    SkillAssetRootSource.Override,
                    null,
                    BuildOverrideMissingDiagnostic(candidate));
            }

            var validation = SkillAssetManifest.Validate(candidate, identity, expectedSkillNames, _fileSystem);
            if (validation.IsValid)
                return SkillAssetRootResolution.Selected(candidate, SkillAssetRootSource.Override, validation);

            return SkillAssetRootResolution.Failed(
                candidate,
                SkillAssetRootSource.Override,
                validation,
                BuildOverrideMismatchDiagnostic(candidate, validation));
        }

        var managedRoot = ResolveManagedRoot();
        if (!string.IsNullOrWhiteSpace(managedRoot))
        {
            var normalized = NormalizeRoot(managedRoot);
            if (_fileSystem.DirectoryExists(normalized))
            {
                var validation = SkillAssetManifest.Validate(normalized, identity, expectedSkillNames, _fileSystem);
                if (validation.IsValid)
                    return SkillAssetRootResolution.Selected(normalized, SkillAssetRootSource.ManagedCache, validation);

                return SkillAssetRootResolution.Failed(
                    normalized,
                    SkillAssetRootSource.ManagedCache,
                    validation,
                    BuildManagedMismatchDiagnostic(normalized, validation));
            }
        }

        var siblingRoot = NormalizeRoot(Path.Combine(AppContext.BaseDirectory, "skill-data"));
        if (_fileSystem.DirectoryExists(siblingRoot))
        {
            var validation = SkillAssetManifest.Validate(siblingRoot, identity, expectedSkillNames, _fileSystem);
            if (validation.IsValid)
                return SkillAssetRootResolution.Selected(siblingRoot, SkillAssetRootSource.SiblingFallback, validation);

            return SkillAssetRootResolution.Failed(
                siblingRoot,
                SkillAssetRootSource.SiblingFallback,
                validation,
                BuildSiblingMismatchDiagnostic(siblingRoot, validation));
        }

        return SkillAssetRootResolution.Failed(
            null,
            SkillAssetRootSource.None,
            null,
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

    private static string NormalizeRoot(string path) => Path.GetFullPath(path);

    private static string BuildOverrideMissingDiagnostic(string candidate) =>
        $"MOHIST_SKILLS_DIR points to '{candidate}' which does not exist. " +
        "Repair by running 'mo update' or 'scripts/install-mo.sh'.";

    private static string BuildOverrideMismatchDiagnostic(string candidate, SkillAssetManifestValidation validation) =>
        $"MOHIST_SKILLS_DIR points to '{candidate}' which is incompatible with the running CLI. " +
        (validation.Summary ?? string.Empty).Trim() + " " +
        "Repair by running 'mo update' or 'scripts/install-mo.sh'.";

    private static string BuildManagedMismatchDiagnostic(string candidate, SkillAssetManifestValidation validation) =>
        $"Managed skill assets at '{candidate}' are incompatible with the running CLI. " +
        (validation.Summary ?? string.Empty).Trim() + " " +
        "Repair by running 'mo update' or 'scripts/install-mo.sh'.";

    private static string BuildSiblingMismatchDiagnostic(string candidate, SkillAssetManifestValidation validation) =>
        $"Sibling packaged skill assets at '{candidate}' are incompatible with the running CLI. " +
        (validation.Summary ?? string.Empty).Trim() + " " +
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
    SkillAssetManifestValidation? Validation,
    string? DiagnosticSummary)
{
    public bool IsSelected => AssetRoot is not null;

    public static SkillAssetRootResolution Selected(
        string assetRoot,
        SkillAssetRootSource source,
        SkillAssetManifestValidation validation) =>
        new(assetRoot, assetRoot, source, validation, null);

    public static SkillAssetRootResolution Failed(
        string? attemptedRoot,
        SkillAssetRootSource source,
        SkillAssetManifestValidation? validation,
        string diagnosticSummary) =>
        new(null, attemptedRoot, source, validation, diagnosticSummary);
}
