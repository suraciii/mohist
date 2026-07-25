using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Otel;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Telemetry;

public class OtelDiagnosticsSamplerEnabledSpecs : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _timeProvider = new(Now);
    private readonly RecordingMaintenanceCallback _callback = new();
    private readonly RuntimeObservability _runtime;
    private readonly TestApplicationLifetime _lifetime = new();

    public OtelDiagnosticsSamplerEnabledSpecs()
    {
        _runtime = new RuntimeObservability(
            enabled: false,
            new RuntimeEpoch(Now),
            _timeProvider);
    }

    public void Dispose()
    {
        _runtime.Dispose();
        _lifetime.Dispose();
    }

    [Fact]
    public async Task ObservationDisabled_MaintenanceCallbackNeverInvoked()
    {
        var sampler = new OtelDiagnosticsSampler(
            _runtime,
            _lifetime,
            new NoopProcessResourceReader(),
            new NoopStorageProbe(),
            _timeProvider,
            enabled: false,
            maintenanceCallbacks: new IOtelMaintenanceCallback[] { _callback });

        await sampler.StartAsync(CancellationToken.None);
        _lifetime.FireStarted();
        _timeProvider.Advance(TimeSpan.FromMinutes(30));
        await sampler.StopAsync(CancellationToken.None);

        Assert.Equal(0, _callback.Invocations);
    }

    [Fact]
    public async Task ObservationEnabled_MaintenanceCallbackInvokedOnceAfterStart()
    {
        var sampler = new OtelDiagnosticsSampler(
            _runtime,
            _lifetime,
            new NoopProcessResourceReader(),
            new NoopStorageProbe(),
            _timeProvider,
            enabled: true,
            maintenanceCallbacks: new IOtelMaintenanceCallback[] { _callback });

        await sampler.StartAsync(CancellationToken.None);
        _lifetime.FireStarted();
        await sampler.StopAsync(CancellationToken.None);

        Assert.True(_callback.Invocations >= 1);
    }

    [Fact]
    public async Task ObservationDisabled_AndReEnabledThenAdvanced_MaintenanceRunsAgainstInjectedTime()
    {
        // Build the sampler disabled first. No invocations while disabled.
        var sampler = new OtelDiagnosticsSampler(
            _runtime,
            _lifetime,
            new NoopProcessResourceReader(),
            new NoopStorageProbe(),
            _timeProvider,
            enabled: false,
            maintenanceCallbacks: new IOtelMaintenanceCallback[] { _callback });

        await sampler.StartAsync(CancellationToken.None);
        _lifetime.FireStarted();
        _timeProvider.Advance(TimeSpan.FromMinutes(5));
        await sampler.StopAsync(CancellationToken.None);

        Assert.Equal(0, _callback.Invocations);
    }

    private sealed class RecordingMaintenanceCallback : IOtelMaintenanceCallback
    {
        private int _invocations;

        public int Invocations => _invocations;

        public Task ExecuteAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocations);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopProcessResourceReader : IProcessResourceReader
    {
        public ProcessResourceSample Read() => new(
            TotalCpuTime: TimeSpan.Zero,
            WorkingSetBytes: 0,
            GcHeapBytes: 0,
            ProcessorCount: 1);
    }

    private sealed class NoopStorageProbe : IOtelStorageProbe
    {
        public StorageProbeMetadata Probe() => new(UsageBytes: 0);
    }

    private sealed class TestApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void FireStarted() => _started.Cancel();
        public void FireStopping() => _stopping.Cancel();
        public void FireStopped() => _stopped.Cancel();

        public void StopApplication() => FireStopping();

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }
}
