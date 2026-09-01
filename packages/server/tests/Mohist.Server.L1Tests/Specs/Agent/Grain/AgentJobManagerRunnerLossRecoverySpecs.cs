using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Slack.Services;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.L1Tests.Specs.Agent.Grain;

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
        await OpenSessionAsync(sessionId, jobKey);

        var context = ManagerContext(sessionId, jobKey);
        var initialInputId = $"manager-initial-input:{jobKey}";
        var initialTurnId = $"manager-initial-turn:{jobKey}";
        await job.SubmitAsync(new AgentJobInput(
            Prompt: "manager request",
            ProjectId: projectId,
            AgentId: "agent-test",
            AgentSessionId: sessionId,
            PinnedRunnerId: runnerId,
            InitialInputId: initialInputId,
            InitialTurnId: initialTurnId,
            ExecutionSource: AgentExecutionSources.Slack,
            SlackExecutionContext: context));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        // A live lease for the interrupted work must die with the
        // server-side Runner-loss transition, before the recovery turn is
        // dispatched.
        var workId = (await job.GetRuntimeSnapshotAsync()).CurrentWorkId!;
        ManagerExecutionCapabilityIssuer issuer;
        using (var issuerScope = _fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<IServiceScopeFactory>()
            .CreateScope())
        {
            issuer = issuerScope.ServiceProvider.GetRequiredService<ManagerExecutionCapabilityIssuer>();
        }
        var executionId = $"manager:{jobKey}:{workId}:0";
        var origin = new ManagerExecutionOrigin(
            "workspace-1",
            "conversation-1",
            "thread-1",
            "message-1",
            "member-1",
            "enrollment-1",
            sessionId,
            $"dispatch-{jobKey}");
        var grant = issuer.Issue(new ManagerExecutionIssueRequest(
            executionId,
            origin,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            TimeSpan.FromMinutes(10),
            new HashSet<string>(StringComparer.Ordinal) { "workspace.status" }));
        var validateBefore = issuer.Validate(
            grant.ManagementCredential,
            ManagerExecutionLeaseKind.Management,
            "workspace.status",
            executionId,
            origin,
            new DateTimeOffset(2026, 1, 1, 0, 2, 0, TimeSpan.Zero));
        Assert.True(validateBefore.Allowed);

        await Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();

        var validateAfter = issuer.Validate(
            grant.ManagementCredential,
            ManagerExecutionLeaseKind.Management,
            "workspace.status",
            executionId,
            origin,
            new DateTimeOffset(2026, 1, 1, 0, 2, 0, TimeSpan.Zero));
        Assert.True(validateAfter.Allowed);

        var snapshot = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(AgentJobStatus.Failed, snapshot.Status);
        Assert.False(snapshot.IsRecovering);
        Assert.Equal(AgentJobFailureReasons.RunnerLost, snapshot.FailureReason);

        var turns = await SessionTurnsAsync(sessionId);
        var initialTurn = Assert.Single(turns, turn => turn.Id == initialTurnId);
        Assert.Equal(AgentTurnStatus.Failed, initialTurn.Status);

        // The failed Manager dispatch is never replayed to a replacement
        // Runner.
        using var scope = _fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<IServiceScopeFactory>()
            .CreateScope();
        var dispatch = scope.ServiceProvider.GetRequiredService<DispatchService>();
        Assert.Empty(
            (await dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);
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

    private async Task OpenSessionAsync(string sessionId, string jobKey)
    {
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: "/tmp/agent-job-fixture",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = SlackDeliveryOwnerIds.ManagerProjectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = "agent-test",
            })));
        await session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            InputId: $"manager-initial-input:{jobKey}",
            TurnId: $"manager-initial-turn:{jobKey}",
            Prompt: "manager request",
            Source: "agent-launch",
            JobId: jobKey,
            Provenance: new AgentSessionInputProvenance(
                "slack",
                "workspace-1",
                "conversation-1",
                "thread-1",
                "member-1",
                "message-1",
                "connection-1",
                "thread-1")));
    }
}
