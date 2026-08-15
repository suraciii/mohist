using System.Runtime.CompilerServices;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Otel;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

internal enum DispatcherHandler
{
    CatchAll,
    Specific,
}

internal readonly record struct DispatcherDeliveryKey(
    EventOrigin Origin,
    long RowId,
    string Source,
    string Type,
    string EventId,
    DispatcherHandler Handler)
{
    internal static DispatcherDeliveryKey From(UndeliveredEvent row, DispatcherHandler handler) =>
        new(row.Origin, row.Id, row.Source, row.Type, row.EventId, handler);

    internal DispatcherEventKey EventKey => new(Source, Type, EventId, Handler);
}

internal readonly record struct DispatcherEventKey(
    string Source,
    string Type,
    string EventId,
    DispatcherHandler Handler)
{
    internal static DispatcherEventKey From(CloudEvent envelope, DispatcherHandler handler) =>
        new(envelope.Source.ToString(), envelope.Type, envelope.Id, handler);
}

internal sealed record DispatcherDeliveryAwaiter(
    Task<RecordingBackgroundTaskLauncher.PokeWork> PokeEnqueued,
    Func<UndeliveredEvent, bool> EventMatcher,
    DispatcherHandler Handler);

internal static class EventDispatcherImmediateTriggerTestSupport
{
    internal static DispatcherDeliveryAwaiter ExpectPokeDelivery(
        DispatcherFixture fixture,
        Func<UndeliveredEvent, bool> eventMatcher,
        DispatcherHandler handler)
    {
        ArgumentNullException.ThrowIfNull(eventMatcher);
        return new(fixture.BackgroundTasks.ExpectNextLaunch(), eventMatcher, handler);
    }

    internal static async Task AwaitPokeDeliveryAsync(
        DispatcherFixture fixture,
        DispatcherDeliveryAwaiter delivery)
    {
        var poke = fixture.BackgroundTasks.RequireExpectedLaunch(
            delivery.PokeEnqueued,
            "producer commit did not enqueue a dispatcher poke");
        var pending = await fixture.EventStore.ListUndeliveredAsync().ConfigureAwait(false);
        var matching = pending.Where(delivery.EventMatcher).ToArray();
        Assert.Single(matching);
        var appended = matching[0];
        var handlerDelivery = WaitForHandlerDeliveryAsync(
            fixture,
            DispatcherDeliveryKey.From(appended, delivery.Handler));

        var started = fixture.BackgroundTasks.StartAsync(poke);
        try
        {
            await poke.Started.ConfigureAwait(false);
            await Task.WhenAny(handlerDelivery, poke.Completed).ConfigureAwait(false);
            if (poke.Completed.IsCompleted && !handlerDelivery.IsCompleted)
            {
                await poke.Completed.ConfigureAwait(false);
                Assert.Fail($"Poke completed before event {appended.EventId} reached its handler.");
            }

            await handlerDelivery.ConfigureAwait(false);
            await poke.Completed.ConfigureAwait(false);
            var remaining = await fixture.EventStore.ListUndeliveredAsync().ConfigureAwait(false);
            Assert.DoesNotContain(remaining, row =>
                row.Origin == appended.Origin
                && row.Id == appended.Id
                && row.Source == appended.Source
                && row.Type == appended.Type
                && row.EventId == appended.EventId);
        }
        finally
        {
            fixture.BackgroundTasks.Release(poke);
            await started.ConfigureAwait(false);
        }
    }

    internal static Task WaitForHandlerInvocationAsync(
        DispatcherFixture fixture,
        DispatcherDeliveryKey key) =>
        fixture.DeliverySignals.WaitForInvocationAsync(key);

    internal static Task WaitForHandlerInvocationAsync(
        DispatcherDeliverySignals signals,
        DispatcherDeliveryKey key) =>
        signals.WaitForInvocationAsync(key);

    internal static Task WaitForHandlerSettlementAsync(
        DispatcherFixture fixture,
        DispatcherDeliveryKey key) =>
        fixture.DeliverySignals.WaitForSettlementAsync(key);

