using System.Collections.Concurrent;
using CloudNative.CloudEvents;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Process-local hot-path mirror of the per-connection subscription
/// set held in <see cref="Mohist.Server.User.Grains.IConnectionSubscriptionGrain"/>.
/// Maintained by <c>MohistHub</c> on every connect / disconnect /
/// <c>Subscribe</c> / <c>Unsubscribe</c>; read by
/// <see cref="UserNotificationDispatcher"/> on every bus emit.
///
/// <para>
/// <b>Why mirror</b>. Asking the grain for
/// <c>ShouldNotify</c> on every emit × every connection is O(N)
/// grain calls per emit. For a single-silo deployment with a
/// handful of open tabs this is fine; the mirror becomes essential
/// the moment we have more than a low-double-digit number of
/// connections or more than one silo. The mirror is
/// <i>authoritative for the dispatcher</i>; the grain is
/// authoritative for "what is the durable state of record" and
/// is the source the mirror is rebuilt from on connection
/// open / reconnect.
/// </para>
///
/// <para>
/// <b>Why not just SignalR groups</b>. A SignalR group membership
/// is process-local to the silo that hosts the hub. With one
/// silo this is equivalent to a registry; with multiple silos,
/// the membership is only known to the silo that handled the
/// <c>Groups.AddToGroupAsync</c> call. Routing the dispatcher
/// through groups therefore needs an extra "which silo has the
/// connection" round trip, which is what the grain + connectionId
/// + IHubContext pattern already gives us for free. The registry
/// is a thin cache, not a replacement for that pattern.
/// </para>
/// </summary>
public sealed class ConnectionSubscriptionRegistry : ISingletonService
{
    /// <summary>
    /// connectionId → set of event types the connection wants. A
    /// connection is registered on SignalR <c>OnConnectedAsync</c>
    /// and unregistered on <c>OnDisconnectedAsync</c>. An empty
    /// set means "the client has not yet called Subscribe" — the
    /// dispatcher will not push anything to such a connection, which
    /// is the correct default for a freshly opened tab.
    /// </summary>
    private readonly ConcurrentDictionary<string, HashSet<string>> _byConnection = new(StringComparer.Ordinal);

    /// <summary>
    /// connectionId → declared project affinity, or <c>null</c> when the
    /// connection did not declare a project (e.g. an admin / cross-project
    /// tab). Captured from the SignalR <c>?projectId=</c> query string by
    /// <c>MohistHub.OnConnectedAsync</c>. Read by
    /// <see cref="UserNotificationDispatcher"/> to gate project-scoped
    /// events: when the event carries <c>extensions["projectid"]</c> AND
    /// this connection has declared a project, the connection only
    /// receives the event on project match. Transport-level presentation
    /// state, intentionally not durable — see the <c>project-inbox</c>
    /// spec which keeps live subscriptions as transport state.
    /// </summary>
    private readonly ConcurrentDictionary<string, string?> _byConnectionProjectId = new(StringComparer.Ordinal);

    /// <summary>
    /// connectionId → set of <c>(workflowRunId, taskId)</c> pairs the
    /// connection has expanded in the Web and therefore wants
    /// task-log deltas for. Mirrors the
    /// <see cref="_byConnectionProjectId"/> affinity map's structure
    /// and lifetime. Read on every task-log fan-out by
    /// <see cref="SignalRTaskLogDeltaPublisher"/> to gate on-demand
    /// distribution: an empty set means the connection has not
    /// expanded any task, so the publisher silently drops any
    /// delta for this connection. A task-log <c>Subscribe</c> /
    /// <c>Unsubscribe</c> hub call updates this set together
    /// with the per-task type-subscription. Transport-level state,
    /// NOT durable — the Web is expected to re-assert the per-task
    /// subscribe on SignalR reconnect.
    /// </summary>
    private readonly ConcurrentDictionary<string, HashSet<TaskLogSubscriptionKey>> _byConnectionTaskLog = new(StringComparer.Ordinal);

