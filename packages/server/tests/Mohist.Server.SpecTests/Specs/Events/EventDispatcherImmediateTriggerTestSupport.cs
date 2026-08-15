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
