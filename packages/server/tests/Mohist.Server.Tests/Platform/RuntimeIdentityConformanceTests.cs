using System.Text.Json;
using System.Text.Json.Nodes;
using EnvironmentAbstractions.TestHelpers;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.SystemInfo;
using Xunit;

namespace Mohist.Server.Tests.Platform;

[Trait("level", "L0")]
public sealed class RuntimeIdentityConformanceTests
{
    private const string FixtureResourceName = "Mohist.RuntimeIdentityV1Fixture.json";

    private static readonly string[] CanonicalKeys =
    [
        "schemaVersion",
        "component",
        "sourceRevision",
        "buildGitHash",
        "treeHash",
        "artifactDigest",
        "releaseId",
        "generation",
        "runnerId",
    ];

    private static string ReadFixtureText()
    {
        using var stream = typeof(RuntimeIdentityConformanceTests).Assembly
            .GetManifestResourceStream(FixtureResourceName)
            ?? throw new Xunit.Sdk.XunitException("RuntimeIdentity v1 fixture was not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string MutateFixture(Action<JsonObject> mutate)
    {
        var node = JsonNode.Parse(ReadFixtureText())!.AsObject();
        mutate(node);
        return node.ToJsonString();
    }

    [Fact]
    public void CanonicalFixture_HasExactlyTheCanonicalNineFieldKeySet()
    {
        using var document = JsonDocument.Parse(ReadFixtureText());
        var keys = document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(CanonicalKeys.OrderBy(name => name, StringComparer.Ordinal).ToArray(), keys);
    }

    [Fact]
    public void CanonicalFixture_DeclaresCanonicalFieldTypesAndMeanings()
    {
        using var document = JsonDocument.Parse(ReadFixtureText());
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Number, root.GetProperty("schemaVersion").ValueKind);
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("generation").ValueKind);
        Assert.True(root.GetProperty("generation").GetInt64() > 0);
        foreach (var field in new[] { "component", "sourceRevision", "buildGitHash", "treeHash", "artifactDigest", "releaseId", "runnerId" })
        {
            Assert.Equal(JsonValueKind.String, root.GetProperty(field).ValueKind);
            Assert.False(string.IsNullOrEmpty(root.GetProperty(field).GetString()));
        }
        Assert.Equal(root.GetProperty("sourceRevision").GetString(), root.GetProperty("buildGitHash").GetString());
    }

    [Fact]
    public void CanonicalFixture_ParsesIntoTheCanonicalManagedIdentity()
    {
        var identity = RuntimeBuildInfo.ParseManagedIdentity(ReadFixtureText());

        Assert.Equal(1, identity.SchemaVersion);
        Assert.Equal("runner", identity.Component);
        Assert.False(string.IsNullOrEmpty(identity.SourceRevision));
        Assert.Equal(identity.SourceRevision, identity.BuildGitHash);
        Assert.False(string.IsNullOrEmpty(identity.TreeHash));
        Assert.False(string.IsNullOrEmpty(identity.ArtifactDigest));
        Assert.False(string.IsNullOrEmpty(identity.ReleaseId));
        Assert.True(identity.Generation is > 0);
        Assert.False(string.IsNullOrEmpty(identity.RunnerId));
    }

    [Fact]
    public void ManagedIdentity_WhenFixtureIsTheManagedManifest_ReportsEveryCanonicalField()
    {
        const string identityPath = "/managed/runner/runtime-identity.json";
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        environment[RuntimeBuildInfo.RuntimeIdentityPathEnvironmentVariable] = identityPath;
        var files = new FakeFileSystem();
        files.AddFile(identityPath, ReadFixtureText());
        using var document = JsonDocument.Parse(ReadFixtureText());
        var root = document.RootElement;

        var info = new RuntimeBuildInfo(
            environment,
            new StubRuntimeSourceIdentity("source-checkout"),
            new FakeTimeProvider(TestTime.UtcNow),
            files);

        Assert.Equal(1, info.SchemaVersion);
        Assert.Equal(root.GetProperty("component").GetString(), info.Component);
        Assert.Equal(root.GetProperty("sourceRevision").GetString(), info.SourceRevision);
        Assert.Equal(root.GetProperty("buildGitHash").GetString(), info.BuildGitHash);
        Assert.Equal(root.GetProperty("treeHash").GetString(), info.TreeHash);
        Assert.Equal(root.GetProperty("artifactDigest").GetString(), info.ArtifactDigest);
        Assert.Equal(root.GetProperty("releaseId").GetString(), info.ReleaseId);
        Assert.Equal(root.GetProperty("generation").GetInt64(), info.Generation);
    }

    [Fact]
    public void ManagedIdentity_WhenSchemaVersionIsUnknown_IsMalformedWithoutSourceFallback()
    {
        var identity = RuntimeBuildInfo.ParseManagedIdentity(
            MutateFixture(node => node["schemaVersion"] = 2));

        Assert.Null(identity.SchemaVersion);
        Assert.Null(identity.SourceRevision);
        Assert.Null(identity.BuildGitHash);
    }

    [Fact]
    public void ManagedIdentity_WhenFieldHasWrongType_IsMalformedWithoutSourceFallback()
    {
        var identity = RuntimeBuildInfo.ParseManagedIdentity(
            MutateFixture(node => node["generation"] = "42"));

        Assert.Null(identity.SchemaVersion);
        Assert.Null(identity.SourceRevision);
        Assert.Null(identity.BuildGitHash);
    }

    [Fact]
    public void ManagedIdentity_WhenRequiredFieldIsMissing_IsMalformedWithoutSourceFallback()
    {
        var identity = RuntimeBuildInfo.ParseManagedIdentity(
            MutateFixture(node => { node.Remove("buildGitHash"); }));

        Assert.Null(identity.SchemaVersion);
        Assert.Null(identity.SourceRevision);
        Assert.Null(identity.BuildGitHash);
    }

    [Fact]
    public void ManagedIdentity_WhenLegacyGitHashOnly_MapsBothFieldsWithoutSchemaVersion()
    {
        var identity = RuntimeBuildInfo.ParseManagedIdentity("{\"gitHash\":\"legacy-sha\"}");

        Assert.Null(identity.SchemaVersion);
        Assert.Equal("legacy-sha", identity.BuildGitHash);
        Assert.Equal("legacy-sha", identity.SourceRevision);
    }

    private sealed class StubRuntimeSourceIdentity(string? gitHead = null) : IRuntimeSourceIdentity
    {
        public string? GitHead { get; } = gitHead;
    }
}