    /// <summary>
    /// Snapshot of all currently-tracked connection IDs. Read by
    /// <see cref="UserNotificationDispatcher"/> on every emit.
    /// </summary>
    public IReadOnlyCollection<string> ConnectionIds
    {
        get
        {
            // Materialise the ConcurrentDictionary's KeyCollection
            // into a snapshot list. The KeyCollection is
            // ICollection<T> and snapshots well.
            lock (_byConnection)
            {
                return _byConnection.Keys.ToList();
            }
        }
    }

    public void RegisterConnection(string connectionId)
    {
        _byConnection.TryAdd(connectionId, new HashSet<string>(StringComparer.Ordinal));
        _byConnectionProjectId.TryAdd(connectionId, null);
        _byConnectionTaskLog.TryAdd(connectionId, new HashSet<TaskLogSubscriptionKey>());
    }

    public void UnregisterConnection(string connectionId)
    {
        _byConnection.TryRemove(connectionId, out _);
        _byConnectionProjectId.TryRemove(connectionId, out _);
        _byConnectionTaskLog.TryRemove(connectionId, out _);
    }

    public void SetSubscriptions(string connectionId, IReadOnlyCollection<string> eventTypes)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (eventTypes is not null)
        {
            foreach (var t in eventTypes)
            {
                if (!string.IsNullOrEmpty(t)) set.Add(t);
            }
        }
        _byConnection[connectionId] = set;
    }

    public void Subscribe(string connectionId, string eventType)
    {
        if (string.IsNullOrEmpty(eventType)) return;
        var set = _byConnection.GetOrAdd(connectionId, _ => new HashSet<string>(StringComparer.Ordinal));
        lock (set) { set.Add(eventType); }
    }

    public void Unsubscribe(string connectionId, string eventType)
    {
        if (string.IsNullOrEmpty(eventType)) return;
        if (_byConnection.TryGetValue(connectionId, out var set))
        {
            lock (set) { set.Remove(eventType); }
        }
    }

    public bool ShouldNotify(string connectionId, string eventType)
    {
        if (string.IsNullOrEmpty(eventType)) return false;
        return _byConnection.TryGetValue(connectionId, out var set)
            && set.Contains(eventType);
    }

    /// <summary>
    /// Declare the project this connection is scoped to. Called once
    /// from <c>MohistHub.OnConnectedAsync</c> with the value of the
    /// <c>?projectId=</c> query string (already sent by the Web's
    /// <c>events-hub.ts</c>). A null / empty / whitespace value
    /// normalises to <c>null</c> — the connection keeps type-only
    /// matching behaviour and is treated as a cross-project
    /// connection by the dispatcher. Re-invoking replaces the
    /// affinity (a fresh SignalR reconnect that picks a different
    /// project rotates it cleanly).
    /// </summary>
    public void SetProjectId(string connectionId, string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            projectId = null;
        }
        _byConnectionProjectId[connectionId] = projectId;
    }

    /// <summary>
    /// Read the declared project affinity for a connection. Returns
    /// <c>false</c> (and <c>null</c>) when the connection is not
    /// registered — the dispatcher's gating rule treats that as
    /// "no declared project" and falls back to type-only matching.
    /// </summary>
    public bool TryGetProjectId(string connectionId, out string? projectId)
    {
        if (_byConnectionProjectId.TryGetValue(connectionId, out var stored))
        {
            projectId = stored;
            return true;
        }
        projectId = null;
        return false;
    }

    /// <summary>
    /// Replace the connection's task-log scope with exactly
    /// <paramref name="subscriptions"/>. Idempotent against
    /// repeated invocations with the same set. The
    /// <see cref="SignalRTaskLogDeltaPublisher"/> reads this set
    /// on every fan-out to gate on-demand delivery: a connection
    /// outside the set never receives a delta even when its
    /// type-subscription contains the task-log marker.
    /// </summary>
    public void SetTaskLogSubscriptions(
        string connectionId,
        IReadOnlyCollection<(string WorkflowRunId, string TaskId)> subscriptions)
    {
        var set = new HashSet<TaskLogSubscriptionKey>();
        if (subscriptions is not null)
        {
            foreach (var (runId, taskId) in subscriptions)
            {
                if (string.IsNullOrEmpty(runId) || string.IsNullOrEmpty(taskId)) continue;
                set.Add(new TaskLogSubscriptionKey(runId, taskId));
            }
        }
        _byConnectionTaskLog[connectionId] = set;
    }

    /// <summary>
    /// Add a single <c>(workflowRunId, taskId)</c> pair to the
    /// connection's task-log scope. Empty / whitespace pair
    /// segments are ignored. Idempotent.
    /// </summary>
    public void SubscribeTaskLog(string connectionId, string workflowRunId, string taskId)
    {
        if (string.IsNullOrEmpty(workflowRunId) || string.IsNullOrEmpty(taskId)) return;
        var set = _byConnectionTaskLog.GetOrAdd(
            connectionId,
            _ => new HashSet<TaskLogSubscriptionKey>());
        var key = new TaskLogSubscriptionKey(workflowRunId, taskId);
        lock (set) { set.Add(key); }
    }

    /// <summary>
    /// Remove a single <c>(workflowRunId, taskId)</c> pair from
    /// the connection's task-log scope. Empty / whitespace pair
    /// segments are ignored. No-op when the pair was never
    /// present or the connection is not registered.
    /// </summary>
    public void UnsubscribeTaskLog(string connectionId, string workflowRunId, string taskId)
    {
        if (string.IsNullOrEmpty(workflowRunId) || string.IsNullOrEmpty(taskId)) return;
        if (_byConnectionTaskLog.TryGetValue(connectionId, out var set))
        {
            var key = new TaskLogSubscriptionKey(workflowRunId, taskId);
            lock (set) { set.Remove(key); }
        }
    }

    /// <summary>
    /// Returns <c>true</c> when the connection has BOTH:
    /// <list type="bullet">
    ///   <item>opted into the task-log realtime channel via its
    ///         type-subscription set (<see cref="ShouldNotify"/>);
    ///         AND</item>
    ///   <item>declared interest in the given
    ///         <c>(workflowRunId, taskId)</c> via its task-log
    ///         scope set.</item>
    /// </list>
    /// A <c>null</c> or empty <paramref name="workflowRunId"/> /
    /// <paramref name="taskId"/> means the task-log scope can
    /// never match — the publisher treats that as "no fan-out".
    /// </summary>
    public bool ShouldNotifyTaskLog(string connectionId, string? workflowRunId, string? taskId)
    {
        if (!ShouldNotify(connectionId, TaskLogDeltaSubscription.TaskLogDeltaSubscriptionType))
            return false;
        if (string.IsNullOrEmpty(workflowRunId) || string.IsNullOrEmpty(taskId))
            return false;
        if (!_byConnectionTaskLog.TryGetValue(connectionId, out var set))
            return false;
        var key = new TaskLogSubscriptionKey(workflowRunId, taskId);
        return set.Contains(key);
    }
}

