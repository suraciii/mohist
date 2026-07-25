using Mohist.Server.Otel;
using Xunit;

namespace Mohist.Server.UnitTests.Telemetry;

public class IngestOutcomeTests
{
    [Fact]
    public void CommittedBatchDerivesAllCountersAndClearsWriteSource()
    {
        var outcome = Build(
            new ClassifiedBatchTotals(3, 1, 2, 1),
            IngestWriteResult.Committed());

        Assert.Equal(IngestResponseDisposition.PartialSuccess, outcome.ResponseDisposition);
        Assert.Equal(4, outcome.Received);
        Assert.Equal(3, outcome.Saved);
        Assert.Equal(1, outcome.Rejected);
        Assert.Equal(3, outcome.Dropped);
        Assert.True(outcome.ClearsStorageWrite);
        Assert.True(outcome.ActivatesProtection);
        Assert.Equal(RuntimeDegradationCodes.TelemetryDropped, outcome.ProtectionCode);
    }

    [Fact]
    public void NotAttemptedBatchPublishesOnlyNonRetryableLoss()
    {
        var outcome = Build(
            new ClassifiedBatchTotals(0, 2, 1, 0),
            IngestWriteResult.NotAttempted());

        Assert.Equal(IngestResponseDisposition.PartialSuccess, outcome.ResponseDisposition);
        Assert.Equal(2, outcome.Received);
        Assert.Equal(0, outcome.Saved);
        Assert.Equal(2, outcome.Rejected);
        Assert.Equal(1, outcome.Dropped);
        Assert.False(outcome.ClearsStorageWrite);
        Assert.False(outcome.ActivatesStorageWrite);
    }

    [Fact]
    public void RolledBackBatchTakesPrecedenceOverProvisionalLoss()
    {
        var outcome = Build(
            new ClassifiedBatchTotals(3, 1, 2, 1),
            IngestWriteResult.RolledBack("failure"));

        Assert.Equal(IngestResponseDisposition.RetryableFailure, outcome.ResponseDisposition);
        Assert.Equal(4, outcome.Received);
        Assert.Equal(0, outcome.Saved);
        Assert.Equal(0, outcome.Rejected);
        Assert.Equal(0, outcome.Dropped);
        Assert.True(outcome.ActivatesStorageWrite);
        Assert.False(outcome.ActivatesProtection);
    }

    [Fact]
    public void CancelledBatchIsReceivedOnlyAndDoesNotChangeSources()
    {
        var outcome = Build(
            new ClassifiedBatchTotals(3, 1, 2, 1),
            IngestWriteResult.Cancelled());

        Assert.Equal(IngestResponseDisposition.Cancelled, outcome.ResponseDisposition);
        Assert.Equal(4, outcome.Received);
        Assert.Equal(0, outcome.Saved);
        Assert.Equal(0, outcome.Rejected);
        Assert.Equal(0, outcome.Dropped);
        Assert.False(outcome.ActivatesStorageWrite);
        Assert.False(outcome.ClearsStorageWrite);
        Assert.False(outcome.ActivatesProtection);
    }

    [Fact]
    public void RepeatedRollbackCannotCreateProtectionCounters()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var runtime = new RuntimeObservability(
            true,
            new RuntimeEpoch(time.GetUtcNow()),
            time,
            initialDegradations: Array.Empty<RuntimeDegradationSeed>());
        var outcome = Build(
            new ClassifiedBatchTotals(2, 1, 1, 0),
            IngestWriteResult.RolledBack("failed"));

        runtime.RecordIngest(outcome);
        runtime.RecordIngest(outcome);
        var snapshot = runtime.GetSnapshot();

        Assert.Equal(6, snapshot.ReceivedSpans);
        Assert.Equal(0, snapshot.RejectedSpans);
        Assert.Equal(0, snapshot.DroppedSpans);
        Assert.Equal(RuntimeDegradationCodes.StorageWriteFailed, snapshot.LatestDegradation!.Code);
        Assert.False(runtime.HasActiveDegradation(DegradationSource.IngestProtection));
    }

    [Fact]
    public void WriteReasonIsBoundedTo256Characters()
    {
        var outcome = Build(
            new ClassifiedBatchTotals(1, 0, 0, 0),
            IngestWriteResult.RolledBack(new string('x', 400)));

        Assert.NotNull(outcome.WriteResult.Reason);
        Assert.Equal(256, outcome.WriteResult.Reason!.Length);
    }

    [Fact]
    public void NotAttemptedCannotContainParsedForWriteAttempts()
    {
        Assert.Throws<ArgumentException>(() => Build(
            new ClassifiedBatchTotals(1, 0, 0, 0),
            IngestWriteResult.NotAttempted()));
    }

    [Fact]
    public void OutcomeHasNoPublicIndependentAggregateConstructor()
    {
        Assert.DoesNotContain(
            typeof(IngestOutcome).GetConstructors(),
            constructor => constructor.IsPublic);
    }

    [Fact]
    public void SuccessfulProcessSampleRequiresAProcessor()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProcessSampleResult.Success(TimeSpan.Zero, 1, 1, 0));
    }

    private static IngestOutcome Build(
        ClassifiedBatchTotals totals,
        IngestWriteResult writeResult) =>
        IngestOutcomeBuilder.Build(totals, writeResult);
}