    internal static Task WaitForHandlerSettlementAsync(
        DispatcherDeliverySignals signals,
        DispatcherDeliveryKey key) =>
        signals.WaitForSettlementAsync(key);

    internal static Task WaitForHandlerDeliveryAsync(
        DispatcherFixture fixture,
        DispatcherDeliveryKey key) =>
        fixture.DeliverySignals.WaitAsync(key);

    internal static Task WaitForHandlerDeliveryAsync(
        DispatcherDeliverySignals signals,
        DispatcherDeliveryKey key) =>
        signals.WaitAsync(key);

    internal static void RecordHandlerInvocation(
        DispatcherFixture fixture,
        DispatcherHandler handler,
        CloudEvent envelope) =>
        fixture.DeliverySignals.RecordInvocation(DispatcherEventKey.From(envelope, handler));

    internal static void RecordHandlerInvocation(
        DispatcherDeliverySignals signals,
        DispatcherHandler handler,
        CloudEvent envelope) =>
        signals.RecordInvocation(DispatcherEventKey.From(envelope, handler));

    internal static void RecordEventSettlement(
        DispatcherFixture fixture,
        UndeliveredEvent row)
        => RecordEventSettlement(fixture.DeliverySignals, row);

    internal static void RecordEventSettlement(
        DispatcherDeliverySignals signals,
        UndeliveredEvent row)
    {
        foreach (var handler in Enum.GetValues<DispatcherHandler>())
        {
            signals.RecordSettlement(DispatcherDeliveryKey.From(row, handler));
        }
    }

    internal static void ResetHandlerDeliveries(DispatcherFixture fixture) =>
        fixture.DeliverySignals.Reset();
}

internal sealed class DispatcherDeliverySignals
{
    private readonly object _gate = new();
    private readonly Dictionary<DispatcherDeliveryKey, DeliverySignal> _signals = [];
    private readonly HashSet<DispatcherEventKey> _invocations = [];
    private readonly HashSet<DispatcherDeliveryKey> _settlements = [];

    internal Task WaitForInvocationAsync(DispatcherDeliveryKey key)
    {
        lock (_gate)
            return GetSignal(key).Invocation.Task;
    }

    internal Task WaitForSettlementAsync(DispatcherDeliveryKey key)
    {
        lock (_gate)
            return GetSignal(key).Settlement.Task;
    }

    internal Task WaitAsync(DispatcherDeliveryKey key)
    {
        lock (_gate)
            return GetSignal(key).Acknowledged.Task;
    }

    internal void RecordInvocation(DispatcherEventKey key)
    {
        lock (_gate)
        {
            _invocations.Add(key);
            foreach (var (deliveryKey, signal) in _signals)
            {
                if (deliveryKey.EventKey != key)
                    continue;
                signal.Invocation.TrySetResult();
                CompleteIfReady(signal);
            }
        }
    }

    internal void RecordSettlement(DispatcherDeliveryKey key)
    {
        lock (_gate)
        {
            _settlements.Add(key);
            if (_signals.TryGetValue(key, out var signal))
            {
                signal.Settlement.TrySetResult();
                CompleteIfReady(signal);
            }
        }
    }

    internal void Reset()
    {
        lock (_gate)
        {
            _signals.Clear();
            _invocations.Clear();
            _settlements.Clear();
        }
    }

    private DeliverySignal GetSignal(DispatcherDeliveryKey key)
    {
        if (_signals.TryGetValue(key, out var signal))
            return signal;
        if (_signals.Keys.Any(candidate => candidate.EventKey == key.EventKey))
            throw new InvalidOperationException(
                $"Event identity {key.EventKey} maps to more than one persisted dispatcher row.");

        signal = new DeliverySignal();
        if (_invocations.Contains(key.EventKey))
            signal.Invocation.TrySetResult();
        if (_settlements.Contains(key))
            signal.Settlement.TrySetResult();
        CompleteIfReady(signal);
        _signals[key] = signal;
        return signal;
    }

