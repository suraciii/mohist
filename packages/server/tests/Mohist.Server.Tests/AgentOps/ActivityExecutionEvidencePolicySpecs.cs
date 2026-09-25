using Mohist.Server.AgentOps.Services;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.Tests.AgentOps;

[Trait("level", "L0")]
public sealed class ActivityExecutionEvidencePolicySpecs
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FreshActivityAtExactFiveMinuteBoundary_IsRunning()
    {
        var record = BuildRecord(AgentTurnStatus.Executing, Now.AddMinutes(-5));

        var assessment = ActivityExecutionEvidencePolicy.Evaluate(record, Now, runner: null);

        Assert.Equal("running", assessment.ExecutionState);
        Assert.Equal("activity", assessment.Evidence.Reason);
        Assert.Equal(Now.AddMinutes(-5), assessment.Evidence.ObservedAt);
    }

    [Fact]
    public void MissingActivityEvidence_NeedsVerification()
    {
        var record = BuildRecord(AgentTurnStatus.Executing, updatedAt: null);

        var assessment = ActivityExecutionEvidencePolicy.Evaluate(record, Now, runner: null);

        Assert.Equal("needs-verification", assessment.ExecutionState);
        Assert.Equal("missing", assessment.Evidence.Reason);
    }

    [Fact]
    public void FutureActivityEvidence_NeedsVerification()
    {
        var record = BuildRecord(AgentTurnStatus.Executing, Now.AddSeconds(1));

        var assessment = ActivityExecutionEvidencePolicy.Evaluate(record, Now, runner: null);

        Assert.Equal("future", assessment.Evidence.Reason);
    }

    [Fact]
    public void AgedActivityEvidence_NeedsVerification()
    {
        var record = BuildRecord(AgentTurnStatus.Executing, Now.AddMinutes(-5).AddSeconds(-1));

        var assessment = ActivityExecutionEvidencePolicy.Evaluate(record, Now, runner: null);

        Assert.Equal("needs-verification", assessment.ExecutionState);
        Assert.Equal("aged", assessment.Evidence.Reason);
    }

    [Fact]
    public void QueuedTurn_RemainsQueuedRegardlessOfAge()
    {
        var record = BuildRecord(AgentTurnStatus.Queued, Now.AddMinutes(-30));

        var assessment = ActivityExecutionEvidencePolicy.Evaluate(record, Now, runner: null);


        Assert.Equal("queued", assessment.ExecutionState);
        Assert.Equal("queued", assessment.Evidence.Reason);
    }

    [Fact]
    public void ActivityFromAnotherTurn_CannotEstablishRunning()
    {
        var record = BuildRecord(AgentTurnStatus.Executing, Now.AddSeconds(-1));

        var assessment = ActivityExecutionEvidencePolicy.Evaluate(
            record,
            Now,
            runner: null,
            activityObservedAt: Now.AddSeconds(-1),
            activityTurnId: "old-turn");

        Assert.Equal("needs-verification", assessment.ExecutionState);
        Assert.Equal("superseded-generation", assessment.Evidence.Reason);
    }

    [Fact]
    public void TerminalTurn_TakesPrecedenceOverFreshActivity()
    {
        var record = BuildRecord(AgentTurnStatus.Completed, Now);

        var assessment = ActivityExecutionEvidencePolicy.Evaluate(record, Now, runner: null);

        Assert.Equal("not-running", assessment.ExecutionState);
        Assert.Equal("terminal", assessment.Evidence.Reason);
    }

    [Fact]
    public void QuietLongRunningTurn_WithFreshOwnerConfirmation_RemainsRunning()
    {
        var record = BuildRecord(AgentTurnStatus.Executing, Now.AddMinutes(-30));
        var confirmedAt = Now.AddMinutes(-1);
        var runner = BuildRunner("generation-1", confirmedAt: confirmedAt);

        var assessment = ActivityExecutionEvidencePolicy.Evaluate(record, Now, runner);

        Assert.Equal("running", assessment.ExecutionState);
        Assert.Equal("owner-confirmed", assessment.Evidence.Reason);
        // The confirmation's own time is the evidence; the read time is not.
        Assert.Equal(confirmedAt, assessment.Evidence.ObservedAt);
        Assert.Equal("turn-1", assessment.Evidence.TurnId);
        Assert.Equal("runner-1", assessment.Evidence.RunnerId);
    }

    [Fact]
    public void QuietLongRunningTurn_WithConfirmationBeyondTheWindow_NeedsVerification()
    {
        var record = BuildRecord(AgentTurnStatus.Executing, Now.AddMinutes(-30));
        var confirmedAt = Now.AddMinutes(-5).AddSeconds(-1);
        var runner = BuildRunner("generation-1", confirmedAt: confirmedAt);

        var assessment = ActivityExecutionEvidencePolicy.Evaluate(record, Now, runner);

        Assert.Equal("needs-verification", assessment.ExecutionState);
        Assert.Equal("aged", assessment.Evidence.Reason);
        Assert.Equal(confirmedAt, assessment.Evidence.ObservedAt);
    }

    [Fact]
    public void QuietLongRunningTurn_WithoutOwnerConfirmation_NeedsVerification()
    {
        var record = BuildRecord(AgentTurnStatus.Executing, Now.AddMinutes(-30));
        var runner = BuildRunner("generation-1");

        var assessment = ActivityExecutionEvidencePolicy.Evaluate(record, Now, runner);

        Assert.Equal("needs-verification", assessment.ExecutionState);
        Assert.Equal("aged", assessment.Evidence.Reason);
    }

    [Fact]
    public void FutureOwnerConfirmation_DoesNotEstablishRunning()
    {
        var record = BuildRecord(AgentTurnStatus.Executing, Now.AddMinutes(-30));
        var runner = BuildRunner("generation-1", confirmedAt: Now.AddSeconds(1));

        var assessment = ActivityExecutionEvidencePolicy.Evaluate(record, Now, runner);

        Assert.Equal("needs-verification", assessment.ExecutionState);
        Assert.Equal("future", assessment.Evidence.Reason);
        Assert.Equal(Now.AddSeconds(1), assessment.Evidence.ObservedAt);
    }

    [Fact]
    public void FutureOwnerConfirmation_DoesNotHideFreshActivity()
    {
        var record = BuildRecord(AgentTurnStatus.Executing, Now.AddSeconds(-1));
        var runner = BuildRunner("generation-1", confirmedAt: Now.AddSeconds(1));

        var assessment = ActivityExecutionEvidencePolicy.Evaluate(record, Now, runner);

        Assert.Equal("running", assessment.ExecutionState);
        Assert.Equal("activity", assessment.Evidence.Reason);
        Assert.Equal(Now.AddSeconds(-1), assessment.Evidence.ObservedAt);
    }

    [Fact]
    public void StaleOwnerConfirmation_DoesNotOverrideFreshActivity()
    {
        var record = BuildRecord(AgentTurnStatus.Executing, Now.AddSeconds(-1));
        var runner = BuildRunner("generation-1", confirmedAt: Now.AddMinutes(-30));

        var assessment = ActivityExecutionEvidencePolicy.Evaluate(record, Now, runner);

        Assert.Equal("running", assessment.ExecutionState);
        Assert.Equal("activity", assessment.Evidence.Reason);
    }

    [Fact]
    public void RepeatedReads_DoNotRenewTheOwnerConfirmation()
    {
        var record = BuildRecord(AgentTurnStatus.Executing, Now.AddMinutes(-30));
        var confirmedAt = Now.AddMinutes(-4);
        // One snapshot: no read can change what the Runner last said.
        var runner = BuildRunner("generation-1", confirmedAt: confirmedAt);

        var first = ActivityExecutionEvidencePolicy.Evaluate(record, Now, runner);
        var later = ActivityExecutionEvidencePolicy.Evaluate(record, Now.AddMinutes(4), runner);

        Assert.Equal("running", first.ExecutionState);
        Assert.Equal(confirmedAt, first.Evidence.ObservedAt);
        Assert.Equal("needs-verification", later.ExecutionState);
        Assert.Equal("aged", later.Evidence.Reason);
        Assert.Equal(confirmedAt, later.Evidence.ObservedAt);
    }

    [Fact]
    public void OwnerWorkForAnotherTurn_DoesNotConfirmThisTurn()
    {
        var record = BuildRecord(AgentTurnStatus.Executing, Now.AddMinutes(-30));
        var runner = BuildRunner(
            "generation-1",
            confirmedAt: Now.AddSeconds(-1),
            workTurnId: "turn-2");

        var assessment = ActivityExecutionEvidencePolicy.Evaluate(record, Now, runner);

        Assert.Equal("needs-verification", assessment.ExecutionState);
        Assert.Equal("aged", assessment.Evidence.Reason);
    }

    [Fact]
    public void OwnerGenerationMismatch_NeedsSupersededGenerationVerification()
    {
        var record = BuildRecord(AgentTurnStatus.Executing, Now.AddMinutes(-30));
        var runner = BuildRunner(
            "generation-2",
            workGeneration: "generation-1",
            confirmedAt: Now.AddSeconds(-1));

        var assessment = ActivityExecutionEvidencePolicy.Evaluate(record, Now, runner);

        Assert.Equal("needs-verification", assessment.ExecutionState);
        Assert.Equal("superseded-generation", assessment.Evidence.Reason);
    }

    [Fact]
    public void OwnerGenerationMismatchWithoutActivityTime_CarriesTheOwnerObservationTime()
    {
        var record = BuildRecord(AgentTurnStatus.Executing, updatedAt: null);
        var runner = BuildRunner(
            "generation-2",
            workGeneration: "generation-1",
            confirmedAt: Now.AddSeconds(-1));

        var assessment = ActivityExecutionEvidencePolicy.Evaluate(record, Now, runner);

        Assert.Equal("needs-verification", assessment.ExecutionState);
        Assert.Equal("superseded-generation", assessment.Evidence.Reason);
        Assert.Equal(Now, assessment.Evidence.ObservedAt);
    }

    [Fact]
    public void OwnerGenerationMismatch_OverridesFreshActivityEvidence()
    {
        var record = BuildRecord(AgentTurnStatus.Executing, Now.AddSeconds(-1));
        var runner = BuildRunner(
            "generation-2",
            workGeneration: "generation-1",
            confirmedAt: Now.AddSeconds(-1));

        var assessment = ActivityExecutionEvidencePolicy.Evaluate(record, Now, runner);

        Assert.Equal("needs-verification", assessment.ExecutionState);
        Assert.Equal("superseded-generation", assessment.Evidence.Reason);
    }

    [Fact]
    public void WorkflowOwnerWork_WithFreshConfirmation_ConfirmsTheBoundTurn()
    {
        var binding = BuildBinding(turnId: "turn-1");
        var record = BuildRecord(AgentTurnStatus.Executing, Now.AddMinutes(-30), binding: binding);
        var confirmedAt = Now.AddSeconds(-1);
        var runner = BuildRunner(
            "generation-1",
            confirmedAt: confirmedAt,
            ownerKind: "workflow",
            ownerId: "run-1",
            workId: "work-1",
            workTurnId: null,
            workSessionId: "session-1");

        var assessment = ActivityExecutionEvidencePolicy.Evaluate(record, Now, runner);

        Assert.Equal("running", assessment.ExecutionState);
        Assert.Equal("owner-confirmed", assessment.Evidence.Reason);
        Assert.Equal(confirmedAt, assessment.Evidence.ObservedAt);
    }

    [Fact]
    public void WorkflowOwnerWork_BoundToAnotherTurn_DoesNotConfirmThisTurn()
    {
        var binding = BuildBinding(turnId: "turn-2");
        var record = BuildRecord(AgentTurnStatus.Executing, Now.AddMinutes(-30), binding: binding);
        var runner = BuildRunner(
            "generation-1",
            confirmedAt: Now.AddSeconds(-1),
            ownerKind: "workflow",
            ownerId: "run-1",
            workId: "work-1",
            workTurnId: null,
            workSessionId: "session-1");

        var assessment = ActivityExecutionEvidencePolicy.Evaluate(record, Now, runner);

        Assert.Equal("needs-verification", assessment.ExecutionState);
        Assert.Equal("aged", assessment.Evidence.Reason);
    }

    [Fact]
    public void WorkflowOwnerWork_ClaimedByTwoLiveTurns_DoesNotConfirmEither()
    {
        var binding = BuildBinding(turnId: "turn-1");
        var shared = BuildBinding(turnId: "turn-2");
        var record = BuildRecord(
            AgentTurnStatus.Executing,
            Now.AddMinutes(-30),
            binding: binding,
            previousTurns: [new AgentTurnRecord(
                "turn-2",
                1,
                ["input-2"],
                AgentTurnStatus.Executing,
                UpdatedAt: Now.AddMinutes(-30).UtcDateTime,
                WorkflowExecution: shared)]);
        var runner = BuildRunner(
            "generation-1",
            confirmedAt: Now.AddSeconds(-1),
            ownerKind: "workflow",
            ownerId: "run-1",
            workId: "work-1",
            workTurnId: null,
            workSessionId: "session-1");

        var assessment = ActivityExecutionEvidencePolicy.Evaluate(record, Now, runner);

        Assert.Equal("needs-verification", assessment.ExecutionState);
        Assert.Equal("aged", assessment.Evidence.Reason);
    }

    [Fact]
    public void Evaluation_DoesNotMutateSessionFacts()
    {
        var record = BuildRecord(AgentTurnStatus.Executing, Now.AddMinutes(-30));
        var status = record.Session.Status;
        var turn = status.Turns![0];
        var labels = record.Labels;
        var runner = BuildRunner("generation-1", confirmedAt: Now.AddSeconds(-1));

        _ = ActivityExecutionEvidencePolicy.Evaluate(record, Now, runner);

        Assert.Same(status, record.Session.Status);
        Assert.Same(turn, record.Session.Status.Turns![0]);
        Assert.Same(labels, record.Labels);
        Assert.Equal(status, record.Session.Status);
    }

    private static SessionWorkflowExecutionBinding BuildBinding(string turnId) =>
        new(
            InputDeliveryId: "delivery-1",
            WorkflowRunId: "run-1",
            ActionAttemptId: "attempt-1",
            WorkId: "work-1",
            RunnerId: "runner-1",
            AgentSessionId: "session-1",
            AgentTurnId: turnId,
            Runtime: "opencode",
            RuntimeSessionId: "runtime-session-1");

    private static AgentSessionRecord BuildRecord(
        AgentTurnStatus turnStatus,
        DateTimeOffset? updatedAt,
        SessionWorkflowExecutionBinding? binding = null,
        IReadOnlyList<AgentTurnRecord>? previousTurns = null)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = "project-1",
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            [GenericAgentSessionMetadata.AgentId] = "agent-1",
        };
        var session = AgentSession.Create(
            id: "session-1",
            runnerId: "runner-1",
            workDir: null,
            metadata: new AgentSessionMetadata(labels),
            now: Now.UtcDateTime);
        var turn = new AgentTurnRecord(
            "turn-1",
            2,
            ["input-1"],
            turnStatus,
            UpdatedAt: updatedAt?.UtcDateTime,
            WorkflowExecution: binding);
        session.Status = session.Status with
        {
            Activity = AgentSessionActivity.Active,
            LastDataAt = updatedAt?.UtcDateTime,
            Turns = [turn, .. previousTurns ?? []],
        };
        var row = new AgentSessionRow
        {
            Id = session.Id,
            State = "{}",
            RunnerId = session.Runtime.RunnerId,
            Status = "opened",
            CreatedAt = session.Status.CreatedAt,
            AgentSessionId = null,
        };
        return new AgentSessionRecord(row, session, labels);
    }

    private static RunnerStatusEntry BuildRunner(
        string runnerGeneration,
        string? workGeneration = null,
        DateTimeOffset? confirmedAt = null,
        string ownerKind = "agent-job",
        string ownerId = "job-1",
        string workId = "work-1",
        string? workTurnId = "turn-1",
        string? workSessionId = "session-1") =>
        new(
            new RunnerIdentityStatusView("runner-1", null, null, null, null, null, null),
            new RunnerPresenceStatusView("online", Now),
            new RunnerControlStatusView("connected", "connection-1"),
            new RunnerAdmissionStatusView("ready", []),
            [],
            [],
            new RunnerStatusCapacityView(1, 1),
            [new RunnerActiveWorkView(
                workId,
                ownerKind,
                ownerId,
                ownerKind,
                AgentSessionId: workSessionId,
                AgentTurnId: workTurnId,
                ProcessGeneration: workGeneration ?? runnerGeneration,
                ConfirmedAt: confirmedAt)],
            null,
            [],
            null,
            runnerGeneration);
}
