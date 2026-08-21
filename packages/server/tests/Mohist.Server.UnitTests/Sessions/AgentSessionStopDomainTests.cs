using Mohist.Server.Sessions.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.Sessions;

public sealed class AgentSessionStopDomainTests
{
    private static readonly DateTime Now = new(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Queued_followup_is_cancelled_without_a_runtime_stop_claim()
    {
        var session = CreateSessionWithFollowup();

        var result = session.StopQueuedTurn("turn-1", Now.AddSeconds(1));

        Assert.True(result.Cancelled);
        Assert.Equal(AgentTurnStatus.Cancelled, result.Control?.Status);
        Assert.Null(session.Status.PendingStop);
    }

    [Fact]
    public void Executing_followup_claim_reuses_the_same_operation()
    {
        var session = CreateExecutingSession();

        var first = session.ClaimTurnStop("turn-1", "operation-1", Now, TimeSpan.FromMinutes(1));
        var replay = session.ClaimTurnStop("turn-1", null, Now.AddSeconds(1), TimeSpan.FromMinutes(1));

        Assert.True(first.CanDispatch);
        Assert.True(replay.CanDispatch);
        Assert.Equal("operation-1", replay.OperationId);
        Assert.Equal("operation-1", session.Status.PendingStop?.OperationId);
    }

    [Fact]
    public void Terminal_runtime_fact_keeps_an_unsettled_claim_replayable()
    {
        var session = CreateExecutingSession();
        session.ClaimTurnStop("turn-1", "operation-1", Now, TimeSpan.FromMinutes(1));
        session.MarkTurnStopDispatched("turn-1", "operation-1");

        session.MarkTurnTerminal("turn-1", AgentTurnStatus.Completed, null, Now.AddSeconds(1));
        var retry = session.ClaimTurnStop("turn-1", null, Now.AddSeconds(2), TimeSpan.FromMinutes(1));

        Assert.Equal(AgentTurnControlClassification.Terminal, retry.Control?.Classification);
        Assert.True(retry.CanDispatch);
        Assert.Equal("operation-1", retry.OperationId);
        Assert.True(session.Status.PendingStop?.DispatchStarted);
    }

    [Fact]
    public void Settled_claim_makes_a_terminal_turn_already_ended()
    {
        var session = CreateExecutingSession();
        session.ClaimTurnStop("turn-1", "operation-1", Now, TimeSpan.FromMinutes(1));
        session.MarkTurnTerminal("turn-1", AgentTurnStatus.Completed, null, Now.AddSeconds(1));
        session.SettleTurnStop("turn-1", "operation-1", AgentSessionStopDisposition.Ended);

        var replay = session.ClaimTurnStop("turn-1", null, Now.AddSeconds(2), TimeSpan.FromMinutes(1));

        Assert.False(replay.CanDispatch);
        Assert.Equal(AgentSessionStopDisposition.Ended, replay.Disposition);
        Assert.Equal("operation-1", replay.OperationId);
    }

    [Fact]
    public void A_different_operation_cannot_settle_the_active_claim()
    {
        var session = CreateExecutingSession();
        session.ClaimTurnStop("turn-1", "operation-1", Now, TimeSpan.FromMinutes(1));

        session.SettleTurnStop("turn-1", "operation-2", AgentSessionStopDisposition.Stopped);

        Assert.Equal(AgentSessionStopDisposition.Pending, session.Status.PendingStop?.Disposition);
        Assert.True(session.Status.PendingStop?.IsActive);
    }

    private static AgentSession CreateExecutingSession()
    {
        var session = CreateSessionWithFollowup();
        session.MarkTurnExecuting("turn-1", Now.AddSeconds(1));
        return session;
    }

    private static AgentSession CreateSessionWithFollowup()
    {
        var metadata = new AgentSessionMetadata()
            .WithLabel("mohist.io/project-id", "project-1")
            .WithLabel("mohist.io/source-kind", "workflow")
            .WithLabel("mohist.io/source-id", "workflow-1")
            .WithLabel("mohist.io/session-name", "session-1");
        var session = AgentSession.Create(
            "session-1",
            "runner-1",
            "/work/session-1",
            metadata: metadata,
            now: Now);
        session.RecordFollowupTurn("input-1", "turn-1", "continue", "generic-followup", Now);
        return session;
    }
}
