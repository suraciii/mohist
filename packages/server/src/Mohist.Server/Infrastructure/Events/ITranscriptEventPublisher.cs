namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Publishes a transcript (non-domain runtime) event from
/// <c>AgentSessionGrain.AppendRuntimeEventsAsync</c> to subscribed SignalR
/// connections on the dedicated <c>OnTranscriptEvent</c> channel.
///
/// <para>
/// Transcript events are NOT domain events: they describe what the
/// agent session is doing (input, text/reasoning deltas, tool calls,
/// usage/model runtime events, liveness, and close notices) without changing the
/// <c>AgentSession</c> lifecycle. They therefore do not — and must
/// not — flow through <see cref="IEventPublisher"/> /
/// <see cref="Mohist.Server.Events.Hub.EventBridge"/>. This publisher
/// is the <b>only</b> realtime path for transcript runtime event data.
/// </para>
///
/// <para>
/// Filtering reuses the existing
/// <see cref="ConnectionSubscriptionRegistry"/>: a connection receives
/// an envelope iff it has called <c>SetSubscriptionsAsync</c> with
/// the envelope's <see cref="TranscriptEnvelope.Type"/> in its
/// subscription set. The Web is expected to include the canonical
/// transcript event set in its subscription list when it wants live
/// transcript data, such as <c>session.input</c>,
/// <c>message.delta</c>, <c>reasoning.delta</c>,
/// <c>tool_call.started</c>, <c>tool_call.updated</c>,
/// <c>tool_call.completed</c>, and <c>session.activity</c>.
/// </para>
/// </summary>
public interface ITranscriptEventPublisher
{
    /// <summary>
    /// Fan <paramref name="envelope"/> out to every SignalR connection
    /// that subscribed to <see cref="TranscriptEnvelope.Type"/>.
    /// Implementations MUST NOT route through
    /// <see cref="IEventPublisher"/> or
    /// <see cref="Mohist.Server.Events.Hub.EventBridge"/>; the
    /// transcript channel is intentionally separate from the domain
    /// event bus.
    /// </summary>
    Task PublishAsync(TranscriptEnvelope envelope, CancellationToken ct = default);
}
