using Mohist.Server.Runner.Grains;
using Xunit;
using Mohist.Server.TestSupport;
using Mohist.Server.SpecTests.Specs.Workflow;
using Mohist.Server.OrleansTests.Support;

namespace Mohist.Server.OrleansTests.Specs.Runner.Grain;

[Collection("RunnerGrain")]
public class RunnerBuildIdentitySpecs : WorkflowGrainSpecs
{
    public RunnerBuildIdentitySpecs(OrleansL0WorkflowGrainFixture fixture) : base(fixture) { }

    private async Task<(string RunnerId, IRunnerGrain Runner)> FreshRunnerAsync()
    {
        // Reuse the activation warmed by the fixture, but clear its
        // registration state before each claim so the Specs stay isolated.
        var runnerId = OrleansL0WorkflowGrainFixture.WarmupRunnerId;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();
        return (runnerId, runner);
    }

    [Fact]
    public async Task Register_StoresBuildGitHashOnRunnerInfo()
    {
        var (runnerId, runner) = await FreshRunnerAsync();
        var hash = "abcdef1234567890abcdef1234567890abcdef12";
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
        var (runnerId, runner) = await FreshRunnerAsync();
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", null));

        var info = await runner.GetInfoAsync();
        Assert.NotNull(info);
        Assert.Null(info!.BuildGitHash);
    }

    [Fact]
    public async Task UpdateBuildGitHashAsync_StoresHashOnRegisteredRunner()
    {
        var (runnerId, runner) = await FreshRunnerAsync();
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
        var (runnerId, runner) = await FreshRunnerAsync();

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
        var (runnerId, runner) = await FreshRunnerAsync();
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", null, BuildGitHash: "stale-hash"));

        await runner.UpdateBuildGitHashAsync("   ");

        var info = await runner.GetInfoAsync();
        Assert.NotNull(info);
        Assert.Null(info!.BuildGitHash);
    }

    [Fact]
    public async Task UpdateBuildGitHashAsync_ClearsStaleHashWhenControlHandshakeOmitsIdentity()
    {
        var (runnerId, runner) = await FreshRunnerAsync();
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", null, BuildGitHash: "old-hash"));

        await runner.UpdateBuildGitHashAsync(null);

        var info = await runner.GetInfoAsync();
        Assert.NotNull(info);
        Assert.Null(info!.BuildGitHash);
    }

    [Fact]
    public async Task ListRunners_ExposesBuildGitHashThroughRegistry()
    {
        var (runnerId, runner) = await FreshRunnerAsync();
        var projectId = $"build-hash-project-{Guid.NewGuid():N}";
        var hash = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef";
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", projectId, BuildGitHash: hash));

        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var runners = await registry.ListRunnersAsync();

        var info = Assert.Single(runners, r => r.RunnerId == runnerId);
        Assert.Equal(hash, info.BuildGitHash);
    }

    [Fact]
    public async Task HeartbeatRepair_UpdatesBuildGitHashFromRequest()
    {
        var (runnerId, runner) = await FreshRunnerAsync();
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
