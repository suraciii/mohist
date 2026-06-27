using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Tests.Specs.Workflow;
using Xunit;

namespace Mohist.Server.Tests.Specs.Runner.Grain;

[Collection("WorkflowGrain")]
public class RunnerGrainTimeProviderSpecs : WorkflowGrainSpecs
{
    public RunnerGrainTimeProviderSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task AssignAgentJobAsync_RecordsTakeTimeFromFakeTimeProvider()
    {
        var runnerId = $"time-runner-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "time-host",
            "test-project"));

        var before = _fixture.TimeProvider.GetUtcNow();
        await runner.AssignAgentJobAsync(new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: "agent-work-1",
            OwnerKind: WorkDispatchOwnerKinds.AgentJob,
            AgentJobId: "agent-job-1"));

        var runtime = await runner.GetRuntimeStateAsync();
        var active = Assert.Single(runtime.ActiveWorks);
        Assert.Equal("agent-work-1", active.WorkId);
        Assert.Equal(before, active.TakenAt);

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(5));
        var after = _fixture.TimeProvider.GetUtcNow();

        await runner.AssignAgentJobAsync(new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: "agent-work-2",
            OwnerKind: WorkDispatchOwnerKinds.AgentJob,
            AgentJobId: "agent-job-2"));

        runtime = await runner.GetRuntimeStateAsync();
        var second = runtime.ActiveWorks.Single(w => w.WorkId == "agent-work-2");
        Assert.Equal(after, second.TakenAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task PollOneWorkflowAsync_RecordsTakeTimeFromFakeTimeProvider()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var before = _fixture.TimeProvider.GetUtcNow();
        var (work, _) = await PollWorkAnyAsync();

        var runtime = await runner.GetRuntimeStateAsync();
        var active = Assert.Single(runtime.ActiveWorks);
        Assert.Equal(work.WorkId, active.WorkId);
        Assert.Equal(before, active.TakenAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public void WorkflowOptions_ResolvesToTestConfiguredWorkCompletionTimeout()
    {
        var provider = _fixture.Cluster.GetSiloServiceProvider(null);
        var options = provider.GetRequiredService<IOptions<WorkflowOptions>>().Value;

        Assert.Equal(TimeSpan.FromMinutes(10), options.WorkCompletionTimeout);
    }
}
