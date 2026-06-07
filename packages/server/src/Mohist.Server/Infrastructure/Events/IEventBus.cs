using CloudNative.CloudEvents;

namespace Mohist.Server.Infrastructure.Events;

public interface IEventBus
{
    /// <summary>
    /// Legacy string-name subscribe. Kept for back-compat with emit sites that
    /// have not yet migrated to <see cref="Emit(CloudEvent)"/>.
    /// </summary>
    void On(string eventName, Action<object> handler);

    void Off(string eventName, Action<object> handler);

    /// <summary>
    /// Legacy string-name emit. Maps to the same internal storage as
    /// <see cref="Emit(CloudEvent)"/> by constructing a minimal envelope.
    /// </summary>
    void Emit(string eventName, object data);

    /// <summary>
    /// CloudEvents 1.0.2 emit. The bus dispatches on
    /// <see cref="CloudEvent.Type"/>; subscribers filter by exact type or
    /// by predicate.
    /// </summary>
    void Emit(CloudEvent cloudEvent);

    /// <summary>
    /// Subscribe to all events of a specific <c>type</c>. The handler receives
    /// the full CloudEvent envelope (id, source, type, subject, extensions,
    /// data).
    /// </summary>
    IDisposable OnType(string type, Action<CloudEvent> handler);

    /// <summary>
    /// Subscribe via a predicate over the full CloudEvent envelope. Use when
    /// filtering by extension attributes (e.g. <c>projectid</c>,
    /// <c>workflowrunid</c>) rather than by exact <c>type</c>.
    /// </summary>
    IDisposable OnAny(Func<CloudEvent, bool> filter, Action<CloudEvent> handler);
}
