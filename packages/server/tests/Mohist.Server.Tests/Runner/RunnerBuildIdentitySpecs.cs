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
}
