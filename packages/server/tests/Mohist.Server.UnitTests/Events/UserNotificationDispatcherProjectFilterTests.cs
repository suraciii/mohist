using CloudNative.CloudEvents;
using Xunit;

namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Behavioural tests for the project-id gate added to
/// <see cref="UserNotificationDispatcher.ResolveTargetConnectionsAsync"/>.
/// The gate is intentionally gated on BOTH the event carrying
/// <c>extensions["projectid"]</c> AND the connection declaring a
/// project affinity; when either side is absent the dispatcher falls
/// back to type-only matching. See design.md D3 for the full rationale
/// and blast-radius discussion.
/// </summary>
public class UserNotificationDispatcherProjectFilterTests
{
    private const string InboxHintType = "com.mohist.inbox.item-persisted";
    private const string LegacyWorkflowType = "com.mohist.workflow.stage.started";

    [Fact]
    public async Task ProjectAConnection_ReceivesProjectAHint()
    {
        var registry = NewRegistry(("conn-A", "proj-A"));
        var dispatcher = new UserNotificationDispatcher(registry);

        var targets = await dispatcher.ResolveTargetConnectionsAsync(
            NewEvent(InboxHintType, projectId: "proj-A"), CancellationToken.None);

        Assert.Contains("conn-A", targets);
    }

    [Fact]
    public async Task ProjectAConnection_DoesNotReceiveProjectBHint()
    {
        var registry = NewRegistry(("conn-A", "proj-A"));
        var dispatcher = new UserNotificationDispatcher(registry);

        var targets = await dispatcher.ResolveTargetConnectionsAsync(
            NewEvent(InboxHintType, projectId: "proj-B"), CancellationToken.None);

        Assert.DoesNotContain("conn-A", targets);
    }

    [Fact]
    public async Task ProjectBConnection_DoesNotReceiveProjectAHint()
    {
        var registry = NewRegistry(("conn-B", "proj-B"));
        var dispatcher = new UserNotificationDispatcher(registry);

        var targets = await dispatcher.ResolveTargetConnectionsAsync(
            NewEvent(InboxHintType, projectId: "proj-A"), CancellationToken.None);

        Assert.DoesNotContain("conn-B", targets);
    }

    [Fact]
    public async Task TwoSessionsSubscribedToOwningProjects_EachOnlyReceivesOwnProjectsHint()
    {
        var registry = NewRegistry(("conn-A", "proj-A"), ("conn-B", "proj-B"));
        var dispatcher = new UserNotificationDispatcher(registry);

        var projectATargets = await dispatcher.ResolveTargetConnectionsAsync(
            NewEvent(InboxHintType, projectId: "proj-A"), CancellationToken.None);
        var projectBTargets = await dispatcher.ResolveTargetConnectionsAsync(
            NewEvent(InboxHintType, projectId: "proj-B"), CancellationToken.None);

        // No cross-project leakage: each session receives only the
        // hint for the project it subscribed to.
        Assert.Single(projectATargets);
        Assert.Contains("conn-A", projectATargets);
        Assert.DoesNotContain("conn-B", projectATargets);

        Assert.Single(projectBTargets);
        Assert.Contains("conn-B", projectBTargets);
        Assert.DoesNotContain("conn-A", projectBTargets);
    }

    [Fact]
    public async Task UnsubscribeOrEmptySubscriptionSet_FiltersEveryProjectHint()
    {
        // A session whose subscription set is empty (default for a
        // freshly opened tab before the first SetSubscriptionsAsync
        // call) must not receive anything, regardless of project
        // affinity or event project stamp.
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-empty");
        registry.SetProjectId("conn-empty", "proj-A");

        var dispatcher = new UserNotificationDispatcher(registry);

        var targets = await dispatcher.ResolveTargetConnectionsAsync(
            NewEvent(InboxHintType, projectId: "proj-A"), CancellationToken.None);

        Assert.DoesNotContain("conn-empty", targets);
    }

    [Fact]
    public async Task EventWithoutProjectExtension_ReachesAllProjectAffinitizedConnections()
    {
        // Regression test: events without extensions["projectid"]
        // (legacy events, agent session runtime events, anything
        // published outside the issue / inbox convention) must be
        // byte-for-byte unchanged. The gate is inert when the
        // extension is absent, so type-only matching applies.
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.RegisterConnection("conn-B");
        registry.SetProjectId("conn-A", "proj-A");
        registry.SetProjectId("conn-B", "proj-B");
        registry.Subscribe("conn-A", LegacyWorkflowType);
        registry.Subscribe("conn-B", LegacyWorkflowType);

        var dispatcher = new UserNotificationDispatcher(registry);

        var targets = await dispatcher.ResolveTargetConnectionsAsync(
            NewEvent(LegacyWorkflowType, projectId: null), CancellationToken.None);

        Assert.Equal(2, targets.Count);
        Assert.Contains("conn-A", targets);
        Assert.Contains("conn-B", targets);
    }

