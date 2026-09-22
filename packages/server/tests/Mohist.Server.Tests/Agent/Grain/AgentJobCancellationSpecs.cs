using Mohist.Server.Agent.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Tests.Workflow;
using Xunit;

namespace Mohist.Server.Tests.Agent.Grain;

[Collection("AgentJobGrain")]
[Trait("level", "L1")]
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
    public async Task CancelAsync_QueuedJobRemovesItsPersistedConcurrencyWaiter()
    {
        await ClearGlobalRunnerRegistryAsync();
        var projectId = $"agent-job-cancel-queued-project-{Guid.NewGuid():N}";
        await _fixture.SeedAgentAsync(projectId, "agent-test", maxConcurrentRuns: 1);
        var gate = Grains.GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, "agent-test"));

        Assert.Equal(
            AgentConcurrencyAcquireResult.Granted,
            await gate.AcquireAsync(
                projectId,
                "agent-test",
                "active-job",
                "active-job",
                AgentConcurrencyPermitOwnerKind.Job));

        var jobKey = $"agent-job-cancel-queued-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);
        await job.SubmitAsync(MakeInput("queued job", projectId));

        Assert.Contains(
            (await gate.GetSnapshotAsync()).Waiters,
            waiter => waiter.OwnerKind == AgentConcurrencyPermitOwnerKind.Job
                && waiter.OwnerId == jobKey);

        var cancelled = await job.CancelAsync();

        Assert.Equal(AgentJobCancelDisposition.Cancelled, cancelled.Disposition);
        Assert.DoesNotContain(
            (await gate.GetSnapshotAsync()).Waiters,
            waiter => waiter.OwnerId == jobKey);

        await gate.ReleaseAsync(projectId, "agent-test", "active-job");
        var afterRelease = await gate.GetSnapshotAsync();
        Assert.DoesNotContain(afterRelease.ActivePermits, permit => permit.OwnerId == jobKey);
        Assert.DoesNotContain(afterRelease.PendingNotifications, notification => notification.OwnerId == jobKey);
    }

    [Fact]
    public async Task AbortPreparedLaunchAsync_UnsubmittedPlanBecomesCancelled()
    {
        var job = JobGrain($"agent-job-abort-prepared-{Guid.NewGuid():N}");
        await job.PrepareManualLaunchAsync(ManualCommand("abort-prepared-project"));
        var eventCount = _fixture.EventStore.Appended.Count;

        await job.AbortPreparedLaunchAsync("session_identity_mismatch");

        Assert.Equal(AgentJobStatus.Cancelled, await job.GetStatusAsync());
        Assert.Equal(eventCount, _fixture.EventStore.Appended.Count);
    }

    [Fact]
    public async Task AbortPreparedLaunchAsync_RunningAndUnknownExecutionRemainInspectable()
    {
        var (_, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-abort-advanced-{Guid.NewGuid():N}");
        var job = JobGrain($"agent-job-abort-advanced-{Guid.NewGuid():N}");
        await job.PrepareManualLaunchAsync(ManualCommand(projectId));
        await job.SubmitPreparedLaunchAsync();
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        await job.AbortPreparedLaunchAsync("session_identity_mismatch");
        Assert.Equal(AgentJobStatus.Running, await job.GetStatusAsync());

        await job.MarkUnknownAsync("execution outcome is uncertain");
        await job.AbortPreparedLaunchAsync("session_identity_mismatch");
        Assert.Equal(AgentJobStatus.Unknown, await job.GetStatusAsync());
    }

    [Fact]
    public async Task AbortPreparedLaunchAsync_TerminalOutcomeRemainsUntouched()
    {
        var job = JobGrain($"agent-job-abort-terminal-{Guid.NewGuid():N}");
        await job.PrepareManualLaunchAsync(ManualCommand("abort-terminal-project"));
        await job.FailAsync("authoritative terminal failure", "agent-test");
        var before = await job.GetRuntimeSnapshotAsync();

        await job.AbortPreparedLaunchAsync("session_identity_mismatch");

        var after = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(AgentJobStatus.Failed, after.Status);
        Assert.Equal(before.FailureReason, after.FailureReason);
        Assert.Equal("authoritative terminal failure", after.FailureReason);
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

    private static PrepareManualLaunchCommand ManualCommand(string projectId) => new(
        SessionId: $"session-{Guid.NewGuid():N}",
        InputId: $"input-{Guid.NewGuid():N}",
        TurnId: $"turn-{Guid.NewGuid():N}",
        Prompt: "prepared work",
        ProjectId: projectId,
        AgentId: "agent-test",
        Runtime: "opencode");
}
