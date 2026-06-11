namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Publishes a transcript (non-domain, observation-only) event from
/// <c>AgentSessionGrain.AppendRuntimeEventsAsync</c> to subscribed
/// SignalR connections on the dedicated <c>OnTranscriptEvent</c>
/// channel.
///
/// <para>
/// Transcript events are NOT domain events: they describe what the
/// agent is doing (a text chunk, a tool call, a Ralph task progress
/// tick, a liveness heartbeat, …) without changing the
/// <c>AgentSession</c> lifecycle. They therefore do not — and must
/// not — flow through <see cref="IEventPublisher"/> /
/// <see cref="Mohist.Server.Events.Hub.EventBridge"/>. This publisher
/// is the <b>only</b> realtime path for transcript observation data.
/// </para>
///
/// <para>
/// Filtering reuses the existing
/// <see cref="ConnectionSubscriptionRegistry"/>: a connection receives
/// an envelope iff it has called <c>SetSubscriptionsAsync</c> with
/// the envelope's <see cref="TranscriptEnvelope.Type"/> in its
/// subscription set. The Web is expected to include the canonical
/// transcript event set in its subscription list when it wants live
/// transcript data, including both the legacy coder event names and
/// runner-native aliases such as <c>agent_message_chunk</c>,
/// <c>agent_thought_chunk</c>, <c>tool_call</c>, and
/// <c>tool_call_update</c>.
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
