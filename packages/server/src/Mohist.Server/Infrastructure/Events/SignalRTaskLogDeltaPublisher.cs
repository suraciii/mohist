using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Events.Hub;

namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// In-process SignalR implementation of
/// <see cref="ITaskLogDeltaPublisher"/>. Fans each envelope out via
/// <c>IHubContext&lt;MohistHub, IEventsClient&gt;</c> to every
/// connection that has BOTH opted into the task-log type AND
/// declared interest in the envelope's
/// <c>(workflowRunId, taskId)</c> pair via
/// <see cref="ConnectionSubscriptionRegistry.ShouldNotifyTaskLog"/>.
///
/// <para>
/// This implementation deliberately does NOT consult
/// <see cref="IUserNotificationDispatcher"/> or publish through
/// <see cref="IEventPublisher"/>: task-log events are work-scoped
/// runtime data, not domain events, and mixing them into the
/// domain bus would pollute audit logs and force unwanted fan-out
/// to consumers that have explicitly opted out of task-log data.
/// </para>
///
/// <para>
/// On-demand fan-out. When no client is currently subscribed to the
/// task, the publisher silently completes; nothing is sent and
/// nothing is thrown. Per-send errors are isolated so one failing
/// client cannot abort delivery to others and cannot affect the
/// authoritative persist that has already completed in
/// <c>TaskLogService</c>.
/// </para>
/// </summary>
public sealed class SignalRTaskLogDeltaPublisher : ITaskLogDeltaPublisher
{
    private readonly IHubContext<MohistHub, IEventsClient> _hub;
    private readonly ConnectionSubscriptionRegistry _registry;
    private readonly ILogger<SignalRTaskLogDeltaPublisher> _log;

    public SignalRTaskLogDeltaPublisher(
        IHubContext<MohistHub, IEventsClient> hub,
        ConnectionSubscriptionRegistry registry,
        ILogger<SignalRTaskLogDeltaPublisher> log)
    {
        _hub = hub;
        _registry = registry;
        _log = log;
    }

    public async Task PublishAsync(TaskLogDeltaEnvelope envelope, CancellationToken ct = default)
    {
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));

        // Empty work scope ⇒ nothing to deliver against. Treat as
        // best-effort no-op: there is no client key the Web would
        // match on, so any push would be invalid.
        if (string.IsNullOrEmpty(envelope.WorkId)) return;

        var clients = _hub.Clients;
        var sent = 0;

        foreach (var connectionId in _registry.ConnectionIds)
        {
            // Both dimensions checked per connection:
            //   * type-subscription contains task-log.delta; AND
            //   * task-log scope contains (workflowRunId, taskId).
            // Either missing ⇒ connection not interested; skip.
            if (!_registry.ShouldNotifyTaskLog(connectionId, envelope.OwnerId, envelope.TaskId))
                continue;

            try
            {
                await clients
                    .Client(connectionId)
                    .OnTaskLogDelta(envelope)
                    .ConfigureAwait(false);
                sent++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "TaskLogDeltaPublisher failed to forward task-log delta for {OwnerKind}/{OwnerId}/{WorkId} to {ConnectionId}",
                    envelope.OwnerKind, envelope.OwnerId, envelope.WorkId, connectionId);
            }
        }

        if (sent == 0)
        {
            _log.LogDebug(
                "TaskLogDeltaPublisher dropped task-log delta for {OwnerKind}/{OwnerId}/{WorkId} (no subscribers)",
                envelope.OwnerKind, envelope.OwnerId, envelope.WorkId);
        }
    }
}
