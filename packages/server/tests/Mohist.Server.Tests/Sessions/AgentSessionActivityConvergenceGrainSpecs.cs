using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Sessions;

/// <summary>
/// Grain-level specs for the durable activity-probe capture and the fenced
/// settlement of Session Activity from Runner lifecycle evidence. The
/// production baseline shape — a completed initial Turn and Job plus a
/// terminal-unknown later Turn — is the regression this convergence exists
/// for.
/// </summary>
[Collection("AgentSessionGrainComponent")]
[Trait("level", "L0")]
public sealed class AgentSessionActivityConvergenceGrainSpecs : AgentSessionGrainPersistenceSpecsBase
{
    public AgentSessionActivityConvergenceGrainSpecs(AgentSessionGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task CompletedInitialJobWithTerminalUnknownTurn_BecomesSafeIdleUnderFencedEvidence()
    {
        var grain = await BoundGrainAsync();
        await CompleteInitialTurnAsync(grain, "initial-job", AgentTurnStatus.Completed);
        var later = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            "later work", "agent-session-followup", "later-key"));
        await grain.MarkFollowupTurnExecutingAsync(later.OperationId);
        await grain.MarkFollowupTurnTerminalAsync(later.OperationId, AgentTurnStatus.Unknown, null);
        Assert.Equal(AgentSessionActivity.Unknown, SavedActivity());

        var request = await grain.PrepareActivityProbeAsync("runner-1");
        Assert.NotNull(request);
        Assert.Equal("runtime-1", request!.RuntimeSessionId);
        Assert.Equal(1, request.ContextGeneration);

        var applied = await grain.ApplyActivityProbeAsync(
            new RunnerSessionActivityProbeResult(request, RunnerSessionActivityObservations.Idle));

        Assert.True(applied);
        Assert.Equal(AgentSessionActivity.Idle, SavedActivity());
        var turns = await grain.ListTurnsAsync();
        var initial = Assert.Single(turns, turn => turn.Id == "initial-turn");
        Assert.Equal(AgentTurnStatus.Completed, initial.Status);
        var settled = Assert.Single(turns, turn => turn.Id == later.TurnId);
        Assert.Equal(AgentTurnStatus.Unknown, settled.Status);
        Assert.NotNull(settled.SupersededAt);
        Assert.Null(SavedState().Status.PendingFollowup);
        Assert.Empty(SavedState().Status.PendingFollowups ?? []);
        Assert.Equal(1, SavedState().Status.UnresolvedPreviousCount);
        Assert.Null(SavedState().Status.MissingRunnerFact);
        Assert.DoesNotContain(Fixture.StateStore.Events, e => e.Value is AgentSessionActivityConverged);

