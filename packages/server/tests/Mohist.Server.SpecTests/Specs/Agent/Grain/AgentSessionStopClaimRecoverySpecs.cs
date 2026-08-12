using Mohist.Server.Infrastructure;
using Mohist.Server.Api;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Orleans.Core.Internal;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

[Collection("AgentJobGrain")]
public class AgentSessionStopClaimRecoverySpecs : AgentJobGrainTestSupport
{
    public AgentSessionStopClaimRecoverySpecs(AgentJobGrainFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task RecoveryReminder_RedeliversSameClaimUntilRunnerConfirmsStop()
    {
        _fixture.StopDelivery.Reset();
        _fixture.StopDelivery.Enqueue(null);
        _fixture.StopDelivery.Enqueue(new RunnerStopReply("stopped"));

        var sessionId = $"session-stop-recovery-{Guid.NewGuid():N}";
        var projectId = $"project-stop-recovery-{Guid.NewGuid():N}";
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: "runner-stop-recovery",
            AgentRuntime: "opencode",
            WorkDir: "/tmp/stop-recovery",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = "agent-test",
            })));
        await session.AttachPhysicalSessionAsync(
            new AttachPhysicalSessionCommand("runtime-stop-recovery", WorkDir: "/tmp/stop-recovery"));
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        const string turnId = "turn-stop-recovery";
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            "input-stop-recovery",
            turnId,
            "follow up",
            "generic-followup"));
        await session.MarkTurnExecutingAsync(turnId);
        var claim = await session.ClaimTurnStopAsync(turnId, "stop-recovery-operation");
        Assert.True(claim.CanDispatch);

        await session.RunStopRecoveryAsync();

        Assert.Equal(AgentTurnStatus.Executing, Assert.Single(await session.ListTurnsAsync()).Status);
        Assert.Equal("stop-recovery-operation", Assert.Single(_fixture.StopDelivery.Requests).OperationId);

        await session.RunStopRecoveryAsync();

        var requests = _fixture.StopDelivery.Requests;
        Assert.Equal(2, requests.Count);
        Assert.All(requests, request => Assert.Equal("stop-recovery-operation", request.OperationId));
        Assert.Equal(AgentTurnStatus.Cancelled, Assert.Single(await session.ListTurnsAsync()).Status);
    }

    [Fact]
    public async Task RecoveryReminder_RedeliversTheRecordedWorkflowTargetAndObservesItsFrozenBinding()
    {
        _fixture.StopDelivery.Reset();
        _fixture.StopDelivery.Enqueue(null);
        _fixture.StopDelivery.Enqueue(new RunnerStopReply("stopped"));
        _fixture.WorkPort.Reset();

        var sessionId = $"session-workflow-stop-{Guid.NewGuid():N}";
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: "runner-workflow-stop",
            AgentRuntime: "opencode",
            WorkDir: "/tmp/workflow-stop",
            Metadata: WorkflowAgentSessionMetadata.Metadata(
                new WorkflowAgentSessionContext("project-workflow-stop", "workflow-stop", "build"))));
        await session.AttachPhysicalSessionAsync(
            new AttachPhysicalSessionCommand("runtime-workflow-stop", WorkDir: "/tmp/workflow-stop"));

        var receipt = await session.AcceptWorkflowInputAsync(new AcceptWorkflowAgentSessionInputCommand(
            "delivery-workflow-stop",
            "stop this workflow turn",
            "workflow-stop",
            "task-stop.1",
            "work-stop",
            "runner-workflow-stop",
            "opencode",
            "runtime-workflow-stop",
            "{\"text\":\"stop this workflow turn\"}"));
        var claim = await session.ClaimTurnStopAsync(receipt.AgentTurnId, "stop-workflow-operation");
        Assert.True(claim.CanDispatch);

        await session.RunStopRecoveryAsync();
        await session.RunStopRecoveryAsync();

        Assert.Equal(2, _fixture.StopDelivery.Requests.Count);
        Assert.All(_fixture.StopDelivery.Requests, request =>
        {
            Assert.Equal("stop-workflow-operation", request.OperationId);
            Assert.Equal(receipt.AgentTurnId, request.TurnId);
            Assert.Equal("runner-workflow-stop", request.RunnerId);
            Assert.Equal("runtime-workflow-stop", request.RuntimeSessionId);
        });
        Assert.Collection(
            _fixture.WorkPort.Requests,
            observation =>
            {
                Assert.Equal(SessionWorkflowObservationKind.StopUnconfirmed, observation.Kind);
                Assert.Equal("stop-delivery-unavailable", observation.ReasonCode);
            },
            observation =>
            {
                Assert.Equal(SessionWorkflowObservationKind.Stopped, observation.Kind);
                Assert.Equal("task-stop.1", observation.Binding.TaskRunId);
                Assert.Equal("work-stop", observation.Binding.WorkId);
                Assert.Equal(receipt.AgentTurnId, observation.Binding.AgentTurnId);
                Assert.Equal("stop-workflow-operation", observation.StopOperationId);
            });
    }

    [Fact]
    public async Task RecoveryReminder_TargetAlreadyIdle_SettlesPhysicalStopWithoutChoosingTaskOutcome()
    {
        _fixture.StopDelivery.Reset();
        _fixture.StopDelivery.Enqueue(new RunnerStopReply("idle"));
        _fixture.WorkPort.Reset();

        var sessionId = $"session-workflow-idle-{Guid.NewGuid():N}";
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: "runner-workflow-idle",
            AgentRuntime: "opencode",
            WorkDir: "/tmp/workflow-idle",
            Metadata: WorkflowAgentSessionMetadata.Metadata(
                new WorkflowAgentSessionContext("project-workflow-idle", "workflow-idle", "build"))));
        await session.AttachPhysicalSessionAsync(
            new AttachPhysicalSessionCommand("runtime-workflow-idle", WorkDir: "/tmp/workflow-idle"));

        var receipt = await session.AcceptWorkflowInputAsync(new AcceptWorkflowAgentSessionInputCommand(
            "delivery-workflow-idle",
            "observe an idle target",
            "workflow-idle",
            "task-idle.1",
            "work-idle",
            "runner-workflow-idle",
            "opencode",
            "runtime-workflow-idle",
            "{\"text\":\"observe an idle target\"}"));
        Assert.True((await session.ClaimTurnStopAsync(receipt.AgentTurnId, "stop-idle-operation")).CanDispatch);

        await session.RunStopRecoveryAsync();

        Assert.Single(_fixture.StopDelivery.Requests);
        Assert.Equal(AgentSessionStopDisposition.Idle, (await session.GetStopClaimAsync())?.Disposition);
        Assert.Equal(AgentTurnStatus.Executing, Assert.Single(await session.ListTurnsAsync()).Status);
        var observation = Assert.Single(_fixture.WorkPort.Requests);
        Assert.Equal(SessionWorkflowObservationKind.Idle, observation.Kind);
        Assert.Equal("stop-target-idle", observation.ReasonCode);
        Assert.Equal(receipt.AgentTurnId, observation.Binding.AgentTurnId);
        Assert.Equal("stop-idle-operation", observation.StopOperationId);
    }

    [Fact]
    public async Task RecoveryReminder_DeadlineExhausted_SettlesBlockedWithoutAnotherDelivery()
    {
        _fixture.StopDelivery.Reset();
        var sessionId = $"session-stop-deadline-{Guid.NewGuid():N}";
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: "runner-stop-deadline",
            AgentRuntime: "opencode",
            WorkDir: "/tmp/stop-deadline",
            Metadata: GenericAgentSessionMetadata.Metadata(new GenericAgentSessionContext(
                $"project-stop-deadline-{Guid.NewGuid():N}", "agent-test", "agent-test"))));
        await session.AttachPhysicalSessionAsync(
            new AttachPhysicalSessionCommand("runtime-stop-deadline", WorkDir: "/tmp/stop-deadline"));
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        const string turnId = "turn-stop-deadline";
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            "input-stop-deadline", turnId, "follow up", "generic-followup"));
        await session.MarkTurnExecutingAsync(turnId);
        Assert.True((await session.ClaimTurnStopAsync(turnId, "stop-deadline-operation")).CanDispatch);

        await session.RunStopRecoveryAsync();
        Assert.Single(_fixture.StopDelivery.Requests);

        _fixture.TimeProvider.Advance(AgentSessionGrain.StopOperationDeadline);
        await session.RunStopRecoveryAsync();

        Assert.Single(_fixture.StopDelivery.Requests);
        var claim = await session.GetStopClaimAsync();
        Assert.Equal(AgentSessionStopDisposition.Blocked, claim?.Disposition);
        Assert.Equal("stop-recovery-deadline-exhausted", claim?.Reason);
        Assert.Equal(AgentTurnStatus.Unknown, Assert.Single(await session.ListTurnsAsync()).Status);
    }

    [Fact]
    public async Task RecoveryUnknown_PersistsJobDeliveryBeforeTheAsyncSessionLeg()
    {
        _fixture.StopDelivery.Reset();
        _fixture.StopDelivery.Enqueue(new RunnerStopReply("unknown"));

        var sessionId = $"session-stop-one-way-{Guid.NewGuid():N}";
        var jobId = $"job-stop-one-way-{Guid.NewGuid():N}";
        const string inputId = "input-stop-one-way";
        const string turnId = "turn-stop-one-way";
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: "runner-stop-one-way",
            AgentRuntime: "opencode",
            WorkDir: "/tmp/stop-one-way",
            Metadata: GenericAgentSessionMetadata.Metadata(new GenericAgentSessionContext(
                $"project-stop-one-way-{Guid.NewGuid():N}", "agent-test", "agent-test"))));
        await session.AttachPhysicalSessionAsync(
            new AttachPhysicalSessionCommand("runtime-stop-one-way", WorkDir: "/tmp/stop-one-way"));
        await session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            inputId, turnId, "stop launch", "agent-launch", jobId));
        await session.MarkInitialTurnExecutingAsync(jobId);

        var job = Grains.GetGrain<IAgentJobGrain>(jobId);
        await job.PrepareManualLaunchAsync(new PrepareManualLaunchCommand(
            sessionId, inputId, turnId, "stop launch", AgentId: "agent-test"));
        Assert.True((await session.ClaimTurnStopAsync(turnId, "stop-one-way-operation")).CanDispatch);

        await session.RunStopRecoveryAsync();

        Assert.Equal(AgentJobStatus.Unknown, await job.GetStatusAsync());
        Assert.Equal(AgentTurnStatus.Executing, Assert.Single(await session.ListTurnsAsync()).Status);
        Assert.Equal(AgentSessionStopDisposition.Unknown, (await session.GetStopClaimAsync())?.Disposition);

        await job.ReceiveReminder(AgentJobGrain.RecoveryReminderName, default);
        Assert.Equal(AgentTurnStatus.Unknown, Assert.Single(await session.ListTurnsAsync()).Status);

        await job.ReceiveReminder(AgentJobGrain.RecoveryReminderName, default);
        Assert.Equal(AgentTurnStatus.Unknown, Assert.Single(await session.ListTurnsAsync()).Status);
    }

    [Fact]
    public async Task TerminalFactAfterReactivationReleasesPersistedStopClaim()
    {
        var sessionId = $"session-522-stop-claim-{Guid.NewGuid():N}";
        var projectId = $"project-522-{Guid.NewGuid():N}";
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: "/tmp/turn-522",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = "agent-test",
            })));
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-1", WorkDir: "/tmp/turn-522"));
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        const string turnId = "turn-stop-claim";
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            "input-stop-claim",
            turnId,
            "follow up",
            "generic-followup"));
        await session.MarkTurnExecutingAsync(turnId);
        var claim = await session.ClaimTurnStopAsync(turnId);
        Assert.True(claim.CanDispatch);

        await session.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        await session.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                $"{{\"activity\":\"idle\",\"status\":\"completed\",\"turnId\":\"{turnId}\",\"stopOperationId\":\"{claim.OperationId}\"}}") },
            "runtime-1"));

        var reservation = await session.BeginFollowupAsync();
        Assert.True(reservation.StartsIdleTurn);
        await session.AbandonFollowupAsync(reservation.OperationId!);
    }

    [Fact]
    public async Task TerminalFactReleasesClaimThatWasNeverDispatched()
    {
        var sessionId = $"session-522-undispatched-stop-{Guid.NewGuid():N}";
        var projectId = $"project-522-{Guid.NewGuid():N}";
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: "/tmp/turn-522",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = "agent-test",
            })));
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-1", WorkDir: "/tmp/turn-522"));
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        const string turnId = "turn-undispatched-stop";
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            "input-undispatched-stop",
            turnId,
            "follow up",
            "generic-followup"));
        await session.MarkTurnExecutingAsync(turnId);
        Assert.True((await session.ClaimTurnStopAsync(turnId)).CanDispatch);

        await session.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                $"{{\"activity\":\"idle\",\"status\":\"completed\",\"turnId\":\"{turnId}\"}}") },
            "runtime-1"));

        var reservation = await session.BeginFollowupAsync();
        Assert.True(reservation.StartsIdleTurn);
        await session.AbandonFollowupAsync(reservation.OperationId!);
    }

    [Fact]
    public async Task UnconfirmedStopFactSettlesClaimWithoutAdmittingAnotherTurn()
    {
        var sessionId = $"session-522-stop-unknown-{Guid.NewGuid():N}";
        var projectId = $"project-522-{Guid.NewGuid():N}";
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: "/tmp/turn-522",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = "agent-test",
            })));
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-1", WorkDir: "/tmp/turn-522"));
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        const string turnId = "turn-stop-unknown";
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            "input-stop-unknown",
            turnId,
            "follow up",
            "generic-followup"));
        await session.MarkTurnExecutingAsync(turnId);
        var claim = await session.ClaimTurnStopAsync(turnId);
        Assert.True(claim.CanDispatch);

        await session.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                $"{{\"activity\":\"unknown\",\"status\":\"failed\",\"turnId\":\"{turnId}\",\"stopOperationId\":\"{claim.OperationId}\"}}") },
            "runtime-1"));

        await Assert.ThrowsAsync<SessionActivityUnknownException>(session.BeginFollowupAsync);

        await session.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                $"{{\"activity\":\"idle\",\"status\":\"completed\",\"turnId\":\"{turnId}\"}}") },
            "runtime-1"));

        var reservation = await session.BeginFollowupAsync();
        Assert.True(reservation.StartsIdleTurn);
        await session.AbandonFollowupAsync(reservation.OperationId!);
    }

    [Fact]
    public async Task RuntimeSettlement_DoesNotObserveAnUnboundGenericTurn()
    {
        _fixture.WorkPort.Reset();

        var sessionId = $"session-work-settlement-{Guid.NewGuid():N}";
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: "runner-work-settlement",
            AgentRuntime: "opencode",
            WorkDir: "/tmp/work-settlement",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = "project-work-settlement",
                [AgentSessionQueryMetadataKeys.SourceKind] = "workflow",
                [AgentSessionQueryMetadataKeys.WorkflowRunId] = "workflow-work-settlement",
                [AgentSessionQueryMetadataKeys.SessionName] = "session-work-settlement",
                [AgentSessionQueryMetadataKeys.WorkId] = "work-work-settlement",
                [GenericAgentSessionMetadata.AgentId] = "agent-test",
            })));
        await session.AttachPhysicalSessionAsync(
            new AttachPhysicalSessionCommand("runtime-work-settlement", WorkDir: "/tmp/work-settlement"));

        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            "input-work-settlement-1",
            "turn-work-settlement-1",
            "first follow up",
            "generic-followup"));
        await session.MarkTurnExecutingAsync("turn-work-settlement-1");
        await session.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                "{\"activity\":\"idle\",\"status\":\"failed\",\"turnId\":\"turn-work-settlement-1\"}") },
            "runtime-work-settlement"));

        Assert.Empty(_fixture.WorkPort.Requests);

        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            "input-work-settlement-2",
            "turn-work-settlement-2",
            "second follow up",
            "generic-followup"));
        await session.MarkTurnExecutingAsync("turn-work-settlement-2");
        await session.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                "{\"activity\":\"active\",\"status\":\"running\",\"turnId\":\"turn-work-settlement-2\"}") },
            "runtime-work-settlement"));

        Assert.Empty(_fixture.WorkPort.Requests);
    }
}
