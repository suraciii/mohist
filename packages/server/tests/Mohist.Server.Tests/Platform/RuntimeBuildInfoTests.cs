using EnvironmentAbstractions.TestHelpers;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.SystemInfo;
using Xunit;

namespace Mohist.Server.Tests.Platform;

[Trait("level", "L0")]
public class RuntimeBuildInfoTests
{
    [Fact]
    public void ManagedIdentity_WhenCanonicalManifest_ReportsEveryCanonicalField()
    {
        const string identityPath = "/managed/server/runtime-identity.json";
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        environment[RuntimeBuildInfo.RuntimeIdentityPathEnvironmentVariable] = identityPath;
        var files = new FakeIdentityFileSystem();
        files.WriteAllText(
            identityPath,
            """
            {
              "schemaVersion": 1,
              "component": "server",
              "version": "0.0.0+candidate",
              "sourceRevision": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "buildGitHash": "dddddddddddddddddddddddddddddddddddddddd",
              "treeHash": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
              "artifactDigest": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
              "releaseId": "mohist-server-candidate",
              "generation": 4,
              "runnerId": ""
            }
            """);

        var info = new RuntimeBuildInfo(
            environment,
            new StubRuntimeSourceIdentity("source-checkout"),
            new FakeTimeProvider(TestTime.UtcNow),
            files);

        Assert.Equal(1, info.SchemaVersion);
        Assert.Equal("server", info.Component);
        Assert.Equal("0.0.0+candidate", info.Version);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", info.SourceRevision);
        Assert.Equal("dddddddddddddddddddddddddddddddddddddddd", info.BuildGitHash);
        Assert.Equal(info.SourceRevision, info.GitHash);
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", info.TreeHash);
        Assert.Equal("cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", info.ArtifactDigest);
        Assert.Equal("mohist-server-candidate", info.ReleaseId);
        Assert.Equal(4, info.Generation);
    }

    [Fact]
    public void ManagedIdentity_WhenCliManifestIsProvided_ReportsEveryCandidateField()
    {
        const string identityPath = "/managed/server/runtime-identity.json";
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        environment[RuntimeBuildInfo.RuntimeIdentityPathEnvironmentVariable] = identityPath;
        var files = new FakeIdentityFileSystem();
        files.WriteAllText(
            identityPath,
            """
            {
              "component": "server",
              "version": "0.0.0+candidate",
              "sourceRevision": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "treeHash": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
              "artifactDigest": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
              "releaseId": "mohist-server-candidate",
              "generation": 4
            }
            """);

        var info = new RuntimeBuildInfo(
            environment,
            new StubRuntimeSourceIdentity("source-checkout"),
            new FakeTimeProvider(TestTime.UtcNow),
            files);

        Assert.Null(info.SchemaVersion);
        Assert.Equal("server", info.Component);
        Assert.Equal("0.0.0+candidate", info.Version);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", info.SourceRevision);
        Assert.Equal(info.SourceRevision, info.GitHash);
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", info.TreeHash);
        Assert.Equal("cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", info.ArtifactDigest);
        Assert.Equal("mohist-server-candidate", info.ReleaseId);
        Assert.Equal(4, info.Generation);
    }

    [Fact]
    public void ManagedIdentity_WhenCanonicalManifestMissingRequiredField_DoesNotFallBackToSourceIdentity()
    {
        const string identityPath = "/managed/server/runtime-identity.json";
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        environment[RuntimeBuildInfo.RuntimeIdentityPathEnvironmentVariable] = identityPath;
        var files = new FakeIdentityFileSystem();
        files.WriteAllText(
            identityPath,
            """
            {
              "schemaVersion": 1,
              "component": "server",
              "sourceRevision": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "buildGitHash": "dddddddddddddddddddddddddddddddddddddddd",
              "treeHash": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
              "releaseId": "mohist-server-candidate",
              "generation": 4,
              "runnerId": ""
            }
            """);

        var info = new RuntimeBuildInfo(
            environment,
            new StubRuntimeSourceIdentity("source-checkout"),
            new FakeTimeProvider(TestTime.UtcNow),
            files);

        Assert.Null(info.SchemaVersion);
        Assert.Null(info.Component);
        Assert.Null(info.SourceRevision);
        Assert.Null(info.BuildGitHash);
        Assert.Null(info.GitHash);
        Assert.Null(info.TreeHash);
        Assert.Null(info.ArtifactDigest);
        Assert.Null(info.ReleaseId);
        Assert.Equal(0, info.Generation);
    }

