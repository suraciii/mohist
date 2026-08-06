using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.UnitTests.Sessions;

public class AgentSessionPersistenceTestProbeTests
{
    [Fact]
    public async Task WaitAsync_ReturnsCycleCompletedAfterCheckpointBeforeWaitStarted()
    {
        var probe = new AgentSessionPersistenceTestProbe();
        var checkpoint = probe.Checkpoint("session-1");
        var cycleId = probe.StartCycle("session-1");
        probe.Report(new AgentSessionPersistenceResult(
            "session-1",
            cycleId,
            AgentSessionPersistenceOutcome.Succeeded));

        var result = await checkpoint.WaitAsync();

        Assert.Equal(cycleId, result.CycleId);
    }

    [Fact]
    public async Task WaitAsync_SkipsCycleAtCheckpoint()
    {
        var probe = new AgentSessionPersistenceTestProbe();
        var oldCycleId = probe.StartCycle("session-1");
        var checkpoint = probe.Checkpoint("session-1");
        probe.Report(new AgentSessionPersistenceResult(
            "session-1",
            oldCycleId,
            AgentSessionPersistenceOutcome.Succeeded));
        var nextCycleId = probe.StartCycle("session-1");
        probe.Report(new AgentSessionPersistenceResult(
            "session-1",
            nextCycleId,
            AgentSessionPersistenceOutcome.TranscriptFailed));

        var result = await checkpoint.WaitAsync();

        Assert.Equal(nextCycleId, result.CycleId);
        Assert.Equal(AgentSessionPersistenceOutcome.TranscriptFailed, result.Outcome);
    }

    [Fact]
    public async Task WaitAsync_CancelledWaiterDoesNotConsumeNextCycle()
    {
        var probe = new AgentSessionPersistenceTestProbe();
        var checkpoint = probe.Checkpoint("session-1");
        using var cancellation = new CancellationTokenSource();
        var cancelledWait = checkpoint.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait);

        var nextWait = checkpoint.WaitAsync();
        var cycleId = probe.StartCycle("session-1");
        probe.Report(new AgentSessionPersistenceResult(
            "session-1",
            cycleId,
            AgentSessionPersistenceOutcome.Succeeded));

        var result = await nextWait;

        Assert.Equal(cycleId, result.CycleId);
    }
}
