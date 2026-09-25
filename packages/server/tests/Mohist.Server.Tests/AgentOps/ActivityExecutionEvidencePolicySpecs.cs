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
    public void QuietLongRunningTurn_WithCurrentOwnerConfirmation_RemainsRunning()
    {
        var record = BuildRecord(AgentTurnStatus.Executing, Now.AddMinutes(-30));
        var runner = BuildRunner("generation-1");

        var assessment = ActivityExecutionEvidencePolicy.Evaluate(record, Now, runner);

        Assert.Equal("running", assessment.ExecutionState);
        Assert.Equal("owner-confirmed", assessment.Evidence.Reason);
        Assert.Equal("turn-1", assessment.Evidence.TurnId);
        Assert.Equal("runner-1", assessment.Evidence.RunnerId);
    }

    [Fact]
    public void OwnerGenerationMismatch_NeedsSupersededGenerationVerification()
    {
        var record = BuildRecord(AgentTurnStatus.Executing, Now.AddMinutes(-30));
        var runner = BuildRunner("generation-2", workGeneration: "generation-1");

        var assessment = ActivityExecutionEvidencePolicy.Evaluate(record, Now, runner);

        Assert.Equal("needs-verification", assessment.ExecutionState);
        Assert.Equal("superseded-generation", assessment.Evidence.Reason);
    }

    [Fact]
    public void OwnerGenerationMismatch_OverridesFreshActivityEvidence()
    {
        var record = BuildRecord(AgentTurnStatus.Executing, Now.AddSeconds(-1));
        var runner = BuildRunner("generation-2", workGeneration: "generation-1");

        var assessment = ActivityExecutionEvidencePolicy.Evaluate(record, Now, runner);

        Assert.Equal("needs-verification", assessment.ExecutionState);
        Assert.Equal("superseded-generation", assessment.Evidence.Reason);
    }

    [Fact]
    public void Evaluation_DoesNotMutateSessionFacts()
    {
        var record = BuildRecord(AgentTurnStatus.Executing, Now.AddMinutes(-30));
        var status = record.Session.Status;
        var turn = status.Turns![0];
        var labels = record.Labels;
        var runner = BuildRunner("generation-1");

        _ = ActivityExecutionEvidencePolicy.Evaluate(record, Now, runner);

        Assert.Same(status, record.Session.Status);
        Assert.Same(turn, record.Session.Status.Turns![0]);
        Assert.Same(labels, record.Labels);
        Assert.Equal(status, record.Session.Status);
    }

    private static AgentSessionRecord BuildRecord(
        AgentTurnStatus turnStatus,
        DateTimeOffset? updatedAt)
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
            1,
            ["input-1"],
            turnStatus,
            UpdatedAt: updatedAt?.UtcDateTime);
        session.Status = session.Status with
        {
            Activity = AgentSessionActivity.Active,
            LastDataAt = updatedAt?.UtcDateTime,
            Turns = [turn],
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
        string? workGeneration = null) =>
        new(
            new RunnerIdentityStatusView("runner-1", null, null, null, null, null, null),
            new RunnerPresenceStatusView("online", Now),
            new RunnerControlStatusView("connected", "connection-1"),
            new RunnerAdmissionStatusView("ready", []),
            [],
            [],
            new RunnerStatusCapacityView(1, 1),
            [new RunnerActiveWorkView(
                "work-1",
                "agent-job",
                "job-1",
                "agent-job",
                AgentSessionId: "session-1",
                AgentTurnId: "turn-1",
                ProcessGeneration: workGeneration ?? runnerGeneration)],
            null,
            [],
            null,
            runnerGeneration);
}
