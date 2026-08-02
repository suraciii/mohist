using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Infrastructure.Slack;

/// <summary>
/// Cluster-singleton safety net for the Slack outbound outbox. The
/// <see cref="ISlackOutboxDispatcherGrain"/> Orleans reminder drives
/// <see cref="DispatchAsync"/>; this service runs four independent
/// sweeps that together enforce the spec's "Delivery uncertain",
/// "DeadLettered", and "Backpressure is reversible" guarantees
/// without overriding AgentJob/AgentTurn authority:
/// <list type="number">
///   <item>
///     <b>Retry budget cutoff</b>: Pending rows whose
///     <see cref="SlackProviderOptions.OutboxMaxAttempts"/> has been
///     exhausted are dead-lettered via <see cref="IDeadLetterStore"/>.
///     These are rows the adapter never reached or whose retries
///     finally gave up.
///   </item>
///   <item>
///     <b>Claim-timeout sweep</b>: Claimed rows whose
///     <see cref="SlackProviderOptions.OutboxClaimTimeout"/> has passed
///     without an adapter ack are flipped to DeliveryUncertain. The
///     adapter's /claim + /ack pair is the happy path; this sweep is
///     what catches a crashed or stuck adapter.
///   </item>
///   <item>
///     <b>Uncertain-timeout sweep</b>: DeliveryUncertain rows whose
///     <see cref="SlackProviderOptions.OutboxUncertainTimeout"/> has
///     passed without an operator action are dead-lettered.
///   </item>
///   <item>
///     <b>Backpressure recovery sweep</b>: Degraded(Backpressured)
///     Connections whose pending inbox AND pending outbox counts have
///     dropped strictly below their per-Connection capacity are
///     reason-guarded-flipped back to Healthy via
///     <see cref="ISlackConnectionHealthBackpressurer.RecoverBackpressuredAsync"/>.
///     The reason guard on the flip ensures we only recover rows whose
///     HealthReason is an inbox or outbox overflow reason — a Degraded
///     row with a different reason is not touched.
///   </item>
/// </list>
/// </summary>
public sealed class SlackOutboxDispatcherService : IDisposable
{
    private readonly SlackOutboxStore _store;
    private readonly SlackProviderInboxStore _inboxStore;
    private readonly AgentConnectionStore _connectionStore;
    private readonly ISlackConnectionHealthBackpressurer _healthBackpressurer;
    private readonly IDeadLetterStore _deadLetters;
    private readonly TimeProvider _timeProvider;
    private readonly IOptions<SlackProviderOptions> _options;
    private readonly ILogger<SlackOutboxDispatcherService> _log;
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private bool _disposed;

    public const string DeadLetterOrigin = "SlackOutbox";

