using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Subscriptions;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Inbox;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.Tests.Specs.Events.Inbox;

/// <summary>
/// Unit specs for <see cref="InboxProjectionHandler"/>. Each test runs
/// against an in-memory SQLite seeded with an issue row + a workflow run
/// row, drives the handler with one CloudEvent envelope, and inspects
/// the resulting inbox row. Shared DB / scope / event-builder helpers
/// live in <see cref="InboxProjectionTestSupport"/>.
/// </summary>
public class InboxProjectionHandlerSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task WorkflowRunFailed_ProducesWorkflowFailedItemInOwningProject()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_1",
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 42);
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 42,
            title: "Issue 42");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            workflowRunId: "wf_1",
            eventId: "evt-failed");

        await handler.HandleAsync(evt, CancellationToken.None);

        var item = Assert.Single(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Equal(NotificationKinds.WorkflowFailed, item.NotificationKind);
        Assert.Equal("/mohist/workflow-runs/wf_1", item.SourceEventSource);
        Assert.Equal("evt-failed", item.SourceEventId);
        Assert.Equal("proj_a", item.ProjectId);
        Assert.Equal("issue_1", item.IssueId);
        Assert.Equal(42, item.IssueNumber);
        Assert.Equal("Issue 42", item.IssueTitle);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task StageApprovalRequested_ProducesApprovalRequestedItemInOwningProject()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_2",
            projectId: "proj_a",
            issueId: "issue_42",
            issueNumber: 42);
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_42",
            issueNumber: 42,
            title: "Approve me");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.StageApprovalRequested,
            workflowRunId: "wf_2",
            eventId: "evt-approval");

        await handler.HandleAsync(evt, CancellationToken.None);

        var item = Assert.Single(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Equal(NotificationKinds.ApprovalRequested, item.NotificationKind);
        Assert.Equal("evt-approval", item.SourceEventId);
        Assert.Equal("Approve me", item.IssueTitle);
        Assert.Equal(42, item.IssueNumber);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task IssueWorkStarted_ProducesIssueStartedItemInOwningProject()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 7,
            title: "Started");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 7,
            eventId: "evt-started");

        await handler.HandleAsync(evt, CancellationToken.None);

        var item = Assert.Single(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Equal(NotificationKinds.IssueStarted, item.NotificationKind);
        Assert.Equal("/mohist/issues/issue_1", item.SourceEventSource);
        Assert.Equal("evt-started", item.SourceEventId);
        Assert.Equal(7, item.IssueNumber);
        Assert.Equal("Started", item.IssueTitle);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task IssueWorkCompleted_ProducesIssueCompletedItemInOwningProject()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 9,
            title: "Done");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueWorkCompleted,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 9,
            eventId: "evt-completed");

        await handler.HandleAsync(evt, CancellationToken.None);

        var item = Assert.Single(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Equal(NotificationKinds.IssueCompleted, item.NotificationKind);
        Assert.Equal("evt-completed", item.SourceEventId);
        Assert.Equal(9, item.IssueNumber);
        Assert.Equal("Done", item.IssueTitle);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task ReplayOfSameCloudEvent_DoesNotCreateSecondItem()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "Issue 1");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            eventId: "evt-replay");

        await handler.HandleAsync(evt, CancellationToken.None);
        await handler.HandleAsync(evt, CancellationToken.None);
        await handler.HandleAsync(evt, CancellationToken.None);

        var items = await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a");
        var item = Assert.Single(items);
        Assert.Equal("evt-replay", item.SourceEventId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task WorkflowEventReplay_DoesNotCreateSecondItem()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_replay",
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1);
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "Issue 1");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            workflowRunId: "wf_replay",
            eventId: "evt-wf-replay");

        await handler.HandleAsync(evt, CancellationToken.None);
        await handler.HandleAsync(evt, CancellationToken.None);

        var items = await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a");
        var item = Assert.Single(items);
        Assert.Equal("evt-wf-replay", item.SourceEventId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task WorkflowStorePublishThenEventStoreReplay_DoesNotDuplicateInboxItem()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "Issue 1");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var bus = new InMemoryEventBus([
            new Subscription(
                "com.mohist.workflow.run.failed",
                handler,
                InboxProjectionTestSupport.DispatchDynamic)
        ], NullLogger<InMemoryEventBus>.Instance);
        var eventStore = new EventStore(database.Factory, NullLogger<EventStore>.Instance);
        var runStore = new WorkflowRunStore(database.Factory, eventStore, bus);
        var run = InboxProjectionTestSupport.BuildWorkflowRun(
            workflowRunId: "wf_store_replay",
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1);

        await runStore.SaveAsync(run, [new WorkflowRunFailed("failed")]);

        var stored = Assert.Single(await eventStore.ListAsync("wf_store_replay"));
        await handler.HandleAsync(stored.Envelope, CancellationToken.None);

        var item = Assert.Single(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Equal(stored.Envelope.Source.ToString(), item.SourceEventSource);
        Assert.Equal(stored.Envelope.Id, item.SourceEventId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task WorkflowEvent_LandsInProjectOwnedByRun_NotInOtherProjects()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_iso",
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1);
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "Issue 1");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            workflowRunId: "wf_iso",
            eventId: "evt-iso");

        await handler.HandleAsync(evt, CancellationToken.None);

        var aItems = await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a");
        var bItems = await InboxProjectionTestSupport.GetInboxAsync(database, "proj_b");
        Assert.Single(aItems);
        Assert.Empty(bItems);
        Assert.Equal("proj_a", aItems[0].ProjectId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task IssueEvent_LandsInProjectOwnedByIssue_NotInOtherProjects()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "A1");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueWorkCompleted,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            eventId: "evt-iso");

        await handler.HandleAsync(evt, CancellationToken.None);

        var aItems = await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a");
        var bItems = await InboxProjectionTestSupport.GetInboxAsync(database, "proj_b");
        Assert.Single(aItems);
        Assert.Empty(bItems);
        Assert.Equal("proj_a", aItems[0].ProjectId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task IssueEvent_ProjectExtensionDisagreesWithLoadedIssue_SkipsWithoutLeakingToClaimedProject()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "A1");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueWorkCompleted,
            projectId: "proj_b",
            issueId: "issue_1",
            issueNumber: 1,
            eventId: "evt-mismatch-project");

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_b"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task IssueEvent_NumberExtensionDisagreesWithLoadedIssue_SkipsWithoutThrowing()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "A1");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 2,
            eventId: "evt-mismatch-number");

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task WorkflowEvent_RunAnnotationsDisagreeWithLoadedIssue_SkipsWithoutLeakingToClaimedProject()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_bad_ann",
            projectId: "proj_b",
            issueId: "issue_1",
            issueNumber: 1);
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "A1");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            workflowRunId: "wf_bad_ann",
            eventId: "evt-wf-mismatch-project");

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_b"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task WorkflowEvent_RunIssueNumberDisagreesWithLoadedIssue_SkipsWithoutThrowing()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_bad_num",
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 2);
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "A1");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.StageApprovalRequested,
            workflowRunId: "wf_bad_num",
            eventId: "evt-wf-mismatch-number");

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task IssueEvent_MissingProjectIdExtension_SkipsWithoutThrowing()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = new CloudEvent(
            id: "evt-no-ext",
            source: new Uri("/mohist/issue/issue_x", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            time: DateTimeOffset.UtcNow,
            data: null,
            extensions: new Dictionary<string, string>
            {
                ["issueid"] = "issue_x",
                ["issueno"] = "1",
            });

        await handler.HandleAsync(evt, CancellationToken.None);

        var items = await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a");
        Assert.Empty(items);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task IssueEvent_MissingIssueIdExtension_SkipsWithoutThrowing()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = new CloudEvent(
            id: "evt-no-issue",
            source: new Uri("/mohist/issue/issue_x", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueWorkCompleted,
            time: DateTimeOffset.UtcNow,
            data: null,
            extensions: new Dictionary<string, string>
            {
                ["projectid"] = "proj_a",
                ["issueno"] = "1",
            });

        await handler.HandleAsync(evt, CancellationToken.None);

        var items = await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a");
        Assert.Empty(items);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task WorkflowEvent_MissingAnnotationsOnRun_SkipsWithoutThrowing()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        // Workflow run exists but has no projectId/issueId annotations.
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_no_ann",
            projectId: null,
            issueId: null,
            issueNumber: null);

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            workflowRunId: "wf_no_ann",
            eventId: "evt-no-ann");

        await handler.HandleAsync(evt, CancellationToken.None);

        var items = await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a");
        Assert.Empty(items);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task WorkflowEvent_UnknownRunId_SkipsWithoutThrowing()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            workflowRunId: "wf_does_not_exist",
            eventId: "evt-unknown");

        await handler.HandleAsync(evt, CancellationToken.None);

        var items = await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a");
        Assert.Empty(items);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task WorkflowEvent_MissingWorkflowRunIdInSource_SkipsWithoutThrowing()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = new CloudEvent(
            id: "evt-no-source",
            source: new Uri("/mohist/something/else", UriKind.Relative),
            type: EventCatalog.ReverseDns.StageApprovalRequested,
            time: DateTimeOffset.UtcNow,
            data: null);

        await handler.HandleAsync(evt, CancellationToken.None);

        var items = await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a");
        Assert.Empty(items);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task IssueEvent_UnknownIssueIdForTitle_SkipsWithoutThrowing()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueWorkCompleted,
            projectId: "proj_a",
            issueId: "issue_missing",
            issueNumber: 7,
            eventId: "evt-missing-issue");

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task WorkflowEvent_TitleTakenFromIssueRowSnapshottedAtProjectionTime()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_title",
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1);
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "Snapshot me");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            workflowRunId: "wf_title",
            eventId: "evt-title");

        await handler.HandleAsync(evt, CancellationToken.None);

        // After projection, mutate the issue title to prove the snapshot
        // is durable and the projection does not re-read on every query.
        await using (var db = database.CreateDbContext())
        {
            var row = await db.Issues.FirstAsync(r => r.IssueId == "issue_1");
            // The Issue.Title is init-only — patch the JSON state directly
            // to simulate a later title edit. The InboxItem row holds the
            // snapshot from projection time.
            row.State = row.State.Replace("\"Snapshot me\"", "\"Renamed later\"");
            await db.SaveChangesAsync();
        }

        var item = Assert.Single(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Equal("Snapshot me", item.IssueTitle);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task Filter_AcceptsAllFourTypes()
    {
        var handler = InboxProjectionTestSupport.CreateHandler(InboxProjectionTestSupport.CreateDatabase());
        Assert.True(handler.Filter(InboxProjectionTestSupport.BuildIssueEvent(EventCatalog.ReverseDns.IssueWorkStarted, "p", "i", 1, "e1")));
        Assert.True(handler.Filter(InboxProjectionTestSupport.BuildIssueEvent(EventCatalog.ReverseDns.IssueWorkCompleted, "p", "i", 1, "e2")));
        Assert.True(handler.Filter(InboxProjectionTestSupport.BuildWorkflowEvent(EventCatalog.ReverseDns.WorkflowRunFailed, "w", "e3")));
        Assert.True(handler.Filter(InboxProjectionTestSupport.BuildWorkflowEvent(EventCatalog.ReverseDns.StageApprovalRequested, "w", "e4")));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task Filter_RejectsOtherEventTypes()
    {
        var handler = InboxProjectionTestSupport.CreateHandler(InboxProjectionTestSupport.CreateDatabase());
        Assert.False(handler.Filter(new CloudEvent(
            id: "e",
            source: new Uri("/mohist/issue/issue_1", UriKind.Relative),
            type: "com.mohist.issue.closed",
            time: DateTimeOffset.UtcNow,
            data: null)));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task HasSubscriptionAttributeWithExpectedFourTypes()
    {
        var attr = (SubscriptionAttribute?)Attribute.GetCustomAttribute(
            typeof(InboxProjectionHandler), typeof(SubscriptionAttribute));
        Assert.NotNull(attr);
        Assert.Equal(
            "com.mohist.workflow.run.failed|" +
            "com.mohist.workflow.stage.approval-requested|" +
            "com.mohist.issue.work-started|" +
            "com.mohist.issue.work-completed",
            attr!.Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task DistinctEvents_ProduceDistinctItemsAcrossKinds()
    {
        // End-to-end: one event of each kind, in sequence, with
        // different evt.Ids. The inbox for the owning project ends up
        // with four items, one per kind.
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_1",
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1);
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_2",
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1);
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "Issue 1");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        await handler.HandleAsync(InboxProjectionTestSupport.BuildWorkflowEvent(EventCatalog.ReverseDns.WorkflowRunFailed, "wf_1", "evt-failed"), CancellationToken.None);
        await handler.HandleAsync(InboxProjectionTestSupport.BuildWorkflowEvent(EventCatalog.ReverseDns.StageApprovalRequested, "wf_2", "evt-approval"), CancellationToken.None);
        await handler.HandleAsync(InboxProjectionTestSupport.BuildIssueEvent(EventCatalog.ReverseDns.IssueWorkStarted, "proj_a", "issue_1", 1, "evt-started"), CancellationToken.None);
        await handler.HandleAsync(InboxProjectionTestSupport.BuildIssueEvent(EventCatalog.ReverseDns.IssueWorkCompleted, "proj_a", "issue_1", 1, "evt-completed"), CancellationToken.None);

        var items = await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a");
        Assert.Equal(4, items.Count);
        var kinds = items.Select(i => i.NotificationKind).ToHashSet();
        Assert.Equal(new HashSet<string>
        {
            NotificationKinds.WorkflowFailed,
            NotificationKinds.ApprovalRequested,
            NotificationKinds.IssueStarted,
            NotificationKinds.IssueCompleted,
        }, kinds);
    }
}
