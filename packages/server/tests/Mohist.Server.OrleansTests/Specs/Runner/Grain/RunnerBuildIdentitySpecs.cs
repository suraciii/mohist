using Mohist.Server.OrleansTests.Support;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Specs.Workflow;
using Xunit;

namespace Mohist.Server.OrleansTests.Specs.Runner.Grain;

[Collection("RunnerGrain")]
public class RunnerBuildIdentitySpecs : WorkflowGrainSpecs
{
    public RunnerBuildIdentitySpecs(OrleansL0WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task ListRunners_ExposesBuildGitHashThroughRegistry()
    {
        var runnerId = OrleansL0WorkflowGrainFixture.WarmupRunnerId;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();
        var projectId = $"build-hash-project-{Guid.NewGuid():N}";
        var hash = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef";
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", projectId, BuildGitHash: hash));

        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var runners = await registry.ListRunnersAsync();

        var info = Assert.Single(runners, r => r.RunnerId == runnerId);
        Assert.Equal(hash, info.BuildGitHash);
    }
}
