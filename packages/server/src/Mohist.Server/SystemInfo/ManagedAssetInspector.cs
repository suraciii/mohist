namespace Mohist.Server.SystemInfo;

public interface IManagedAssetInspector
{
    bool HasSkill(string assetRoot);
}

internal sealed class FileSystemManagedAssetInspector : IManagedAssetInspector
{
    public static readonly FileSystemManagedAssetInspector Instance = new();

    private FileSystemManagedAssetInspector()
    {
    }

    public bool HasSkill(string assetRoot)
    {
        if (string.IsNullOrWhiteSpace(assetRoot) || !Directory.Exists(assetRoot))
            return false;

        return Directory.EnumerateFiles(assetRoot, "SKILL.md", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        }).Any();
    }
}
