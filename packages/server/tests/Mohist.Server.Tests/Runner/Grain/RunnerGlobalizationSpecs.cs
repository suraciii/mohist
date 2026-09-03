using Mohist.Server.Tests.Support;
using Mohist.Server.Runner.Grains;
using Xunit;

namespace Mohist.Server.Tests.Runner.Grain;

[Collection("ComponentGrain")]
[Trait("level", "L0")]
public sealed class RunnerGlobalizationSpecs
{
    private readonly ComponentWorkflowGrainFixture _fixture;

    public RunnerGlobalizationSpecs(ComponentWorkflowGrainFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Register_RunnerIsRecordedInGlobalRegistry_NotInProjectRegistry()
    {
        var runnerId = $"globalized-runner-{Guid.NewGuid():N}";
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);

        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "globalized-host",
            "some-legacy-project-id"));

        var globalRegistry = _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var globalIds = await globalRegistry.ListRunnerIdsAsync();
        Assert.Contains(runnerId, globalIds);
    }

    [Fact]
    public async Task ListEligibleRunnersAsync_ReturnsAllRegisteredRunnersRegardlessOfProjectIdField()
    {
        var globalRunnerId = $"global-runner-{Guid.NewGuid():N}";
        var projectRunnerId = $"legacy-project-runner-{Guid.NewGuid():N}";

        var globalRunner = _fixture.Grains.GetGrain<IRunnerGrain>(globalRunnerId);
        await globalRunner.RegisterAsync(new RunnerInfo(globalRunnerId, ["spec/*"], "host-g", null));

        var projectRunner = _fixture.Grains.GetGrain<IRunnerGrain>(projectRunnerId);
        await projectRunner.RegisterAsync(new RunnerInfo(projectRunnerId, ["spec/*"], "host-p", "any-project-id"));

        var registry = _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var eligible = await registry.ListEligibleRunnersAsync("querying-project");

        Assert.Contains(eligible, r => r.RunnerId == globalRunnerId);
        Assert.Contains(eligible, r => r.RunnerId == projectRunnerId);
    }
}
