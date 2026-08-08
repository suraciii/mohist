using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

[Collection("AgentJobGrain")]
public sealed class AgentJobDispatchObserverSpecs
{
    private readonly AgentJobGrainFixture _fixture;

    public AgentJobDispatchObserverSpecs(AgentJobGrainFixture fixture)
    {
        _fixture = fixture;
        _fixture.DispatchObserver.Reset();
    }

    [Fact]
    public async Task WaitForRunnerAcceptedAsync_FailsWhenSignalNeverArrives()
    {
        var error = await Record.ExceptionAsync(() => _fixture.DispatchObserver.WaitForRunnerAcceptedAsync());

        Assert.NotNull(error);
        Assert.Contains("Timed out waiting for: AgentJob dispatch observer runner accepted", error.Message);
    }

    [Fact]
    public async Task WaitForAssignmentPreparedAsync_CompletesWhenSignalAlreadyArrived()
    {
        await _fixture.DispatchObserver.AssignmentPreparedAsync("job", "runner", "work");

        await _fixture.DispatchObserver.WaitForAssignmentPreparedAsync();
    }
}