/// <summary>
/// Composite key for the per-connection task-log scope set. The
/// <c>(workflowRunId, taskId)</c> pair is the on-demand delivery
/// dimension: a client receives a delta for a
/// task only when it has explicitly subscribed to that pair.
/// </summary>
public readonly record struct TaskLogSubscriptionKey(
    string WorkflowRunId,
    string TaskId);

/// <summary>
/// Default <see cref="IUserNotificationDispatcher"/> implementation:
/// for one incoming <see cref="CloudEvent"/>, ask the
/// <see cref="ConnectionSubscriptionRegistry"/> "do you want
/// this?" for every active connection and return the set of
/// connection IDs that said yes. The
/// <see cref="Mohist.Server.Events.Hub.EventBridge"/> then pushes
/// the event to those connection IDs via SignalR
/// <c>IHubContext.Clients.Client(connectionId)</c>.
///
/// <para>
/// <b>Cost model</b>. One dispatcher call is O(N) over the
/// current connection set, where N is the number of open browser
/// tabs. The check per connection is a single hash-set lookup, no
/// allocation, no I/O. N is bounded by the deployment size; at the
/// scales Mohist actually runs (low double digits in test, low
/// hundreds in production) this is the right shape.
/// </para>
///
/// <para>
/// <b>Why not SignalR groups</b>. See the discussion on
/// <see cref="ConnectionSubscriptionRegistry"/> — groups are
/// process-local, the registry/grain pair is portable across
/// silos.
/// </para>
/// </summary>
public sealed class UserNotificationDispatcher : IUserNotificationDispatcher
{
    private readonly ConnectionSubscriptionRegistry _registry;

