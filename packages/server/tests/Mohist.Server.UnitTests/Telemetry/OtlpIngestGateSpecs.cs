using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Otel;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.UnitTests.Telemetry;

[Collection("MohistDb")]
public class OtlpIngestGateSpecs
{
    private readonly MohistDbFixture _fixture;

    public OtlpIngestGateSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    private OtlpIngestGate Gate => _fixture.Services.GetRequiredService<OtlpIngestGate>();

    [Fact]
    public void FourAdmissions_AreAllAdmitted()
    {
        var gate = Gate;
        try
        {
            for (var i = 0; i < OtlpIngestGate.RequestLeaseLimit; i++)
            {
                var decision = gate.TryAcquireRequestLease();

                Assert.True(decision.Admitted);
                Assert.Equal(i + 1, gate.RequestLeasesInUse);
            }
        }
        finally
        {
            ReleaseHeldLeases(gate);
        }
    }

    [Fact]
    public void FifthAdmission_IsRejectedWithRetryAfterOne()
    {
        var gate = Gate;
        try
        {
            AcquireAllLeases(gate);

            var fifth = gate.TryAcquireRequestLease();

            Assert.False(fifth.Admitted);
            Assert.Equal(OtlpIngestGate.TemporaryAdmissionRetryAfterSeconds, fifth.RetryAfterSeconds);
            Assert.Equal(OtlpIngestGate.RequestLeaseLimit, gate.RequestLeasesInUse);
        }
        finally
        {
            ReleaseHeldLeases(gate);
        }
    }

    [Fact]
    public void ReleaseAllLeases_RestoresAdmissionCapacity()
    {
        var gate = Gate;
        try
        {
            AcquireAllLeases(gate);

            for (var i = 0; i < OtlpIngestGate.RequestLeaseLimit; i++)
                gate.ReleaseRequestLease();

            Assert.Equal(0, gate.RequestLeasesInUse);
            Assert.True(gate.TryAcquireRequestLease().Admitted);
            gate.ReleaseRequestLease();
        }
        finally
        {
            ReleaseHeldLeases(gate);
        }
    }

    [Fact]
    public void ReleaseAfterExceptionPath_LetsThroughNewRequest()
    {
        var gate = Gate;
        // The route releases its lease in a finally block even when the
        // admitted request fails; the gate must then admit new work.
        Assert.True(gate.TryAcquireRequestLease().Admitted);
        gate.ReleaseRequestLease();

        Assert.True(gate.TryAcquireRequestLease().Admitted);
        gate.ReleaseRequestLease();
    }

    [Fact]
    public void RejectedAdmission_PublishesNoRuntimeObservabilityOutcome()
    {
        var gate = Gate;
        var runtime = _fixture.Services.GetRequiredService<RuntimeObservability>();
        try
        {
            var before = runtime.GetSnapshot().Telemetry;
            AcquireAllLeases(gate);

            Assert.False(gate.TryAcquireRequestLease().Admitted);

            var after = runtime.GetSnapshot().Telemetry;
            Assert.Equal(before.ReceivedSpans, after.ReceivedSpans);
            Assert.Equal(before.SavedSpans, after.SavedSpans);
            Assert.Equal(before.RejectedSpans, after.RejectedSpans);
            Assert.Equal(before.DroppedSpans, after.DroppedSpans);
        }
        finally
        {
            ReleaseHeldLeases(gate);
        }
    }

    [Fact]
    public void RejectedAdmission_PersistsNoRows()
    {
        var gate = Gate;
        var db = _fixture.Services.GetRequiredService<OtelDb>();
        try
        {
            var tracesBefore = CountRows(db, OtelDb.TracesTable);
            var spansBefore = CountRows(db, OtelDb.SpansTable);
            AcquireAllLeases(gate);

            Assert.False(gate.TryAcquireRequestLease().Admitted);

            Assert.Equal(tracesBefore, CountRows(db, OtelDb.TracesTable));
            Assert.Equal(spansBefore, CountRows(db, OtelDb.SpansTable));
        }
        finally
        {
            ReleaseHeldLeases(gate);
        }
    }

    private static void AcquireAllLeases(OtlpIngestGate gate)
    {
        for (var i = 0; i < OtlpIngestGate.RequestLeaseLimit; i++)
            Assert.True(gate.TryAcquireRequestLease().Admitted);
    }

    private static void ReleaseHeldLeases(OtlpIngestGate gate)
    {
        while (gate.RequestLeasesInUse > 0)
            gate.ReleaseRequestLease();
    }

    private static long CountRows(OtelDb db, string table)
    {
        using var connection = db.OpenReadOnlyConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)cmd.ExecuteScalar()!;
    }
}
