using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Otel;
using Xunit;

namespace Mohist.Server.UnitTests.Telemetry;

public class RuntimeObservabilityTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DefaultEnabledStateStartsWithIndependentCollectorAndWriteSources()
    {
        var time = new FakeTimeProvider(Start);
        using var runtime = new RuntimeObservability(true, new RuntimeEpoch(Start), time);

        var snapshot = runtime.GetSnapshot();

        Assert.Equal(RuntimeState.Degraded, snapshot.Status);
        Assert.False(snapshot.CollectorOnline);
        Assert.Equal(RuntimeDegradationCodes.StorageUnverified, snapshot.LatestDegradation!.Code);
        Assert.True(runtime.HasActiveDegradation(DegradationSource.Collector));
        Assert.True(runtime.HasActiveDegradation(DegradationSource.StorageWrite));
        Assert.False(runtime.HasActiveDegradation(DegradationSource.StorageRead));
    }

    [Fact]
    public void SourceOwnedSuccessCannotClearAnotherSource()
    {
        var time = new FakeTimeProvider(Start);
        using var runtime = HealthyRuntime(time);

        runtime.PublishProcess(ProcessSampleResult.Failure("process"));
        runtime.PublishStorage(StorageProbeResult.Failure("storage"));
        runtime.PublishCollector(CollectorResult.BindFailed("collector"));

        runtime.PublishProcess(ProcessSampleResult.Success(TimeSpan.FromSeconds(1), 10, 20, 2));
        runtime.PublishStorage(StorageProbeResult.Success(100));
        var snapshot = runtime.GetSnapshot();

        Assert.Equal(RuntimeState.Degraded, snapshot.Status);
        Assert.True(runtime.HasActiveDegradation(DegradationSource.Collector));
        Assert.False(runtime.HasActiveDegradation(DegradationSource.ProcessRead));
        Assert.False(runtime.HasActiveDegradation(DegradationSource.StorageRead));
        Assert.Equal(RuntimeDegradationCodes.CollectorBindFailed, snapshot.LatestDegradation!.Code);
    }

    [Fact]
    public void EachPublicationMethodOnlyChangesItsOwnedSource()
    {
        var time = new FakeTimeProvider(Start);
        using var runtime = HealthyRuntime(time);

        runtime.PublishCollector(CollectorResult.BindFailed());
        runtime.PublishProcess(ProcessSampleResult.Failure());
        runtime.PublishStorage(StorageProbeResult.Failure());
        runtime.RecordIngest(IngestOutcomeBuilder.Build(
            new ClassifiedBatchTotals(1, 0, 0, 0),
            IngestWriteResult.RolledBack("write")));

        runtime.PublishProcess(ProcessSampleResult.Success(TimeSpan.FromSeconds(1), 1, 2, 1));
        runtime.PublishStorage(StorageProbeResult.Success(10));
        runtime.PublishCollector(CollectorResult.Online());

        Assert.False(runtime.HasActiveDegradation(DegradationSource.ProcessRead));
        Assert.False(runtime.HasActiveDegradation(DegradationSource.StorageRead));
        Assert.False(runtime.HasActiveDegradation(DegradationSource.Collector));
        Assert.True(runtime.HasActiveDegradation(DegradationSource.StorageWrite));
    }

    [Fact]
    public void RequestAndAgentFactsNormalizeAllMetricDimensions()
    {
        var time = new FakeTimeProvider(Start);
        using var runtime = HealthyRuntime(time);

        var request = runtime.CompleteRequest(
            " https://example.test/projects/proj_123 ",
            " trace ",
            600,
            double.NaN,
            -1,
            -2);
        runtime.RecordAgentPath(" agent.unknown ", -1, -1, -1);

        Assert.Equal("unmatched", request.Route);
        Assert.Equal("OTHER", request.Method);
        Assert.Equal(0, request.StatusCode);
        Assert.Equal(0, request.DurationMilliseconds);
        Assert.Equal(0, request.DatabaseCalls);
        Assert.Equal(0, request.DownstreamCalls);
    }

    [Fact]
    public void CollectorAndProductionWriteRecoveryReachHealthyIndependently()
    {
        var time = new FakeTimeProvider(Start);
        using var runtime = HealthyRuntime(time);

        runtime.PublishCollector(CollectorResult.BindFailed());
        runtime.PublishStorage(StorageProbeResult.Failure());
        runtime.PublishCollector(CollectorResult.Online());

        Assert.Equal(RuntimeState.Degraded, runtime.GetSnapshot().Status);
        Assert.True(runtime.HasActiveDegradation(DegradationSource.StorageRead));

        var outcome = IngestOutcomeBuilder.Build(
            new ClassifiedBatchTotals(1, 0, 0, 0),
            IngestWriteResult.Committed());
        runtime.RecordIngest(outcome);
        runtime.PublishStorage(StorageProbeResult.Success(42, 2, 10));

        var snapshot = runtime.GetSnapshot();
        Assert.Equal(RuntimeState.Healthy, snapshot.Status);
        Assert.True(snapshot.CollectorOnline);
        Assert.Equal(42, snapshot.Storage.UsageBytes);
        Assert.Equal(2, snapshot.Storage.GrowthBytesPerSecond);
    }

    [Fact]
    public void ProtectionRefreshesUntilFiveMinutesAfterLatestLoss()
    {
        var transitions = new List<RuntimeStateTransition>();
        var time = new FakeTimeProvider(Start);
        using var runtime = HealthyRuntime(time, transitions);
        var rejected = IngestOutcomeBuilder.Build(
            new ClassifiedBatchTotals(0, 1, 0, 0),
            IngestWriteResult.NotAttempted());
        var dropped = IngestOutcomeBuilder.Build(
            new ClassifiedBatchTotals(0, 0, 1, 0),
            IngestWriteResult.NotAttempted());

        runtime.RecordIngest(rejected);
        time.Advance(TimeSpan.FromMinutes(4));
        runtime.RecordIngest(dropped);
        time.Advance(TimeSpan.FromMinutes(4).Add(TimeSpan.FromSeconds(59)));
        Assert.Equal(RuntimeState.Degraded, runtime.GetSnapshot().Status);
        Assert.Single(transitions);

        time.Advance(TimeSpan.FromSeconds(1));
        var recovered = runtime.GetSnapshot();

        Assert.Equal(RuntimeState.Healthy, recovered.Status);
        Assert.Equal(2, transitions.Count);
        Assert.Equal(RuntimeDegradationCodes.TelemetryDropped, recovered.LatestDegradation!.Code);
        Assert.Equal(RuntimeState.Healthy, transitions[0].PreviousState);
        Assert.Equal(RuntimeState.Healthy, transitions[1].NewState);
    }

    [Fact]
    public void SeedOrderAndLaterActivationDetermineLatestReason()
    {
        var time = new FakeTimeProvider(Start);
        var seeds = new[]
        {
            RuntimeDegradationSeed.StorageUnverified(),
            RuntimeDegradationSeed.CollectorBindFailed(),
        };
        using var runtime = new RuntimeObservability(
            true,
            new RuntimeEpoch(Start),
            time,
            initialDegradations: seeds);

        Assert.Equal(RuntimeDegradationCodes.CollectorBindFailed, runtime.GetSnapshot().LatestDegradation!.Code);

        runtime.PublishProcess(ProcessSampleResult.Failure(new string('x', 300)));
        var snapshot = runtime.GetSnapshot();

        Assert.Equal(RuntimeDegradationCodes.ProcessReadFailed, snapshot.LatestDegradation!.Code);
        Assert.Equal(256, snapshot.LatestDegradation.Message.Length);
    }

    [Fact]
    public void OffStateKeepsProcessFailureVisibleWithoutBecomingDegraded()
    {
        var time = new FakeTimeProvider(Start);
        using var runtime = new RuntimeObservability(false, new RuntimeEpoch(Start), time);

        runtime.PublishProcess(ProcessSampleResult.Failure("unavailable"));
        var snapshot = runtime.GetSnapshot();

        Assert.Equal(RuntimeState.Off, snapshot.Status);
        Assert.Equal(RuntimeDegradationCodes.ProcessReadFailed, snapshot.LatestDegradation!.Code);
        Assert.Null(snapshot.Process.CpuUtilization);
        Assert.Null(snapshot.Process.WorkingSetBytes);
        Assert.Null(snapshot.Process.GcHeapBytes);
    }

    [Fact]
    public void SnapshotCopiesStateAndDoesNotChangeAfterLaterPublication()
    {
        var time = new FakeTimeProvider(Start);
        using var runtime = HealthyRuntime(time);
        runtime.PublishProcess(ProcessSampleResult.Success(TimeSpan.FromSeconds(1), 10, 20, 2, .25));
        var first = runtime.GetSnapshot();

        runtime.PublishProcess(ProcessSampleResult.Failure());
        var second = runtime.GetSnapshot();

        Assert.Equal(.25, first.Process.CpuUtilization);
        Assert.Equal(10, first.Process.WorkingSetBytes);
        Assert.Equal(20, first.Process.GcHeapBytes);
        Assert.Null(second.Process.CpuUtilization);
        Assert.Empty(first.Routes);
    }

    [Fact]
    public void RuntimeEpochCanBeSharedByMultipleAuthorities()
    {
        var time = new FakeTimeProvider(Start);
        var epoch = RuntimeEpoch.Capture(time);
        using var first = HealthyRuntime(time, epoch: epoch);
        using var second = HealthyRuntime(time, epoch: epoch);

        Assert.Same(epoch, first.Epoch);
        Assert.Same(epoch, second.Epoch);
        Assert.Equal(first.Since, second.Since);
    }

    private static RuntimeObservability HealthyRuntime(
        FakeTimeProvider time,
        List<RuntimeStateTransition>? transitions = null,
        RuntimeEpoch? epoch = null) =>
        new(
            true,
            epoch ?? new RuntimeEpoch(Start),
            time,
            initialDegradations: Array.Empty<RuntimeDegradationSeed>(),
            transitionSink: transitions is null ? null : transitions.Add);
}
