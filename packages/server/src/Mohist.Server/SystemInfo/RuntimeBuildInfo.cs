using System.Reflection;
using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.SystemInfo;

public interface IRuntimeBuildInfo
{
    string? Version { get; }
    string? GitHash { get; }
    DateTimeOffset StartedAt { get; }
    string? Component => null;
    string? SourceRevision => null;
    string? BuildGitHash => null;
    string? TreeHash => null;
    string? ArtifactDigest => null;
    string? ReleaseId => null;
    long Generation => 0;
    int? SchemaVersion => null;
}

public sealed class RuntimeBuildInfo : IRuntimeBuildInfo, ISingletonService
{
    public const string GitHashEnvironmentVariable = "MOHIST_GIT_HASH";
    public const string RuntimeIdentityPathEnvironmentVariable = "MOHIST_RUNTIME_IDENTITY_PATH";

    public string? Version { get; }
    public string? GitHash { get; }
    public DateTimeOffset StartedAt { get; }
    public string? Component { get; }
    public string? SourceRevision { get; }
    public string? BuildGitHash { get; }
    public string? TreeHash { get; }
    public string? ArtifactDigest { get; }
    public string? ReleaseId { get; }
    public long Generation { get; }
    public int? SchemaVersion { get; }

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
            Component = managed.Component;
            Version = managed.Version;
            SourceRevision = managed.SourceRevision;
            BuildGitHash = managed.BuildGitHash;
            SchemaVersion = managed.SchemaVersion;
            // GitHash is the server-side source revision used by update/status code.
            GitHash = managed.SourceRevision;
            TreeHash = managed.TreeHash;
            ArtifactDigest = managed.ArtifactDigest;
            ReleaseId = managed.ReleaseId;
            Generation = managed.Generation ?? 0;
            return;
        }

        (Version, GitHash) = ResolveIdentity(environment, sourceIdentity);
        Component = null;
        SourceRevision = null;
        BuildGitHash = null;
        SchemaVersion = null;
        TreeHash = null;
        ArtifactDigest = null;
        ReleaseId = null;
        Generation = 0;
    }

    /// <summary>
    /// Reads the managed runtime identity. A null result means no managed
    /// identity source exists (local/dev build), which permits the
    /// source-checkout fallback. A present but malformed manifest yields an
    /// empty (non-null) identity so a managed process never silently claims
    /// source-checkout identity.
    /// </summary>
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
            return ParseManagedIdentity(json);
        }
        catch
        {
            return RuntimeIdentityMetadata.Malformed;
        }
    }

    /// <summary>
    /// Parses a managed manifest. A payload with <c>schemaVersion</c> other
    /// than <c>1</c>, a wrong JSON type, or a canonical payload missing any
    /// required field is malformed and must not fall back to source identity.
    /// A payload without <c>schemaVersion</c> is legacy v0:
    /// <c>buildGitHash = buildGitHash ?? gitHash ?? sourceRevision</c> and
    /// <c>sourceRevision = sourceRevision ?? buildGitHash</c>.
    /// </summary>
    internal static RuntimeIdentityMetadata ParseManagedIdentity(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return RuntimeIdentityMetadata.Malformed;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return RuntimeIdentityMetadata.Malformed;

            if (TryGetProperty(root, "schemaVersion", out var schemaElement)
                && schemaElement.ValueKind != JsonValueKind.Null)
            {
                if (schemaElement.ValueKind != JsonValueKind.Number
                    || !schemaElement.TryGetInt32(out var schemaVersion)
                    || schemaVersion != 1)
                {
                    return RuntimeIdentityMetadata.Malformed;
                }

                var component = ReadString(root, "component");
                var sourceRevision = ReadText(ReadString(root, "sourceRevision"));
                var buildGitHash = ReadText(ReadString(root, "buildGitHash"));
                var treeHash = ReadText(ReadString(root, "treeHash"));
                var artifactDigest = ReadText(ReadString(root, "artifactDigest"));
                var releaseId = ReadText(ReadString(root, "releaseId"));
                var generation = ReadLong(root, "generation");
                var runnerId = ReadString(root, "runnerId");

                if (component is not ("server" or "runner")
                    || sourceRevision is null
                    || buildGitHash is null
                    || treeHash is null
                    || artifactDigest is null
                    || releaseId is null
                    || generation is not > 0
                    || runnerId is null)
                {
                    return RuntimeIdentityMetadata.Malformed;
                }

                if (component == "runner" && runnerId.Length == 0)
                    return RuntimeIdentityMetadata.Malformed;
                if (component == "server" && runnerId.Length != 0)
                    return RuntimeIdentityMetadata.Malformed;

                return new RuntimeIdentityMetadata(
                    1,
                    component,
                    ReadText(ReadString(root, "version")),
                    sourceRevision,
                    buildGitHash,
                    treeHash,
                    artifactDigest,
                    releaseId,
                    generation,
                    runnerId);
            }

            var gitHash = ReadText(ReadString(root, "gitHash"));
            var legacySourceRevision = ReadText(ReadString(root, "sourceRevision"));
            var legacyBuildGitHash = ReadText(ReadString(root, "buildGitHash"))
                ?? gitHash
                ?? legacySourceRevision;
            return new RuntimeIdentityMetadata(
                null,
                ReadText(ReadString(root, "component")),
                ReadText(ReadString(root, "version")),
                legacySourceRevision ?? legacyBuildGitHash,
                legacyBuildGitHash,
                ReadText(ReadString(root, "treeHash")),
                ReadText(ReadString(root, "artifactDigest")),
                ReadText(ReadString(root, "releaseId")),
                ReadLong(root, "generation") is { } legacyGeneration and > 0 ? legacyGeneration : null,
                ReadText(ReadString(root, "runnerId")));
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
            return true;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadLong(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static string? ReadText(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

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
    int? SchemaVersion,
    string? Component,
    string? Version,
    string? SourceRevision,
    string? BuildGitHash,
    string? TreeHash,
    string? ArtifactDigest,
    string? ReleaseId,
    long? Generation,
    string? RunnerId)
{
    /// <summary>
    /// A present-but-malformed managed manifest. Non-null so the constructor
    /// does not fall back to source-checkout identity.
    /// </summary>
    public static readonly RuntimeIdentityMetadata Malformed = new(
        null, null, null, null, null, null, null, null, null, null);
}