    public SlackOutboxDispatcherService(
        SlackOutboxStore store,
        SlackProviderInboxStore inboxStore,
        AgentConnectionStore connectionStore,
        ISlackConnectionHealthBackpressurer healthBackpressurer,
        IDeadLetterStore deadLetters,
        TimeProvider timeProvider,
        IOptions<SlackProviderOptions> options,
        ILogger<SlackOutboxDispatcherService> log)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _inboxStore = inboxStore ?? throw new ArgumentNullException(nameof(inboxStore));
        _connectionStore = connectionStore ?? throw new ArgumentNullException(nameof(connectionStore));
        _healthBackpressurer = healthBackpressurer ?? throw new ArgumentNullException(nameof(healthBackpressurer));
        _deadLetters = deadLetters ?? throw new ArgumentNullException(nameof(deadLetters));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log;
    }

    public async Task DispatchAsync(CancellationToken ct)
    {
        await _dispatchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var batch = _options.Value.DispatcherBatchSize;
            await DeadLetterRetryExhaustedAsync(batch, ct).ConfigureAwait(false);
            await SurfaceClaimedTimeoutAsync(batch, ct).ConfigureAwait(false);
            await DeadLetterUncertainTimeoutAsync(batch, ct).ConfigureAwait(false);
            await RecoverBackpressureAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _dispatchGate.Release();
        }
    }

    private async Task DeadLetterRetryExhaustedAsync(int batchSize, CancellationToken ct)
    {
        var rows = await _store.ListPendingReadyForRetryAsync(batchSize, ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var updated = await _store.MarkDeadLetteredAsync(row.ProjectId, row.Id, "retry budget exhausted", ct).ConfigureAwait(false);
            if (updated == 0)
                continue;

            await _deadLetters.WriteAsync(BuildDeadLetter(row, "retry budget exhausted"), ct).ConfigureAwait(false);
            _log.LogInformation(
                "Slack outbox row {RowId} (ConnectionId={ConnectionId}, Kind={Kind}, AttemptCount={AttemptCount}) dead-lettered: retry budget exhausted",
                row.Id, row.ConnectionId, row.Kind, row.AttemptCount);
        }
    }

    private async Task SurfaceClaimedTimeoutAsync(int batchSize, CancellationToken ct)
    {
        var rows = await _store.ListClaimedPastTimeoutAsync(batchSize, ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            await _store.MarkDeliveryUncertainAsync(row.ProjectId, row.Id, "claim timeout", ct).ConfigureAwait(false);
            _log.LogInformation(
                "Slack outbox row {RowId} (ConnectionId={ConnectionId}, ClaimedAt={ClaimedAt}) flipped to DeliveryUncertain: claim timeout",
                row.Id, row.ConnectionId, row.ClaimedAt);
        }
    }

    private async Task DeadLetterUncertainTimeoutAsync(int batchSize, CancellationToken ct)
    {
        var rows = await _store.ListUncertainPastTimeoutAsync(batchSize, ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var updated = await _store.MarkDeadLetteredAsync(row.ProjectId, row.Id, "delivery uncertain timeout", ct).ConfigureAwait(false);
            if (updated == 0)
                continue;

            await _deadLetters.WriteAsync(BuildDeadLetter(row, "delivery uncertain timeout"), ct).ConfigureAwait(false);
            _log.LogInformation(
                "Slack outbox row {RowId} (ConnectionId={ConnectionId}, DeliveryUncertainAt={DeliveryUncertainAt}) dead-lettered: uncertain timeout",
                row.Id, row.ConnectionId, row.DeliveryUncertainAt);
        }
    }

    private async Task RecoverBackpressureAsync(CancellationToken ct)
    {
        var candidates = await _connectionStore.ListBackpressuredAsync(ct).ConfigureAwait(false);
        if (candidates.Count == 0)
            return;

        var options = _options.Value;
        var inboxCapacity = options.InboxCapacityPerConnection;
        var outboxCapacity = options.OutboxCapacityPerConnection;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var pendingInbox = await _inboxStore.CountPendingAsync(candidate.ProjectId, candidate.ConnectionId, ct).ConfigureAwait(false);
            var pendingOutbox = await _store.CountPendingAsync(candidate.ProjectId, candidate.ConnectionId, ct).ConfigureAwait(false);
            if (pendingInbox >= inboxCapacity || pendingOutbox >= outboxCapacity)
                continue;

            var updated = await _healthBackpressurer.RecoverBackpressuredAsync(candidate.ProjectId, candidate.ConnectionId, ct).ConfigureAwait(false);
            if (updated == 0)
                continue;

            _log.LogInformation(
                "Slack Connection {ProjectId}/{ConnectionId} recovered from backpressure (reason={Reason}); pendingInbox={PendingInbox} pendingOutbox={PendingOutbox} inboxCapacity={InboxCapacity} outboxCapacity={OutboxCapacity}",
                candidate.ProjectId, candidate.ConnectionId, candidate.HealthReason, pendingInbox, pendingOutbox, inboxCapacity, outboxCapacity);
        }
    }

    private DeadLetterRow BuildDeadLetter(SlackOutboxRow row, string reason) => new()
    {
        Origin = DeadLetterOrigin,
        Id = StableDeadLetterId(row.Id),
        Source = $"slack/outbox/{row.ConnectionId}",
        EventId = row.Id,
        Type = $"slack.outbox.{row.Kind}",
        Time = _timeProvider.GetUtcNow(),
        SpecVersion = "1.0",
        Subject = row.DispatchRef,
        DataContentType = "application/json",
        Data = ParseData(row),
        ExtensionsJson = "{}",
        FailingHandler = "slack-outbox-dispatcher",
        ErrorMessage = reason,
        ErrorStack = null,
        AttemptCount = row.AttemptCount,
        DeadLetteredAt = _timeProvider.GetUtcNow(),
    };

    private static JsonElement ParseData(SlackOutboxRow row)
    {
        var payload = string.IsNullOrEmpty(row.PayloadJson) ? "{}" : row.PayloadJson;
        return JsonDocument.Parse(payload).RootElement.Clone();
    }

    private static long StableDeadLetterId(string rowId)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(rowId), digest);
        return BinaryPrimitives.ReadInt64LittleEndian(digest);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _dispatchGate.Dispose();
    }
}
