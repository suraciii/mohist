using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Otel;

namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Stream-lease dispatch engine. Workers (or explicit drains) claim one
/// stream, deliver its undispatched rows in per-source Id order, and
/// settle the contiguous delivered prefix in chunks. A failing head row
/// parks the whole stream on its lease with a durable attempt budget;
/// other streams are unaffected. Delivery is at-least-once: handlers must
/// be idempotent by EventId.
/// </summary>
public sealed class EventDispatcherService : IEventDispatcher, IDisposable
{
    public const string MeterName = "Mohist.Server.EventDispatcher";

    private const int SettleChunkSize = 25;

    private readonly IEventStore _events;
    private readonly IReadOnlyList<Subscription> _subscriptions;
    private readonly IDeadLetterStore _deadLetters;
    private readonly IDispatchStreamLeaseStore _leases;
    private readonly TimeProvider _time;
    private readonly EventDispatcherOptions _options;
    private readonly ILogger<EventDispatcherService> _log;
    private readonly IEventPushQueue _pushQueue;
    private readonly Meter _meter;
    private readonly ObservableGauge<long> _parkedStreamsGauge;
    private long _lastKnownParkedStreams;
    private bool _disposed;

    /// <summary>
    /// Owners with a claim in flight in this process. Lets DrainAsync act
    /// as an in-proc barrier: a foreign claim may be delivering exactly
    /// the stream the caller needs settled, so instead of returning early
    /// the drain waits for those claims to finish and re-checks. Cross-
    /// process owners are invisible here and stay covered by lease
    /// expiry. Registrations live only inside ClaimAndDrainOneAsync, so a
    /// draining caller never holds one and concurrent drains cannot wait
    /// on each other forever. The pulse is task-based, never clock-based:
    /// test hosts run fake clocks that do not advance on their own.
    /// </summary>
    private readonly object _claimsGate = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _activeClaims = [];
    private TaskCompletionSource _claimsIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public EventDispatcherService(
        IEventStore events,
        IEnumerable<Subscription> subscriptions,
        IDeadLetterStore deadLetters,
        IDispatchStreamLeaseStore leases,
        TimeProvider time,
        IOptions<EventDispatcherOptions> options,
        ILogger<EventDispatcherService> log,
        IEventPushQueue pushQueue)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _subscriptions = (subscriptions ?? throw new ArgumentNullException(nameof(subscriptions))).ToList();
        _deadLetters = deadLetters ?? throw new ArgumentNullException(nameof(deadLetters));
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _log = log;
        _pushQueue = pushQueue;

