using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Runner;

[Collection("ComponentGrain")]
[Trait("level", "L0")]
public class RunnerBuildIdentitySpecs
{
    private readonly ComponentWorkflowGrainFixture _fixture;

    public RunnerBuildIdentitySpecs(ComponentWorkflowGrainFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ListRunners_ExposesBuildGitHashThroughRegistry()
    {
        var runnerId = ComponentWorkflowGrainFixture.WarmupRunnerId;
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();
        var projectId = $"build-hash-project-{Guid.NewGuid():N}";
        var hash = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef";
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", projectId, BuildGitHash: hash));

        var registry = _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var runners = await registry.ListRunnersAsync();

        var info = Assert.Single(runners, r => r.RunnerId == runnerId);
        Assert.Equal(hash, info.BuildGitHash);
    }

    [Fact]
    public async Task Register_PreservesCanonicalSchemaVersionAndBuildGitHash()
    {
        var runnerId = $"canonical-identity-{Guid.NewGuid():N}";
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            $"canonical-project-{Guid.NewGuid():N}",
            BuildGitHash: "build-sha",
            Component: "runner",
            SourceRevision: "source-sha",
            TreeHash: "tree-sha",
            ArtifactDigest: "digest",
            ReleaseId: "release-1",
            Generation: 4,
            SchemaVersion: 1), "process-canonical");

        var info = await runner.GetInfoAsync();

        Assert.Equal(1, info?.SchemaVersion);
        Assert.Equal("build-sha", info?.BuildGitHash);
        Assert.Equal("source-sha", info?.SourceRevision);
        Assert.Equal("release-1", info?.ReleaseId);
        Assert.Equal(4, info?.Generation);
    }

    [Fact]
    public async Task ControlHandshakeIdentity_RecordedWhenItArrivesBeforeRegister()
    {
        var runnerId = $"canonical-handshake-{Guid.NewGuid():N}";
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UpdateRuntimeIdentityAsync(
            "build-sha",
            "runner",
            "0.0.0+source-sha",
            "source-sha",
            "tree-sha",
            "digest",
            "release-1",
            4,
            "conn:1",
            1);

        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            $"canonical-handshake-project-{Guid.NewGuid():N}"), "process-handshake");

        var info = await runner.GetInfoAsync();

        Assert.Equal(1, info?.SchemaVersion);
        Assert.Equal("build-sha", info?.BuildGitHash);
        Assert.Equal("source-sha", info?.SourceRevision);
    }

    [Fact]
    public async Task ControlHandshakeIdentity_UpdatesRegisteredRunner()
    {
        var runnerId = $"canonical-handshake-update-{Guid.NewGuid():N}";
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            $"canonical-handshake-update-project-{Guid.NewGuid():N}"), "process-handshake-update");

        await runner.UpdateRuntimeIdentityAsync(
            "build-sha",
            "runner",
            "0.0.0+source-sha",
            "source-sha",
            "tree-sha",
            "digest",
            "release-1",
            4,
            "conn:2",
            1);

        var info = await runner.GetInfoAsync();

        Assert.Equal(1, info?.SchemaVersion);
        Assert.Equal("build-sha", info?.BuildGitHash);
        Assert.Equal("source-sha", info?.SourceRevision);
    }
}
