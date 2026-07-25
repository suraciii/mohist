namespace Mohist.Cli;

// Resolves the preset asset root independently of skill-data resolution, per
// design D2 ("预设独立解析"). Presets do not piggyback on MOHIST_SKILLS_DIR:
// they look first at the managed cache (<home>/.mohist/cli/presets, populated by
// `mo update`), then fall back to the sibling directory shipped next to the
// CLI binary (AppContext.BaseDirectory/presets, the dev/source-build case).
internal sealed class PresetAssetRootResolver
{
    public const string ManagedSubdirectory = "presets";

    private readonly IFileSystem _fileSystem;
    private readonly Func<string?> _getUserHome;
    private readonly Func<string> _getSiblingRoot;

    public PresetAssetRootResolver(IFileSystem fileSystem, Func<string?> getUserHome)
        : this(fileSystem, getUserHome, () => Path.Combine(AppContext.BaseDirectory, ManagedSubdirectory))
    {
    }

    internal PresetAssetRootResolver(IFileSystem fileSystem, Func<string?> getUserHome, Func<string> getSiblingRoot)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _getUserHome = getUserHome ?? throw new ArgumentNullException(nameof(getUserHome));
        _getSiblingRoot = getSiblingRoot ?? throw new ArgumentNullException(nameof(getSiblingRoot));
    }

    public string? Resolve()
    {
        var home = _getUserHome();
        if (!string.IsNullOrWhiteSpace(home))
        {
            var managed = Path.Combine(home, ".mohist", "cli", ManagedSubdirectory);
            if (_fileSystem.DirectoryExists(managed))
                return managed;
        }

        var sibling = _getSiblingRoot();
        if (_fileSystem.DirectoryExists(sibling))
            return sibling;

        return null;
    }
}