    [Fact]
    public async Task EventWithProjectExtension_ConnectionWithoutProjectAffinity_KeepsTypeOnlyMatching()
    {
        // Connection that did not declare a projectId keeps
        // type-only matching behaviour — it still receives the
        // event on type match. This is the right default for
        // cross-project / admin tabs and any consumer that hasn't
        // yet been migrated to declare a project.
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-cross");
        registry.Subscribe("conn-cross", InboxHintType);
        // Deliberately no SetProjectId call.

        var dispatcher = new UserNotificationDispatcher(registry);

        var targets = await dispatcher.ResolveTargetConnectionsAsync(
            NewEvent(InboxHintType, projectId: "proj-A"), CancellationToken.None);

        Assert.Contains("conn-cross", targets);
    }

    [Fact]
    public async Task EventWithProjectExtension_ConnectionWithEmptyProjectAffinity_KeepsTypeOnlyMatching()
    {
        // Connection whose project affinity was explicitly cleared
        // (e.g. via SetProjectId(null)) keeps type-only matching
        // even when the event carries a projectid stamp.
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-cleared");
        registry.Subscribe("conn-cleared", InboxHintType);
        registry.SetProjectId("conn-cleared", null);

        var dispatcher = new UserNotificationDispatcher(registry);

        var targets = await dispatcher.ResolveTargetConnectionsAsync(
            NewEvent(InboxHintType, projectId: "proj-A"), CancellationToken.None);

        Assert.Contains("conn-cleared", targets);
    }

    [Fact]
    public async Task ProjectAConnection_EventWithBlankProjectExtension_KeepsTypeOnlyMatching()
    {
        // Edge case: an event whose projectid extension is blank /
        // whitespace must not be gated by the project filter — that
        // is functionally equivalent to no project stamp at all.
        var registry = NewRegistry(("conn-A", "proj-A"));
        var dispatcher = new UserNotificationDispatcher(registry);

        var targets = await dispatcher.ResolveTargetConnectionsAsync(
            NewEvent(InboxHintType, projectId: "   "), CancellationToken.None);

        Assert.Contains("conn-A", targets);
    }

    [Fact]
    public async Task UnsubscribedConnection_ProjectHint_DoesNotReceive()
    {
        // The project filter is layered ON TOP of the type filter;
        // a connection must pass both. A project-A connection
        // subscribed to a different type does not receive the
        // project-A inbox hint.
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.SetProjectId("conn-A", "proj-A");
        registry.Subscribe("conn-A", LegacyWorkflowType);

        var dispatcher = new UserNotificationDispatcher(registry);

        var targets = await dispatcher.ResolveTargetConnectionsAsync(
            NewEvent(InboxHintType, projectId: "proj-A"), CancellationToken.None);

        Assert.DoesNotContain("conn-A", targets);
    }

    [Fact]
    public async Task ProjectMatch_OverManyConnections_ReturnsOnlyOwningProjectConnections()
    {
        // Larger set: confirm the dispatcher iterates the full
        // connection list and applies the gate per-connection
        // without leaking across the rest.
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A1");
        registry.RegisterConnection("conn-A2");
        registry.RegisterConnection("conn-B1");
        registry.RegisterConnection("conn-B2");
        registry.RegisterConnection("conn-cross");
        registry.SetProjectId("conn-A1", "proj-A");
        registry.SetProjectId("conn-A2", "proj-A");
        registry.SetProjectId("conn-B1", "proj-B");
        registry.SetProjectId("conn-B2", "proj-B");
        // conn-cross has no project affinity.

        registry.Subscribe("conn-A1", InboxHintType);
        registry.Subscribe("conn-A2", InboxHintType);
        registry.Subscribe("conn-B1", InboxHintType);
        registry.Subscribe("conn-B2", InboxHintType);
        registry.Subscribe("conn-cross", InboxHintType);

        var dispatcher = new UserNotificationDispatcher(registry);

        var targets = await dispatcher.ResolveTargetConnectionsAsync(
            NewEvent(InboxHintType, projectId: "proj-A"), CancellationToken.None);

        // Only project-A conns and the cross-project conn get
        // through. Project-B conns are filtered out.
        Assert.Contains("conn-A1", targets);
        Assert.Contains("conn-A2", targets);
        Assert.Contains("conn-cross", targets);
        Assert.DoesNotContain("conn-B1", targets);
        Assert.DoesNotContain("conn-B2", targets);
    }

    private static ConnectionSubscriptionRegistry NewRegistry(params (string connectionId, string projectId)[] connections)
    {
        var registry = new ConnectionSubscriptionRegistry();
        foreach (var (connectionId, projectId) in connections)
        {
            registry.RegisterConnection(connectionId);
            registry.SetProjectId(connectionId, projectId);
            registry.Subscribe(connectionId, InboxHintType);
        }
        return registry;
    }

    private static CloudEvent NewEvent(string type, string? projectId)
    {
        var extensions = projectId is null
            ? null
            : new Dictionary<string, string> { ["projectid"] = projectId };
        return new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/test", UriKind.Relative),
            type: type,
            time: TestTime.UtcNow,
            data: null,
            extensions: extensions);
    }
}