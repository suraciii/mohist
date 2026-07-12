using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Events;

namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Pull–fan-out–mark dispatch core. One tick:
///
/// <list type="number">
///   <item><description>Pulls a batch of undelivered rows from
///     <see cref="IEventStore.ListUndeliveredAsync"/> (single UNION over
///     all four truth tables, ordered by <c>(Source, Id)</c>).</description></item>
///   <item><description>For each row, fans out to every registered
///     <see cref="Subscription"/> whose <see cref="CloudEventTypeMatcher.Matches"/>
///     succeeds.</description></item>
///   <item><description>Each matching handler is retried up to
///     <see cref="DispatcherOptions.HandlerMaxAttempts"/> times
///     independently of its siblings.</description></item>
///   <item><description>On retry exhaustion the dispatcher writes a
///     <see cref="DeadLetterRow"/> via <see cref="IDeadLetterStore"/> and
///     sets <c>DispatchedAt</c> on the original event row so subsequent
///     ticks stop retrying it.</description></item>
///   <item><description>Once every matching handler has settled
///     (succeeded or dead-lettered) the dispatcher sets
///     <c>DispatchedAt</c> on the original event row — the row is
///     never marked before delivery.</description></item>
/// </list>
///
/// Processing is serial in <c>(Source, Id)</c> order — the batch is
/// already globally sorted by the UNION query — guaranteeing
/// per-stream FIFO with no reorder and no skip. All timestamps
/// (<c>DispatchedAt</c>, <c>DeadLetteredAt</c>) come from the injected
/// <see cref="TimeProvider"/>; no wall-clock reads.
///
/// Pure DI service: unit-testable with a fake <see cref="IEventStore"/>
/// + fake / Noop <see cref="IDeadLetterStore"/> + a
/// <c>FakeTimeProvider</c> with no silo. The
/// <see cref="Mohist.Server.Events.Grains.DispatcherGrain"/> is a thin
/// shell that delegates each tick here.
/// </summary>
public sealed class EventDispatcherService
{
    private readonly IEventStore _events;
    private readonly IReadOnlyList<Subscription> _subscriptions;
    private readonly IDeadLetterStore _deadLetters;
    private readonly TimeProvider _time;
    private readonly DispatcherOptions _options;
    private readonly ILogger<EventDispatcherService> _log;

    public EventDispatcherService(
        IEventStore events,
        IEnumerable<Subscription> subscriptions,
        IDeadLetterStore deadLetters,
        TimeProvider time,
        IOptions<DispatcherOptions> options,
        ILogger<EventDispatcherService> log)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _subscriptions = (subscriptions ?? throw new ArgumentNullException(nameof(subscriptions)))
            .ToList();
        _deadLetters = deadLetters ?? throw new ArgumentNullException(nameof(deadLetters));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _log = log;

