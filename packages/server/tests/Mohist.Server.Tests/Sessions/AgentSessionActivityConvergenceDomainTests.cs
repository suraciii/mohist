using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.Tests.Sessions;

[Trait("level", "L0")]
public sealed class AgentSessionActivityConvergenceDomainTests
{
    private static readonly DateTime Now = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);
    private int _observations;

    [Fact]
    public void CompletedInitialTurnWithTerminalUnknownFollowup_BecomesSafeIdleUnderFencedEvidence()
    {
        var session = CompletedInitialTurnWithUnknownFollowup();

        var request = Capture(session);
        var applied = session.SettleActivityFromEvidence(Answer(request, RunnerSessionActivityObservations.Idle), Now.AddMinutes(1));

        Assert.True(applied.Applied);
        Assert.Equal(AgentSessionActivity.Idle, session.Status.Activity);
        Assert.Equal(AgentTurnStatus.Completed, SingleTurn(session, "turn-1").Status);
        var settled = SingleTurn(session, "turn-2");
        Assert.Equal(AgentTurnStatus.Unknown, settled.Status);
        Assert.NotNull(settled.SupersededAt);
        Assert.Equal("op-followup", settled.OperationId);
        Assert.Null(session.Status.PendingActivityObservation);
        Assert.Equal(1, session.Status.UnresolvedPreviousCount);
        Assert.Equal(AgentSessionStatusSnapshot.NextActionInspectPreviousExecution, session.Status.NextAction);
        Assert.Empty(session.Status.PendingFollowups ?? []);
        // The settled facts keep the session usable: an accepted follow-up no
        // longer collides with an unknown turn.
        var accepted = session.AcceptFollowup(
            "input-3", "turn-3", "op-3", "again", "agent-session-followup", "key-3", Now.AddMinutes(2));
        Assert.Equal("turn-3", accepted.TurnId);
    }

    [Fact]
    public void SettledInitialUnknownTurn_EmitsOneJobSettlementFact()
    {
        var session = BoundSession();
        session.EnsureInitialLaunch("input-1", "turn-1", "prompt", "agent-connection", "job-1", Now);
        session.MarkInitialTurnExecuting("job-1", Now);
        session.MarkInitialTurnTerminal("job-1", AgentTurnStatus.Unknown, null, Now);
        Assert.Equal(AgentSessionActivity.Unknown, session.Status.Activity);

        var request = Capture(session);
        var applied = session.SettleActivityFromEvidence(
            Answer(request, RunnerSessionActivityObservations.UnknownToRunner), Now.AddMinutes(1));

        Assert.True(applied.Applied);
        Assert.Equal(["turn-1"], applied.SettledTurnIds);
        var converged = Assert.Single(applied.Events, e => e.Value is AgentSessionActivityConverged);
        var fact = Assert.IsType<AgentSessionActivityConverged>(converged.Value);
        Assert.Equal(["job-1"], fact.SettledJobIds);
        Assert.Equal("unknown-to-runner", fact.Observation);
        Assert.NotNull(session.Status.MissingRunnerFact);
        Assert.Equal("runner-1", session.Status.MissingRunnerFact!.RunnerId);
    }

    [Fact]
    public void CompletedOldInitialTurn_EmitsNoJobSettlementFact()
    {
        var session = CompletedInitialTurnWithUnknownFollowup();

        var request = Capture(session);
        var applied = session.SettleActivityFromEvidence(Answer(request, RunnerSessionActivityObservations.Idle), Now.AddMinutes(1));

        Assert.True(applied.Applied);
        Assert.DoesNotContain(applied.Events, e => e.Value is AgentSessionActivityConverged);
    }

    [Fact]
    public void ExecutingEvidence_RestoresActiveWithoutSettlingTurnsOrLeases()
    {
        var session = CompletedInitialTurnWithUnknownFollowup();

        var request = Capture(session);
        var applied = session.SettleActivityFromEvidence(
            Answer(request, RunnerSessionActivityObservations.Executing), Now.AddMinutes(1));

        Assert.True(applied.Applied);
        Assert.True(applied.RestoredActive);
        Assert.Equal(AgentSessionActivity.Active, session.Status.Activity);
        Assert.Equal(AgentTurnStatus.Unknown, SingleTurn(session, "turn-2").Status);
        Assert.Null(SingleTurn(session, "turn-2").SupersededAt);
        Assert.Equal(0, session.Status.UnresolvedPreviousCount);
        Assert.Null(session.Status.PendingActivityObservation);
    }

    [Fact]
    public void IdleEvidence_SupersedesInFlightTurnAndCancelsQueuedTurnWithLeases()
    {
        var session = BoundSession();
        session.EnsureInitialLaunch("input-1", "turn-1", "prompt", "agent-connection", "job-1", Now);
        session.MarkInitialTurnTerminal("job-1", AgentTurnStatus.Completed, null, Now);
        var running = session.AcceptFollowup("input-2", "turn-2", "op-2", "run", "agent-session-followup", "key-2", Now);
        var queued = session.AcceptFollowup("input-3", "turn-3", "op-3", "wait", "agent-session-followup", "key-3", Now, forceNewTurn: true);
        session.MarkFollowupTurnExecuting(running.OperationId, Now);
        session.SetActivity(AgentSessionActivity.Unknown, Now);
        Assert.Equal(AgentTurnStatus.Executing, SingleTurn(session, "turn-2").Status);
        Assert.Equal(AgentTurnStatus.Queued, SingleTurn(session, "turn-3").Status);

        var request = Capture(session);
        var applied = session.SettleActivityFromEvidence(Answer(request, RunnerSessionActivityObservations.Idle), Now.AddMinutes(1));

        Assert.True(applied.Applied);
        Assert.Equal(AgentSessionActivity.Idle, session.Status.Activity);
        Assert.Equal(AgentTurnStatus.Unknown, SingleTurn(session, "turn-2").Status);
        // An accepted queued Turn may already have crossed the dispatch
        // boundary, so it settles as terminal unknown rather than cancelled.
        Assert.Equal(AgentTurnStatus.Cancelled, SingleTurn(session, "turn-3").Status);
        Assert.Equal("op-2", SingleTurn(session, "turn-2").OperationId);
        Assert.Equal("op-3", SingleTurn(session, "turn-3").OperationId);
        Assert.Equal(["turn-2"], applied.SettledTurnIds);
        Assert.Equal(["turn-3"], applied.CancelledTurnIds);
        Assert.Empty(session.Status.PendingFollowups ?? []);
        Assert.Equal(2, session.Status.UnresolvedPreviousCount);
    }

    [Fact]
    public void ProvenUndispatchedQueue_IsCancelledAndRemoved()
    {
        var session = BoundSession();
        session.EnsureInitialLaunch("input-1", "turn-1", "prompt", "agent-connection", "job-1", Now);
        session.MarkInitialTurnTerminal("job-1", AgentTurnStatus.Completed, null, Now);
        var accepted = session.AcceptFollowup("input-2", "turn-2", "op-2", "run", "agent-session-followup", "key-2", Now);
        session.MarkFollowupTurnExecuting(accepted.OperationId, Now);
        // Logical acceptance stamps Accepted/StartedAt, but does not cross the
        // Runtime effect boundary. An unsealed, non-dispatching lease proves
        // this queued Turn can still be cancelled.
        session.AcceptFollowup("input-3", "turn-3", "op-3", "reserved", "agent-session-followup", "key-3", Now, forceNewTurn: true);
        session.Status = session.Status with
        {
            Turns = (session.Status.Turns ?? []).Append(new AgentTurnRecord(
                "turn-4", 4, ["input-4"], AgentTurnStatus.Queued, JobId: null, RecordedAt: Now, UpdatedAt: Now)).ToArray(),
        };
        session.SetActivity(AgentSessionActivity.Unknown, Now);

        var request = Capture(session);
        var applied = session.SettleActivityFromEvidence(Answer(request, RunnerSessionActivityObservations.Idle), Now.AddMinutes(1));

        Assert.True(applied.Applied);
        Assert.Equal(AgentTurnStatus.Unknown, SingleTurn(session, "turn-2").Status);
        Assert.Equal(AgentTurnStatus.Cancelled, SingleTurn(session, "turn-3").Status);
        Assert.Equal(AgentTurnStatus.Unknown, SingleTurn(session, "turn-4").Status);
        Assert.Equal(["turn-3"], applied.CancelledTurnIds);
        Assert.Equal(["turn-2", "turn-4"], applied.SettledTurnIds);
        Assert.Empty(session.Status.PendingFollowups ?? []);
        Assert.Equal(AgentSessionActivity.Idle, session.Status.Activity);
    }

    [Theory]
    [InlineData("runtimeSessionId")]
    [InlineData("runtime")]
    [InlineData("runnerId")]
    [InlineData("workDir")]
    [InlineData("bindingEpoch")]
    public void AnswerForAnotherTarget_IsDiscarded(string mutatedField)
    {
        var session = CompletedInitialTurnWithUnknownFollowup();
        var request = Capture(session);
        var mismatched = mutatedField switch
        {
            "runtimeSessionId" => request with { RuntimeSessionId = "runtime-other" },
            "runtime" => request with { Runtime = "pi" },
            "runnerId" => request with { RunnerId = "runner-other" },
            "workDir" => request with { WorkDir = "/elsewhere" },
            _ => request with { BindingEpoch = request.BindingEpoch + 1 },
        };

        Assert.False(session.SettleActivityFromEvidence(
            Answer(mismatched, RunnerSessionActivityObservations.Idle), Now.AddMinutes(1)).Applied);
        Assert.Equal(AgentSessionActivity.Unknown, session.Status.Activity);
        Assert.Null(SingleTurn(session, "turn-2").SupersededAt);
    }

    [Fact]
    public void AnswerAfterTheBindingWasReplaced_IsDiscarded()
    {
        var session = BoundSession();
        session.SetActivity(AgentSessionActivity.Unknown, Now);
        var request = Capture(session);
        session.SetActivity(AgentSessionActivity.Idle, Now.AddMinutes(1));

        session.RebindRuntimeSession(
            new AgentRuntimeBinding("runner-1", "opencode", "runtime-1"),
            new AgentRuntimeBinding("runner-1", "opencode", "runtime-replacement"),
            "missing-recovery",
            Now.AddMinutes(1),
            session.BindingEpoch);

        Assert.False(session.SettleActivityFromEvidence(
            Answer(request, RunnerSessionActivityObservations.Idle), Now.AddMinutes(2)).Applied);
        Assert.Equal("runtime-replacement", session.Status.AgentRuntimeSessionId);
        Assert.Equal(2, session.Status.ContextGeneration);
    }

    [Fact]
    public void CurrentGenerationFence_DiscardsTheAnswer()
    {
        var session = CompletedInitialTurnWithUnknownFollowup();
        var request = Capture(session);

        var answer = Answer(request with { ContextGeneration = request.ContextGeneration + 1 }, RunnerSessionActivityObservations.Idle);

        Assert.False(session.SettleActivityFromEvidence(answer, Now.AddMinutes(1)).Applied);
        Assert.Equal(AgentSessionActivity.Unknown, session.Status.Activity);
    }

    [Fact]
    public void SupersededObservationId_CannotMutate()
    {
        var session = CompletedInitialTurnWithUnknownFollowup();
        var first = Capture(session);
        // A competing operation supersedes the capture: the answer for the
        // older snapshot must not reach the newer facts.
        session.Status = session.Status with
        {
            PendingReset = new AgentSessionResetReservation("reset-1", "runtime-1", "opencode", Now.AddMinutes(1)),
        };
        var second = Capture(session);
        Assert.NotEqual(first.ObservationId, second.ObservationId);

        Assert.False(session.SettleActivityFromEvidence(Answer(first, RunnerSessionActivityObservations.Idle), Now.AddMinutes(2)).Applied);
        Assert.True(session.SettleActivityFromEvidence(Answer(second, RunnerSessionActivityObservations.Idle), Now.AddMinutes(3)).Applied);
    }

    [Fact]
    public void RepeatedAnswer_SettlesNothingNew()
    {
        var session = CompletedInitialTurnWithUnknownFollowup();
        var request = Capture(session);

        Assert.True(session.SettleActivityFromEvidence(Answer(request, RunnerSessionActivityObservations.Idle), Now.AddMinutes(1)).Applied);
        var eventCount = session.Status.UnresolvedPreviousCount;
        var replay = session.SettleActivityFromEvidence(Answer(request, RunnerSessionActivityObservations.Idle), Now.AddMinutes(2));

        Assert.False(replay.Applied);
        Assert.Equal(eventCount, session.Status.UnresolvedPreviousCount);
        Assert.Equal(AgentSessionActivity.Idle, session.Status.Activity);
    }

    [Fact]
    public void InputAcceptedAfterCapture_DiscardsTheAnswer()
    {
        var session = CompletedInitialTurnWithUnknownFollowup();
        var request = Capture(session);
        // Manager recovery is the one path that accepts work while Activity is
        // unknown; the answer for the older snapshot must not cancel it.
        session.RecordManagerRecoveryTurn("input-9", "turn-9", "recover", "manager-recovery", Now.AddMinutes(1),
            new AgentSessionInputProvenance("provider", "workspace", "conversation", null, "member", "message"));

        var applied = session.SettleActivityFromEvidence(Answer(request, RunnerSessionActivityObservations.Idle), Now.AddMinutes(2));

        Assert.False(applied.Applied);
        Assert.Equal(AgentTurnStatus.Queued, SingleTurn(session, "turn-9").Status);
        Assert.Null(SingleTurn(session, "turn-2").SupersededAt);
    }

    [Fact]
    public void CompetingOperationAfterCapture_DiscardsTheAnswer()
    {
        var session = InFlightTurnUnderUnknownActivity();
        var request = Capture(session);
        session.ClaimTurnStop("turn-2", "stop-1", Now, TimeSpan.FromMinutes(1));

        Assert.False(session.SettleActivityFromEvidence(Answer(request, RunnerSessionActivityObservations.Idle), Now.AddMinutes(1)).Applied);
        Assert.Equal(AgentSessionActivity.Unknown, session.Status.Activity);
        Assert.Null(SingleTurn(session, "turn-2").SupersededAt);
    }

    [Fact]
    public void Settlement_SupersedesCapturedStopClaimAndResetReservation_RetainingIdentity()
    {
        var session = InFlightTurnUnderUnknownActivity();
        session.ClaimTurnStop("turn-2", "stop-1", Now, TimeSpan.FromMinutes(5));
        session.MarkTurnStopDispatched("turn-2", "stop-1");
        session.Status = session.Status with
        {
            PendingReset = new AgentSessionResetReservation(
                "reset-1", "runtime-1", "opencode", Now,
                IdempotencyKey: "reset-key", OwnerProcessGeneration: "process-1"),
            SessionCommandAdmissionFacts =
            [
                new AgentSessionCommandAdmissionTombstone(
                    "reset", "reset-1", "reset-key", "process-1"),
            ],
        };

        var request = Capture(session);
        var applied = session.SettleActivityFromEvidence(
            Answer(request, RunnerSessionActivityObservations.UnknownToRunner), Now.AddMinutes(1));

        Assert.True(applied.Applied);
        Assert.Equal(["stop-1", "reset-1"], applied.SupersededOperationIds);
        var claim = session.Status.PendingStop!;
        Assert.Equal("stop-1", claim.OperationId);
        Assert.False(claim.IsActive);
        Assert.NotNull(claim.SupersededAt);
        Assert.Equal("stop-1", Assert.Single(session.Status.SupersededStopClaims!).OperationId);
        var reservation = session.Status.PendingReset!;
        Assert.Equal("reset-1", reservation.OperationId);
        Assert.NotNull(reservation.SupersededAt);
        Assert.Equal("superseded", reservation.Outcome!.Status);
        var resetAudit = Assert.Single(session.Status.SessionCommandAdmissionFacts!);
        Assert.Equal("reset-1", resetAudit.OperationId);
        Assert.Equal("superseded", resetAudit.Outcome!.Status);
        Assert.Equal(AgentSessionActivity.Idle, session.Status.Activity);
    }

    [Fact]
    public void LateTerminalEvidence_DoesNotReviveASupersededTurn()
    {
        var session = CompletedInitialTurnWithUnknownFollowup();
        var request = Capture(session);
        Assert.True(session.SettleActivityFromEvidence(Answer(request, RunnerSessionActivityObservations.Idle), Now.AddMinutes(1)).Applied);

        Assert.Empty(session.MarkTurnExecuting("turn-2", Now.AddMinutes(2)));
        Assert.Empty(session.MarkTurnTerminal("turn-2", AgentTurnStatus.Completed, null, Now.AddMinutes(2)));
        Assert.Equal(AgentTurnStatus.Unknown, SingleTurn(session, "turn-2").Status);
        Assert.Equal(AgentSessionActivity.Idle, session.Status.Activity);
    }

    [Fact]
    public void BindingReplacement_AdvancesGenerationAndClearsMissingFact()
    {
        var session = CompletedInitialTurnWithUnknownFollowup();
        var request = Capture(session);
        session.SettleActivityFromEvidence(Answer(request, RunnerSessionActivityObservations.UnknownToRunner), Now.AddMinutes(1));
        var generation = session.Status.ContextGeneration;
        Assert.NotNull(session.Status.MissingRunnerFact);

        session.RebindRuntimeSession(
            new AgentRuntimeBinding("runner-1", "opencode", "runtime-1"),
            new AgentRuntimeBinding("runner-1", "opencode", "runtime-2"),
            "missing-recovery",
            Now.AddMinutes(2),
            session.BindingEpoch);

        Assert.Equal(generation + 1, session.Status.ContextGeneration);
        Assert.Null(session.Status.MissingRunnerFact);
        Assert.Equal(generation, SingleTurn(session, "turn-1").ContextGeneration);
        session.EnsureInitialLaunch("input-4", "turn-4", "later", "agent-connection", "job-4", Now.AddMinutes(3));
        Assert.Equal(generation + 1, SingleTurn(session, "turn-4").ContextGeneration);
    }

    [Fact]
    public void ProbeCapture_RequiresUnknownActivity()
    {
        var session = BoundSession();
        session.EnsureInitialLaunch("input-1", "turn-1", "prompt", "agent-connection", "job-1", Now);
        session.SetActivity(AgentSessionActivity.Active, Now);

        Assert.Null(session.CaptureActivityObservation("runner-1", "obs-probe", runnerRemoved: false, Now));
        Assert.NotNull(session.CaptureActivityObservation("runner-1", "obs-removal", runnerRemoved: true, Now));
    }

    [Fact]
    public void RemovalCapture_ReadsUnresolvedFactsOnAnIdleSession()
    {
        var session = BoundSession();
        session.EnsureInitialLaunch("input-1", "turn-1", "prompt", "agent-connection", "job-1", Now);
        session.MarkInitialTurnTerminal("job-1", AgentTurnStatus.Completed, null, Now);
        var queued = session.AcceptFollowup("input-2", "turn-2", "op-2", "wait", "agent-session-followup", "key-2", Now);
        Assert.Equal(AgentSessionActivity.Idle, session.Status.Activity);
        Assert.Equal(AgentTurnStatus.Queued, SingleTurn(session, queued.TurnId).Status);

        Assert.NotNull(session.CaptureActivityObservation("runner-1", "obs-removal", runnerRemoved: true, Now));
        Assert.Null(session.CaptureActivityObservation("runner-1", "obs-probe", runnerRemoved: false, Now));
    }

    [Fact]
    public void RemovalCapture_IgnoresASettledIdleSession()
    {
        var session = CompletedInitialTurnWithUnknownFollowup();
        var request = Capture(session);
        session.SettleActivityFromEvidence(Answer(request, RunnerSessionActivityObservations.Idle), Now.AddMinutes(1));

        Assert.Null(session.CaptureActivityObservation("runner-1", "obs-removal", runnerRemoved: true, Now.AddMinutes(2)));
    }

    [Fact]
    public void Capture_RequiresACompleteBindingForTheObservedRunner()
    {
        var session = CompletedInitialTurnWithUnknownFollowup();

        Assert.Null(session.CaptureActivityObservation("runner-other", "obs-other", runnerRemoved: false, Now));

        var unbound = AgentSession.Create("sessions/unbound", "runner-1", "/work", metadata: BoundMetadata(), now: Now);
        unbound.Settings = new AgentSessionSettings("opencode");
        unbound.SetActivity(AgentSessionActivity.Unknown, Now);
        Assert.Null(unbound.CaptureActivityObservation("runner-1", "obs-unbound", runnerRemoved: false, Now));
    }

    [Fact]
    public void RepeatedCaptureOfUnchangedState_ReusesTheObservationIdentity()
    {
        var session = CompletedInitialTurnWithUnknownFollowup();

        var first = session.CaptureActivityObservation("runner-1", "obs-1", runnerRemoved: false, Now)!;
        var second = session.CaptureActivityObservation("runner-1", "obs-2", runnerRemoved: false, Now);

        Assert.Same(first, second);
        Assert.Equal("obs-1", session.Status.PendingActivityObservation!.ObservationId);
    }

    [Fact]
    public void IncompleteOrUnknownEvidence_IsDiscarded()
    {
        var session = CompletedInitialTurnWithUnknownFollowup();
        var request = Capture(session);

        Assert.False(session.SettleActivityFromEvidence(Answer(request, "absent"), Now.AddMinutes(1)).Applied);
        Assert.False(session.SettleActivityFromEvidence(Answer(request, ""), Now.AddMinutes(1)).Applied);
        Assert.False(session.SettleActivityFromEvidence(
            Answer(request with { SessionId = "other-session" }, RunnerSessionActivityObservations.Idle), Now.AddMinutes(1)).Applied);
        Assert.False(session.SettleActivityFromEvidence(
            Answer(request with { ObservationId = "" }, RunnerSessionActivityObservations.Idle), Now.AddMinutes(1)).Applied);
        Assert.False(session.SettleActivityFromEvidence(
            Answer(request with { BindingEpoch = -1 }, RunnerSessionActivityObservations.Idle), Now.AddMinutes(1)).Applied);
        Assert.False(session.SettleActivityFromEvidence(
            Answer(request with { WorkDir = " " }, RunnerSessionActivityObservations.Idle), Now.AddMinutes(1)).Applied);
        Assert.False(session.SettleActivityFromEvidence(
            Answer(default!, RunnerSessionActivityObservations.Idle), Now.AddMinutes(1)).Applied);
        Assert.Equal(AgentSessionActivity.Unknown, session.Status.Activity);
        Assert.Null(SingleTurn(session, "turn-2").SupersededAt);
    }

    [Fact]
    public void DeriveCurrentActivity_ReadsSupersededFactsAsSettled()
    {
        var session = CompletedInitialTurnWithUnknownFollowup();
        session.AcceptFollowup("input-3", "turn-3", "op-3", "queued", "agent-session-followup", "key-3", Now, forceNewTurn: true);
        session.SetActivity(AgentSessionActivity.Unknown, Now);

        Assert.Equal(AgentSessionActivity.Unknown, session.DeriveCurrentActivity());

        var request = Capture(session);
        session.SettleActivityFromEvidence(Answer(request, RunnerSessionActivityObservations.Idle), Now.AddMinutes(1));

        Assert.Equal(AgentSessionActivity.Idle, session.DeriveCurrentActivity());
    }

    [Fact]
    public void TerminalMark_DoesNotForceIdleWhileAnotherTurnRuns()
    {
        var session = BoundSession();
        session.EnsureInitialLaunch("input-1", "turn-1", "prompt", "agent-connection", "job-1", Now);
        session.MarkInitialTurnExecuting("job-1", Now);
        var followup = session.AcceptFollowup("input-2", "turn-2", "op-2", "run", "agent-session-followup", "key-2", Now, forceNewTurn: true);
        session.MarkFollowupTurnExecuting(followup.OperationId, Now);

        session.MarkInitialTurnTerminal("job-1", AgentTurnStatus.Completed, null, Now);

        Assert.Equal(AgentTurnStatus.Executing, SingleTurn(session, "turn-2").Status);
        Assert.Equal(AgentSessionActivity.Active, session.Status.Activity);
    }

    [Fact]
    public void QueuedTurnCancel_DoesNotForceIdleWhileAnotherTurnRuns()
    {
        var session = BoundSession();
        session.EnsureInitialLaunch("input-1", "turn-1", "prompt", "agent-connection", "job-1", Now);
        var running = session.AcceptFollowup("input-2", "turn-2", "op-2", "run", "agent-session-followup", "key-2", Now);
        var queued = session.AcceptFollowup("input-3", "turn-3", "op-3", "wait", "agent-session-followup", "key-3", Now, forceNewTurn: true);
        session.MarkFollowupTurnExecuting(running.OperationId, Now);

        session.StopQueuedTurn(queued.TurnId, Now);

        Assert.Equal(AgentTurnStatus.Cancelled, SingleTurn(session, "turn-3").Status);
        Assert.Equal(AgentSessionActivity.Active, session.Status.Activity);
    }

    private static AgentSession BoundSession()
    {
        var session = AgentSession.Create("sessions/one", "runner-1", "/work", metadata: BoundMetadata(), now: Now);
        session.Settings = new AgentSessionSettings("opencode");
        session.AttachPhysicalSession("runtime-1", "model", "/work", null, null, Now);
        return session;
    }

    private static AgentSessionMetadata BoundMetadata() => new AgentSessionMetadata()
        .WithLabel("mohist.io/project-id", "project-1")
        .WithLabel("mohist.io/source-kind", "agent-launch")
        .WithLabel("mohist.io/agent-id", "agent-1");

    /// <summary>
    /// A session whose current Turn is executing while the Runner evidence
    /// says the activity cannot be confirmed.
    /// </summary>
    private static AgentSession InFlightTurnUnderUnknownActivity()
    {
        var session = BoundSession();
        session.EnsureInitialLaunch("input-1", "turn-1", "prompt", "agent-connection", "job-1", Now);
        session.MarkInitialTurnExecuting("job-1", Now);
        var running = session.AcceptFollowup("input-2", "turn-2", "op-2", "run", "agent-session-followup", "key-2", Now);
        session.MarkFollowupTurnExecuting(running.OperationId, Now);
        session.SetActivity(AgentSessionActivity.Unknown, Now);
        return session;
    }

    /// <summary>
    /// The production baseline shape: the initial Turn and Job completed, a
    /// later Turn went terminal unknown, and nothing current is left.
    /// </summary>
    private static AgentSession CompletedInitialTurnWithUnknownFollowup()
    {
        var session = BoundSession();
        session.EnsureInitialLaunch("input-1", "turn-1", "prompt", "agent-connection", "job-1", Now);
        session.MarkInitialTurnExecuting("job-1", Now);
        session.MarkInitialTurnTerminal("job-1", AgentTurnStatus.Completed, null, Now);
        var followup = session.AcceptFollowup(
            "input-2", "turn-2", "op-followup", "later", "agent-session-followup", "key-2", Now);
        session.MarkFollowupTurnExecuting(followup.OperationId, Now);
        session.MarkFollowupTurnTerminal(followup.OperationId, AgentTurnStatus.Unknown, null, Now);
        Assert.Equal(AgentSessionActivity.Unknown, session.Status.Activity);
        return session;
    }

    private static AgentTurnRecord SingleTurn(AgentSession session, string turnId) =>
        Assert.Single(session.Status.Turns!, turn => string.Equals(turn.Id, turnId, StringComparison.Ordinal));

    private RunnerSessionActivityProbeRequest Capture(AgentSession session)
    {
        var observation = session.CaptureActivityObservation("runner-1", $"obs-{++_observations}", runnerRemoved: false, Now);
        Assert.NotNull(observation);
        return new RunnerSessionActivityProbeRequest(
            session.Id,
            observation!.ObservationId,
            observation.RunnerId,
            observation.Runtime,
            observation.RuntimeSessionId,
            observation.WorkDir,
            observation.BindingEpoch,
            observation.ContextGeneration);
    }

    private static RunnerSessionActivityProbeResult Answer(
        RunnerSessionActivityProbeRequest request,
        string observation) => new(request, observation);
}
