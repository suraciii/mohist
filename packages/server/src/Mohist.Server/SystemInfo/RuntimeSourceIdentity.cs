using System.Text.Json;

namespace Mohist.Server.SystemInfo;

public interface IRuntimeSourceIdentity
{
    string? GitHead { get; }
}

public sealed class RuntimeSourceIdentity : IRuntimeSourceIdentity
{
    internal const string InstalledBuildManifestFileName = "mohist-build.json";

    public string? GitHead { get; }

    public RuntimeSourceIdentity(IFileSystem fileSystem)
        : this(fileSystem, AppContext.BaseDirectory)
    {
    }

    internal RuntimeSourceIdentity(IFileSystem fileSystem, string startPath)
    {
        GitHead = ResolveGitHead(fileSystem, startPath);
    }

    internal static string? ResolveGitHead(IFileSystem fileSystem, string startPath)
    {
        try
        {
            var installedIdentity = ReadInstalledBuildIdentity(fileSystem, startPath);
            if (!string.IsNullOrWhiteSpace(installedIdentity))
                return installedIdentity;

            var root = startPath;
            while (!string.IsNullOrWhiteSpace(root))
            {
                var markerPath = Path.Combine(root, ".git");
                if (fileSystem.Exists(markerPath))
                    return ReadHead(fileSystem, root, markerPath);

                root = Directory.GetParent(root)?.FullName;
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? ReadInstalledBuildIdentity(IFileSystem fileSystem, string startPath)
    {
        var manifestPath = Path.Combine(startPath, InstalledBuildManifestFileName);
        if (!fileSystem.Exists(manifestPath))
            return null;

        try
        {
            using var document = JsonDocument.Parse(fileSystem.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("gitHash", out var value)
                || value.ValueKind != JsonValueKind.String)
                return null;
            return NullIfWhiteSpace(value.GetString()?.Trim() ?? string.Empty);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadHead(
        IFileSystem fileSystem,
        string repositoryRoot,
        string markerPath)
    {
        var gitDirectory = markerPath;
        try
        {
            var marker = fileSystem.ReadAllText(markerPath).Trim();
            const string prefix = "gitdir:";
            if (marker.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var path = marker[prefix.Length..].Trim();
                gitDirectory = Path.IsPathRooted(path)
                    ? path
                    : Path.GetFullPath(path, repositoryRoot);
            }
        }
        catch
        {
        }

        var headPath = Path.Combine(gitDirectory, "HEAD");
        if (!fileSystem.Exists(headPath))
            return null;

        var head = fileSystem.ReadAllText(headPath).Trim();
        const string refPrefix = "ref:";
        if (!head.StartsWith(refPrefix, StringComparison.Ordinal))
            return string.IsNullOrWhiteSpace(head) ? null : head;

        var referencePath = Path.Combine(
            gitDirectory,
            head[refPrefix.Length..].Trim());
        return fileSystem.Exists(referencePath)
            ? NullIfWhiteSpace(fileSystem.ReadAllText(referencePath).Trim())
            : null;
    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
