using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Otel;

namespace Mohist.Server.Infrastructure.Events;

public sealed class EventDispatcherService : IDisposable
{
    public const string MeterName = "Mohist.Server.EventDispatcher";

    private readonly IEventStore _events;
    private readonly IReadOnlyList<Subscription> _subscriptions;
    private readonly IDeadLetterStore _deadLetters;
    private readonly TimeProvider _time;
    private readonly EventDispatcherOptions _options;
    private readonly ILogger<EventDispatcherService> _log;
    private readonly IEventPushQueue _pushQueue;
    private readonly Dictionary<EventKey, Dictionary<int, HandlerState>> _states = [];
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private readonly Meter _meter;
    private readonly ObservableGauge<long> _blockedSourcesGauge;
    private long _lastCompletedCycleBlockedSources;
    private bool _disposed;

    public EventDispatcherService(
        IEventStore events,
        IEnumerable<Subscription> subscriptions,
        IDeadLetterStore deadLetters,
        TimeProvider time,
        IOptions<EventDispatcherOptions> options,
        ILogger<EventDispatcherService> log,
        IEventPushQueue pushQueue)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _subscriptions = (subscriptions ?? throw new ArgumentNullException(nameof(subscriptions))).ToList();
        _deadLetters = deadLetters ?? throw new ArgumentNullException(nameof(deadLetters));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _log = log;
        _pushQueue = pushQueue;

        if (_options.BatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "BatchSize must be positive");
        if (_options.MaxAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxAttempts must be positive");
        if (_options.BaseBackoff < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "BaseBackoff must not be negative");
        if (_options.MaxBackoff < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxBackoff must not be negative");
        if (_options.PushQueueCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "PushQueueCapacity must be positive");
        if (_options.PushDeliveryTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "PushDeliveryTimeout must be positive");

        _meter = new Meter(MeterName);
        _blockedSourcesGauge = _meter.CreateObservableGauge(
            RuntimeMetricCatalog.EventDispatcherBlockedSources,
            ReadLastCompletedCycleBlockedSources,
            "1");
    }

    public async Task DispatchAsync(CancellationToken ct)
    {
        await _dispatchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var batch = await _events.ListUndeliveredAsync(_options.BatchSize, ct).ConfigureAwait(false);
            var blockedSources = new HashSet<string>(StringComparer.Ordinal);
            foreach (var evt in batch)
            {
                ct.ThrowIfCancellationRequested();
                if (blockedSources.Contains(evt.Source))
                    continue;
                var settled = await DispatchOneAsync(evt, ct).ConfigureAwait(false);
                if (!settled)
                    blockedSources.Add(evt.Source);
            }
            Interlocked.Exchange(
                ref _lastCompletedCycleBlockedSources,
                blockedSources.Count);
        }
        finally
        {
            _dispatchGate.Release();
        }
    }