        if (_options.BatchLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "BatchLimit must be positive");
        if (_options.HandlerMaxAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "HandlerMaxAttempts must be positive");
    }

    /// <summary>
    /// Runs one pull–fan-out–mark cycle. Safe to call concurrently with
    /// itself only when callers serialize access externally (the
    /// dispatcher grain is single-activated, so its
    /// <c>ReceiveReminder</c> / <c>PulseAsync</c> invocations are
    /// inherently serialized by Orleans).
    /// </summary>
    public async Task DispatchAsync(CancellationToken ct)
    {
        var batch = await _events.ListUndeliveredAsync(_options.BatchLimit, ct).ConfigureAwait(false);
        if (batch.Count == 0)
            return;

        _log.LogDebug(
            "Dispatcher tick: pulled {Count} undelivered event(s); {Subs} subscription(s)",
            batch.Count, _subscriptions.Count);

        foreach (var evt in batch)
        {
            ct.ThrowIfCancellationRequested();
            await DispatchOneAsync(evt, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Loads the dead-letter row identified by
    /// <paramref name="deadLetterId"/> and re-dispatches the original
    /// event only to the failing handler recorded on that row. Already
    /// successful sibling handlers are not repeated. A successful retry
    /// deletes the resolved row; a failed retry leaves it available for
    /// later operator recovery.
    /// </summary>
    public async Task<DeadLetterRedeliveryResult> RedeliverAsync(long deadLetterId, CancellationToken ct)
    {
        var row = await _deadLetters.GetAsync(deadLetterId, ct).ConfigureAwait(false);
        if (row is null)
        {
            _log.LogDebug(
                "Dispatcher RedeliverAsync skipped: dead-letter {Id} not found",
                deadLetterId);
            return new DeadLetterRedeliveryResult(false, false, 0, "Dead-letter row not found");
        }

        _log.LogInformation(
            "Dispatcher re-dispatching dead-letter {Id} (origin={Origin} id={EventId} type={Type})",
            row.DeadLetterId, row.Origin, row.EventId, row.Type);

        var evt = new UndeliveredEvent(
            Origin: ParseOrigin(row.Origin),
            Id: row.Id,
            Source: row.Source,
            EventId: row.EventId,
            Type: row.Type,
            Time: row.Time,
            SpecVersion: row.SpecVersion,
            Subject: row.Subject,
            DataContentType: row.DataContentType,
            Data: row.Data,
            ExtensionsJson: row.ExtensionsJson);

        var envelope = ReconstructEnvelope(evt);
        var subscription = _subscriptions.FirstOrDefault(sub =>
            string.Equals(HandlerName(sub), row.FailingHandler, StringComparison.Ordinal)
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
            return new DeadLetterRedeliveryResult(false, false, 0, "Dead-letter row is already resolved");

        var (outcome, error, attempts, errorStack) = await InvokeWithRetryAsync(subscription, envelope, ct).ConfigureAwait(false);
        if (outcome == HandlerOutcome.Exhausted)
        {
            await _deadLetters.RecordRedeliveryFailureAsync(
                deadLetterId,
                error ?? "unknown",
                errorStack,
                attempts,
                _time.GetUtcNow(),
                ct).ConfigureAwait(false);
            return new DeadLetterRedeliveryResult(true, false, attempts, error);
        }

        await _deadLetters.ResolveAsync(deadLetterId, _time.GetUtcNow(), ct).ConfigureAwait(false);
        return new DeadLetterRedeliveryResult(true, true, attempts, null);
    }

    private async Task DispatchOneAsync(UndeliveredEvent evt, CancellationToken ct)
    {
        var envelope = ReconstructEnvelope(evt);
        var anyMatched = false;
        var deadLetters = new List<DeadLetterRow>();

        foreach (var sub in _subscriptions)
        {
            if (!CloudEventTypeMatcher.Matches(sub.Type, envelope.Type))
                continue;
            anyMatched = true;

            var (settled, error, attemptCount, errorStack) = await InvokeWithRetryAsync(sub, envelope, ct).ConfigureAwait(false);
            if (settled == HandlerOutcome.Delivered)
                continue;

            deadLetters.Add(BuildDeadLetter(evt, sub, error, attemptCount, errorStack));
        }

        if (!anyMatched)
        {
            _log.LogDebug(
                "Dispatcher: event {Type} {EventId} ({Origin}/{Id}) had no matching subscriptions",
                envelope.Type, envelope.Id, evt.Origin, evt.Id);
        }

        try
        {
            var settledAt = _time.GetUtcNow();
            if (deadLetters.Count == 0)
            {
                await _events
                    .MarkDispatchedAsync(evt.Origin, evt.Source, evt.Id, settledAt, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                await _deadLetters
                    .SettleAsync(evt, deadLetters, settledAt, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Dispatcher: failed to settle {Origin}/{Id}; stopping the tick to preserve FIFO",
                evt.Origin, evt.Id);
            throw;
        }
    }

    private async Task<(HandlerOutcome Outcome, string? Error, int Attempts, string? ErrorStack)> InvokeWithRetryAsync(
        Subscription sub, CloudEvent envelope, CancellationToken ct)
    {
        var attempts = 0;
        Exception? lastError = null;

        while (attempts < _options.HandlerMaxAttempts)
        {
            attempts++;
            try
            {
                await sub.Dispatch(sub.Handler, envelope, ct).ConfigureAwait(false);
                if (attempts > 1)
                {
                    _log.LogInformation(
                        "Dispatcher: handler {Handler} recovered for {Type} {EventId} on attempt {Attempt}",
                        sub.Handler.GetType().FullName, envelope.Type, envelope.Id, attempts);
                }
                return (HandlerOutcome.Delivered, null, attempts, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _log.LogWarning(ex,
                    "Dispatcher: handler {Handler} threw on {Type} {EventId} (attempt {Attempt}/{Max})",
                    sub.Handler.GetType().FullName, envelope.Type, envelope.Id,
                    attempts, _options.HandlerMaxAttempts);
            }
        }

        return (HandlerOutcome.Exhausted, OperatorDiagnostic.Summarize(lastError), attempts, lastError?.ToString());
    }

    private DeadLetterRow BuildDeadLetter(
        UndeliveredEvent evt,
        Subscription sub,
        string? error,
        int attempts,
        string? errorStack)
    {
        return new DeadLetterRow
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
            FailingHandler = sub.Handler.GetType().FullName ?? sub.Handler.GetType().Name,
            ErrorMessage = error ?? "unknown",
            ErrorStack = errorStack,
            AttemptCount = attempts,
            DeadLetteredAt = _time.GetUtcNow(),
        };
    }

    private static CloudEvent ReconstructEnvelope(UndeliveredEvent evt)
    {
        IReadOnlyDictionary<string, string> extensions;
        if (string.IsNullOrEmpty(evt.ExtensionsJson))
        {
            extensions = new Dictionary<string, string>();
        }
        else
        {
            extensions = JsonSerializer.Deserialize<Dictionary<string, string>>(evt.ExtensionsJson, CloudEvent.JsonOptions)
                ?? new Dictionary<string, string>();
        }

        return new CloudEvent(
            id: evt.EventId,
            source: new Uri(evt.Source, UriKind.RelativeOrAbsolute),
            type: evt.Type,
            time: evt.Time,
            data: evt.Data,
            dataContentType: evt.DataContentType,
            subject: evt.Subject,
            specVersion: evt.SpecVersion,
            extensions: extensions);
    }

    private static EventOrigin ParseOrigin(string text) => text switch
    {
        nameof(EventOrigin.WorkflowRun) => EventOrigin.WorkflowRun,
        nameof(EventOrigin.Issue) => EventOrigin.Issue,
        nameof(EventOrigin.Epic) => EventOrigin.Epic,
        nameof(EventOrigin.AgentSession) => EventOrigin.AgentSession,
        _ => throw new InvalidOperationException($"Unknown event origin '{text}'."),
    };

    private static string HandlerName(Subscription subscription) =>
        subscription.Handler.GetType().FullName ?? subscription.Handler.GetType().Name;

    private enum HandlerOutcome
    {
        Delivered,
        Exhausted,
    }
}
