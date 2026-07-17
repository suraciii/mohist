using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Specs.Workflow;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Grain;

[Collection("RunnerGrain")]
public class RunnerGrainTimeProviderSpecs : WorkflowGrainSpecs
{
    public RunnerGrainTimeProviderSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

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
        await runner.UpdateAsync(2);

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

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
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

    [Fact]
    public async Task Heartbeat_DoesNotRefreshPresence_ButPollPresenceDoes()
    {
        var runnerId = $"presence-runner-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "presence-host",
            "test-project"));

        var registeredPresence = (await runner.GetRuntimeStateAsync()).LastHeartbeatAt;
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(30));

        await runner.HeartbeatAsync();
        Assert.Equal(registeredPresence, (await runner.GetRuntimeStateAsync()).LastHeartbeatAt);

        await runner.TouchPresenceAsync();
        Assert.Equal(_fixture.TimeProvider.GetUtcNow(), (await runner.GetRuntimeStateAsync()).LastHeartbeatAt);
    }

    // PollOneWorkflowAsync_RecordsTakeTimeFromFakeTimeProvider was removed:
    // under the reconciliation model the runner grain holds no workflow work
    // records, so it no longer records a take-time for dispatched workflow
    // work. The workflow run owns that timing (task.StartedAt), set with real
    // UTC time when the work is claimed — not the runner's injectable time
    // provider. Agent-job work (above) still flows through the runner ledger
    // and still records the fake-provider take-time.
}
