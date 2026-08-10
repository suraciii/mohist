using System.Reflection;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.SystemInfo;

public interface IRuntimeBuildInfo
{
    string? Version { get; }
    string? GitHash { get; }
    DateTimeOffset StartedAt { get; }
    string? TreeHash => null;
    string? ArtifactDigest => null;
    string? ReleaseId => null;
    long Generation => 0;
}

public sealed class RuntimeBuildInfo : IRuntimeBuildInfo, ISingletonService
{
    public const string GitHashEnvironmentVariable = "MOHIST_GIT_HASH";

    public string? Version { get; }
    public string? GitHash { get; }
    public DateTimeOffset StartedAt { get; }
    public string? TreeHash { get; }
    public string? ArtifactDigest { get; }
    public string? ReleaseId { get; }
    public long Generation { get; }

    public RuntimeBuildInfo(
        IEnvironmentVariableProvider environment,
        IRuntimeSourceIdentity sourceIdentity,
        TimeProvider timeProvider,
        IFileSystem? fileSystem = null)
    {
        StartedAt = timeProvider.GetUtcNow();
        var managed = ReadManagedIdentity(environment, fileSystem);
        if (managed is not null)
        {
            Version = managed.Version;
            GitHash = managed.SourceRevision;
            TreeHash = managed.TreeHash;
            ArtifactDigest = managed.ArtifactDigest;
            ReleaseId = managed.ReleaseId;
            Generation = managed.Generation;
            return;
        }

        (Version, GitHash) = ResolveIdentity(environment, sourceIdentity);
        TreeHash = null;
        ArtifactDigest = null;
        ReleaseId = null;
        Generation = 0;
    }

    public const string RuntimeIdentityPathEnvironmentVariable = "MOHIST_RUNTIME_IDENTITY_PATH";

    private static RuntimeIdentityMetadata? ReadManagedIdentity(
        IEnvironmentVariableProvider environment,
        IFileSystem? fileSystem)
    {
        var path = environment.GetEnvironmentVariable(RuntimeIdentityPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var json = fileSystem is null ? File.ReadAllText(path) : fileSystem.ReadAllText(path);
            var identity = System.Text.Json.JsonSerializer.Deserialize<RuntimeIdentityMetadata>(json);
            return identity is { IsComplete: true } ? identity : null;
        }
        catch
        {
            // A managed process must not silently claim source checkout identity when its
            // immutable runtime identity is absent or malformed.
            return new RuntimeIdentityMetadata(null, null, null, null, null, null, 0);
        }
    }

    private static (string? Version, string? GitHash) ResolveIdentity(
        IEnvironmentVariableProvider environment,
        IRuntimeSourceIdentity sourceIdentity)
    {
        var assembly = typeof(RuntimeBuildInfo).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var versionFromAssembly = assembly.GetName().Version?.ToString();

        return ResolveIdentity(
            informationalVersion,
            versionFromAssembly,
            () => environment.GetEnvironmentVariable(
                GitHashEnvironmentVariable),
            () => sourceIdentity.GitHead);
    }

    internal static (string? Version, string? GitHash) ResolveIdentity(
        string? informationalVersion,
        string? versionFromAssembly,
        Func<string?> getGitHashFromEnv,
        Func<string?> getGitHead)
    {
        string? version = versionFromAssembly;
        string? gitHash = null;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var plusIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);
            if (plusIndex >= 0)
            {
                version = informationalVersion[..plusIndex];
                gitHash = informationalVersion[(plusIndex + 1)..];
            }
            else
            {
                version = informationalVersion;
            }
        }

        if (string.IsNullOrWhiteSpace(gitHash))
            gitHash = getGitHashFromEnv();

        if (string.IsNullOrWhiteSpace(gitHash))
            gitHash = getGitHead();

        return (version, string.IsNullOrWhiteSpace(gitHash) ? null : gitHash);
    }

}

internal sealed record RuntimeIdentityMetadata(
    string? Component,
    string? Version,
    string? SourceRevision,
    string? TreeHash,
    string? ArtifactDigest,
    string? ReleaseId,
    long Generation)
{
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Component)
        && !string.IsNullOrWhiteSpace(Version)
        && !string.IsNullOrWhiteSpace(SourceRevision)
        && !string.IsNullOrWhiteSpace(TreeHash)
        && !string.IsNullOrWhiteSpace(ArtifactDigest)
        && !string.IsNullOrWhiteSpace(ReleaseId)
        && Generation > 0;
}
