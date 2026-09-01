using Mohist.Server.Runner.Grains;
using Mohist.Server.L0Tests.Support;
using Xunit;

namespace Mohist.Server.L0Tests.Runner;

[Collection("OrleansGrainL0")]
public class RunnerBuildIdentitySpecs
{
    private readonly OrleansL0WorkflowGrainFixture _fixture;

    public RunnerBuildIdentitySpecs(OrleansL0WorkflowGrainFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ListRunners_ExposesBuildGitHashThroughRegistry()
    {
        var runnerId = OrleansL0WorkflowGrainFixture.WarmupRunnerId;
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
}
