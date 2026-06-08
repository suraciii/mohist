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
    /// by predicate. Synchronous return; typed subscribers are invoked
    /// concurrently (fire-and-forget). Use <see cref="EmitAsync"/> when
    /// the caller wants to await subscriber completion or surface
    /// subscriber exceptions.
    /// </summary>
    void Emit(CloudEvent cloudEvent);

    /// <summary>
    /// CloudEvents 1.0.2 emit that awaits typed subscribers. Use this from
    /// Orleans grain methods or hosted services that need to observe
    /// subscriber exceptions (e.g. so a subscriber's <c>InvalidOperationException</c>
    /// does not surface as <c>UnobservedTaskException</c> on the next GC).
    /// </summary>
    Task EmitAsync(CloudEvent cloudEvent, CancellationToken ct = default);

    /// <summary>
    /// Subscribe to all events of a specific <c>type</c>. The handler may
    /// be an <c>async Task</c> lambda so it can <c>await</c> cross-grain
    /// Orleans calls without fire-and-forget discarding exceptions. The
    /// bus runs each handler concurrently; one slow handler does not block
    /// others. Exceptions are caught and logged per handler.
    /// </summary>
    IDisposable OnType(string type, Func<CloudEvent, Task> handler);

    /// <summary>
    /// Subscribe via a predicate over the full CloudEvent envelope. Use when
    /// filtering by extension attributes (e.g. <c>projectid</c>,
    /// <c>workflowrunid</c>) rather than by exact <c>type</c>.
    /// </summary>
    IDisposable OnAny(Func<CloudEvent, bool> filter, Func<CloudEvent, Task> handler);
}
