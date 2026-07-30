using Mohist.Server.Agent.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Specs.Workflow;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

[Collection("AgentJobGrain")]
public sealed class AgentJobCancellationSpecs : AgentJobGrainTestSupport
{
    public AgentJobCancellationSpecs(AgentJobGrainFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task CancelAsync_PendingJobBecomesCancelledWithoutDispatch()
    {
        await ClearGlobalRunnerRegistryAsync();
        var jobKey = $"agent-job-cancel-{Guid.NewGuid():N}";
        var sessionId = $"session-cancel-{Guid.NewGuid():N}";
        var turnId = $"turn-cancel-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);

        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            Metadata: GenericAgentSessionMetadata.Metadata(new GenericAgentSessionContext(
                ProjectId: "cancel-project",
                AgentId: "cancel-agent",
                AgentName: "cancel-agent"))));
        await session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            JobId: jobKey,
            InputId: $"input-{Guid.NewGuid():N}",
            TurnId: turnId,
            Prompt: "cancel me",
            Source: "agent-launch"));
        await job.PrepareManualLaunchAsync(new PrepareManualLaunchCommand(
            SessionId: sessionId,
            InputId: (await session.GetInitialLaunchAsync())!.Input!.Id,
            TurnId: turnId,
            Prompt: "cancel me",
            AgentId: "cancel-agent"));

        var result = await job.CancelAsync();

        Assert.Equal(AgentJobCancelDisposition.Cancelled, result.Disposition);
        Assert.Equal(AgentJobStatus.Cancelled, await job.GetStatusAsync());
        Assert.Equal(AgentTurnStatus.Cancelled, (await session.ListTurnsAsync()).Single().Status);
        Assert.Equal("idle", (await session.GetAsync())!.Status);
    }

    [Fact]
    public async Task CancelAsync_RunningJobRejectsCancelAndPreservesExecution()
    {
        var (_, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-cancel-race-{Guid.NewGuid():N}");
        var job = JobGrain($"agent-job-cancel-race-{Guid.NewGuid():N}");

        await job.SubmitAsync(MakeInput("already running", projectId, "/tmp/agent-job-cancel-race"));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var result = await job.CancelAsync();

        Assert.Equal(AgentJobCancelDisposition.Executing, result.Disposition);
        Assert.Equal(AgentJobStatus.Running, result.Status);
        Assert.Equal(AgentJobStatus.Running, await job.GetStatusAsync());
    }
}
