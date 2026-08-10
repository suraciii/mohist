using System.Text.Json;

namespace Mohist.Server.SystemInfo;

public interface IRuntimeSourceIdentity
{
    string? GitHead { get; }
    string? ArtifactDigest => null;
}

public sealed class RuntimeSourceIdentity : IRuntimeSourceIdentity
{
    internal const string InstalledBuildManifestFileName = "mohist-build.json";

    public string? GitHead { get; }
    public string? ArtifactDigest { get; }

    public RuntimeSourceIdentity(IFileSystem fileSystem)
        : this(fileSystem, AppContext.BaseDirectory)
    {
    }

    internal RuntimeSourceIdentity(IFileSystem fileSystem, string startPath)
    {
        var installedIdentity = ReadInstalledBuildIdentity(fileSystem, startPath);
        GitHead = installedIdentity?.GitHash ?? ResolveGitHead(fileSystem, startPath);
        ArtifactDigest = installedIdentity?.ArtifactDigest;
    }

    internal static string? ResolveGitHead(IFileSystem fileSystem, string startPath)
    {
        try
        {
            var installedIdentity = ReadInstalledBuildIdentity(fileSystem, startPath);
            if (!string.IsNullOrWhiteSpace(installedIdentity?.GitHash))
                return installedIdentity.GitHash;

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

    private static InstalledBuildIdentity? ReadInstalledBuildIdentity(IFileSystem fileSystem, string startPath)
    {
        var manifestPath = Path.Combine(startPath, InstalledBuildManifestFileName);
        if (!fileSystem.Exists(manifestPath))
            return null;

        try
        {
            using var document = JsonDocument.Parse(fileSystem.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("gitHash", out var hashValue)
                || hashValue.ValueKind != JsonValueKind.String
                || !document.RootElement.TryGetProperty("artifactDigest", out var digestValue)
                || digestValue.ValueKind != JsonValueKind.String)
                return null;
            var gitHash = NullIfWhiteSpace(hashValue.GetString()?.Trim() ?? string.Empty);
            var artifactDigest = NullIfWhiteSpace(digestValue.GetString()?.Trim() ?? string.Empty);
            if (gitHash is null || artifactDigest is null || !IsDigest(artifactDigest))
                return null;

            return new InstalledBuildIdentity(gitHash, artifactDigest);
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

    private static bool IsDigest(string? value) =>
        value is { Length: 64 }
        && value.All(c => (c is >= 'a' and <= 'f') || (c is >= '0' and <= '9'));

    private sealed record InstalledBuildIdentity(string GitHash, string ArtifactDigest);
}
