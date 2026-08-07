using Mohist.Server.Sessions.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.Sessions;

public sealed class SessionTreeStopOperationTests
{
    [Fact]
    public void PublicStopRequestHasNoClientSnapshotInputs()
    {
        var properties = typeof(SessionTreeStopRequest)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(
            ["ProjectId", "RootSessionId", "OperationId", "IdempotencyKey", "RequestFingerprint"],
            properties);
    }

    [Fact]
    public void UnknownTargetKeepsTheFrozenAdmissionFenceAndRetryReusesSnapshot()
    {
        var request = new SessionTreeStopRequest(
            "project-stop-contract",
            "session-root",
            "operation-stop-contract",
            "stop-key",
            "stop-fingerprint");
        var snapshot = new SessionTreeStopSnapshot(
            request.ProjectId,
            request.RootSessionId,
            request.OperationId,
            request.IdempotencyKey,
            request.RequestFingerprint,
            7,
            [
                new SessionTreeStopMembership("session-root", null, null, null, 0),
                new SessionTreeStopMembership("session-child", "session-root", "edge-1", "job-1", 3),
            ],
            [
                new SessionTreeStopTargetSnapshot(
                    "session-root",
                    "turn-root",
                    "job-root",
                    AgentTurnStatus.Executing,
                    "runner-1",
                    "opencode",
                    "runtime-root",
                    "/workspace",
                    SessionTreeStopOperationIds.ForTarget(request.OperationId, "session-root")),
                new SessionTreeStopTargetSnapshot(
                    "session-child",
                    "turn-child",
                    "job-child",
                    AgentTurnStatus.Executing,
                    "runner-1",
                    "opencode",
                    "runtime-child",
                    "/workspace",
                    SessionTreeStopOperationIds.ForTarget(request.OperationId, "session-child")),
            ]);

        var operation = SessionTreeStopOperation.Create(request).Publish(snapshot);
        var afterUnknown = operation.RecordTarget(new SessionTreeStopTargetResult(
            "session-child",
            snapshot.Targets[1].StopOperationId,
            SessionTreeStopTargetOutcome.Unknown,
            "runner reply was not confirmed"));

        Assert.Equal(SessionTreeStopOperationStatus.Unknown, afterUnknown.Status);
        Assert.True(afterUnknown.AdmissionFenceActive);
        Assert.Equal(snapshot, afterUnknown.Snapshot);

        var replay = afterUnknown.Replay(snapshot);

        Assert.Equal(snapshot, replay.Snapshot);
        Assert.Equal(afterUnknown.TargetResults, replay.TargetResults);
        Assert.Equal(SessionTreeStopOperationStatus.Unknown, replay.Status);
        Assert.True(replay.AdmissionFenceActive);
    }

    [Fact]
    public void TerminalSummaryCanBePartialWithoutClosingTheSessions()
    {
        var request = new SessionTreeStopRequest(
            "project-stop-contract",
            "session-root",
            "operation-stop-partial",
            "stop-key-partial",
            "stop-fingerprint-partial");
        var snapshot = new SessionTreeStopSnapshot(
            request.ProjectId,
            request.RootSessionId,
            request.OperationId,
            request.IdempotencyKey,
            request.RequestFingerprint,
            2,
            [new SessionTreeStopMembership("session-root", null, null, null, 0)],
            [new SessionTreeStopTargetSnapshot(
                "session-root",
                "turn-root",
                "job-root",
                AgentTurnStatus.Executing,
                "runner-1",
                "opencode",
                "runtime-root",
                "/workspace",
                SessionTreeStopOperationIds.ForTarget(request.OperationId, "session-root"))]);

        var operation = SessionTreeStopOperation.Create(request)
            .Publish(snapshot)
            .RecordTarget(new SessionTreeStopTargetResult(
                "session-root",
                snapshot.Targets[0].StopOperationId,
                SessionTreeStopTargetOutcome.Rejected,
                "binding replaced"));

        Assert.Equal(SessionTreeStopOperationStatus.Partial, operation.Status);
        Assert.False(operation.AdmissionFenceActive);
        Assert.Equal(SessionTreeStopTargetOutcome.Rejected, operation.TargetResults!.Single().Outcome);
    }
}
