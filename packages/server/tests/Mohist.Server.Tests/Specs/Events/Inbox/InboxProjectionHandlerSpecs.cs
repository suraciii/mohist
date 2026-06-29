using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Subscriptions;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Inbox;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.Tests.Specs.Events.Inbox;

/// <summary>
/// Unit specs for <see cref="InboxProjectionHandler"/>. Each test runs
/// against an in-memory SQLite seeded with an issue row + a workflow run
/// row, drives the handler with one CloudEvent envelope, and inspects
/// the resulting inbox row.
/// </summary>
public class InboxProjectionHandlerSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task WorkflowRunFailed_ProducesWorkflowFailedItemInOwningProject()
    {
        await using var database = CreateDatabase();
        await SeedWorkflowRunAsync(database,
            workflowRunId: "wf_1",
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 42);
        await SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 42,
            title: "Issue 42");

        var handler = CreateHandler(database);
        var evt = BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            workflowRunId: "wf_1",
            eventId: "evt-failed");

        await handler.HandleAsync(evt, CancellationToken.None);

        var item = Assert.Single(await GetInboxAsync(database, "proj_a"));
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
        await using var database = CreateDatabase();
        await SeedWorkflowRunAsync(database,
            workflowRunId: "wf_2",
            projectId: "proj_a",
            issueId: "issue_42",
            issueNumber: 42);
        await SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_42",
            issueNumber: 42,
            title: "Approve me");

        var handler = CreateHandler(database);
        var evt = BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.StageApprovalRequested,
            workflowRunId: "wf_2",
            eventId: "evt-approval");

        await handler.HandleAsync(evt, CancellationToken.None);

        var item = Assert.Single(await GetInboxAsync(database, "proj_a"));
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
        await using var database = CreateDatabase();
        await SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 7,
            title: "Started");

        var handler = CreateHandler(database);
        var evt = BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 7,
            eventId: "evt-started");

        await handler.HandleAsync(evt, CancellationToken.None);

        var item = Assert.Single(await GetInboxAsync(database, "proj_a"));
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
        await using var database = CreateDatabase();
        await SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 9,
            title: "Done");

        var handler = CreateHandler(database);
        var evt = BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueWorkCompleted,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 9,
            eventId: "evt-completed");

        await handler.HandleAsync(evt, CancellationToken.None);

        var item = Assert.Single(await GetInboxAsync(database, "proj_a"));
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
        await using var database = CreateDatabase();
        await SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "Issue 1");

        var handler = CreateHandler(database);
        var evt = BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            eventId: "evt-replay");

        await handler.HandleAsync(evt, CancellationToken.None);
        await handler.HandleAsync(evt, CancellationToken.None);
        await handler.HandleAsync(evt, CancellationToken.None);

        var items = await GetInboxAsync(database, "proj_a");
        var item = Assert.Single(items);
        Assert.Equal("evt-replay", item.SourceEventId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task WorkflowEventReplay_DoesNotCreateSecondItem()
    {
        await using var database = CreateDatabase();
        await SeedWorkflowRunAsync(database,
            workflowRunId: "wf_replay",
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1);
        await SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "Issue 1");

        var handler = CreateHandler(database);
        var evt = BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            workflowRunId: "wf_replay",
            eventId: "evt-wf-replay");

        await handler.HandleAsync(evt, CancellationToken.None);
        await handler.HandleAsync(evt, CancellationToken.None);

        var items = await GetInboxAsync(database, "proj_a");
        var item = Assert.Single(items);
        Assert.Equal("evt-wf-replay", item.SourceEventId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task WorkflowStorePublishThenEventStoreReplay_DoesNotDuplicateInboxItem()
    {
        await using var database = CreateDatabase();
        await SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "Issue 1");

        var handler = CreateHandler(database);
        var bus = new InMemoryEventBus([
            new Subscription(
                "com.mohist.workflow.run.failed",
                handler,
                DispatchDynamic)
        ], NullLogger<InMemoryEventBus>.Instance);
        var eventStore = new EventStore(database.Factory, NullLogger<EventStore>.Instance);
        var runStore = new WorkflowRunStore(database.Factory, eventStore, bus);
        var run = BuildWorkflowRun(
            workflowRunId: "wf_store_replay",
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1);

        await runStore.SaveAsync(run, [new WorkflowRunFailed("failed")]);

        var stored = Assert.Single(await eventStore.ListAsync("wf_store_replay"));
        await handler.HandleAsync(stored.Envelope, CancellationToken.None);

        var item = Assert.Single(await GetInboxAsync(database, "proj_a"));
        Assert.Equal(stored.Envelope.Source.ToString(), item.SourceEventSource);
        Assert.Equal(stored.Envelope.Id, item.SourceEventId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task WorkflowEvent_LandsInProjectOwnedByRun_NotInOtherProjects()
    {
        await using var database = CreateDatabase();
        await SeedWorkflowRunAsync(database,
            workflowRunId: "wf_iso",
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1);
        await SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "Issue 1");

        var handler = CreateHandler(database);
        var evt = BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            workflowRunId: "wf_iso",
            eventId: "evt-iso");

        await handler.HandleAsync(evt, CancellationToken.None);

        var aItems = await GetInboxAsync(database, "proj_a");
        var bItems = await GetInboxAsync(database, "proj_b");
        Assert.Single(aItems);
        Assert.Empty(bItems);
        Assert.Equal("proj_a", aItems[0].ProjectId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task IssueEvent_LandsInProjectOwnedByIssue_NotInOtherProjects()
    {
        await using var database = CreateDatabase();
        await SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "A1");

        var handler = CreateHandler(database);
        var evt = BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueWorkCompleted,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            eventId: "evt-iso");

        await handler.HandleAsync(evt, CancellationToken.None);

        var aItems = await GetInboxAsync(database, "proj_a");
        var bItems = await GetInboxAsync(database, "proj_b");
        Assert.Single(aItems);
        Assert.Empty(bItems);
        Assert.Equal("proj_a", aItems[0].ProjectId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task IssueEvent_ProjectExtensionDisagreesWithLoadedIssue_SkipsWithoutLeakingToClaimedProject()
    {
        await using var database = CreateDatabase();
        await SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "A1");

        var handler = CreateHandler(database);
        var evt = BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueWorkCompleted,
            projectId: "proj_b",
            issueId: "issue_1",
            issueNumber: 1,
            eventId: "evt-mismatch-project");

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(await GetInboxAsync(database, "proj_a"));
        Assert.Empty(await GetInboxAsync(database, "proj_b"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task IssueEvent_NumberExtensionDisagreesWithLoadedIssue_SkipsWithoutThrowing()
    {
        await using var database = CreateDatabase();
        await SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "A1");

        var handler = CreateHandler(database);
        var evt = BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 2,
            eventId: "evt-mismatch-number");

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(await GetInboxAsync(database, "proj_a"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task WorkflowEvent_RunAnnotationsDisagreeWithLoadedIssue_SkipsWithoutLeakingToClaimedProject()
    {
        await using var database = CreateDatabase();
        await SeedWorkflowRunAsync(database,
            workflowRunId: "wf_bad_ann",
            projectId: "proj_b",
            issueId: "issue_1",
            issueNumber: 1);
        await SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "A1");

        var handler = CreateHandler(database);
        var evt = BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            workflowRunId: "wf_bad_ann",
            eventId: "evt-wf-mismatch-project");

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(await GetInboxAsync(database, "proj_a"));
        Assert.Empty(await GetInboxAsync(database, "proj_b"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task WorkflowEvent_RunIssueNumberDisagreesWithLoadedIssue_SkipsWithoutThrowing()
    {
        await using var database = CreateDatabase();
        await SeedWorkflowRunAsync(database,
            workflowRunId: "wf_bad_num",
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 2);
        await SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "A1");

        var handler = CreateHandler(database);
        var evt = BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.StageApprovalRequested,
            workflowRunId: "wf_bad_num",
            eventId: "evt-wf-mismatch-number");

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(await GetInboxAsync(database, "proj_a"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task IssueEvent_MissingProjectIdExtension_SkipsWithoutThrowing()
    {
        await using var database = CreateDatabase();

        var handler = CreateHandler(database);
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

        var items = await GetInboxAsync(database, "proj_a");
        Assert.Empty(items);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task IssueEvent_MissingIssueIdExtension_SkipsWithoutThrowing()
    {
        await using var database = CreateDatabase();

        var handler = CreateHandler(database);
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

        var items = await GetInboxAsync(database, "proj_a");
        Assert.Empty(items);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task WorkflowEvent_MissingAnnotationsOnRun_SkipsWithoutThrowing()
    {
        await using var database = CreateDatabase();
        // Workflow run exists but has no projectId/issueId annotations.
        await SeedWorkflowRunAsync(database,
            workflowRunId: "wf_no_ann",
            projectId: null,
            issueId: null,
            issueNumber: null);

        var handler = CreateHandler(database);
        var evt = BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            workflowRunId: "wf_no_ann",
            eventId: "evt-no-ann");

        await handler.HandleAsync(evt, CancellationToken.None);

        var items = await GetInboxAsync(database, "proj_a");
        Assert.Empty(items);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task WorkflowEvent_UnknownRunId_SkipsWithoutThrowing()
    {
        await using var database = CreateDatabase();

        var handler = CreateHandler(database);
        var evt = BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            workflowRunId: "wf_does_not_exist",
            eventId: "evt-unknown");

        await handler.HandleAsync(evt, CancellationToken.None);

        var items = await GetInboxAsync(database, "proj_a");
        Assert.Empty(items);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task WorkflowEvent_MissingWorkflowRunIdInSource_SkipsWithoutThrowing()
    {
        await using var database = CreateDatabase();

        var handler = CreateHandler(database);
        var evt = new CloudEvent(
            id: "evt-no-source",
            source: new Uri("/mohist/something/else", UriKind.Relative),
            type: EventCatalog.ReverseDns.StageApprovalRequested,
            time: DateTimeOffset.UtcNow,
            data: null);

        await handler.HandleAsync(evt, CancellationToken.None);

        var items = await GetInboxAsync(database, "proj_a");
        Assert.Empty(items);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task IssueEvent_UnknownIssueIdForTitle_SkipsWithoutThrowing()
    {
        await using var database = CreateDatabase();

        var handler = CreateHandler(database);
        var evt = BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueWorkCompleted,
            projectId: "proj_a",
            issueId: "issue_missing",
            issueNumber: 7,
            eventId: "evt-missing-issue");

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(await GetInboxAsync(database, "proj_a"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task WorkflowEvent_TitleTakenFromIssueRowSnapshottedAtProjectionTime()
    {
        await using var database = CreateDatabase();
        await SeedWorkflowRunAsync(database,
            workflowRunId: "wf_title",
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1);
        await SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "Snapshot me");

        var handler = CreateHandler(database);
        var evt = BuildWorkflowEvent(
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

        var item = Assert.Single(await GetInboxAsync(database, "proj_a"));
        Assert.Equal("Snapshot me", item.IssueTitle);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task Filter_AcceptsAllFourTypes()
    {
        var handler = CreateHandler(CreateDatabase());
        Assert.True(handler.Filter(BuildIssueEvent(EventCatalog.ReverseDns.IssueWorkStarted, "p", "i", 1, "e1")));
        Assert.True(handler.Filter(BuildIssueEvent(EventCatalog.ReverseDns.IssueWorkCompleted, "p", "i", 1, "e2")));
        Assert.True(handler.Filter(BuildWorkflowEvent(EventCatalog.ReverseDns.WorkflowRunFailed, "w", "e3")));
        Assert.True(handler.Filter(BuildWorkflowEvent(EventCatalog.ReverseDns.StageApprovalRequested, "w", "e4")));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task Filter_RejectsOtherEventTypes()
    {
        var handler = CreateHandler(CreateDatabase());
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
        await using var database = CreateDatabase();
        await SeedWorkflowRunAsync(database,
            workflowRunId: "wf_1",
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1);
        await SeedWorkflowRunAsync(database,
            workflowRunId: "wf_2",
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1);
        await SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "Issue 1");

        var handler = CreateHandler(database);
        await handler.HandleAsync(BuildWorkflowEvent(EventCatalog.ReverseDns.WorkflowRunFailed, "wf_1", "evt-failed"), CancellationToken.None);
        await handler.HandleAsync(BuildWorkflowEvent(EventCatalog.ReverseDns.StageApprovalRequested, "wf_2", "evt-approval"), CancellationToken.None);
        await handler.HandleAsync(BuildIssueEvent(EventCatalog.ReverseDns.IssueWorkStarted, "proj_a", "issue_1", 1, "evt-started"), CancellationToken.None);
        await handler.HandleAsync(BuildIssueEvent(EventCatalog.ReverseDns.IssueWorkCompleted, "proj_a", "issue_1", 1, "evt-completed"), CancellationToken.None);

        var items = await GetInboxAsync(database, "proj_a");
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

    private static InboxProjectionHandler CreateHandler(TestDatabase database) =>
        new(
            scopeFactory: new TestScopeFactory(database),
            log: NullLogger<InboxProjectionHandler>.Instance);

    private sealed class TestScopeFactory : IServiceScopeFactory
    {
        private readonly TestDatabase _database;
        public TestScopeFactory(TestDatabase database) => _database = database;

        public IServiceScope CreateScope() => new TestScope(_database);

        private sealed class TestScope : IServiceScope
        {
            private readonly TestDatabase _database;
            public TestScope(TestDatabase database)
            {
                _database = database;
                ServiceProvider = BuildProvider();
            }

            public IServiceProvider ServiceProvider { get; }

            public void Dispose() { }

            private IServiceProvider BuildProvider()
            {
                var services = new ServiceCollection();
                services.AddSingleton<IDbContextFactory<MohistDbContext>>(_database.Factory);
                services.AddScoped<InboxStore>();
                services.AddScoped<IWorkflowRunStore>(sp => new WorkflowRunStore(
                    _database.Factory,
                    new NoopEventStore(),
                    new NoopEventPublisher()));
                services.AddScoped<IStateStore<DomainIssue>>(sp => new IssueStore(_database.Factory));
                return services.BuildServiceProvider();
            }
        }
    }

    private static CloudEvent BuildIssueEvent(string type, string projectId, string issueId, int issueNumber, string eventId)
    {
        return new CloudEvent(
            id: eventId,
            source: new Uri($"/mohist/issues/{issueId}", UriKind.Relative),
            type: type,
            time: DateTimeOffset.UtcNow,
            data: null,
            extensions: new Dictionary<string, string>
            {
                ["projectid"] = projectId,
                ["issueid"] = issueId,
                ["issueno"] = issueNumber.ToString(),
            });
    }

    private static CloudEvent BuildWorkflowEvent(string type, string workflowRunId, string eventId)
    {
        return new CloudEvent(
            id: eventId,
            source: new Uri($"/mohist/workflow-runs/{workflowRunId}", UriKind.Relative),
            type: type,
            time: DateTimeOffset.UtcNow,
            data: null);
    }

    private static async Task<List<InboxItemView>> GetInboxAsync(TestDatabase database, string projectId)
    {
        await using var db = database.CreateDbContext();
        var rows = await db.InboxItems.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.ArchivedAt == null)
            .ToListAsync();
        return rows
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .Select(r => new InboxItemView
            {
                Id = r.Id,
                ProjectId = r.ProjectId,
                IssueId = r.IssueId,
                IssueNumber = r.IssueNumber,
                IssueTitle = r.IssueTitle,
                NotificationKind = r.NotificationKind,
                SourceEventSource = r.SourceEventSource,
                SourceEventId = r.SourceEventId,
                CreatedAt = r.CreatedAt,
                ReadAt = r.ReadAt,
                ArchivedAt = r.ArchivedAt,
            })
            .ToList();
    }

    private static async Task SeedIssueAsync(
        TestDatabase database,
        string projectId,
        string issueId,
        int issueNumber,
        string title)
    {
        var issue = new DomainIssue
        {
            Id = issueId,
            ProjectId = projectId,
            Number = issueNumber,
            Title = title,
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
        };
        await using var db = database.CreateDbContext();
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedWorkflowRunAsync(
        TestDatabase database,
        string workflowRunId,
        string? projectId,
        string? issueId,
        int? issueNumber)
    {
        var run = BuildWorkflowRun(workflowRunId, projectId, issueId, issueNumber);
        var json = Mohist.Server.Infrastructure.JSON.Serialize(run);
        await using var db = database.CreateDbContext();
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowRunId,
            State = json,
        });
        await db.SaveChangesAsync();
    }

    private static WorkflowRun BuildWorkflowRun(
        string workflowRunId,
        string? projectId,
        string? issueId,
        int? issueNumber)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        if (projectId is not null) annotations["projectId"] = projectId;
        if (issueId is not null) annotations["issueId"] = issueId;
        if (issueNumber is not null) annotations["issueNumber"] = issueNumber.Value.ToString();

        return new WorkflowRun
        {
            Id = workflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: DateTimeOffset.UtcNow,
                Annotations: annotations),
            Stages = new List<StageRun>(),
        };
    }

    private static Task DispatchDynamic(object handler, CloudEvent evt, CancellationToken ct)
    {
        var h = (ICloudEventHandler)handler;
        if (!h.Filter(evt)) return Task.CompletedTask;
        return h.HandleAsync(evt, ct);
    }

    private static TestDatabase CreateDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestDbContextFactory(options);
        using (var db = factory.CreateDbContext())
            db.Database.Migrate();
        return new TestDatabase(connection, factory);
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public TestDatabase(SqliteConnection connection, TestDbContextFactory factory)
        {
            _connection = connection;
            Factory = factory;
        }

        public TestDbContextFactory Factory { get; }

        public MohistDbContext CreateDbContext() => Factory.CreateDbContext();

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<MohistDbContext>
    {
        public TestDbContextFactory(DbContextOptions<MohistDbContext> options) => Options = options;

        public DbContextOptions<MohistDbContext> Options { get; }

        public MohistDbContext CreateDbContext() => new(Options);
    }

    private sealed class NoopEventStore : Mohist.Server.Infrastructure.Events.IEventStore
    {
        public Task AppendAsync(CloudEvent evt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Mohist.Server.Infrastructure.Events.StoredCloudEvent>> ListAsync(string workflowRunId, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Mohist.Server.Infrastructure.Events.StoredCloudEvent>>(Array.Empty<Mohist.Server.Infrastructure.Events.StoredCloudEvent>());
        public Task<IReadOnlyList<Mohist.Server.Infrastructure.Events.StoredCloudEvent>> ListIssueEventsAsync(string issueId, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Mohist.Server.Infrastructure.Events.StoredCloudEvent>>(Array.Empty<Mohist.Server.Infrastructure.Events.StoredCloudEvent>());
    }

    private sealed class NoopEventPublisher : Mohist.Server.Infrastructure.Events.IEventPublisher
    {
        public Task PublishAsync(CloudEvent envelope, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PublishAsync<TData>(TData data, string type, string source, string? subject = null, IReadOnlyDictionary<string, string>? extensions = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
