using Mohist.Server.Contracts;
using Mohist.Server.Sessions.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.Contracts;

public sealed class AgentWorkInterruptionProjectionTests
{
    [Fact]
    public void Apply_ReplayedLifecycleKeepsOneLatestTransitionPerWorkAndGeneration()
    {
        var recordedAt = DateTimeOffset.Parse("2026-08-15T00:00:00Z");
        var history = new List<AgentWorkInterruptionTransition>();

        foreach (var state in new[]
        {
            AgentWorkInterruptionStates.Interrupting,
            AgentWorkInterruptionStates.Interrupted,
            AgentWorkInterruptionStates.Interrupted,
        })
        {
            history = AgentWorkInterruptionProjection.Apply(
                history,
                Transition(state, "work-1", 0, recordedAt)).ToList();
        }

        history = AgentWorkInterruptionProjection.Apply(
            history,
            Transition(AgentWorkInterruptionStates.Recovering, "work-1.recovery.1", 1, recordedAt.AddMinutes(1))).ToList();
        history = AgentWorkInterruptionProjection.Apply(
            history,
            Transition(AgentWorkInterruptionStates.Recovered, "work-1.recovery.1", 1, recordedAt.AddMinutes(2))).ToList();
        history = AgentWorkInterruptionProjection.Apply(
            history,
            Transition(AgentWorkInterruptionStates.Recovering, "work-1.recovery.1", 1, recordedAt.AddMinutes(3))).ToList();

        Assert.Equal(2, history.Count);
        Assert.Equal(AgentWorkInterruptionStates.Interrupted, history.Single(item => item.RecoveryGeneration == 0).State);
        Assert.Equal(AgentWorkInterruptionStates.Recovered, history.Single(item => item.RecoveryGeneration == 1).State);
        Assert.Equal(
            AgentWorkInterruptionStates.Recovered,
            AgentWorkInterruptionProjection.Latest(history)?.State);
    }

    [Fact]
    public void SessionProjection_AttachesReplacementToNewTurnAndPreservesOriginalHistory()
    {
        var at = DateTimeOffset.Parse("2026-08-15T00:00:00Z").UtcDateTime;
        var session = AgentSession.Create("session-1", "runner-1", "/work", now: at);
        session.Status = session.Status with
        {
            Turns =
            [
                new AgentTurnRecord("turn-old", 1, ["input-old"], AgentTurnStatus.Executing),
                new AgentTurnRecord("turn-new", 2, ["input-new"], AgentTurnStatus.Queued),
            ]
        };

        var interrupted = Transition(
            AgentWorkInterruptionStates.Interrupted,
            "work-old",
            0,
            new DateTimeOffset(at));
        var recovering = interrupted with
        {
            State = AgentWorkInterruptionStates.Recovering,
            WorkId = "work-new",
            RecoveryGeneration = 1,
            OriginalTurnId = "turn-old",
            ReplacementTurnId = "turn-new",
        };

        var interrupting = interrupted with { State = AgentWorkInterruptionStates.Interrupting };
        Assert.Single(session.ApplyInterruption(interrupting, at.AddSeconds(1)));
        Assert.Single(session.ApplyInterruption(interrupted, at.AddSeconds(2)));
        Assert.Single(session.ApplyInterruption(recovering, at.AddSeconds(3)));
        Assert.Single(session.ApplyInterruption(recovering with { State = AgentWorkInterruptionStates.Recovered }, at.AddSeconds(4)));
        Assert.Empty(session.ApplyInterruption(recovering with { State = AgentWorkInterruptionStates.Recovering }, at.AddSeconds(5)));

        var oldTurn = session.Status.Turns!.Single(turn => turn.Id == "turn-old");
        var newTurn = session.Status.Turns!.Single(turn => turn.Id == "turn-new");
        Assert.Equal(AgentWorkInterruptionStates.Interrupted, oldTurn.Interruption!.State);
        Assert.Equal(AgentWorkInterruptionStates.Recovered, newTurn.Interruption!.State);
        Assert.Equal(2, session.Status.InterruptionHistory!.Count);
        Assert.Equal("update-1", AgentWorkInterruptionProjection.Latest(session.Status.InterruptionHistory)!.UpdateOperationId);
    }

    [Fact]
    public void StopFailureIsReplacedWithActionableRecoveryContext()
    {
        var transition = Transition(AgentWorkInterruptionStates.Interrupted, "work-1", 0, DateTimeOffset.UtcNow)
            with { StopFailure = "session.abort fetch failed: socket closed" };

        var projected = AgentWorkInterruptionProjection.Apply([], transition).Single();

        Assert.Equal(
            "The Runner could not confirm the stop before shutdown; the recorded recovery path remains active.",
            projected.StopFailure);
        Assert.DoesNotContain("session.abort", projected.StopFailure, StringComparison.Ordinal);
    }

    [Fact]
    public void UnnamedWorkHasNoProjection()
    {
        var transition = Transition(AgentWorkInterruptionStates.Interrupted, "named-work", 0, DateTimeOffset.UtcNow);

        Assert.Null(AgentWorkInterruptionProjection.Latest([transition], "other-work"));
    }

    private static AgentWorkInterruptionTransition Transition(
        string state,
        string workId,
        int generation,
        DateTimeOffset recordedAt) =>
        new(
            state,
            "update-1",
            workId,
            "task-1",
            generation,
            "turn-old",
            generation == 0 ? null : "turn-new",
            null,
            "The replacement dispatch will resume this work.",
            recordedAt);
}