    private static void CompleteIfReady(DeliverySignal signal)
    {
        if (signal.Invocation.Task.IsCompletedSuccessfully
            && signal.Settlement.Task.IsCompletedSuccessfully)
            signal.Acknowledged.TrySetResult();
    }

    private sealed class DeliverySignal
    {
        internal TaskCompletionSource Invocation { get; } = NewSignal();
        internal TaskCompletionSource Settlement { get; } = NewSignal();
        internal TaskCompletionSource Acknowledged { get; } = NewSignal();

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed class RecordingBackgroundTaskLauncher : IBackgroundTaskLauncher
{
    private readonly Queue<PokeWork> _pending = [];
    private readonly HashSet<PokeWork> _claimed = [];
    private readonly HashSet<PokeWork> _running = [];
    private readonly Queue<TaskCompletionSource<PokeWork>> _launchWaiters = [];
    private readonly object _gate = new();
    private int _launchCount;
    private bool _disposed;
    private TaskCompletionSource? _drainCompletion;

    public int LaunchCount => Volatile.Read(ref _launchCount);

    public int PendingCount
    {
        get { lock (_gate) return _pending.Count; }
    }

    public Task<PokeWork> ExpectNextLaunch()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_drainCompletion is not null)
                throw new InvalidOperationException("Cannot observe a launch while the test launcher is draining.");
            if (_pending.Count != 0)
            {
                var poke = _pending.Dequeue();
                poke.Claim();
                _claimed.Add(poke);
                return Task.FromResult(poke);
            }

