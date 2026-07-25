using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Otel;
using Xunit;

namespace Mohist.Server.UnitTests.HostLifecycle;

public class MohistHostRunnerTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static MohistHostPlan PrimaryPlan() =>
        MohistHostPlan.Primary(
            new RuntimeEpoch(Start),
            enabled: true,
            listenerIntent: new OtelListenerIntent("localhost", 4318));

    private static MohistHostPlan DisabledPlan() =>
        MohistHostPlan.Primary(
            new RuntimeEpoch(Start),
            enabled: false,
            listenerIntent: null);

    private static RuntimeObservability NewEnabledRuntime(FakeTimeProvider time) =>
        new(true, new RuntimeEpoch(Start), time);

    private static async Task RunToCompletion(FakeMohistHost primary)
    {
        primary.ReleaseStart();
        primary.ReleaseShutdown();
    }

    [Fact]
    public async Task PrimaryInitializationFailure_DisposesUnstartedHostAndRethrows()
    {
        var primary = new FakeMohistHost("primary");
        var factory = new FakeMohistHostFactory(_ => primary);
        var classifier = new FakeOtelBindFailureClassifier();
        var initializer = new FakeMohistDatabaseInitializer()
            .EnqueueFailure(new InvalidOperationException("db boom"));
        var runner = new MohistHostRunner(factory, classifier, initializer);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(PrimaryPlan(), CancellationToken.None));
        Assert.Equal("db boom", ex.Message);

        Assert.False(primary.Started);
        Assert.True(primary.Disposed);
        Assert.Empty(factory.AlternateHosts);
        Assert.Single(initializer.Invocations);
    }

    [Fact]
    public async Task PrimaryInitFailure_WithDisposeError_AggregatesWithInitializationFirst()
    {
        var primary = new FakeMohistHost("primary")
        {
            DisposeError = new InvalidOperationException("dispose boom"),
        };
        var factory = new FakeMohistHostFactory(_ => primary);
        var classifier = new FakeOtelBindFailureClassifier();
        var initializer = new FakeMohistDatabaseInitializer()
            .EnqueueFailure(new InvalidCastException("db boom"));
        var runner = new MohistHostRunner(factory, classifier, initializer);

        var aggregate = await Assert.ThrowsAsync<AggregateException>(
            () => runner.RunAsync(PrimaryPlan(), CancellationToken.None));

        Assert.Equal(2, aggregate.InnerExceptions.Count);
        Assert.IsType<InvalidCastException>(aggregate.InnerExceptions[0]);
        Assert.IsType<InvalidOperationException>(aggregate.InnerExceptions[1]);
        Assert.Equal("db boom", aggregate.InnerExceptions[0].Message);
        Assert.Equal("dispose boom", aggregate.InnerExceptions[1].Message);
        Assert.False(primary.Started);
        Assert.True(primary.Disposed);
        Assert.Empty(factory.AlternateHosts);
    }

    [Fact]
    public async Task ClassifiedBindFailure_WithDualFailure_PreservesStopAndDisposeOrder()
    {
        var primary = new FakeMohistHost("primary")
        {
            StartError = new IOException("Failed to bind to address http://127.0.0.1:4318: address already in use."),
            StopError = new InvalidOperationException("stop boom"),
            DisposeError = new InvalidCastException("dispose boom"),
        };
        var factory = new FakeMohistHostFactory(_ => primary);
        var classifier = new FakeOtelBindFailureClassifier
        {
            Result = CollectorResult.BindFailed(),
        };
        var initializer = new FakeMohistDatabaseInitializer();
        var runner = new MohistHostRunner(factory, classifier, initializer);

        var aggregate = await Assert.ThrowsAsync<AggregateException>(
            () => runner.RunAsync(PrimaryPlan(), CancellationToken.None));

        Assert.Equal(3, aggregate.InnerExceptions.Count);
        Assert.IsType<IOException>(aggregate.InnerExceptions[0]);
        Assert.Equal("stop boom", aggregate.InnerExceptions[1].Message);
        Assert.Equal("dispose boom", aggregate.InnerExceptions[2].Message);
        Assert.Empty(factory.AlternateHosts);
    }

    [Fact]
    public async Task ClassifiedBindFailure_OnlyStopError_AggregatesStopAndStartup()
    {
        var primary = new FakeMohistHost("primary")
        {
            StartError = new IOException("Failed to bind to address http://127.0.0.1:4318: address already in use."),
            StopError = new InvalidOperationException("stop boom"),
        };
        var factory = new FakeMohistHostFactory(_ => primary);
        var classifier = new FakeOtelBindFailureClassifier
        {
            Result = CollectorResult.BindFailed(),
        };
        var initializer = new FakeMohistDatabaseInitializer();
        var runner = new MohistHostRunner(factory, classifier, initializer);

        var aggregate = await Assert.ThrowsAsync<AggregateException>(
            () => runner.RunAsync(PrimaryPlan(), CancellationToken.None));

        Assert.Equal(2, aggregate.InnerExceptions.Count);
        Assert.IsType<IOException>(aggregate.InnerExceptions[0]);
        Assert.Equal("stop boom", aggregate.InnerExceptions[1].Message);
        Assert.Empty(factory.AlternateHosts);
    }

    [Fact]
    public async Task GenericStartupFailure_IsTerminalWithoutAlternate()
    {
        var primary = new FakeMohistHost("primary")
        {
            StartError = new InvalidOperationException("not a bind error"),
        };
        var factory = new FakeMohistHostFactory(_ => primary);
        var classifier = new FakeOtelBindFailureClassifier
        {
            Result = null,
        };
        var initializer = new FakeMohistDatabaseInitializer();
        var runner = new MohistHostRunner(factory, classifier, initializer);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(PrimaryPlan(), CancellationToken.None));
        Assert.Equal("not a bind error", ex.Message);

        Assert.True(primary.Disposed);
        Assert.Empty(factory.AlternateHosts);
        Assert.Single(initializer.Invocations);
    }

    [Fact]
    public void AlternatePlan_MirrorsEpochAndEnabledAndDropsListenerIntent()
    {
        var epoch = new RuntimeEpoch(Start);
        var primary = MohistHostPlan.Primary(
            epoch,
            enabled: true,
            listenerIntent: new OtelListenerIntent("localhost", 4318));
        var alternate = MohistHostPlan.Alternate(primary);

        Assert.Same(epoch, alternate.Epoch);
        Assert.True(alternate.Enabled);
        Assert.Null(alternate.ListenerIntent);
        Assert.False(alternate.InitialCollectorResult.IsOnline);
        Assert.Equal(
            RuntimeDegradationCodes.CollectorBindFailed,
            alternate.InitialCollectorResult.FailureCode);
    }

    [Fact]
    public void AlternateRuntimeObservability_SeedsLatestDegradationAsCollectorBindFailed()
    {
        var time = new FakeTimeProvider(Start);
        var epoch = new RuntimeEpoch(Start);
        var runtime = new RuntimeObservability(
            true,
            epoch,
            time,
            initialDegradations: new[]
            {
                RuntimeDegradationSeed.StorageUnverified(),
                RuntimeDegradationSeed.CollectorBindFailed(),
            });

        var snapshot = runtime.GetSnapshot();

        Assert.True(runtime.HasActiveDegradation(DegradationSource.StorageWrite));
        Assert.True(runtime.HasActiveDegradation(DegradationSource.Collector));
        Assert.False(snapshot.CollectorOnline);
        Assert.Equal(RuntimeState.Degraded, snapshot.Status);
        Assert.Equal(
            RuntimeDegradationCodes.CollectorBindFailed,
            snapshot.LatestDegradation!.Code);

        runtime.PublishProcess(ProcessSampleResult.Failure("process outage"));
        var snapshot2 = runtime.GetSnapshot();
        Assert.Equal(
            RuntimeDegradationCodes.ProcessReadFailed,
            snapshot2.LatestDegradation!.Code);
        Assert.True(runtime.HasActiveDegradation(DegradationSource.Collector));
    }

    [Fact]
    public void ClassifierReceivesCollectorIntentAndIgnoresExporterEndpoint()
    {
        var collectorIntent = new OtelListenerIntent("localhost", 4318);
        var plan = MohistHostPlan.Primary(new RuntimeEpoch(Start), true, collectorIntent);
        var expected = new IOException("Failed to bind to address http://127.0.0.1:4318: address already in use.");
        var classifier = new OtelBindFailureClassifier();

        var decision = classifier.Classify(expected, plan);

        Assert.NotNull(decision.Result);
        Assert.False(decision.Result!.IsOnline);
        Assert.Equal(RuntimeDegradationCodes.CollectorBindFailed, decision.Result.FailureCode);
        Assert.Equal(expected.Message, decision.Result.FailureReason);
    }
}
