using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Mohist.Server.SystemInfo;
using OpenTelemetry;

namespace Mohist.Server.Otel;

public readonly record struct ProcessResourceSample(
    TimeSpan TotalCpuTime,
    long WorkingSetBytes,
    long GcHeapBytes,
    int ProcessorCount);

public interface IProcessResourceReader
{
    ProcessResourceSample Read();
}

public sealed class ProcessResourceReader : IProcessResourceReader
{
    public ProcessResourceSample Read()
    {
        using var process = Process.GetCurrentProcess();
        return new(
            process.TotalProcessorTime,
            process.WorkingSet64,
            GC.GetGCMemoryInfo().HeapSizeBytes,
            Environment.ProcessorCount);
    }
}

public interface IOtelStorageProbe
{
    StorageProbeMetadata Probe();
}

public interface IOtelMaintenanceCallback
{
    Task ExecuteAsync(CancellationToken cancellationToken);
}

public readonly record struct StorageProbeMetadata(long UsageBytes);

public sealed class OtelStorageProbe : IOtelStorageProbe
{
    private readonly OtelDb _db;
    private readonly IFileSystem _fileSystem;

    public OtelStorageProbe(OtelDb db, IFileSystem fileSystem)
    {
        _db = db;
        _fileSystem = fileSystem;
    }

    public StorageProbeMetadata Probe()
    {
        using var connection = _db.OpenReadinessConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA schema_version;";
        _ = command.ExecuteScalar();

        var database = _fileSystem.GetFileLength(_db.DatabasePath) ?? 0;
        var wal = _fileSystem.GetFileLength(_db.DatabasePath + "-wal") ?? 0;
        var shm = _fileSystem.GetFileLength(_db.DatabasePath + "-shm") ?? 0;
        return new StorageProbeMetadata(checked(database + wal + shm));
    }
}

public sealed class OtelDiagnosticsSampler : IHostedService
{
    private readonly RuntimeObservability _runtime;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IProcessResourceReader _processReader;
    private readonly IOtelStorageProbe _storageProbe;
    private readonly TimeProvider _timeProvider;
    private readonly bool _enabled;
    private readonly IReadOnlyList<IOtelMaintenanceCallback> _maintenanceCallbacks;
    private readonly object _gate = new();
    private Task? _loop;
    private CancellationTokenSource? _stop;
    private TimeSpan _lastProcessCpu;
    private long _lastProcessTimestamp;
    private bool _hasProcessBaseline;
    private readonly Queue<(long Timestamp, long Usage)> _growth = new();

    public OtelDiagnosticsSampler(
        RuntimeObservability runtime,
        IHostApplicationLifetime lifetime,
        IProcessResourceReader processReader,
        IOtelStorageProbe storageProbe,
        TimeProvider timeProvider,
        Microsoft.Extensions.Options.IOptions<OtelOptions> options,
        IEnumerable<IOtelMaintenanceCallback> maintenanceCallbacks)
        : this(runtime, lifetime, processReader, storageProbe, timeProvider, options.Value.Enabled, maintenanceCallbacks)
    {
    }

    public OtelDiagnosticsSampler(
        RuntimeObservability runtime,
        IHostApplicationLifetime lifetime,
        IProcessResourceReader processReader,
        IOtelStorageProbe storageProbe,
        TimeProvider timeProvider,
        bool enabled,
        IEnumerable<IOtelMaintenanceCallback>? maintenanceCallbacks = null)
    {
        _runtime = runtime;
        _lifetime = lifetime;
        _processReader = processReader;
        _storageProbe = storageProbe;
        _timeProvider = timeProvider;
        _enabled = enabled;
        _maintenanceCallbacks = maintenanceCallbacks?.ToArray() ?? [];
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SampleProcess();
        _loop = RunAsync(_stop.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stop?.Cancel();
        if (_loop is not null)
            await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken stopping)
    {
        using var started = _lifetime.ApplicationStarted.Register(() => { });
        try
        {
            await WaitForApplicationStartedAsync(stopping).ConfigureAwait(false);
            if (_enabled)
            {
                SampleStorage();
                await RunMaintenanceAsync(stopping).ConfigureAwait(false);
            }

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10), _timeProvider);
            while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false))
            {
                SampleProcess();
                if (_enabled)
                {
                    SampleStorage();
                    await RunMaintenanceAsync(stopping).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    private async Task WaitForApplicationStartedAsync(CancellationToken stopping)
    {
        if (_lifetime.ApplicationStarted.IsCancellationRequested)
            return;

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var startedRegistration = _lifetime.ApplicationStarted.Register(static state =>
            ((TaskCompletionSource)state!).TrySetResult(), started);
        using var stoppingRegistration = stopping.Register(static state =>
            ((TaskCompletionSource)state!).TrySetCanceled(), started);
        await started.Task.ConfigureAwait(false);
    }

    private void SampleProcess()
    {
        try
        {
            var sample = _processReader.Read();
            if (sample.ProcessorCount <= 0)
                throw new InvalidOperationException("Processor count must be positive.");

            var timestamp = _timeProvider.GetTimestamp();
            double? utilization = null;
            if (_hasProcessBaseline)
            {
                var elapsed = _timeProvider.GetElapsedTime(_lastProcessTimestamp, timestamp).TotalSeconds;
                var cpu = (sample.TotalCpuTime - _lastProcessCpu).TotalSeconds;
                utilization = elapsed > 0 ? Math.Clamp(cpu / (elapsed * sample.ProcessorCount), 0, 1) : null;
            }
            _lastProcessCpu = sample.TotalCpuTime;
            _lastProcessTimestamp = timestamp;
            _hasProcessBaseline = true;
            _runtime.PublishProcess(ProcessSampleResult.Success(
                sample.TotalCpuTime,
                sample.WorkingSetBytes,
                sample.GcHeapBytes,
                sample.ProcessorCount,
                utilization));
        }
        catch (Exception ex)
        {
            _hasProcessBaseline = false;
            _runtime.PublishProcess(ProcessSampleResult.Failure(ex.Message));
        }
    }

    private void SampleStorage()
    {
        try
        {
            var now = _timeProvider.GetTimestamp();
            StorageProbeMetadata sample;
            using (var suppression = SuppressInstrumentationScope.Begin())
            {
                sample = _storageProbe.Probe();
            }
            lock (_gate)
            {
                _growth.Enqueue((now, sample.UsageBytes));
                while (_growth.Count > 7)
                    _growth.Dequeue();
            }

            (double? growth, double? window) = (null, null);
            lock (_gate)
            {
                if (_growth.Count >= 2)
                {
                    var first = _growth.Peek();
                    var last = _growth.Last();
                    var seconds = _timeProvider.GetElapsedTime(first.Timestamp, last.Timestamp).TotalSeconds;
                    if (seconds > 0)
                    {
                        growth = (last.Usage - first.Usage) / seconds;
                        window = seconds;
                    }
                }
            }
            _runtime.PublishStorage(StorageProbeResult.Success(sample.UsageBytes, growth, window));
        }
        catch (Exception ex)
        {
            lock (_gate)
                _growth.Clear();
            _runtime.PublishStorage(StorageProbeResult.Failure(ex.Message));
        }
    }

    private async Task RunMaintenanceAsync(CancellationToken stopping)
    {
        foreach (var callback in _maintenanceCallbacks)
        {
            try
            {
                using var suppression = SuppressInstrumentationScope.Begin();
                await callback.ExecuteAsync(stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }
    }
}
