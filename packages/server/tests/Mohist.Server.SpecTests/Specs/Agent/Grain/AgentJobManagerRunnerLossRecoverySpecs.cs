using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

/// <summary>
/// Server-side Runner loss is the one Manager unknown transition that no
/// Runner report follows up on: <see cref="IAgentJobGrain.MarkUnknownAsync"/>
/// with a recovery deadline must create the single Manager recovery turn
/// itself, and the unknown dispatch stays suppressed instead of replaying
/// the uncertain Manager prompt.
/// </summary>
[Collection("AgentJobGrain")]
public sealed class AgentJobManagerRunnerLossRecoverySpecs : AgentJobGrainTestSupport
{
    public AgentJobManagerRunnerLossRecoverySpecs(AgentJobGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RunnerLoss_OnManagerJob_CreatesExactlyOneRecoveryTurnAndSuppressesRedelivery()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            "agent-job-manager-loss-runner",
            projectId: "agent-job-manager-loss-project");
        var jobKey = $"agent-job-manager-loss-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);
        var sessionId = $"manager-loss-session-{Guid.NewGuid():N}";
        await OpenSessionAsync(sessionId, projectId);

        var context = ManagerContext(sessionId, jobKey);
        await job.SubmitAsync(new AgentJobInput(
            Prompt: "manager request",
            ProjectId: projectId,
            AgentId: "agent-test",
            AgentSessionId: sessionId,
            ExecutionSource: AgentExecutionSources.Slack,
            SlackExecutionContext: context));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        await Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();

        var snapshot = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(AgentJobStatus.Unknown, snapshot.Status);
        Assert.True(snapshot.IsRecovering);
        Assert.Equal(AgentJobFailureReasons.RunnerLost, snapshot.FailureReason);

        var recoveryTurnId = $"manager-recovery-turn:{jobKey}";
        var recoveryTurn = Assert.Single(
            (await SessionTurnsAsync(sessionId)),
            turn => turn.Id == recoveryTurnId);
        Assert.Null(recoveryTurn.WorkflowExecution);

        // Repeated server-side loss transitions stay idempotent: the
        // recovery turn exists exactly once.
        await job.MarkUnknownAsync(
            AgentJobFailureReasons.RunnerLost,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(30));
        Assert.Single(
            await SessionTurnsAsync(sessionId),
            turn => turn.Id == recoveryTurnId);

        // The uncertain Manager dispatch is never replayed to a replacement
        // Runner; recovery continues through the recorded recovery turn.
        using var scope = _fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<IServiceScopeFactory>()
            .CreateScope();
        var dispatch = scope.ServiceProvider.GetRequiredService<DispatchService>();
        Assert.Empty(
            (await dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);
    }

    [Fact]
    public async Task RunnerLoss_OnOrdinaryJob_StillRedeliversWithoutRecoveryTurn()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            "agent-job-manager-loss-ordinary-runner",
            projectId: "agent-job-manager-loss-ordinary-project");
        var jobKey = $"agent-job-manager-loss-ordinary-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);
        var sessionId = $"ordinary-loss-session-{Guid.NewGuid():N}";
        await OpenSessionAsync(sessionId, projectId);

        await job.SubmitAsync(new AgentJobInput(
            Prompt: "ordinary request",
            ProjectId: projectId,
            AgentId: "agent-test",
            AgentSessionId: sessionId));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        await Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();
        var snapshot = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(AgentJobStatus.Unknown, snapshot.Status);

        var turns = await SessionTurnsAsync(sessionId);
        Assert.DoesNotContain(turns, turn => turn.Id.StartsWith("manager-recovery-turn:", StringComparison.Ordinal));
    }

    private static AgentSlackExecutionContext ManagerContext(string sessionId, string jobKey) =>
        SlackExecutionContextFactory.Create(
            "workspace-1",
            "conversation-1",
            "thread-1",
            "message-1",
            "member-1",
            "connection-1",
            sessionId,
            $"dispatch-{jobKey}",
            projectId: SlackDeliveryOwnerIds.ManagerProjectId,
            ownerKind: SlackDeliveryOwnerKinds.Manager);

    private async Task<IReadOnlyList<AgentTurnRecord>> SessionTurnsAsync(string sessionId) =>
        await Grains.GetGrain<IAgentSessionGrain>(sessionId).ListTurnsAsync();

    private async Task OpenSessionAsync(string sessionId, string projectId)
    {
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: "/tmp/agent-job-fixture",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = "agent-test",
            })));
    }
}
