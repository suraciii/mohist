using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Events.Hub;

namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// In-process SignalR implementation of
/// <see cref="ITranscriptEventPublisher"/>. Fans each envelope out via
/// <c>IHubContext&lt;MohistHub, IEventsClient&gt;</c> to every connection
/// whose per-connection subscription set in
/// <see cref="ConnectionSubscriptionRegistry"/> contains
/// <see cref="TranscriptEnvelope.Type"/>.
///
/// <para>
/// This implementation deliberately does NOT consult
/// <see cref="IUserNotificationDispatcher"/> or publish through
/// <see cref="IEventPublisher"/>: transcript events are observation
/// data, not domain events, and mixing them into the domain bus would
/// pollute audit logs and force unwanted fan-out to consumers that
/// have explicitly opted out of transcript data.
/// </para>
/// </summary>
public sealed class SignalRTranscriptEventPublisher : ITranscriptEventPublisher
{
    private readonly IHubContext<MohistHub, IEventsClient> _hub;
    private readonly ConnectionSubscriptionRegistry _registry;
    private readonly ILogger<SignalRTranscriptEventPublisher> _log;

    public SignalRTranscriptEventPublisher(
        IHubContext<MohistHub, IEventsClient> hub,
        ConnectionSubscriptionRegistry registry,
        ILogger<SignalRTranscriptEventPublisher> log)
    {
        _hub = hub;
        _registry = registry;
        _log = log;
    }

    public async Task PublishAsync(TranscriptEnvelope envelope, CancellationToken ct = default)
    {
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));
        if (string.IsNullOrEmpty(envelope.Type)) return;

        var clients = _hub.Clients;
        var sent = 0;

        foreach (var connectionId in _registry.ConnectionIds)
        {
            if (!_registry.ShouldNotify(connectionId, envelope.Type))
                continue;

            try
            {
                await clients
                    .Client(connectionId)
                    .OnTranscriptEvent(envelope)
                    .ConfigureAwait(false);
                sent++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "TranscriptEventPublisher failed to forward {Type} to {ConnectionId}",
                    envelope.Type, connectionId);
            }
        }

        if (sent == 0)
        {
            _log.LogDebug(
                "TranscriptEventPublisher dropped {Type} for session {SessionId} (no subscribers)",
                envelope.Type, envelope.SessionId);
        }
    }
}