    [Fact]
    public void ManagedIdentity_WhenSchemaVersionIsUnknown_DoesNotFallBackToSourceIdentity()
    {
        const string identityPath = "/managed/server/runtime-identity.json";
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        environment[RuntimeBuildInfo.RuntimeIdentityPathEnvironmentVariable] = identityPath;
        var files = new FakeIdentityFileSystem();
        files.WriteAllText(
            identityPath,
            """
            {
              "schemaVersion": 2,
              "component": "server",
              "sourceRevision": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "buildGitHash": "dddddddddddddddddddddddddddddddddddddddd",
              "treeHash": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
              "artifactDigest": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
              "releaseId": "mohist-server-candidate",
              "generation": 4,
              "runnerId": ""
            }
            """);

        var info = new RuntimeBuildInfo(
            environment,
            new StubRuntimeSourceIdentity("source-checkout"),
            new FakeTimeProvider(TestTime.UtcNow),
            files);

        Assert.Null(info.SchemaVersion);
        Assert.Null(info.SourceRevision);
        Assert.Null(info.BuildGitHash);
        Assert.Null(info.GitHash);
    }

    [Fact]
    public void ManagedIdentity_WhenFieldHasWrongJsonType_DoesNotFallBackToSourceIdentity()
    {
        const string identityPath = "/managed/server/runtime-identity.json";
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        environment[RuntimeBuildInfo.RuntimeIdentityPathEnvironmentVariable] = identityPath;
        var files = new FakeIdentityFileSystem();
        files.WriteAllText(
            identityPath,
            """
            {
              "schemaVersion": 1,
              "component": "server",
              "sourceRevision": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "buildGitHash": "dddddddddddddddddddddddddddddddddddddddd",
              "treeHash": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
              "artifactDigest": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
              "releaseId": "mohist-server-candidate",
              "generation": "4",
              "runnerId": ""
            }
            """);

        var info = new RuntimeBuildInfo(
            environment,
            new StubRuntimeSourceIdentity("source-checkout"),
            new FakeTimeProvider(TestTime.UtcNow),
            files);

        Assert.Null(info.SourceRevision);
        Assert.Null(info.GitHash);
        Assert.Equal(0, info.Generation);
    }

    [Fact]
    public void ManagedIdentity_WhenLegacyManifestWithoutSchemaVersion_DoesNotMapBuildGitHashFromSourceRevision()
    {
        const string identityPath = "/managed/server/runtime-identity.json";
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        environment[RuntimeBuildInfo.RuntimeIdentityPathEnvironmentVariable] = identityPath;
        var files = new FakeIdentityFileSystem();
        files.WriteAllText(
            identityPath,
            """
            {
              "component": "server",
              "sourceRevision": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "treeHash": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
              "generation": 4
            }
            """);

        var info = new RuntimeBuildInfo(
            environment,
            new StubRuntimeSourceIdentity("source-checkout"),
            new FakeTimeProvider(TestTime.UtcNow),
            files);

        Assert.Null(info.SchemaVersion);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", info.SourceRevision);
        Assert.Null(info.BuildGitHash);
        Assert.Equal(info.SourceRevision, info.GitHash);
        Assert.Equal(4, info.Generation);
    }

    [Fact]
    public void ManagedIdentity_WhenLegacyManifestHasBuildGitHash_KeepsItDistinctFromSourceRevision()
    {
        const string identityPath = "/managed/server/runtime-identity.json";
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        environment[RuntimeBuildInfo.RuntimeIdentityPathEnvironmentVariable] = identityPath;
        var files = new FakeIdentityFileSystem();
        files.WriteAllText(
            identityPath,
            """
            {
              "component": "server",
              "sourceRevision": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "buildGitHash": "dddddddddddddddddddddddddddddddddddddddd",
              "treeHash": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
              "generation": 4
            }
            """);

        var info = new RuntimeBuildInfo(
            environment,
            new StubRuntimeSourceIdentity("source-checkout"),
            new FakeTimeProvider(TestTime.UtcNow),
            files);

        Assert.Null(info.SchemaVersion);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", info.SourceRevision);
        Assert.Equal("dddddddddddddddddddddddddddddddddddddddd", info.BuildGitHash);
    }

