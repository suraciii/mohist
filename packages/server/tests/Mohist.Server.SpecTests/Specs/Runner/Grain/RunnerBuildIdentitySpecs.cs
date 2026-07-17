using Mohist.Server.Runner.Grains;
using Xunit;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Runner.Grain;

[Collection("RunnerGrain")]
public class RunnerBuildIdentitySpecs : WorkflowGrainSpecs
{
    public RunnerBuildIdentitySpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Register_StoresBuildGitHashOnRunnerInfo()
    {
        var runnerId = $"runner-build-{Guid.NewGuid():N}";
        var hash = "abcdef1234567890abcdef1234567890abcdef12";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            null,
            BuildGitHash: hash));

        var info = await runner.GetInfoAsync();
        Assert.NotNull(info);
        Assert.Equal(hash, info!.BuildGitHash);
    }

    [Fact]
    public async Task Register_DefaultsBuildGitHashToNullWhenOmitted()
    {
        var runnerId = $"runner-nohash-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", null));

        var info = await runner.GetInfoAsync();
        Assert.NotNull(info);
        Assert.Null(info!.BuildGitHash);
    }

    [Fact]
    public async Task UpdateBuildGitHashAsync_StoresHashOnRegisteredRunner()
    {
        var runnerId = $"runner-update-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", null));

        var hash = "0123456789abcdef0123456789abcdef01234567";
        await runner.UpdateBuildGitHashAsync(hash);

        var info = await runner.GetInfoAsync();
        Assert.NotNull(info);
        Assert.Equal(hash, info!.BuildGitHash);
    }

    [Fact]
    public async Task UpdateBuildGitHashAsync_BuffersHashUntilRegister()
    {
        var runnerId = $"runner-buffer-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var hash = "feedfacefeedfacefeedfacefeedfacefeedface";
        await runner.UpdateBuildGitHashAsync(hash);

        // The grain is offline; buffered hash is consumed on next register.
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", null));

        var info = await runner.GetInfoAsync();
        Assert.NotNull(info);
        Assert.Equal(hash, info!.BuildGitHash);
    }

    [Fact]
    public async Task UpdateBuildGitHashAsync_NormalisesBlankHashToNull()
    {
        var runnerId = $"runner-blank-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", null, BuildGitHash: "stale-hash"));

        await runner.UpdateBuildGitHashAsync("   ");

        var info = await runner.GetInfoAsync();
        Assert.NotNull(info);
        Assert.Null(info!.BuildGitHash);
    }

    [Fact]
    public async Task UpdateBuildGitHashAsync_ClearsStaleHashWhenSignalRHandshakeOmitsIdentity()
    {
        var runnerId = $"runner-clear-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", null, BuildGitHash: "old-hash"));

        await runner.UpdateBuildGitHashAsync(null);

        var info = await runner.GetInfoAsync();
        Assert.NotNull(info);
        Assert.Null(info!.BuildGitHash);
    }

    [Fact]
    public async Task ListRunners_ExposesBuildGitHashThroughRegistry()
    {
        var projectId = $"build-hash-project-{Guid.NewGuid():N}";
        var runnerId = $"runner-registry-{Guid.NewGuid():N}";
        var hash = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", projectId, BuildGitHash: hash));

        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var runners = await registry.ListRunnersAsync();

        var info = Assert.Single(runners, r => r.RunnerId == runnerId);
        Assert.Equal(hash, info.BuildGitHash);
    }

    [Fact]
    public async Task HeartbeatRepair_UpdatesBuildGitHashFromRequest()
    {
        var runnerId = $"runner-heartbeat-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", null, BuildGitHash: "old-hash"));

        var newHash = "newhashnewhashnewhashnewhashnewhashnewhas";
        await runner.HeartbeatRepairAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            null,
            BuildGitHash: newHash));

        var info = await runner.GetInfoAsync();
        Assert.NotNull(info);
        Assert.Equal(newHash, info!.BuildGitHash);
    }
}