        if (_options.WorkerCount < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "WorkerCount must not be negative");
        if (_options.LeaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "LeaseDuration must be positive");
        if (_options.SlowPollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "SlowPollInterval must be positive");
        if (_options.MaxStreamsPerPass <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxStreamsPerPass must be positive");
        if (_options.MaxEventsPerStreamPass <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxEventsPerStreamPass must be positive");
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
        _parkedStreamsGauge = _meter.CreateObservableGauge(
            RuntimeMetricCatalog.EventDispatcherBlockedSources,
            ReadLastKnownParkedStreams,
            "1");
    }

    public Meter Meter => _meter;

    /// <summary>
    /// Claims a claimable stream and drains it. Returns true when a claim
    /// was made. A claimed stream that parks its head without settling
    /// anything is skipped in favor of the next candidate, so a
    /// permanently failing stream neither blocks its siblings nor
    /// livelocks a drain-all caller (the parked one is gated until its
    /// backoff elapses).
    /// </summary>
    public async Task<bool> ClaimAndDrainOneAsync(string owner, CancellationToken ct = default)
    {
        lock (_claimsGate)
        {
            if (_activeClaims.IsEmpty)
                _claimsIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeClaims.TryAdd(owner, 0);
        }
        try
        {
            return await ClaimAndDrainOneCoreAsync(owner, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_claimsGate)
            {
                _activeClaims.TryRemove(owner, out _);
                if (_activeClaims.IsEmpty)
                    _claimsIdle.TrySetResult();
            }
        }
    }

    private async Task<bool> ClaimAndDrainOneCoreAsync(string owner, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var candidates = await _events
            .ListPendingStreamsAsync(_options.MaxStreamsPerPass, ct)
            .ConfigureAwait(false);

        foreach (var stream in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var attempts = await _leases
                .ClaimAsync(
                    stream.Origin.ToString(),
                    stream.Source,
                    owner,
                    now,
                    _options.LeaseDuration,
                    ct)
                .ConfigureAwait(false);
            if (attempts is null)
                continue;

            var progressed = await DrainClaimedStreamAsync(stream.Origin, stream.Source, owner, attempts.Value, ct)
                .ConfigureAwait(false);
            await UpdateParkedGaugeAsync(ct).ConfigureAwait(false);
            if (progressed)
                return true;
            now = _time.GetUtcNow();
        }

        return false;
    }

    public async Task DrainAsync(CancellationToken ct = default)
    {
        var owner = $"drain-{Guid.NewGuid():N}";
        while (true)
        {
            if (await ClaimAndDrainOneAsync(owner, ct).ConfigureAwait(false))
                continue;
            Task idle;
            lock (_claimsGate)
            {
                if (_activeClaims.IsEmpty)
                    break;
                idle = _claimsIdle.Task;
            }
            await idle.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<DeadLetterRedeliveryResult> RedeliverAsync(long deadLetterId, CancellationToken ct = default)
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

    private static EventOrigin ParseOrigin(string text) => text switch
    {
        nameof(EventOrigin.WorkflowRun) => EventOrigin.WorkflowRun,
        nameof(EventOrigin.Issue) => EventOrigin.Issue,
        nameof(EventOrigin.Epic) => EventOrigin.Epic,
        nameof(EventOrigin.AgentSession) => EventOrigin.AgentSession,
        nameof(EventOrigin.AgentJob) => EventOrigin.AgentJob,
        nameof(EventOrigin.Ingress) => EventOrigin.Ingress,
        nameof(EventOrigin.Workspace) => EventOrigin.Workspace,
        _ => throw new InvalidOperationException($"Unknown event origin '{text}'."),
    };

    /// <summary>
    /// Drains one claimed stream. Returns true when at least one row was
    /// durably settled; false when the pass ended parked (or released)
    /// with no progress.
    /// </summary>
    private async Task<bool> DrainClaimedStreamAsync(
        EventOrigin origin,
        string source,
        string owner,
        int attempts,
        CancellationToken ct)
    {
        var settledAny = false;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var rows = await _events
                    .ListUndeliveredByStreamAsync(origin, source, _options.MaxEventsPerStreamPass, ct)
                    .ConfigureAwait(false);
                if (rows.Count == 0)
                {
                    await _leases.ReleaseAsync(origin.ToString(), source, owner, CancellationToken.None);
                    return settledAny;
                }

                var delivered = new List<long>(rows.Count);
                UndeliveredEvent? failedHead = null;
                DeliveryOutcome? failure = null;
                foreach (var evt in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    var settled = await DeliverOneAsync(evt, ct).ConfigureAwait(false);
                    if (settled.FailedHandler is not null)
                    {
                        failedHead = evt;
                        failure = settled;
                        break;
                    }
                    delivered.Add(evt.Id);
                }

                await SettleDeliveredAsync(origin, source, delivered, CancellationToken.None).ConfigureAwait(false);
                settledAny |= delivered.Count > 0;

                if (failure is null)
                {
                    if (!await _leases
                            .TouchAsync(origin.ToString(), source, owner, _time.GetUtcNow(), _options.LeaseDuration, ct)
                            .ConfigureAwait(false))
                    {
                        // Lease stolen mid-drain; the new owner re-drives from
                        // the undispatched rows. At-least-once covers the overlap.
                        return settledAny;
                    }

                    if (rows.Count < _options.MaxEventsPerStreamPass)
                    {
                        await _leases.ReleaseAsync(origin.ToString(), source, owner, CancellationToken.None);
                        return settledAny;
                    }
                    continue;
                }

                attempts++;
                if (attempts >= _options.MaxAttempts)
                {
                    if (!await DeadLetterHeadAsync(origin, source, owner, failedHead!, failure!.Value, ct).ConfigureAwait(false))
                        return settledAny;
                    settledAny = true;
                    attempts = 0;
                    continue;
                }

                var parked = await _leases
                    .ParkAsync(
                        origin.ToString(),
                        source,
                        owner,
                        attempts,
                        _time.GetUtcNow() + Backoff(attempts),
                        OperatorDiagnostic.Summarize(failure.Value.Error) ?? "unknown",
                        _time.GetUtcNow(),
                        ct)
                    .ConfigureAwait(false);
                if (parked)
                {
                    _log.LogWarning(
                        failure.Value.Error,
                        "Event dispatcher handler {Handler} failed for {Type} {EventId} on attempt {Attempt}/{MaxAttempts}; stream parked until {NextAttempt}",
                        failure.Value.FailedHandler,
                        failedHead!.Type,
                        failedHead.EventId,
                        attempts,
                        _options.MaxAttempts,
                        _time.GetUtcNow() + Backoff(attempts));
                }
                else
                {
                    _log.LogWarning(
                        "Lease on stream {Origin}/{Source} lost while parking after attempt {Attempt}; new owner continues",
                        origin,
                        source,
                        attempts);
                }
                return settledAny;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The drain itself broke (settle/dead-letter write failed). Park
            // so the attempt budget survives and another pass retries, or
            // release when even parking fails — never hold the lease hostage.
            var parked = false;
            try
            {
                parked = await _leases
                    .ParkAsync(
                        origin.ToString(),
                        source,
                        owner,
                        Math.Max(attempts, 1),
                        _time.GetUtcNow() + Backoff(1),
                        OperatorDiagnostic.Summarize(ex) ?? "drain failed",
                        _time.GetUtcNow(),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Parking is best-effort; fall through to release.
            }

            if (!parked)
            {
                await _leases.ReleaseAsync(origin.ToString(), source, owner, CancellationToken.None);
            }

            throw;
        }
    }

    private async Task SettleDeliveredAsync(
        EventOrigin origin,
        string source,
        List<long> delivered,
        CancellationToken ct)
    {
        for (var start = 0; start < delivered.Count; start += SettleChunkSize)
        {
            var chunk = delivered.Skip(start).Take(SettleChunkSize).ToList();
            await _events
                .MarkDispatchedRangeAsync(origin, source, chunk, _time.GetUtcNow(), ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes the exhausted head to the dead-letter store — which also marks
    /// the event dispatched — and resets the stream's attempt budget for
    /// the next head. Returns false when settlement failed: the stream is
    /// parked holding its budget and the caller must stop draining it;
    /// the next pass retries only the settlement.
    /// </summary>
    private async Task<bool> DeadLetterHeadAsync(
        EventOrigin origin,
        string source,
        string owner,
        UndeliveredEvent evt,
        DeliveryOutcome failed,
        CancellationToken ct)
    {
        var settledAt = _time.GetUtcNow();
        try
        {
            await _deadLetters
                .SettleAsync(evt, [BuildDeadLetter(evt, failed, settledAt)], settledAt, ct)
                .ConfigureAwait(false);
            await _leases
                .ResetAttemptsAsync(origin.ToString(), source, owner, settledAt, ct)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Dead-letter settlement failed for {Type} {EventId}; the stream stays parked and retries settlement",
                evt.Type,
                evt.EventId);
            await _leases
                .ParkAsync(
                    origin.ToString(),
                    source,
                    owner,
                    _options.MaxAttempts,
                    settledAt + Backoff(1),
                    OperatorDiagnostic.Summarize(ex) ?? "settlement failed",
                    settledAt,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return false;
        }
    }

    private readonly record struct DeliveryOutcome(string? FailedHandler, Exception? Error);

    private async Task<DeliveryOutcome> DeliverOneAsync(UndeliveredEvent evt, CancellationToken ct)
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

        foreach (var subscription in _subscriptions)
        {
            if (!CloudEventTypeMatcher.Matches(subscription.Type, envelope.Type))
                continue;
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
                return new DeliveryOutcome(subscription.Identity, ex);
            }
        }

        return new DeliveryOutcome(null, null);
    }

    private TimeSpan Backoff(int attemptCount)
    {
        if (attemptCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(attemptCount));

        var multiplier = Math.Pow(2, Math.Min(attemptCount - 1, 62));
        var ticks = Math.Min(_options.BaseBackoff.Ticks * multiplier, _options.MaxBackoff.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    private async Task UpdateParkedGaugeAsync(CancellationToken ct)
    {
        try
        {
            var parked = await _leases.CountParkedAsync(_time.GetUtcNow(), ct).ConfigureAwait(false);
            Interlocked.Exchange(ref _lastKnownParkedStreams, parked);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Parked-stream gauge refresh failed");
        }
    }

    private IEnumerable<Measurement<long>> ReadLastKnownParkedStreams()
    {
        if (_disposed)
            return [];
        var snapshot = Interlocked.Read(ref _lastKnownParkedStreams);
        return [new Measurement<long>(snapshot)];
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _meter.Dispose();
    }

    private DeadLetterRow BuildDeadLetter(UndeliveredEvent evt, DeliveryOutcome failed, DateTimeOffset settledAt) =>
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
            FailingHandler = failed.FailedHandler ?? "unknown",
            ErrorMessage = failed.Error is null ? "unknown" : OperatorDiagnostic.Summarize(failed.Error) ?? "unknown",
            ErrorStack = failed.Error?.ToString(),
            AttemptCount = _options.MaxAttempts,
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
}