    [Fact]
    public void ManagedIdentity_WhenLegacyManifestHasGitHashOnly_MapsBothFields()
    {
        const string identityPath = "/managed/server/runtime-identity.json";
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        environment[RuntimeBuildInfo.RuntimeIdentityPathEnvironmentVariable] = identityPath;
        var files = new FakeIdentityFileSystem();
        files.WriteAllText(identityPath, "{\"gitHash\":\"legacy-checkout\"}");

        var info = new RuntimeBuildInfo(
            environment,
            new StubRuntimeSourceIdentity("source-checkout"),
            new FakeTimeProvider(TestTime.UtcNow),
            files);

        Assert.Null(info.SchemaVersion);
        Assert.Equal("legacy-checkout", info.SourceRevision);
        Assert.Equal("legacy-checkout", info.BuildGitHash);
        Assert.Equal("legacy-checkout", info.GitHash);
    }

    [Fact]
    public void ManagedIdentity_WhenPathIsMissing_UsesSourceIdentity()
    {
        var info = new RuntimeBuildInfo(
            new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false),
            new StubRuntimeSourceIdentity("source-checkout"),
            new FakeTimeProvider(TestTime.UtcNow),
            new FakeIdentityFileSystem());

        Assert.Null(info.SchemaVersion);
        Assert.Null(info.Component);
        Assert.Null(info.SourceRevision);
        Assert.Null(info.BuildGitHash);
        Assert.NotNull(info.GitHash);
    }

    [Fact]
    public void MetadataIdentity_WhenAssemblyHasInformationalVersion_ReturnsHashWithSourceFallback()
    {
        var time = new FakeTimeProvider(TestTime.UtcNow);
        var info = new RuntimeBuildInfo(
            new MockEnvironmentVariableProvider(),
            new StubRuntimeSourceIdentity("headhash456"),
            time);

        Assert.NotNull(info.Version);
        Assert.NotEmpty(info.Version);
        Assert.False(string.IsNullOrWhiteSpace(info.GitHash));
        Assert.Null(info.Component);
        Assert.Null(info.SourceRevision);
        Assert.Equal(0, info.Generation);
        Assert.Equal(TestTime.UtcNow, info.StartedAt);
    }

    [Fact]
    public void GitHash_WhenInitialized_RemainsStableForProcessLifetime()
    {
        var environment = new MockEnvironmentVariableProvider();
        var sourceIdentity = new StubRuntimeSourceIdentity("headhash456");
        var time = new FakeTimeProvider(TestTime.UtcNow);

        var info1 = new RuntimeBuildInfo(environment, sourceIdentity, time);
        var info2 = new RuntimeBuildInfo(environment, sourceIdentity, time);

        Assert.Equal(info1.GitHash, info2.GitHash);
        Assert.Equal(info1.Version, info2.Version);
    }

    [Fact]
    public void ResolveIdentity_WhenInformationalVersionHasNoHash_FallsBackToEnvironmentHash()
    {
        var identity = RuntimeBuildInfo.ResolveIdentity(
            "1.2.3",
            "1.2.3.0",
            () => "envhash123",
            () => "headhash456");

        Assert.Equal("1.2.3", identity.Version);
        Assert.Equal("envhash123", identity.GitHash);
    }

    [Fact]
    public void ResolveIdentity_WhenInformationalVersionHasNoHashAndEnvironmentIsEmpty_FallsBackToGitHead()
    {
        var identity = RuntimeBuildInfo.ResolveIdentity(
            "1.2.3",
            "1.2.3.0",
            () => null,
            () => "headhash456");

        Assert.Equal("1.2.3", identity.Version);
        Assert.Equal("headhash456", identity.GitHash);
    }

    [Fact]
    public void StartedAt_IsCapturedAtInitialization()
    {
        var time = new FakeTimeProvider(TestTime.UtcNow);

        var info = new RuntimeBuildInfo(
            new MockEnvironmentVariableProvider(),
            new StubRuntimeSourceIdentity(),
            time);

        Assert.Equal(TestTime.UtcNow, info.StartedAt);
    }

    private sealed class StubRuntimeSourceIdentity(string? gitHead = null) : IRuntimeSourceIdentity
    {
        public string? GitHead { get; } = gitHead;
    }

    private sealed class FakeIdentityFileSystem : IFileSystem
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

        public bool Exists(string path) => _files.ContainsKey(path);
        public string ReadAllText(string path) => _files[path];
        public void CreateDirectory(string path) { }
        public long? GetFileLength(string path) =>
            _files.TryGetValue(path, out var contents)
                ? System.Text.Encoding.UTF8.GetByteCount(contents)
                : null;
        public void WriteAllText(string path, string contents) => _files[path] = contents;
        public void Delete(string path) => _files.Remove(path);
    }
}
