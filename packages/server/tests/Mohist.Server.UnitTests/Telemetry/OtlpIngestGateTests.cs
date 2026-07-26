using Mohist.Server.Otel;
using Xunit;

namespace Mohist.Server.UnitTests.Telemetry;

public sealed class OtlpIngestGateTests
{
    [Fact]
    public void AcquireLease_BelowLimit_AlwaysAdmitted()
    {
        var gate = new OtlpIngestGate();
        for (var i = 0; i < OtlpIngestGate.RequestLeaseLimit; i++)
        {
            var decision = gate.TryAcquireRequestLease();
            Assert.True(decision.Admitted);
        }
        Assert.Equal(OtlpIngestGate.RequestLeaseLimit, gate.RequestLeasesInUse);
    }

    [Fact]
    public void AcquireLease_AtLimit_ReturnsRejectedWithRetryAfter()
    {
        var gate = new OtlpIngestGate();
        for (var i = 0; i < OtlpIngestGate.RequestLeaseLimit; i++)
            gate.TryAcquireRequestLease();

        var decision = gate.TryAcquireRequestLease();
        Assert.False(decision.Admitted);
        Assert.Equal(OtlpIngestGate.TemporaryAdmissionRetryAfterSeconds, decision.RetryAfterSeconds);
    }

    [Fact]
    public void ReleaseLease_DecrementsLeasesInUse()
    {
        var gate = new OtlpIngestGate();
        for (var i = 0; i < OtlpIngestGate.RequestLeaseLimit; i++)
            gate.TryAcquireRequestLease();

        gate.ReleaseRequestLease();
        Assert.Equal(OtlpIngestGate.RequestLeaseLimit - 1, gate.RequestLeasesInUse);

        var decision = gate.TryAcquireRequestLease();
        Assert.True(decision.Admitted);
    }

    [Fact]
    public void ReleaseLease_AllowsNewAdmissionAfterFullCycle()
    {
        var gate = new OtlpIngestGate();
        for (var i = 0; i < OtlpIngestGate.RequestLeaseLimit; i++)
            gate.TryAcquireRequestLease();
        Assert.False(gate.TryAcquireRequestLease().Admitted);

        for (var i = 0; i < OtlpIngestGate.RequestLeaseLimit; i++)
            gate.ReleaseRequestLease();

        for (var i = 0; i < OtlpIngestGate.RequestLeaseLimit; i++)
            Assert.True(gate.TryAcquireRequestLease().Admitted);
    }

    [Fact]
    public void ReleaseLease_WithoutHold_Throws()
    {
        var gate = new OtlpIngestGate();
        Assert.Throws<InvalidOperationException>(() => gate.ReleaseRequestLease());
    }

    [Fact]
    public async Task WriterLease_QueuesContendersAndHandsOffOneAtATime()
    {
        var gate = new OtlpIngestGate();
        var first = await gate.AcquireWriterLeaseAsync(CancellationToken.None);

        var secondTask = gate.AcquireWriterLeaseAsync(CancellationToken.None);
        var thirdTask = gate.AcquireWriterLeaseAsync(CancellationToken.None);

        Assert.False(secondTask.IsCompleted);
        Assert.False(thirdTask.IsCompleted);

        first.Dispose();
        var completed = await Task.WhenAny(secondTask, thirdTask);
        Assert.True(completed.IsCompletedSuccessfully);
        Assert.NotEqual(secondTask.IsCompleted, thirdTask.IsCompleted);

        using (await completed)
        {
            Assert.False(secondTask.IsCompleted && thirdTask.IsCompleted);
        }

        using var remaining = await (completed == secondTask ? thirdTask : secondTask);
    }

    [Fact]
    public async Task WriterLease_CancellingOneWaiterDoesNotCancelAnother()
    {
        var gate = new OtlpIngestGate();
        var holder = await gate.AcquireWriterLeaseAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        var cancelledTask = gate.AcquireWriterLeaseAsync(cancellation.Token);
        var waitingTask = gate.AcquireWriterLeaseAsync(CancellationToken.None);

        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelledTask);

        holder.Dispose();
        using var acquired = await waitingTask;
    }
}