            var waiter = NewLaunchSignal();
            _launchWaiters.Enqueue(waiter);
            return waiter.Task;
        }
    }

    public PokeWork RequireExpectedLaunch(Task<PokeWork> expectation, string failure)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);

        TaskCompletionSource<PokeWork>? abandoned = null;
        lock (_gate)
        {
            if (expectation.IsCompleted)
                return expectation.GetAwaiter().GetResult();

            var waiterCount = _launchWaiters.Count;
            for (var index = 0; index < waiterCount; index++)
            {
                var waiter = _launchWaiters.Dequeue();
                if (abandoned is null && ReferenceEquals(waiter.Task, expectation))
                    abandoned = waiter;
                else
                    _launchWaiters.Enqueue(waiter);
            }
        }

        abandoned?.TrySetCanceled();
        throw new InvalidOperationException(failure);
    }

    public void Launch(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        PokeWork poke;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_drainCompletion is not null)
                throw new InvalidOperationException("Cannot launch work while the test launcher is draining.");
            if (cancellationToken.IsCancellationRequested)
                return;

            poke = new PokeWork(work, cancellationToken);
            if (_launchWaiters.Count != 0)
            {
                var waiter = _launchWaiters.Dequeue();
                poke.Claim();
                _claimed.Add(poke);
                waiter.TrySetResult(poke);
            }
            else
            {
                _pending.Enqueue(poke);
            }
            Interlocked.Increment(ref _launchCount);
        }

    }

    public Task StartAsync(PokeWork poke)
    {
        ArgumentNullException.ThrowIfNull(poke);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_claimed.Remove(poke) || !poke.TryStart())
                throw new InvalidOperationException("The requested poke is not owned by this test.");
            _running.Add(poke);
        }

        return RunAsync(poke);
    }

    private async Task RunAsync(PokeWork poke)
    {
        try
        {
            await poke.ExecuteAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
                _running.Remove(poke);
            await poke.DisposeCancellationAsync().ConfigureAwait(false);
            poke.SignalRunCompleted();
        }
    }

    public void Release(PokeWork poke)
    {
        ArgumentNullException.ThrowIfNull(poke);
        lock (_gate)
        {
            if (!_running.Contains(poke) && !poke.Completed.IsCompleted)
                throw new InvalidOperationException("The requested poke is not an active test-owned callback.");
            poke.ReleaseByOwner();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            if (_pending.Count != 0
                || _claimed.Count != 0
                || _running.Count != 0
                || _launchWaiters.Count != 0
                || _drainCompletion is not null)
                throw new InvalidOperationException("Cannot reset while poke work is outstanding.");
            Interlocked.Exchange(ref _launchCount, 0);
        }
    }

    public ValueTask DrainAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_drainCompletion is not null)
                return new(_drainCompletion.Task);
            completion = NewCompletionSignal();
            _drainCompletion = completion;
        }

        _ = CompleteDrainAsync(completion);
        return new(completion.Task);
    }

    private async Task CompleteDrainAsync(TaskCompletionSource completion)
    {
        try
        {
            await DrainCoreAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_drainCompletion, completion))
                    _drainCompletion = null;
            }
        }
    }

    private async Task DrainCoreAsync()
    {
        PokeWork[] pending;
        PokeWork[] claimed;
        PokeWork[] running;
        TaskCompletionSource<PokeWork>[] waiters;
        lock (_gate)
        {
            pending = _pending.ToArray();
            _pending.Clear();
            claimed = _claimed.ToArray();
            _claimed.Clear();
            running = _running.ToArray();
            waiters = _launchWaiters.ToArray();
            _launchWaiters.Clear();
        }

        foreach (var waiter in waiters)
            waiter.TrySetCanceled();

        foreach (var poke in pending)
            await poke.CancelBeforeStartAsync().ConfigureAwait(false);
        foreach (var poke in claimed)
            await poke.CancelBeforeStartAsync().ConfigureAwait(false);
        foreach (var poke in running)
        {
            var callbackStarted = poke.IsCallbackStarted;
            poke.Cancel();
            if (!callbackStarted)
                poke.CancelBeforeCallback();
        }

        var unreleased = running
            .Where(poke => poke.IsCallbackStarted && !poke.Completed.IsCompleted && !poke.IsReleased)
            .ToArray();
        if (unreleased.Length != 0)
        {
            throw new InvalidOperationException(
                $"Cannot drain {unreleased.Length} non-cooperative callback(s); "
                + $"release test-owned work before requesting drain again: {string.Join(", ", unreleased.Select(poke => poke.Id))}.");
        }

        await Task.WhenAll(running.Select(poke => poke.RunCompleted)).ConfigureAwait(false);
        foreach (var poke in running)
        {
            try
            {
                await poke.Completed.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation requested by teardown is an expected settled outcome.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
            _disposed = true;

        await DrainAsync().ConfigureAwait(false);
    }

    private static TaskCompletionSource<PokeWork> NewLaunchSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource NewCompletionSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public sealed class PokeWork
    {
        private readonly Func<CancellationToken, Task> _work;
        private readonly CancellationTokenSource _cancellation;
        private readonly object _lifecycleGate = new();
        private readonly TaskCompletionSource _started = NewSignal();
        private readonly TaskCompletionSource _completed = NewSignal();
        private readonly TaskCompletionSource _runCompleted = NewSignal();
        private TaskCompletionSource? _beforeCallback;
        private bool _callbackStarted;
        private bool _cancelBeforeCallback;
        private bool _released;
        private bool _cancellationDisposed;
        private bool _cancellationDisposeRequested;
        private int _activeCancellations;
        private TaskCompletionSource? _cancellationsCompleted;
        private int _state;

        internal PokeWork(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
        {
            _work = work;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        public Task Started => _started.Task;
        public Task Completed => _completed.Task;
        internal Task RunCompleted => _runCompleted.Task;
        internal string Id => RuntimeHelpers.GetHashCode(this).ToString("X8");

        internal bool IsCancellationRequested
        {
            get
            {
                lock (_lifecycleGate)
                    return _cancellation.IsCancellationRequested;
            }
        }

        internal bool IsCallbackStarted
        {
            get
            {
                lock (_lifecycleGate)
                    return _callbackStarted;
            }
        }

        internal bool IsReleased
        {
            get
            {
                lock (_lifecycleGate)
                    return _released;
            }
        }

        internal void Claim()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
                throw new InvalidOperationException("Poke work was claimed more than once.");
        }

        internal bool TryStart() => Interlocked.CompareExchange(ref _state, 2, 1) == 1;

        internal void HoldBeforeCallback()
        {
            lock (_lifecycleGate)
            {
                if (_callbackStarted || Volatile.Read(ref _state) != 1)
                    throw new InvalidOperationException("The pre-start gate must be installed on claimed work.");
                _beforeCallback ??= NewSignal();
            }
        }

        internal void ReleaseBeforeCallback()
        {
            lock (_lifecycleGate)
                _beforeCallback?.TrySetResult();
        }

        internal void ReleaseByOwner()
        {
            lock (_lifecycleGate)
                _released = true;
        }

        internal async Task ExecuteAsync()
        {
            try
            {
                Task? beforeCallback;
                lock (_lifecycleGate)
                    beforeCallback = _beforeCallback?.Task;
                if (beforeCallback is not null)
                    await beforeCallback.ConfigureAwait(false);

                lock (_lifecycleGate)
                {
                    if (_cancelBeforeCallback || _cancellation.IsCancellationRequested)
                        throw new OperationCanceledException(_cancellation.Token);
                    _callbackStarted = true;
                    _started.TrySetResult();
                }

                using var ambient = RequestWorkScope.Push(null);
                await _work(_cancellation.Token).ConfigureAwait(false);
                _completed.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                _started.TrySetCanceled();
                _completed.TrySetCanceled();
            }
            catch (Exception ex)
            {
                _completed.TrySetException(ex);
            }
            finally
            {
                Interlocked.Exchange(ref _state, 3);
            }
        }

        internal void Cancel()
        {
            CancelCore();
        }

        internal async Task DisposeCancellationAsync()
        {
            Task? cancellationCompletion = null;
            lock (_lifecycleGate)
            {
                if (_cancellationDisposed)
                    return;
                _cancellationDisposeRequested = true;
                if (_activeCancellations != 0)
                {
                    _cancellationsCompleted ??= NewSignal();
                    cancellationCompletion = _cancellationsCompleted.Task;
                }
            }

            if (cancellationCompletion is not null)
                await cancellationCompletion.ConfigureAwait(false);

            lock (_lifecycleGate)
            {
                if (_cancellationDisposed)
                    return;
                _cancellationDisposed = true;
                _cancellation.Dispose();
            }
        }

        internal async Task CancelBeforeStartAsync()
        {
            lock (_lifecycleGate)
            {
                if (_callbackStarted)
                    throw new InvalidOperationException("Started work cannot be canceled as queued work.");
                _cancelBeforeCallback = true;
                _beforeCallback?.TrySetResult();
                _started.TrySetCanceled();
                _completed.TrySetCanceled();
                Interlocked.Exchange(ref _state, 4);
            }
            try
            {
                Cancel();
            }
            finally
            {
                await DisposeCancellationAsync().ConfigureAwait(false);
            }
        }

        internal void CancelBeforeCallback()
        {
            lock (_lifecycleGate)
            {
                if (_callbackStarted)
                    return;
                _cancelBeforeCallback = true;
                _beforeCallback?.TrySetResult();
                _started.TrySetCanceled();
            }
            Cancel();
        }

        internal void SignalRunCompleted() => _runCompleted.TrySetResult();

        private void CancelCore()
        {
            CancellationTokenSource cancellation;
            lock (_lifecycleGate)
            {
                if (_cancellationDisposed || _cancellationDisposeRequested)
                    return;
                _activeCancellations++;
                cancellation = _cancellation;
            }

            try
            {
                cancellation.Cancel();
            }
            finally
            {
                TaskCompletionSource? completed = null;
                lock (_lifecycleGate)
                {
                    _activeCancellations--;
                    if (_activeCancellations == 0)
                        completed = _cancellationsCompleted;
                }
                completed?.TrySetResult();
            }
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
