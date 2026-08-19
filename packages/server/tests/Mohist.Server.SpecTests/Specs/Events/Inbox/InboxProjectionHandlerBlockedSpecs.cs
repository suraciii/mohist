using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Inbox;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events.Inbox;

public class InboxProjectionHandlerBlockedSpecs
{
    [Fact]
    public async Task WorkflowRunBlocked_ProducesNonFailureAgentResultUnconfirmedItem()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database, "proj_a", 42, "Issue 42");
        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunBlocked,
            workflowRunId: "wf_blocked",
            eventId: "evt-blocked",
            projectId: "proj_a",
            issueNumber: 42);

        await handler.HandleAsync(evt, CancellationToken.None);

        var item = Assert.Single(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Equal(NotificationKinds.AgentResultUnconfirmed, item.NotificationKind);
        Assert.NotEqual(NotificationKinds.WorkflowFailed, item.NotificationKind);
    }

    [Fact]
    public async Task WorkflowRunBlocked_ReplayIsIdempotentAndRemainsActionableNonFailure()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database, "proj_a", 42, "Issue 42");
        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var blocked = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunBlocked,
            workflowRunId: "wf_blocked",
            eventId: "evt-blocked",
            projectId: "proj_a",
            issueNumber: 42);

        // Reminder replay / dispatcher redelivery of the same blocked event
        // must not create duplicate blocked attention or a failure item.
        await handler.HandleAsync(blocked, CancellationToken.None);
        await handler.HandleAsync(blocked, CancellationToken.None);

        var items = await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a");
        var item = Assert.Single(items);
        Assert.Equal(NotificationKinds.AgentResultUnconfirmed, item.NotificationKind);
        Assert.Equal("evt-blocked", item.SourceEventId);

        // A distinct blocked settlement (a second attempt blocked at its own
        // boundary) produces its own actionable non-failure item; blocked
        // attention is never presented as an ordinary task/run failure.
        var second = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunBlocked,
            workflowRunId: "wf_blocked_2",
            eventId: "evt-blocked-2",
            projectId: "proj_a",
            issueNumber: 42);
        await handler.HandleAsync(second, CancellationToken.None);

        var afterSecond = await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a");
        Assert.Equal(2, afterSecond.Count);
        Assert.All(afterSecond, item => Assert.Equal(NotificationKinds.AgentResultUnconfirmed, item.NotificationKind));
        Assert.DoesNotContain(NotificationKinds.WorkflowFailed, afterSecond.Select(item => item.NotificationKind));
    }

    [Fact]
    public async Task WorkflowRunBlocked_StaysDistinctFromWorkflowFailedAndExplicitCancellation()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database, "proj_a", 42, "Issue 42");
        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var blocked = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunBlocked,
            workflowRunId: "wf_blocked",
            eventId: "evt-blocked",
            projectId: "proj_a",
            issueNumber: 42);
        var failed = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            workflowRunId: "wf_failed",
            eventId: "evt-failed",
            projectId: "proj_a",
            issueNumber: 42);

        await handler.HandleAsync(blocked, CancellationToken.None);
        await handler.HandleAsync(failed, CancellationToken.None);

        var items = (await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"))
            .OrderBy(item => item.SourceEventId, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal(NotificationKinds.AgentResultUnconfirmed, items[0].NotificationKind);
        Assert.Equal(NotificationKinds.WorkflowFailed, items[1].NotificationKind);
    }
}
