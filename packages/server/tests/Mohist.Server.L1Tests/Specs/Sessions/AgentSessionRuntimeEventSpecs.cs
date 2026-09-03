using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Api;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.L1Tests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.L1Tests.Specs.Sessions;

[Trait("level", "L1")]
public class AgentSessionRuntimeEventSpecs : AgentSessionTestSupport, IClassFixture<DefaultMohistIntegrationFixture>
{
    public AgentSessionRuntimeEventSpecs(DefaultMohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task ManagerCredentialExpiryRoute_CreatesOneQueuedRecoveryTurn()
    {
        var sessionId = $"manager-route-expiry-{Guid.NewGuid():N}";
        var provenance = new AgentSessionInputProvenance(
            "slack",
            "workspace-route",
            "conversation-route",
            "thread-route",
            "member-route",
            "message-route",
            "connection-route",
            "thread-route");
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            _runnerId,
            "opencode",
            WorkDir: "/work",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = SlackDeliveryOwnerIds.ManagerProjectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = "manager-agent",
            })));
        await grain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            "manager-route-input",
            "manager-route-turn",
            "manager request",
            "agent-launch",
            "manager-route-job",
            Provenance: provenance));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-route"));
        await grain.MarkInitialTurnTerminalAsync("manager-route-job", AgentTurnStatus.Completed, null);
        var followup = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            "continue",
            "agent-session-followup",
            "manager-route-followup",
            Provenance: provenance));

        await _client.PostOkAsync(
            $"/api/runner/{_runnerId}/agent-sessions/{Uri.EscapeDataString(SlackDeliveryOwnerIds.ManagerProjectId)}/{sessionId}/runtime-events",
            new
            {
                runtimeSessionId = "runtime-route",
                agentSessionId = sessionId,
                agentTurnId = followup.TurnId,
                runtimeEvents = new[]
                {
                    new
                    {
                        type = "session.activity",
                        payload = new
                        {
                            activity = "unknown",
                            status = "unknown",
                            reason = "manager-credential-expired",
                            failureCategory = "unknown",
                            operationId = followup.OperationId,
                            turnId = followup.TurnId,
                        },
                    },
                },
            });

        var turns = await grain.ListTurnsAsync();
        var recoveryTurn = Assert.Single(
            turns,
            turn => turn.Id == $"manager-recovery-turn:{sessionId}");
        Assert.Equal(AgentTurnStatus.Queued, recoveryTurn.Status);
        Assert.Equal(AgentTurnStatus.Unknown, Assert.Single(turns, turn => turn.Id == followup.TurnId).Status);
        Assert.Single(turns, turn => turn.Id == $"manager-recovery-turn:{sessionId}");

        // The recovery turn must enter the ordinary dispatch contract: the
        // dispatcher claims it and would hand the recovery agent a fresh
        // Manager grant instead of leaving the turn recorded but unexecuted.
        var dispatch = await grain.BeginNextFollowupDispatchAsync();
        Assert.NotNull(dispatch);
        Assert.Equal($"manager-recovery-turn:{sessionId}", dispatch!.TurnId);
        Assert.Equal($"manager-recovery-input:{sessionId}", dispatch.InputId);
    }

    [Fact]
    public async Task UnknownInitialManagerTurn_RecoveryDispatchesFreshGrant()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var sessionId = $"manager-initial-recovery-{suffix}";
        var runnerId = $"manager-initial-recovery-runner-{suffix}";
        var workspaceId = $"workspace-initial-recovery-{suffix}";
        var enrollmentId = $"enrollment-initial-recovery-{suffix}";
        var memberId = $"member-initial-recovery-{suffix}";
        var initialProvenance = new AgentSessionInputProvenance(
            "slack",
            workspaceId,
            $"conversation-{suffix}",
            $"thread-{suffix}",
            memberId,
            $"message-{suffix}",
            enrollmentId,
            $"thread-{suffix}",
            AgentOriginMarkers.SlackManager);

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var now = _fixture.TimeProvider.GetUtcNow();
            db.SlackWorkspaceEnrollments.Add(new SlackWorkspaceEnrollmentRow
            {
                Id = enrollmentId,
                WorkspaceTeamId = workspaceId,
                Lifecycle = SlackEnrollmentLifecycle.Active,
                ManagerCapability = SlackManagerCapability.Available,
                ManagerReadiness = SlackManagerReadiness.Ready,
                ManagerActorId = $"manager-actor-{suffix}",
                ClaimedSlackUserId = memberId,
                PlanCode = "unknown",
                AuditJson = "[]",
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var transport = _fixture.Services.GetRequiredService<RecordingRunnerControlTransport>();
        using var transportOwner = transport.CreateOwner(runnerId);
        var delivered = new TaskCompletionSource<IReadOnlyList<object?>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transportOwner.SetInvocationResponseFactory("session.followup", arguments =>
        {
            delivered.TrySetResult(arguments);
            return new RunnerFollowupDeliveryResult(true);
        });

        await grain.OpenAsync(new OpenAgentSessionCommand(
            runnerId,
            "opencode",
            WorkDir: "/work",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = SlackDeliveryOwnerIds.ManagerProjectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-connection",
                [AgentSessionQueryMetadataKeys.ConnectionId] = enrollmentId,
                [AgentSessionQueryMetadataKeys.OriginMarker] = AgentOriginMarkers.SlackManager,
                [GenericAgentSessionMetadata.AgentId] = "manager-agent",
            })));
        await grain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            "manager-initial-input",
            "manager-initial-turn",
            "manager request",
            "agent-connection",
            "manager-initial-job",
            Provenance: initialProvenance));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-initial-recovery"));
        await grain.MarkInitialTurnTerminalAsync("manager-initial-job", AgentTurnStatus.Unknown, null);
        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.EnsureManagerCredentialExpiryRecoveryAsync();
        await persistence.WaitAsync(TestContext.Current.CancellationToken);

        var recoveryTurnId = $"manager-recovery-turn:{sessionId}";
        var turns = await grain.ListTurnsAsync();
        Assert.Contains(turns, turn => turn.Id == recoveryTurnId && turn.Status == AgentTurnStatus.Queued);

        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "manager-recovery-runner",
            SlackDeliveryOwnerIds.ManagerProjectId,
            RuntimeCatalogs: CapabilityCatalogTestHelpers.Create()));

        try
        {
            await using var scope = _fixture.Services.CreateAsyncScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<AgentSessionFollowupDispatcher>();
            await dispatcher.DispatchForTurnAsync(
                SlackDeliveryOwnerIds.ManagerProjectId,
                sessionId,
                recoveryTurnId,
                TestContext.Current.CancellationToken);

            var parameters = Assert.IsType<FollowupParams>(Assert.Single(
                await delivered.Task.WaitAsync(TestContext.Current.CancellationToken)));
            Assert.Equal(recoveryTurnId, parameters.TurnId);
            Assert.Equal(AgentExecutionSources.Slack, parameters.ExecutionSource);
            var grant = Assert.IsType<ManagerExecutionGrant>(parameters.ManagerExecutionGrant);
            var issuer = scope.ServiceProvider.GetRequiredService<ManagerExecutionCapabilityIssuer>();
            var validation = issuer.ValidatePresented(
                grant.ManagementCredential,
                ManagerExecutionLeaseKind.Management,
                "workspace.status",
                _fixture.TimeProvider.GetUtcNow());
            Assert.True(validation.Allowed, validation.Message);
            Assert.False(string.IsNullOrWhiteSpace(grant.ReplyCredential));
            Assert.Single(transportOwner.Invocations);
        }
        finally
        {
            await runner.UnregisterAsync();
        }
    }

}
