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
}