    public async Task<DeadLetterRedeliveryResult> RedeliverAsync(long deadLetterId, CancellationToken ct)
    {
        var row = await _deadLetters.GetAsync(deadLetterId, ct).ConfigureAwait(false);
        if (row is null)
            return new DeadLetterRedeliveryResult(false, false, 0, "Dead-letter row not found");

        var evt = new UndeliveredEvent(
            ParseOrigin(row.Origin),
            row.Id,
            row.Source,
            row.EventId,
            row.Type,
            row.Time,
            row.SpecVersion,
            row.Subject,
            row.DataContentType,
            row.Data,
            row.ExtensionsJson);
        var envelope = ReconstructEnvelope(evt);
        var subscription = _subscriptions.FirstOrDefault(sub =>
            string.Equals(sub.Identity, row.FailingHandler, StringComparison.Ordinal)
            && CloudEventTypeMatcher.Matches(sub.Type, envelope.Type));
        if (subscription is null)
        {
            return new DeadLetterRedeliveryResult(
                true,
                false,
                0,
                $"Handler '{row.FailingHandler}' is not registered for event type '{row.Type}'");
        }

        row = await _deadLetters
            .StartRedeliveryAsync(deadLetterId, _time.GetUtcNow(), ct)
            .ConfigureAwait(false);
        if (row is null)
            return new DeadLetterRedeliveryResult(true, false, 0, "Dead-letter row is already resolved");

        try
        {
            await subscription.Dispatch(subscription.Handler, envelope, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var error = OperatorDiagnostic.Summarize(ex) ?? "unknown";
            await _deadLetters.RecordRedeliveryFailureAsync(
                deadLetterId,
                error,
                ex.ToString(),
                1,
                _time.GetUtcNow(),
                ct).ConfigureAwait(false);
            return new DeadLetterRedeliveryResult(true, false, 1, error);
        }

        try
        {
            await _deadLetters.ResolveAsync(deadLetterId, _time.GetUtcNow(), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Dead-letter {DeadLetterId} handler succeeded but ResolveAsync failed; row remains Redelivering",
                deadLetterId);
            return new DeadLetterRedeliveryResult(
                true,
                false,
                1,
                "Handler succeeded but persistence failed; row remains in Redelivering state");
        }

        return new DeadLetterRedeliveryResult(true, true, 1, null);
    }

    private TimeSpan Backoff(int attemptCount)
    {
        if (attemptCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(attemptCount));

        var multiplier = Math.Pow(2, Math.Min(attemptCount - 1, 62));
        var ticks = Math.Min(_options.BaseBackoff.Ticks * multiplier, _options.MaxBackoff.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    public Meter Meter => _meter;

    private async Task<bool> DispatchOneAsync(UndeliveredEvent evt, CancellationToken ct)
    {
        var envelope = ReconstructEnvelope(evt);
        try
        {
            _pushQueue.TryEnqueue(envelope);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Event push enqueue failed for {Type} {EventId}", envelope.Type, envelope.Id);
        }
        var matching = _subscriptions
            .Select((subscription, index) => new IndexedSubscription(index, subscription))
            .Where(item => CloudEventTypeMatcher.Matches(item.Subscription.Type, envelope.Type))
            .ToList();
        if (matching.Count == 0)
        {
            await MarkDispatchedAsync(evt, ct).ConfigureAwait(false);
            return true;
        }

        var key = new EventKey(evt.Source, evt.Id);
        if (!_states.TryGetValue(key, out var states))
        {
            states = [];
            _states.Add(key, states);
        }

        var now = _time.GetUtcNow();
        foreach (var item in matching)
        {
            if (!states.TryGetValue(item.Index, out var state))
            {
                state = new HandlerState();
                states.Add(item.Index, state);
            }
            if (state.Status != HandlerStatus.Pending || state.NextAttemptTime > now)
                continue;

            try
            {
                await item.Subscription.Dispatch(item.Subscription.Handler, envelope, ct).ConfigureAwait(false);
                state.Status = HandlerStatus.Completed;
                state.NextAttemptTime = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                state.AttemptCount++;
                state.Error = ex;
                if (state.AttemptCount >= _options.MaxAttempts)
                {
                    state.Status = HandlerStatus.DeadLettered;
                }
                else
                {
                    state.NextAttemptTime = now + Backoff(state.AttemptCount);
                }
                _log.LogWarning(
                    ex,
                    "Event dispatcher handler {Handler} failed for {Type} {EventId} on attempt {Attempt}/{MaxAttempts}",
                    item.Subscription.Identity,
                    envelope.Type,
                    envelope.Id,
                    state.AttemptCount,
                    _options.MaxAttempts);
            }
        }

        if (matching.Any(item => states[item.Index].Status == HandlerStatus.Pending))
            return false;

        var settledAt = _time.GetUtcNow();
        var deadLetters = matching
            .Where(item => states[item.Index].Status == HandlerStatus.DeadLettered)
            .Select(item => BuildDeadLetter(evt, item.Subscription, states[item.Index], settledAt))
            .ToList();
        try
        {
            if (deadLetters.Count == 0)
            {
                await _events
                    .MarkDispatchedAsync(evt.Origin, evt.Source, evt.Id, settledAt, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                await _deadLetters.SettleAsync(evt, deadLetters, settledAt, ct).ConfigureAwait(false);
            }
            _states.Remove(key);
            return true;
        }
        catch
        {
            throw;
        }
    }

    private Task MarkDispatchedAsync(UndeliveredEvent evt, CancellationToken ct) =>
        _events.MarkDispatchedAsync(evt.Origin, evt.Source, evt.Id, _time.GetUtcNow(), ct);

    private IEnumerable<Measurement<long>> ReadLastCompletedCycleBlockedSources()
    {
        if (_disposed)
            return [];
        var snapshot = Interlocked.Read(ref _lastCompletedCycleBlockedSources);
        return [new Measurement<long>(snapshot)];
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _meter.Dispose();
    }

    private DeadLetterRow BuildDeadLetter(
        UndeliveredEvent evt,
        Subscription subscription,
        HandlerState state,
        DateTimeOffset settledAt) =>
        new()
        {
            Origin = evt.Origin.ToString(),
            Id = evt.Id,
            Source = evt.Source,
            EventId = evt.EventId,
            Type = evt.Type,
            Time = evt.Time,
            SpecVersion = evt.SpecVersion,
            Subject = evt.Subject,
            DataContentType = evt.DataContentType,
            Data = evt.Data,
            ExtensionsJson = evt.ExtensionsJson,
            FailingHandler = subscription.Identity,
            ErrorMessage = state.Error is null ? "unknown" : OperatorDiagnostic.Summarize(state.Error) ?? "unknown",
            ErrorStack = state.Error?.ToString(),
            AttemptCount = state.AttemptCount,
            DeadLetteredAt = settledAt,
        };

    private static CloudEvent ReconstructEnvelope(UndeliveredEvent evt)
    {
        var extensions = string.IsNullOrEmpty(evt.ExtensionsJson)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(evt.ExtensionsJson, CloudEvent.JsonOptions)
                ?? new Dictionary<string, string>();
        return new CloudEvent(
            evt.EventId,
            new Uri(evt.Source, UriKind.RelativeOrAbsolute),
            evt.Type,
            evt.Time,
            evt.Data,
            evt.DataContentType,
            evt.Subject,
            evt.SpecVersion,
            extensions);
    }

    private static EventOrigin ParseOrigin(string text) => text switch
    {
        nameof(EventOrigin.WorkflowRun) => EventOrigin.WorkflowRun,
        nameof(EventOrigin.Issue) => EventOrigin.Issue,
        nameof(EventOrigin.Epic) => EventOrigin.Epic,
        nameof(EventOrigin.AgentSession) => EventOrigin.AgentSession,
        nameof(EventOrigin.AgentJob) => EventOrigin.AgentJob,
        _ => throw new InvalidOperationException($"Unknown event origin '{text}'."),
    };

    private readonly record struct EventKey(string Source, long Id);

    private readonly record struct IndexedSubscription(int Index, Subscription Subscription);

    private sealed class HandlerState
    {
        public int AttemptCount { get; set; }

        public DateTimeOffset? NextAttemptTime { get; set; }

        public HandlerStatus Status { get; set; }

        public Exception? Error { get; set; }
    }

    private enum HandlerStatus
    {
        Pending,
        Completed,
        DeadLettered,
    }
}
