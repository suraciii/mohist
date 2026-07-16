namespace Mohist.Server.SystemInfo;

public interface IManagedAssetCatalog
{
    ManagedAssetCatalogState GetState();
}

public enum ManagedAssetCatalogState
{
    Unavailable,
    Empty,
    Available,
}

public sealed class FileSystemManagedAssetCatalog : IManagedAssetCatalog
{
    private readonly IConfiguration _configuration;
    private readonly IEnvironmentVariableProvider _environment;

    public FileSystemManagedAssetCatalog(
        IConfiguration configuration,
        IEnvironmentVariableProvider environment)
    {
        _configuration = configuration;
        _environment = environment;
    }

    public ManagedAssetCatalogState GetState()
    {
        var assetRoot = ResolveRoot();
        if (string.IsNullOrWhiteSpace(assetRoot) || !Directory.Exists(assetRoot))
            return ManagedAssetCatalogState.Unavailable;

        try
        {
            return Directory.EnumerateFiles(assetRoot, "SKILL.md", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            }).Any()
                ? ManagedAssetCatalogState.Available
                : ManagedAssetCatalogState.Empty;
        }
        catch
        {
            return ManagedAssetCatalogState.Unavailable;
        }
    }

    private string ResolveRoot()
    {
        var configured = _configuration["Mohist:CliSkillDataPath"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var home = _environment.GetEnvironmentVariable(SystemUpdateService.HomeEnvironmentVariable)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".mohist", "cli", "skill-data");
    }
}