    /// <summary>
    /// The CloudEvents extension attribute key that the project's
    /// established routing convention uses to stamp the owning
    /// project on a CloudEvent. Mirrored by
    /// <c>IssueGrain.cs:605</c> and read by <c>InboxProjectionHandler</c>;
    /// the inbox hint published from <c>InboxProjectionHandler.ProjectAsync</c>
    /// also stamps this key.
    /// </summary>
    internal const string ProjectIdExtension = "projectid";

    public UserNotificationDispatcher(ConnectionSubscriptionRegistry registry)
    {
        _registry = registry;
    }

    public Task<IReadOnlySet<string>> ResolveTargetConnectionsAsync(
        CloudEvent cloudEvent,
        CancellationToken ct)
    {
        var eventType = cloudEvent.Type ?? string.Empty;
        if (string.IsNullOrEmpty(eventType))
        {
            return Task.FromResult<IReadOnlySet<string>>(_empty);
        }

        // Project-scoped routing is gated on BOTH sides: the event
        // must carry extensions["projectid"] AND the connection must
        // have declared a project. When either side is absent the
        // dispatcher falls back to type-only matching — every
        // existing non-projectid event (e.g. agent session runtime
        // events, transcript events that reach this dispatcher by
        // mistake, anything published without a project stamp) is
        // byte-for-byte unchanged. See design.md D3 for the blast-
        // radius discussion.
        string? eventProjectId = null;
        if (cloudEvent.Extensions is { Count: > 0 }
            && cloudEvent.Extensions.TryGetValue(ProjectIdExtension, out var rawProjectExtension)
            && rawProjectExtension is string stamped
            && !string.IsNullOrWhiteSpace(stamped))
        {
            eventProjectId = stamped;
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var connectionId in _registry.ConnectionIds)
        {
            if (!_registry.ShouldNotify(connectionId, eventType))
            {
                continue;
            }

            // Apply the project gate only when BOTH the event
            // carries extensions["projectid"] AND the connection
            // has declared a project. If either side is missing
            // the affinity, we fall back to type-only matching —
            // this preserves the existing behaviour for every
            // connection that hasn't yet declared a project
            // (cross-project / admin tabs) and for every event
            // that arrives without the projectid stamp (legacy
            // emits, agent session runtime events, anything
            // published outside the inbox / issue convention).
            if (eventProjectId is not null
                && _registry.TryGetProjectId(connectionId, out var connectionProjectId)
                && !string.IsNullOrEmpty(connectionProjectId))
            {
                if (!StringComparer.Ordinal.Equals(connectionProjectId, eventProjectId))
                {
                    // The connection declared a project AND the
                    // event has a projectid, but they don't match
                    // — this is the cross-project leakage guard.
                    continue;
                }
            }

            result.Add(connectionId);
        }
        return Task.FromResult<IReadOnlySet<string>>(result);
    }

    private static readonly IReadOnlySet<string> _empty =
        new HashSet<string>(StringComparer.Ordinal);
}
