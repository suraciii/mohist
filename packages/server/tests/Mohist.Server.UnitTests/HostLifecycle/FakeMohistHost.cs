using System.Collections.Concurrent;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Otel;

namespace Mohist.Server.UnitTests.HostLifecycle;

/// <summary>
/// Test double for <see cref="IMohistHost"/>. Lifecycle methods block
/// on <see cref="TaskCompletionSource"/> instances until the test
/// releases them, allowing deterministic ordering without wall-clock
/// waits or production-silo coordination.
/// </summary>
public sealed class FakeMohistHost : IMohistHost
{
    private readonly TaskCompletionSource _started = NewTcs();
    private readonly TaskCompletionSource _stopped = NewTcs();
    private readonly TaskCompletionSource _shutDown = NewTcs();
    private readonly ConcurrentQueue<string> _events = new();
    private readonly ConcurrentQueue<Exception> _disposalErrors = new();
    private int _disposed;

    public FakeMohistHost(string name, IServiceProvider? services = null)
    {
        Name = name;
        Services = services ?? new FakeServiceProvider();
    }

    public string Name { get; }
    public IServiceProvider Services { get; set; }
    public bool Started { get; private set; }
    public bool Stopped { get; private set; }
    public bool Disposed { get; private set; }

    public IReadOnlyCollection<string> Events => _events.ToArray();
    public IReadOnlyCollection<Exception> DisposalErrors => _disposalErrors.ToArray();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (StartError is { } startError)
            return Task.FromException(startError);
        return AwaitWithCancellationAsync(_started.Task, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (StopError is { } stopError)
            return Task.FromException(stopError);
        return AwaitWithCancellationAsync(_stopped.Task, cancellationToken);
    }

    public Task WaitForShutdownAsync(CancellationToken cancellationToken)
    {
        if (ShutdownError is { } shutdownError)
            return Task.FromException(shutdownError);
        return AwaitWithCancellationAsync(_shutDown.Task, cancellationToken);
    }

    public Exception? ShutdownError { get; set; }

    private static async Task AwaitWithCancellationAsync(Task task, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        var completed = await Task.WhenAny(task, tcs.Task).ConfigureAwait(false);
        if (completed != task)
            cancellationToken.ThrowIfCancellationRequested();
        await task.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(CompleteDisposeAsync());
    }

    private async Task CompleteDisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        try
        {
            // Allow fake hosts to throw on dispose when tests demand it.
            if (DisposeError is { } disposeError)
                throw disposeError;
        }
        catch (Exception ex)
        {
            _disposalErrors.Enqueue(ex);
            throw;
        }
        finally
        {
            Disposed = true;
            _events.Enqueue($"{Name}:disposed");
        }
    }

    public Action? StartAction { get; set; }
    public Exception? StartError { get; set; }
    public Action? StopAction { get; set; }
    public Exception? StopError { get; set; }
    public Exception? DisposeError { get; set; }
    public Action? DisposeAction { get; set; }

    public void ReleaseStart()
    {
        _events.Enqueue($"{Name}:release_start");
        if (StartError is { } startError)
            _started.TrySetException(startError);
        else
        {
            StartAction?.Invoke();
            Started = true;
            _started.TrySetResult();
        }
    }

    public void ReleaseStop()
    {
        _events.Enqueue($"{Name}:release_stop");
        if (StopError is { } stopError)
            _stopped.TrySetException(stopError);
        else
        {
            StopAction?.Invoke();
            Stopped = true;
            _stopped.TrySetResult();
        }
    }

    public void ReleaseShutdown()
    {
        _events.Enqueue($"{Name}:release_shutdown");
        _shutDown.TrySetResult();
    }

    public void FailStart(Exception exception) =>
        _started.TrySetException(exception);

    public void FailStop(Exception exception) =>
        _stopped.TrySetException(exception);

    public void FailShutdown(Exception exception) =>
        _shutDown.TrySetException(exception);

    private static TaskCompletionSource NewTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Lightweight <see cref="IServiceProvider"/> for fakes. Supports
/// registering singletons so <see cref="MohistHostRunner"/> can resolve
/// <see cref="RuntimeObservability"/> from the alternate's services.
/// </summary>
public sealed class FakeServiceProvider : IServiceProvider
{
    private readonly Dictionary<Type, object> _instances = new();

    public FakeServiceProvider Register<T>(T instance) where T : class
    {
        _instances[typeof(T)] = instance;
        return this;
    }

    public object? GetService(Type serviceType)
    {
        if (_instances.TryGetValue(serviceType, out var instance))
            return instance;
        return null;
    }
}