        // The settled unknown no longer blocks new work.
        var accepted = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            "next work", "agent-session-followup", "next-key"));
        Assert.Equal(AgentTurnStatus.Queued, accepted.TurnStatus);
    }

    [Fact]
    public async Task SettledInitialUnknownTurn_EmitsTheJobSettlementFact()
    {
        var grain = await BoundGrainAsync();
        await CompleteInitialTurnAsync(grain, "initial-job", AgentTurnStatus.Unknown);
        Assert.Equal(AgentSessionActivity.Unknown, SavedActivity());

        var request = await grain.PrepareActivityProbeAsync("runner-1");
        var applied = await grain.ApplyActivityProbeAsync(
            new RunnerSessionActivityProbeResult(request!, RunnerSessionActivityObservations.UnknownToRunner));

        Assert.True(applied);
        Assert.Equal(AgentSessionActivity.Idle, SavedActivity());
        Assert.NotNull(SavedState().Status.MissingRunnerFact);
        Assert.Equal("runner-1", SavedState().Status.MissingRunnerFact!.RunnerId);
        var converged = Assert.Single(Fixture.StateStore.Events, e => e.Value is AgentSessionActivityConverged);
        var fact = Assert.IsType<AgentSessionActivityConverged>(converged.Value);
        Assert.Equal(["initial-job"], fact.SettledJobIds);
        Assert.Equal(["initial-turn"], fact.SettledTurnIds);
        Assert.Equal("unknown-to-runner", fact.Observation);
    }

    [Fact]
    public async Task ExecutingAnswer_RestoresActiveAndSettlesNothing()
    {
        var grain = await BoundGrainAsync();
        await CompleteInitialTurnAsync(grain, "initial-job", AgentTurnStatus.Completed);
        var later = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            "later work", "agent-session-followup", "executing-key"));
        await grain.MarkFollowupTurnExecutingAsync(later.OperationId);
        await grain.MarkFollowupTurnTerminalAsync(later.OperationId, AgentTurnStatus.Unknown, null);

        var request = await grain.PrepareActivityProbeAsync("runner-1");
        var applied = await grain.ApplyActivityProbeAsync(
            new RunnerSessionActivityProbeResult(request!, RunnerSessionActivityObservations.Executing));

        Assert.True(applied);
        Assert.Equal(AgentSessionActivity.Active, SavedActivity());
        var turns = await grain.ListTurnsAsync();
        Assert.Equal(AgentTurnStatus.Unknown,
            Assert.Single(turns, turn => turn.Id == later.TurnId).Status);
        Assert.Null(Assert.Single(turns, turn => turn.Id == later.TurnId).SupersededAt);
        Assert.Equal(0, SavedState().Status.UnresolvedPreviousCount);
    }

    [Fact]
    public async Task ExecutingAnswer_KeepsQueuedWorkFromDispatchUntilOwnerTerminates()
    {
        var grain = await BoundGrainAsync();
        await CompleteInitialTurnAsync(grain, "initial-job", AgentTurnStatus.Completed);
        var owner = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            "owner work", "agent-session-followup", "owner-key"));
        await grain.MarkFollowupTurnExecutingAsync(owner.OperationId);
        await grain.MarkFollowupTurnTerminalAsync(owner.OperationId, AgentTurnStatus.Unknown, null);
        var request = await grain.PrepareActivityProbeAsync("runner-1");
        Assert.True(await grain.ApplyActivityProbeAsync(
            new RunnerSessionActivityProbeResult(request!, RunnerSessionActivityObservations.Executing)));

        var queued = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            "queued behind owner", "agent-session-followup", "queued-behind-owner-key"));
        Assert.Null(await grain.BeginNextFollowupDispatchAsync());

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                $"{{\"activity\":\"idle\",\"status\":\"completed\",\"turnId\":\"{owner.TurnId}\"}}") },
            "runtime-1"));

        Assert.Equal(AgentTurnStatus.Completed,
            Assert.Single(await grain.ListTurnsAsync(), turn => turn.Id == owner.TurnId).Status);
        Assert.Equal(queued.TurnId, (await grain.BeginNextFollowupDispatchAsync())!.TurnId);
    }

    [Fact]
    public async Task ActiveEventWithUnknownExplicitIdentity_CannotBorrowCurrentOwner()
    {
        var grain = await BoundGrainAsync();
        await CompleteInitialTurnAsync(grain, "initial-job", AgentTurnStatus.Completed);
        var owner = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            "owner work", "agent-session-followup", "explicit-owner-key"));
        await grain.MarkFollowupTurnExecutingAsync(owner.OperationId);
        await grain.MarkFollowupTurnTerminalAsync(owner.OperationId, AgentTurnStatus.Unknown, null);
        Assert.Equal(AgentSessionActivity.Unknown, SavedActivity());

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[]
            {
                new AgentSessionRuntimeEventInput(
                    RuntimeEventTypes.SessionActivity,
                    "{\"activity\":\"active\",\"turnId\":\"missing-old-turn\"}"),
                new AgentSessionRuntimeEventInput(
                    RuntimeEventTypes.SessionActivity,
                    "{\"activity\":\"active\",\"operationId\":\"missing-old-operation\"}"),
            },
            "runtime-1"));

        Assert.Equal(AgentSessionActivity.Unknown, SavedActivity());
        Assert.Equal(AgentTurnStatus.Unknown,
            Assert.Single(await grain.ListTurnsAsync(), turn => turn.Id == owner.TurnId).Status);
    }

    [Fact]
    public async Task AcceptedButUndispatchedQueuedTurn_IsCancelledAndItsLeaseRemoved()
    {
        var grain = await BoundGrainAsync();
        await CompleteInitialTurnAsync(grain, "initial-job", AgentTurnStatus.Completed);
        var queued = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            "queued work", "agent-session-followup", "queued-key"));
        var persisted = grain.PersistenceCheckpoint(Fixture.Persistence);
        await grain.AppendSystemEventsAsync(new AppendAgentSessionSystemEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(
                Type: RuntimeEventTypes.SessionActivity,
                PayloadJson: "{\"activity\":\"unknown\"}"),
        }));
        await persisted.WaitAsync();
        Assert.Equal(AgentSessionActivity.Unknown, SavedActivity());
        Assert.Single(SavedState().Status.PendingFollowups!);

        var request = await grain.PrepareActivityProbeAsync("runner-1");
        var applied = await grain.ApplyActivityProbeAsync(
            new RunnerSessionActivityProbeResult(request!, RunnerSessionActivityObservations.Idle));

        Assert.True(applied);
        Assert.Equal(AgentSessionActivity.Idle, SavedActivity());
        var turns = await grain.ListTurnsAsync();
        var turn = Assert.Single(turns, candidate => candidate.Id == queued.TurnId);
        // Accepted/StartedAt are logical acceptance facts. The unsealed,
        // non-dispatching lease proves the Runtime effect was never attempted.
        Assert.Equal(AgentTurnStatus.Cancelled, turn.Status);
        Assert.NotNull(turn.SupersededAt);
        Assert.Equal(queued.OperationId, turn.OperationId);
        Assert.Empty(SavedState().Status.PendingFollowups ?? []);
    }

    [Fact]
    public async Task DispatchingQueuedTurn_SettlesUnknownRatherThanCancelled()
    {
        var grain = await BoundGrainAsync();
        await CompleteInitialTurnAsync(grain, "initial-job", AgentTurnStatus.Completed);
        var queued = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            "queued work", "agent-session-followup", "dispatching-key"));
        var dispatch = await grain.BeginNextFollowupDispatchAsync();
        Assert.Equal(queued.TurnId, dispatch!.TurnId);
        var persisted = grain.PersistenceCheckpoint(Fixture.Persistence);
        await grain.AppendSystemEventsAsync(new AppendAgentSessionSystemEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(
                Type: RuntimeEventTypes.SessionActivity,
                PayloadJson: "{\"activity\":\"unknown\"}"),
        }));
        await persisted.WaitAsync();

        var request = await grain.PrepareActivityProbeAsync("runner-1");
        Assert.True(await grain.ApplyActivityProbeAsync(
            new RunnerSessionActivityProbeResult(request!, RunnerSessionActivityObservations.Idle)));

        var turn = Assert.Single(await grain.ListTurnsAsync(), candidate => candidate.Id == queued.TurnId);
        Assert.Equal(AgentTurnStatus.Unknown, turn.Status);
        Assert.NotNull(turn.SupersededAt);
    }

    [Fact]
    public async Task RemovalCapture_AppliesUnknownToRunnerToAnActiveSession()
    {
        var grain = await BoundGrainAsync();
        await grain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            "removal-input", "removal-turn", "prompt", "agent-connection", "removal-job",
            Metadata: Open().Metadata));
        await grain.MarkInitialTurnExecutingAsync("removal-job");
        Assert.Equal(AgentSessionActivity.Active, SavedActivity());
        Assert.Null(await grain.PrepareActivityProbeAsync("runner-1"));

        var request = await grain.PrepareActivityProbeAsync("runner-1", runnerRemoved: true);
        Assert.NotNull(request);
        var applied = await grain.ApplyActivityProbeAsync(
            new RunnerSessionActivityProbeResult(request!, RunnerSessionActivityObservations.UnknownToRunner));

        Assert.True(applied);
        Assert.Equal(AgentSessionActivity.Idle, SavedActivity());
        var turn = Assert.Single(await grain.ListTurnsAsync());
        Assert.Equal(AgentTurnStatus.Unknown, turn.Status);
        Assert.NotNull(SavedState().Status.MissingRunnerFact);
    }

    [Fact]
    public async Task Prepare_ReturnsNullForAForeignRunner()
    {
        var grain = await BoundGrainAsync();
        await CompleteInitialTurnAsync(grain, "initial-job", AgentTurnStatus.Completed);
        var later = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            "later work", "agent-session-followup", "foreign-key"));
        await grain.MarkFollowupTurnExecutingAsync(later.OperationId);
        await grain.MarkFollowupTurnTerminalAsync(later.OperationId, AgentTurnStatus.Unknown, null);

        Assert.Null(await grain.PrepareActivityProbeAsync("runner-other"));
        Assert.NotNull(await grain.PrepareActivityProbeAsync("runner-1"));
    }

    [Fact]
    public async Task RepeatedAndStaleAnswers_ChangeNothing()
    {
        var grain = await BoundGrainAsync();
        await CompleteInitialTurnAsync(grain, "initial-job", AgentTurnStatus.Completed);
        var later = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            "later work", "agent-session-followup", "replay-key"));
        await grain.MarkFollowupTurnExecutingAsync(later.OperationId);
        await grain.MarkFollowupTurnTerminalAsync(later.OperationId, AgentTurnStatus.Unknown, null);
        var request = await grain.PrepareActivityProbeAsync("runner-1");
        Assert.True(await grain.ApplyActivityProbeAsync(
            new RunnerSessionActivityProbeResult(request!, RunnerSessionActivityObservations.Idle)));

        var afterFirst = await grain.GetAsync();
        Assert.False(await grain.ApplyActivityProbeAsync(
            new RunnerSessionActivityProbeResult(request!, RunnerSessionActivityObservations.Idle)));
        Assert.False(await grain.ApplyActivityProbeAsync(
            new RunnerSessionActivityProbeResult(
                request! with { BindingEpoch = request!.BindingEpoch + 1 },
                RunnerSessionActivityObservations.Idle)));
        Assert.False(await grain.ApplyActivityProbeAsync(
            new RunnerSessionActivityProbeResult(request!, "absent")));
        Assert.Equal(afterFirst, await grain.GetAsync());
        Assert.Equal(AgentTurnStatus.Unknown,
            Assert.Single(await grain.ListTurnsAsync(), turn => turn.Id == later.TurnId).Status);
        Assert.NotNull(Assert.Single(await grain.ListTurnsAsync(), turn => turn.Id == later.TurnId).SupersededAt);
        Assert.Equal(1, SavedState().Status.UnresolvedPreviousCount);
    }

    [Fact]
    public async Task CompetingOperationAfterCapture_RejectsTheAnswer()
    {
        var grain = await BoundGrainAsync();
        await grain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            "competing-input", "competing-turn", "prompt", "agent-connection", "competing-job",
            Metadata: Open().Metadata));
        await grain.MarkInitialTurnExecutingAsync("competing-job");
        var request = await grain.PrepareActivityProbeAsync("runner-1", runnerRemoved: true);
        Assert.NotNull(request);

        // A stop operation is accepted while the removal answer is in flight.
        var claim = await grain.ClaimTurnStopAsync("competing-turn");
        Assert.True(claim.CanDispatch);
        var applied = await grain.ApplyActivityProbeAsync(
            new RunnerSessionActivityProbeResult(request!, RunnerSessionActivityObservations.UnknownToRunner));

        Assert.False(applied);
        Assert.Equal(AgentSessionActivity.Active, SavedActivity());
        Assert.Null(Assert.Single(await grain.ListTurnsAsync(), turn => turn.Id == "competing-turn").SupersededAt);
    }

    [Fact]
    public async Task FollowupCrossingDispatchBoundaryAfterCapture_RejectsOldEvidence()
    {
        var grain = await BoundGrainAsync();
        await CompleteInitialTurnAsync(grain, "initial-job", AgentTurnStatus.Completed);
        var queued = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            "queued work", "agent-session-followup", "phase-followup-key"));
        await grain.AppendSystemEventsAsync(new AppendAgentSessionSystemEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                "{\"activity\":\"unknown\"}") }));
        var request = await grain.PrepareActivityProbeAsync("runner-1");

        Assert.Equal(queued.TurnId, (await grain.BeginNextFollowupDispatchAsync())!.TurnId);
        Assert.False(await grain.ApplyActivityProbeAsync(
            new RunnerSessionActivityProbeResult(request!, RunnerSessionActivityObservations.Idle)));
        Assert.Null(Assert.Single(await grain.ListTurnsAsync(), turn => turn.Id == queued.TurnId).SupersededAt);
    }

    [Fact]
    public async Task ResetCrossingEffectBoundaryAfterCapture_RejectsOldEvidence()
    {
        var grain = await BoundGrainAsync();
        var reset = await grain.BeginResetAsync("process-1", "phase-reset-key");
        await grain.AppendSystemEventsAsync(new AppendAgentSessionSystemEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                "{\"activity\":\"unknown\"}") }));
        var request = await grain.PrepareActivityProbeAsync("runner-1");

        Assert.Equal(SessionCommandAdmissionOutcome.AdmittedNow,
            await grain.AdmitSessionCommandEffectAsync(reset.OperationId!, "process-1"));
        Assert.False(await grain.ApplyActivityProbeAsync(
            new RunnerSessionActivityProbeResult(request!, RunnerSessionActivityObservations.Idle)));
        Assert.True(SavedState().Status.PendingReset!.EffectAdmitted);
        Assert.Null(SavedState().Status.PendingReset!.SupersededAt);
    }

    [Fact]
    public async Task StopCrossingDispatchBoundaryAfterCapture_RejectsOldEvidence()
    {
        var grain = await BoundGrainAsync();
        await grain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            "phase-stop-input", "phase-stop-turn", "prompt", "agent-connection", "phase-stop-job",
            Metadata: Open().Metadata));
        await grain.MarkInitialTurnExecutingAsync("phase-stop-job");
        var claim = await grain.ClaimTurnStopAsync("phase-stop-turn", "phase-stop-operation");
        var request = await grain.PrepareActivityProbeAsync("runner-1", runnerRemoved: true);

        await grain.MarkTurnStopDispatchedAsync("phase-stop-turn", claim.OperationId!);
        Assert.False(await grain.ApplyActivityProbeAsync(
            new RunnerSessionActivityProbeResult(request!, RunnerSessionActivityObservations.UnknownToRunner)));
        Assert.True(SavedState().Status.PendingStop!.DispatchStarted);
        Assert.Null(SavedState().Status.PendingStop!.SupersededAt);
    }

    [Fact]
    public async Task SupersededReset_ReplaysItsExactKeyAndDoesNotBlockANewOperation()
    {
        var grain = await BoundGrainAsync();
        var reset = await grain.BeginResetAsync("process-1", "reset-key");
        Assert.Equal(SessionCommandAdmissionOutcome.AdmittedNow,
            await grain.AdmitSessionCommandEffectAsync(reset.OperationId!, "process-1"));
        var persisted = grain.PersistenceCheckpoint(Fixture.Persistence);
        await grain.AppendSystemEventsAsync(new AppendAgentSessionSystemEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(
                Type: RuntimeEventTypes.SessionActivity,
                PayloadJson: "{\"activity\":\"unknown\"}"),
        }));
        await persisted.WaitAsync();

        var request = await grain.PrepareActivityProbeAsync("runner-1");
        Assert.True(await grain.ApplyActivityProbeAsync(
            new RunnerSessionActivityProbeResult(request!, RunnerSessionActivityObservations.Idle)));

        var completed = await grain.GetCompletedRecoveryAsync(SessionCommandKind.Reset, "reset-key");
        Assert.Equal("superseded", completed!.Status);
        var replay = await grain.BeginResetAsync("process-1", "reset-key");
        Assert.Equal(reset.OperationId, replay.OperationId);
        var next = await grain.BeginResetAsync("process-1", "next-reset-key");
        Assert.NotEqual(reset.OperationId, next.OperationId);
        Assert.Equal("superseded", (await grain.GetCompletedRecoveryAsync(
            SessionCommandKind.Reset, "reset-key"))!.Status);
    }

    [Fact]
    public async Task ProbeCaptureQuarantinesOnPersistenceFailureAndRetriesAfterReload()
    {
        var grain = await BoundGrainAsync();
        await CompleteInitialTurnAsync(grain, "initial-job", AgentTurnStatus.Unknown);
        Fixture.StateStore.FailNextSave(
            grain.GetPrimaryKeyString(), new InvalidOperationException("state unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.PrepareActivityProbeAsync("runner-1"));
        Assert.Null(SavedState().Status.PendingActivityObservation);

        var request = await grain.PrepareActivityProbeAsync("runner-1");

        Assert.NotNull(request);
        Assert.Equal(request!.ObservationId, SavedState().Status.PendingActivityObservation!.ObservationId);
    }

    [Fact]
    public async Task SettlementQuarantinesOnPersistenceFailureAndRetriesAfterReload()
    {
        var grain = await BoundGrainAsync();
        await CompleteInitialTurnAsync(grain, "initial-job", AgentTurnStatus.Completed);
        var later = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            "later work", "agent-session-followup", "failure-key"));
        await grain.MarkFollowupTurnExecutingAsync(later.OperationId);
        await grain.MarkFollowupTurnTerminalAsync(later.OperationId, AgentTurnStatus.Unknown, null);
        var request = await grain.PrepareActivityProbeAsync("runner-1");

        Fixture.StateStore.FailNextSave(
            grain.GetPrimaryKeyString(), new InvalidOperationException("state unavailable"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.ApplyActivityProbeAsync(
            new RunnerSessionActivityProbeResult(request!, RunnerSessionActivityObservations.Idle)));

        await DeactivateAsync(grain);
        var retried = await grain.ApplyActivityProbeAsync(
            new RunnerSessionActivityProbeResult(request!, RunnerSessionActivityObservations.Idle));

        Assert.True(retried);
        Assert.Equal(AgentSessionActivity.Idle, SavedActivity());
        Assert.NotNull(Assert.Single(await grain.ListTurnsAsync(), turn => turn.Id == later.TurnId).SupersededAt);
    }

    [Fact]
    public async Task LateStopCompletion_CannotRewriteASupersededOperation()
    {
        var grain = await BoundGrainAsync();
        await grain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            "stop-input", "stop-turn", "prompt", "agent-connection", "stop-job",
            Metadata: Open().Metadata));
        await grain.MarkInitialTurnExecutingAsync("stop-job");
        var claim = await grain.ClaimTurnStopAsync("stop-turn", "stop-operation");
        Assert.True(claim.CanDispatch);
        await grain.MarkTurnStopDispatchedAsync("stop-turn", "stop-operation");
        var request = await grain.PrepareActivityProbeAsync("runner-1", runnerRemoved: true);
        Assert.True(await grain.ApplyActivityProbeAsync(
            new RunnerSessionActivityProbeResult(request!, RunnerSessionActivityObservations.UnknownToRunner)));
        var before = SavedState().Status.PendingStop;

        await grain.CompleteTurnStopAsync("stop-turn", "stop-operation");

        Assert.Equal(before, SavedState().Status.PendingStop);
        Assert.Equal(AgentSessionStopDisposition.Unknown, before!.Disposition);
        Assert.Equal(AgentSessionActivity.Idle, SavedActivity());
        Assert.Equal(AgentTurnStatus.Unknown,
            Assert.Single(await grain.ListTurnsAsync(), turn => turn.Id == "stop-turn").Status);
    }

    [Fact]
    public async Task SupersededStop_ReplaysAfterNewerStopWithoutRedispatch()
    {
        var grain = await BoundGrainAsync();
        await grain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            "old-stop-input", "old-stop-turn", "prompt", "agent-connection", "old-stop-job",
            Metadata: Open().Metadata));
        await grain.MarkInitialTurnExecutingAsync("old-stop-job");
        await grain.ClaimTurnStopAsync("old-stop-turn", "old-stop-operation");
        var request = await grain.PrepareActivityProbeAsync("runner-1", runnerRemoved: true);
        Assert.True(await grain.ApplyActivityProbeAsync(
            new RunnerSessionActivityProbeResult(request!, RunnerSessionActivityObservations.UnknownToRunner)));

        var newer = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            "new stop target", "agent-session-followup", "new-stop-target-key"));
        var dispatch = await grain.BeginNextFollowupDispatchAsync();
        await grain.MarkFollowupTurnExecutingAsync(dispatch!.OperationId);
        var newerClaim = await grain.ClaimTurnStopAsync(newer.TurnId, "new-stop-operation");
        Assert.True(newerClaim.CanDispatch);

        var archived = await grain.GetStopClaimAsync("old-stop-turn", "old-stop-operation");
        var replay = await grain.ClaimTurnStopAsync("old-stop-turn", "old-stop-operation");
        Assert.Equal(AgentSessionStopDisposition.Unknown, archived!.Disposition);
        Assert.Equal("old-stop-operation", replay.OperationId);
        Assert.Equal(AgentSessionStopDisposition.Unknown, replay.Disposition);
        Assert.False(replay.CanDispatch);
        Assert.Null(await grain.GetStopClaimAsync("old-stop-turn", "new-stop-operation"));

        await grain.MarkTurnStopDispatchedAsync("old-stop-turn", "old-stop-operation");
        await grain.ApplyStopDeliveryAsync(
            "old-stop-turn", "old-stop-operation", AgentSessionStopDisposition.Stopped);
        await grain.CompleteTurnStopAsync("old-stop-turn", "old-stop-operation");

        Assert.Equal(newerClaim.OperationId, (await grain.GetStopClaimAsync())!.OperationId);
        Assert.False((await grain.GetStopClaimAsync("old-stop-turn", "old-stop-operation"))!.DispatchStarted);
    }

    [Fact]
    public async Task LateRuntimeEvent_CannotReviveASupersededTurn()
    {
        var grain = await BoundGrainAsync();
        await CompleteInitialTurnAsync(grain, "initial-job", AgentTurnStatus.Completed);
        var later = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            "later work", "agent-session-followup", "late-event-key"));
        await grain.MarkFollowupTurnExecutingAsync(later.OperationId);
        await grain.MarkFollowupTurnTerminalAsync(later.OperationId, AgentTurnStatus.Unknown, null);
        var request = await grain.PrepareActivityProbeAsync("runner-1");
        await grain.ApplyActivityProbeAsync(
            new RunnerSessionActivityProbeResult(request!, RunnerSessionActivityObservations.Idle));
        var newer = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            "newer work", "agent-session-followup", "newer-after-settlement-key"));
        var activityBefore = SavedActivity();

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                Type: RuntimeEventTypes.SessionActivity,
                PayloadJson: $"{{\"activity\":\"active\",\"turnId\":\"{later.TurnId}\"}}") },
            "runtime-1"));
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                Type: RuntimeEventTypes.SessionActivity,
                PayloadJson: "{\"activity\":\"active\"}") },
            "runtime-1"));

        var turns = await grain.ListTurnsAsync();
        Assert.Equal(AgentTurnStatus.Unknown,
            Assert.Single(turns, candidate => candidate.Id == later.TurnId).Status);
        Assert.Equal(AgentTurnStatus.Queued,
            Assert.Single(turns, candidate => candidate.Id == newer.TurnId).Status);
        Assert.Equal(activityBefore, SavedActivity());
    }

    private async Task<IAgentSessionGrain> BoundGrainAsync()
    {
        var grain = NewGrain();
        // A successful follow-up needs real capacity evidence for the
        // Session's accepted identity; the derived store admits the queued
        // head against this seeded definition.
        await Fixture.SeedAgentAsync("project-1", "agent-1", maxConcurrentRuns: null);
        await grain.OpenAsync(Open("opencode"));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-1"));
        return grain;
    }

    private async Task CompleteInitialTurnAsync(
        IAgentSessionGrain grain,
        string jobId,
        AgentTurnStatus status)
    {
        await grain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            "initial-input", "initial-turn", "initial prompt", "agent-connection", jobId,
            Metadata: Open().Metadata));
        await grain.MarkInitialTurnExecutingAsync(jobId);
        await grain.MarkInitialTurnTerminalAsync(jobId, status, null);
    }

    private AgentSession SavedState() => Fixture.StateStore.State!;

    private AgentSessionActivity SavedActivity() => SavedState().Status.Activity;
}
