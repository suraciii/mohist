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
}
