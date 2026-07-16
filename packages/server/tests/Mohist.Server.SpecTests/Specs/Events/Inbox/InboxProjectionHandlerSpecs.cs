using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Grains;
using Mohist.Server.Events.Subscriptions;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Inbox;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events.Inbox;

/// <summary>
/// Unit specs for <see cref="InboxProjectionHandler"/>. Each test runs
/// against an in-memory SQLite seeded with an issue row + a workflow run
/// row, drives the handler with one CloudEvent envelope, and inspects
/// the resulting inbox row. Shared DB / scope / event-builder helpers
/// live in <see cref="InboxProjectionTestSupport"/>.
/// </summary>
public class InboxProjectionHandlerSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task IssueCompleted_ProducesIssueCompletedItemInOwningProject()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 9,
            title: "Done");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueCompleted,
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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
        var eventStore = new EventStore(database.Factory, NullLogger<EventStore>.Instance);
        var runStore = new WorkflowRunStore(database.Factory, eventStore, new NullDispatchGrainFactory(), NullLogger<WorkflowRunStore>.Instance);
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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
            type: EventCatalog.ReverseDns.IssueCompleted,
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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
            type: EventCatalog.ReverseDns.IssueCompleted,
            projectId: "proj_b",
            issueId: "issue_1",
            issueNumber: 1,
            eventId: "evt-mismatch-project");

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_b"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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
            time: TestTime.UtcNow,
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task IssueEvent_MissingIssueIdExtension_SkipsWithoutThrowing()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = new CloudEvent(
            id: "evt-no-issue",
            source: new Uri("/mohist/issue/issue_x", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCompleted,
            time: TestTime.UtcNow,
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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
            time: TestTime.UtcNow,
            data: null);

        await handler.HandleAsync(evt, CancellationToken.None);

        var items = await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a");
        Assert.Empty(items);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task IssueEvent_UnknownIssueIdForTitle_SkipsWithoutThrowing()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueCompleted,
            projectId: "proj_a",
            issueId: "issue_missing",
            issueNumber: 7,
            eventId: "evt-missing-issue");

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task Filter_AcceptsAllFourTypes()
    {
        var handler = InboxProjectionTestSupport.CreateHandler(InboxProjectionTestSupport.CreateDatabase());
        Assert.True(handler.Filter(InboxProjectionTestSupport.BuildIssueEvent(EventCatalog.ReverseDns.IssueWorkStarted, "p", "i", 1, "e1")));
        Assert.True(handler.Filter(InboxProjectionTestSupport.BuildIssueEvent(EventCatalog.ReverseDns.IssueCompleted, "p", "i", 1, "e2")));
        Assert.True(handler.Filter(InboxProjectionTestSupport.BuildWorkflowEvent(EventCatalog.ReverseDns.WorkflowRunFailed, "w", "e3")));
        Assert.True(handler.Filter(InboxProjectionTestSupport.BuildWorkflowEvent(EventCatalog.ReverseDns.StageApprovalRequested, "w", "e4")));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task Filter_RejectsOtherEventTypes()
    {
        var handler = InboxProjectionTestSupport.CreateHandler(InboxProjectionTestSupport.CreateDatabase());
        Assert.False(handler.Filter(new CloudEvent(
            id: "e",
            source: new Uri("/mohist/issue/issue_1", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCancelled,
            time: TestTime.UtcNow,
            data: null)));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task HasSubscriptionAttributeWithExpectedFourTypes()
    {
        var attr = (SubscriptionAttribute?)Attribute.GetCustomAttribute(
            typeof(InboxProjectionHandler), typeof(SubscriptionAttribute));
        Assert.NotNull(attr);
        Assert.Equal(
            EventCatalog.ReverseDns.WorkflowRunFailed + "|" +
            EventCatalog.ReverseDns.StageApprovalRequested + "|" +
            EventCatalog.ReverseDns.IssueWorkStarted + "|" +
            EventCatalog.ReverseDns.IssueCompleted,
            attr!.Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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
        await handler.HandleAsync(InboxProjectionTestSupport.BuildIssueEvent(EventCatalog.ReverseDns.IssueCompleted, "proj_a", "issue_1", 1, "evt-completed"), CancellationToken.None);

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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task Subscription_EnabledKind_CreatesItem()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "Enabled test");
        await InboxProjectionTestSupport.SeedSubscriptionAsync(database, "proj_a",
            workflowFailedEnabled: true);

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueCompleted,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            eventId: "evt-enabled");

        await handler.HandleAsync(evt, CancellationToken.None);

        var item = Assert.Single(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Equal(NotificationKinds.IssueCompleted, item.NotificationKind);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task Subscription_DisabledKind_DoesNotCreateItem()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "Disabled test");
        await InboxProjectionTestSupport.SeedSubscriptionAsync(database, "proj_a",
            workflowFailedEnabled: false);

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            workflowRunId: "wf_disabled",
            eventId: "evt-disabled");

        // Seed the workflow run (needed for resolution) but subscription
        // gate should prevent the insert.
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_disabled",
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1);

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task Subscription_DifferentDisabledKinds_EachPreventsItsOwnKind()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "Multi disabled");
        await InboxProjectionTestSupport.SeedSubscriptionAsync(database, "proj_a",
            workflowFailedEnabled: false,
            approvalRequestedEnabled: false,
            issueStartedEnabled: false,
            issueCompletedEnabled: false);

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_fail", projectId: "proj_a", issueId: "issue_1", issueNumber: 1);
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_approve", projectId: "proj_a", issueId: "issue_1", issueNumber: 1);

        await handler.HandleAsync(InboxProjectionTestSupport.BuildWorkflowEvent(EventCatalog.ReverseDns.WorkflowRunFailed, "wf_fail", "evt-f"), CancellationToken.None);
        await handler.HandleAsync(InboxProjectionTestSupport.BuildWorkflowEvent(EventCatalog.ReverseDns.StageApprovalRequested, "wf_approve", "evt-a"), CancellationToken.None);
        await handler.HandleAsync(InboxProjectionTestSupport.BuildIssueEvent(EventCatalog.ReverseDns.IssueWorkStarted, "proj_a", "issue_1", 1, "evt-s"), CancellationToken.None);
        await handler.HandleAsync(InboxProjectionTestSupport.BuildIssueEvent(EventCatalog.ReverseDns.IssueCompleted, "proj_a", "issue_1", 1, "evt-c"), CancellationToken.None);

        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task Subscription_MissingSubscription_CreatesItem()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "Missing sub");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            eventId: "evt-no-sub");

        await handler.HandleAsync(evt, CancellationToken.None);

        var item = Assert.Single(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Equal(NotificationKinds.IssueStarted, item.NotificationKind);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task Subscription_DisabledKind_LeavesExistingItemsUntouched()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "Existing item");
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_existing",
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1);

        // First, create an item with kind enabled.
        await InboxProjectionTestSupport.SeedSubscriptionAsync(database, "proj_a",
            workflowFailedEnabled: true);

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt1 = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            workflowRunId: "wf_existing",
            eventId: "evt-first");
        await handler.HandleAsync(evt1, CancellationToken.None);

        var before = Assert.Single(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));

        // Now disable the kind and fire a new event.
        await InboxProjectionTestSupport.SeedSubscriptionAsync(database, "proj_a",
            workflowFailedEnabled: false);

        var evt2 = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            workflowRunId: "wf_existing",
            eventId: "evt-second");
        await handler.HandleAsync(evt2, CancellationToken.None);

        // Existing item is unchanged; no new item added.
        var after = await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a");
        var item = Assert.Single(after);
        Assert.Equal(before.Id, item.Id);
        Assert.Equal(NotificationKinds.WorkflowFailed, item.NotificationKind);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task Subscription_ReEnabledKind_CreatesItemForSubsequentEvent()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "Re-enable test");
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_re",
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1);

        var handler = InboxProjectionTestSupport.CreateHandler(database);

        // Start with disabled.
        await InboxProjectionTestSupport.SeedSubscriptionAsync(database, "proj_a",
            workflowFailedEnabled: false);

        var evtDisabled = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            workflowRunId: "wf_re",
            eventId: "evt-while-disabled");
        await handler.HandleAsync(evtDisabled, CancellationToken.None);
        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));

        // Re-enable the kind.
        await InboxProjectionTestSupport.SeedSubscriptionAsync(database, "proj_a",
            workflowFailedEnabled: true);

        // Fire a different event (new SourceEventId) after re-enable.
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_re2",
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1);

        var evtAfter = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            workflowRunId: "wf_re2",
            eventId: "evt-after-reenable");
        await handler.HandleAsync(evtAfter, CancellationToken.None);

        // Exactly one item — the one fired after re-enable.
        var item = Assert.Single(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Equal("evt-after-reenable", item.SourceEventId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Fact]
    public async Task Subscription_ReplayingDisabledKindEvent_RemainsNoOp()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1,
            title: "Replay disabled");
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_replay_disabled",
            projectId: "proj_a",
            issueId: "issue_1",
            issueNumber: 1);

        await InboxProjectionTestSupport.SeedSubscriptionAsync(database, "proj_a",
            approvalRequestedEnabled: false);

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.StageApprovalRequested,
            workflowRunId: "wf_replay_disabled",
            eventId: "evt-replay-disabled");

        await handler.HandleAsync(evt, CancellationToken.None);
        await handler.HandleAsync(evt, CancellationToken.None);
        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
    }

    /// <summary>
    /// Minimal <see cref="IGrainFactory"/> stand-in for transactional
    /// unit specs. The dispatcher is a no-op grain reference; producers
    /// only need to call DispatchNowAsync without exceptions. Lets the
    /// store exercise its post-commit poke code path without spinning up
    /// an Orleans silo.
    /// </summary>
    private sealed class NullDispatchGrainFactory : IGrainFactory
    {
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix)
        {
            if (typeof(TGrainInterface) == typeof(IEventDispatcherGrain))
                return (TGrainInterface)(object)new NullEventDispatcherGrain();
            throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");
        }

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix)
            => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix)
            => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

        TGrainObserverInterface IGrainFactory.CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            => throw new NotSupportedException();

        void IGrainFactory.DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, string grainPrimaryKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension)
            => throw new NotSupportedException();

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(GrainId grainId)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(GrainId grainId)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(GrainId grainId, GrainInterfaceType interfaceType)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Drop-in <see cref="IEventDispatcherGrain"/> reference whose
    /// <see cref="DispatchNowAsync"/> returns <see cref="Task.CompletedTask"/>.
    /// Lets the post-commit poke fire without an Orleans silo.
    /// </summary>
    private sealed class NullEventDispatcherGrain : IGrainWithStringKey, IEventDispatcherGrain
    {
        public Task DispatchNowAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<DeadLetterRedeliveryResult> RedeliverAsync(long deadLetterId, CancellationToken ct = default) =>
            Task.FromResult(new DeadLetterRedeliveryResult(false, false, 0, "null grain"));

        public Task ReceiveReminder(string reminderName, TickStatus status) => Task.CompletedTask;

        public GrainId GrainId => default;
        public string Key => string.Empty;
    }
}
