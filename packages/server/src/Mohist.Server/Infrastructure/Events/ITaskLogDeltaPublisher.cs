namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Publishes a freshly persisted task-log increment from
/// <c>TaskLogService.AppendAsync</c> to subscribed SignalR
/// connections on the dedicated <c>OnTaskLogDelta</c> channel.
///
/// <para>
/// Task-log events are NOT domain events: they describe what an ops
/// task is doing (its captured output lines) without changing any
/// lifecycle state. They therefore do not — and must not — flow through
/// <see cref="IEventPublisher"/> /
/// <see cref="Mohist.Server.Events.Hub.EventBridge"/>. This publisher
/// is the <b>only</b> realtime path for task-log runtime data.
/// </para>
///
/// <para>
/// Filtering has TWO dimensions, both checked on every fan-out:
/// <list type="number">
///   <item><b>Type-subscription</b>: the connection must have
///         <see cref="TaskLogDeltaSubscriptionType"/> in its
///         <see cref="ConnectionSubscriptionRegistry"/>
///         subscription set. Without this opt-in the publisher
///         never reaches the client. The Web is expected to add
///         this type when it wants live task-log fan-out.</item>
///   <item><b>Task scope</b>: the connection must additionally have
///         the delta's <c>(workflowRunId, taskId)</c> pair in its
///         per-task subscribe set (see
///         <see cref="ConnectionSubscriptionRegistry.ShouldNotifyTaskLog"/>).
///         This is the on-demand gate — a client only receives
///         deltas for tasks it has currently expanded. When no
///         client has subscribed to a given task, no fan-out is
///         produced.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Channel separation</b>. This publisher is a sibling of
/// <see cref="ITranscriptEventPublisher"/>, never a reuse. The agent
/// transcript rail pushes session-scoped deltas on
/// <c>OnTranscriptEvent</c>; this rail pushes work/task-scoped
/// deltas on <c>OnTaskLogDelta</c>. A transcript envelope never
/// appears here and a task-log envelope never appears on the
/// transcript path.
/// </para>
/// </summary>
public interface ITaskLogDeltaPublisher
{
    /// <summary>
    /// Fan <paramref name="envelope"/> out to every SignalR
    /// connection whose subscription set contains
    /// <see cref="TaskLogDeltaSubscriptionType"/> AND whose
    /// task-log scope contains the envelope's
    /// <c>(workflowRunId, taskId)</c>. Implementations MUST NOT
    /// route through <see cref="IEventPublisher"/> or
    /// <see cref="Mohist.Server.Events.Hub.EventBridge"/>; the
    /// task-log channel is intentionally separate from the domain
    /// event bus. A failure on any single connection MUST be
    /// swallowed and logged so fan-out continues with remaining
    /// subscribers — best-effort distribution never breaks
    /// persistence or execution.
    /// </summary>
    Task PublishAsync(TaskLogDeltaEnvelope envelope, CancellationToken ct = default);
}

/// <summary>
/// Canonical subscription marker for the task-log realtime channel.
/// Clients add this string to their
/// <see cref="ConnectionSubscriptionRegistry"/> subscription set
/// (via <c>SetSubscriptionsAsync</c> / <c>SubscribeAsync</c> on
/// <c>MohistHub</c>) to opt into live task-log fan-out. Distinct
/// from agent-session transcript types so the two channels stay
/// physically separate.
/// </summary>
public static class TaskLogDeltaSubscription
{
    public const string TaskLogDeltaSubscriptionType = "task-log.delta";
}
