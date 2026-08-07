using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Inbox;
using Mohist.Server.Inbox.Subscriptions;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
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
    [Fact]
    public async Task WorkflowRunFailed_ProducesWorkflowFailedItemInOwningProject()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_1",
            projectId: "proj_a",
            issueNumber: 42);
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueNumber: 42,
            title: "Issue 42");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            workflowRunId: "wf_1",
            eventId: "evt-failed",
            projectId: "proj_a",
            issueNumber: 42);

        await handler.HandleAsync(evt, CancellationToken.None);

        var item = Assert.Single(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Equal(NotificationKinds.WorkflowFailed, item.NotificationKind);
        Assert.Equal("/mohist/workflow-runs/wf_1", item.SourceEventSource);
        Assert.Equal("evt-failed", item.SourceEventId);
        Assert.Equal("proj_a", item.ProjectId);
        Assert.Equal(42, item.IssueNumber);
        Assert.Equal("Issue 42", item.IssueTitle);
    }

    [Fact]
    public async Task AgentJobFailed_ProducesAgentResponseFailedItemInOwningProject()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueNumber: 42,
            title: "Issue 42");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = new CloudEvent(
            id: "evt-agent-failed",
            source: new Uri("/mohist/agent-job/job_1", UriKind.Relative),
            type: EventCatalog.ReverseDns.AgentJobFailed,
            time: TestTime.UtcNow,
            data: null,
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.AgentId] = "agent_1",
                [EventCatalog.Lineage.ProjectId] = "proj_a",
                [EventCatalog.Lineage.Issue] = "42",
            });

        await handler.HandleAsync(evt, CancellationToken.None);

        var item = Assert.Single(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Equal(NotificationKinds.AgentResponseFailed, item.NotificationKind);
        Assert.Equal("evt-agent-failed", item.SourceEventId);
    }

    [Fact]
    public async Task StageApprovalRequested_ProducesApprovalRequestedItemInOwningProject()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_2",
            projectId: "proj_a",
            issueNumber: 42);
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueNumber: 42,
            title: "Approve me");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.StageApprovalRequested,
            workflowRunId: "wf_2",
            eventId: "evt-approval",
            projectId: "proj_a",
            issueNumber: 42);

        await handler.HandleAsync(evt, CancellationToken.None);

        var item = Assert.Single(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Equal(NotificationKinds.ApprovalRequested, item.NotificationKind);
        Assert.Equal("evt-approval", item.SourceEventId);
        Assert.Equal("Approve me", item.IssueTitle);
        Assert.Equal(42, item.IssueNumber);
    }

    [Fact]
    public async Task IssueWorkStarted_ProducesIssueStartedItemInOwningProject()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueNumber: 7,
            title: "Started");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            projectId: "proj_a",
            issueNumber: 7,
            eventId: "evt-started");

        await handler.HandleAsync(evt, CancellationToken.None);

        var item = Assert.Single(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Equal(NotificationKinds.IssueStarted, item.NotificationKind);
        Assert.Equal("/mohist/projects/proj_a/issues/7", item.SourceEventSource);
        Assert.Equal("evt-started", item.SourceEventId);
        Assert.Equal(7, item.IssueNumber);
        Assert.Equal("Started", item.IssueTitle);
    }

    [Fact]
    public async Task IssueCompleted_ProducesIssueCompletedItemInOwningProject()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueNumber: 9,
            title: "Done");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueCompleted,
            projectId: "proj_a",
            issueNumber: 9,
            eventId: "evt-completed");

        await handler.HandleAsync(evt, CancellationToken.None);

        var item = Assert.Single(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Equal(NotificationKinds.IssueCompleted, item.NotificationKind);
        Assert.Equal("evt-completed", item.SourceEventId);
        Assert.Equal(9, item.IssueNumber);
        Assert.Equal("Done", item.IssueTitle);
    }

    [Fact]
    public async Task ReplayOfSameCloudEvent_DoesNotCreateSecondItem()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueNumber: 1,
            title: "Issue 1");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            projectId: "proj_a",
            issueNumber: 1,
            eventId: "evt-replay");

        await handler.HandleAsync(evt, CancellationToken.None);
        await handler.HandleAsync(evt, CancellationToken.None);
        await handler.HandleAsync(evt, CancellationToken.None);

        var items = await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a");
        var item = Assert.Single(items);
        Assert.Equal("evt-replay", item.SourceEventId);
    }

    [Fact]
    public async Task WorkflowEventReplay_DoesNotCreateSecondItem()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_replay",
            projectId: "proj_a",
            issueNumber: 1);
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueNumber: 1,
            title: "Issue 1");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            workflowRunId: "wf_replay",
            eventId: "evt-wf-replay",
            projectId: "proj_a",
            issueNumber: 1);

        await handler.HandleAsync(evt, CancellationToken.None);
        await handler.HandleAsync(evt, CancellationToken.None);

        var items = await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a");
        var item = Assert.Single(items);
        Assert.Equal("evt-wf-replay", item.SourceEventId);
    }

    [Fact]
    public async Task WorkflowStorePublishThenEventStoreReplay_DoesNotDuplicateInboxItem()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueNumber: 1,
            title: "Issue 1");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var eventStore = new EventStore(new TestDbContextFactory(database.Options), NullLogger<EventStore>.Instance);
        var runStore = InboxProjectionTestSupport.CreateRunStore(new TestDbContextFactory(database.Options), eventStore);
        var run = InboxProjectionTestSupport.BuildWorkflowRun(
            workflowRunId: "wf_store_replay",
            projectId: "proj_a",
            issueNumber: 1);

        await runStore.SaveAsync(run, [new WorkflowRunFailed("failed")]);

        var stored = Assert.Single(await eventStore.ListAsync("wf_store_replay"));
        await handler.HandleAsync(stored.Envelope, CancellationToken.None);

        var item = Assert.Single(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Equal(stored.Envelope.Source.ToString(), item.SourceEventSource);
        Assert.Equal(stored.Envelope.Id, item.SourceEventId);
    }

    [Fact]
    public async Task WorkflowEvent_LandsInProjectOwnedByRun_NotInOtherProjects()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_iso",
            projectId: "proj_a",
            issueNumber: 1);
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueNumber: 1,
            title: "Issue 1");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            workflowRunId: "wf_iso",
            eventId: "evt-iso",
            projectId: "proj_a",
            issueNumber: 1);

        await handler.HandleAsync(evt, CancellationToken.None);

        var aItems = await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a");
        var bItems = await InboxProjectionTestSupport.GetInboxAsync(database, "proj_b");
        Assert.Single(aItems);
        Assert.Empty(bItems);
        Assert.Equal("proj_a", aItems[0].ProjectId);
    }

    [Fact]
    public async Task IssueEvent_LandsInProjectOwnedByIssue_NotInOtherProjects()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueNumber: 1,
            title: "A1");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueCompleted,
            projectId: "proj_a",
            issueNumber: 1,
            eventId: "evt-iso");

        await handler.HandleAsync(evt, CancellationToken.None);

        var aItems = await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a");
        var bItems = await InboxProjectionTestSupport.GetInboxAsync(database, "proj_b");
        Assert.Single(aItems);
        Assert.Empty(bItems);
        Assert.Equal("proj_a", aItems[0].ProjectId);
    }

    [Fact]
    public async Task IssueEvent_ProjectExtensionDisagreesWithLoadedIssue_SkipsWithoutLeakingToClaimedProject()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueNumber: 1,
            title: "A1");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueCompleted,
            projectId: "proj_b",
            issueNumber: 1,
            eventId: "evt-mismatch-project");

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_b"));
    }

    [Fact]
    public async Task IssueEvent_NumberExtensionDisagreesWithLoadedIssue_SkipsWithoutThrowing()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueNumber: 1,
            title: "A1");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            projectId: "proj_a",
            issueNumber: 2,
            eventId: "evt-mismatch-number");

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
    }

    [Fact]
    public async Task WorkflowEvent_RunAnnotationsDisagreeWithLoadedIssue_SkipsWithoutLeakingToClaimedProject()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_bad_ann",
            projectId: "proj_b",
            issueNumber: 1);
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueNumber: 1,
            title: "A1");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            workflowRunId: "wf_bad_ann",
            eventId: "evt-wf-mismatch-project",
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.ProjectId] = "proj_b",
                [EventCatalog.Lineage.Issue] = "1",
                [EventCatalog.Lineage.WorkflowRunId] = "wf_bad_ann",
            });

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_b"));
    }

    [Fact]
    public async Task WorkflowEvent_RunIssueNumberDisagreesWithLoadedIssue_SkipsWithoutThrowing()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_bad_num",
            projectId: "proj_a",
            issueNumber: 2);
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueNumber: 1,
            title: "A1");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildWorkflowEvent(
            type: EventCatalog.ReverseDns.StageApprovalRequested,
            workflowRunId: "wf_bad_num",
            eventId: "evt-wf-mismatch-number",
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.ProjectId] = "proj_a",
                [EventCatalog.Lineage.Issue] = "2",
                [EventCatalog.Lineage.WorkflowRunId] = "wf_bad_num",
            });

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
    }

    [Fact]
    public async Task IssueEvent_MissingProjectIdExtension_SkipsWithoutThrowing()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = new CloudEvent(
            id: "evt-no-ext",
            source: new Uri("/mohist/projects/proj_a/issues/1", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            time: TestTime.UtcNow,
            data: null,
            extensions: new Dictionary<string, string>
            {
                [EventCatalog.Lineage.Issue] = "1",
            });

        await handler.HandleAsync(evt, CancellationToken.None);

        var items = await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a");
        Assert.Empty(items);
    }

    [Fact]
    public async Task IssueEvent_MissingIssueNumberExtension_SkipsWithoutThrowing()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = new CloudEvent(
            id: "evt-no-issue",
            source: new Uri("/mohist/projects/proj_a/issues/1", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCompleted,
            time: TestTime.UtcNow,
            data: null,
            extensions: new Dictionary<string, string>
            {
                [EventCatalog.Lineage.ProjectId] = "proj_a",
            });

        await handler.HandleAsync(evt, CancellationToken.None);

        var items = await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a");
        Assert.Empty(items);
    }

    [Fact]
    public async Task IssueEvent_WithoutCanonicalIssueKey_Skips()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = new CloudEvent(
            id: "evt-without-canonical-issue",
            source: new Uri("/mohist/projects/proj_a/issues/7", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            time: InboxProjectionTestSupport.FixedEventTime,
            data: null,
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.ProjectId] = "proj_a",
            });

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
    }

    [Fact]
    public async Task IssueEvent_NoIssueNumberKey_ReturnsNullWithoutThrowing()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueNumber: 3,
            title: "Orphan");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        // Neither `issue` nor `issueno` is present: identity cannot be
        // resolved, the handler must skip silently.
        var evt = new CloudEvent(
            id: "evt-no-number",
            source: new Uri("/mohist/projects/proj_a/issues/3", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            time: InboxProjectionTestSupport.FixedEventTime,
            data: null,
            extensions: new Dictionary<string, string>
            {
                [EventCatalog.Lineage.ProjectId] = "proj_a",
            });

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
    }

    [Fact]
    public async Task IssueEvent_CanonicalIssueKey_Resolves()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueNumber: 9,
            title: "Both");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = new CloudEvent(
            id: "evt-both",
            source: new Uri("/mohist/projects/proj_a/issues/9", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            time: InboxProjectionTestSupport.FixedEventTime,
            data: null,
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.ProjectId] = "proj_a",
                [EventCatalog.Lineage.Issue] = "9",
            });

        await handler.HandleAsync(evt, CancellationToken.None);

        var item = Assert.Single(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Equal(9, item.IssueNumber);
    }

    [Fact]
    public async Task IssueEvent_RoutesByEnvelopeWithoutMutatingPayload()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueNumber: 42,
            title: "Envelope target");

        var payload = System.Text.Json.JsonSerializer.SerializeToElement(
            new { issueNumber = 99 }, CloudEvent.JsonOptions);
        var evt = new CloudEvent(
            id: "evt-envelope-route",
            source: new Uri("/mohist/projects/proj_a/issues/99", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCompleted,
            time: InboxProjectionTestSupport.FixedEventTime,
            data: payload,
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.ProjectId] = "proj_a",
                [EventCatalog.Lineage.Issue] = "42",
            });

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        await handler.HandleAsync(evt, CancellationToken.None);

        var item = Assert.Single(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Equal(42, item.IssueNumber);
        Assert.Equal(99, payload.GetProperty("issueNumber").GetInt32());
    }

    [Fact]
    public async Task WorkflowEvent_MissingAnnotationsOnRun_SkipsWithoutThrowing()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        // Workflow run exists but has no projectId/issueNumber annotations.
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_no_ann",
            projectId: null,
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

    [Fact]
    public async Task IssueEvent_UnknownIssueNumberForTitle_SkipsWithoutThrowing()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueCompleted,
            projectId: "proj_a",
            issueNumber: 7,
            eventId: "evt-missing-issue");

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
    }

    [Fact]
    public async Task WorkflowEvent_TitleTakenFromIssueRowSnapshottedAtProjectionTime()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_title",
            projectId: "proj_a",
            issueNumber: 1);
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
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
        await using (var db = database.CreateContext())
        {
            var row = await db.Issues.FirstAsync(r => r.ProjectId == "proj_a" && r.Number == 1);
            // The Issue.Title is init-only — patch the JSON state directly
            // to simulate a later title edit. The InboxItem row holds the
            // snapshot from projection time.
            row.State = row.State.Replace("\"Snapshot me\"", "\"Renamed later\"");
            await db.SaveChangesAsync();
        }

        var item = Assert.Single(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Equal("Snapshot me", item.IssueTitle);
    }

    [Fact]
    public async Task Filter_AcceptsAllFiveTypes()
    {
        var handler = InboxProjectionTestSupport.CreateHandler(InboxProjectionTestSupport.CreateDatabase());
        Assert.True(handler.Filter(InboxProjectionTestSupport.BuildIssueEvent(EventCatalog.ReverseDns.IssueWorkStarted, "p", 1, "e1")));
        Assert.True(handler.Filter(InboxProjectionTestSupport.BuildIssueEvent(EventCatalog.ReverseDns.IssueCompleted, "p", 1, "e2")));
        Assert.True(handler.Filter(InboxProjectionTestSupport.BuildWorkflowEvent(EventCatalog.ReverseDns.WorkflowRunFailed, "w", "e3")));
        Assert.True(handler.Filter(InboxProjectionTestSupport.BuildWorkflowEvent(EventCatalog.ReverseDns.StageApprovalRequested, "w", "e4")));
        Assert.True(handler.Filter(new CloudEvent(
            id: "e5",
            source: new Uri("/mohist/agent-job/job_1", UriKind.Relative),
            type: EventCatalog.ReverseDns.AgentJobFailed,
            time: TestTime.UtcNow,
            data: null)));
    }

    [Fact]
    public async Task Filter_RejectsOtherEventTypes()
    {
        var handler = InboxProjectionTestSupport.CreateHandler(InboxProjectionTestSupport.CreateDatabase());
        Assert.False(handler.Filter(new CloudEvent(
            id: "e",
            source: new Uri("/mohist/projects/proj_a/issues/1", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCancelled,
            time: TestTime.UtcNow,
            data: null)));
    }

    [Fact]
    public async Task HasSubscriptionAttributeWithExpectedFiveTypes()
    {
        var attr = (SubscriptionAttribute?)Attribute.GetCustomAttribute(
            typeof(InboxProjectionHandler), typeof(SubscriptionAttribute));
        Assert.NotNull(attr);
        Assert.Equal(
            EventCatalog.ReverseDns.WorkflowRunFailed + "|" +
            EventCatalog.ReverseDns.StageApprovalRequested + "|" +
            EventCatalog.ReverseDns.IssueWorkStarted + "|" +
            EventCatalog.ReverseDns.IssueCompleted + "|" +
            EventCatalog.ReverseDns.AgentJobFailed,
            attr!.Type);
    }

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
            issueNumber: 1);
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_2",
            projectId: "proj_a",
            issueNumber: 1);
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueNumber: 1,
            title: "Issue 1");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        await handler.HandleAsync(InboxProjectionTestSupport.BuildWorkflowEvent(EventCatalog.ReverseDns.WorkflowRunFailed, "wf_1", "evt-failed"), CancellationToken.None);
        await handler.HandleAsync(InboxProjectionTestSupport.BuildWorkflowEvent(EventCatalog.ReverseDns.StageApprovalRequested, "wf_2", "evt-approval"), CancellationToken.None);
        await handler.HandleAsync(InboxProjectionTestSupport.BuildIssueEvent(EventCatalog.ReverseDns.IssueWorkStarted, "proj_a", 1, "evt-started"), CancellationToken.None);
        await handler.HandleAsync(InboxProjectionTestSupport.BuildIssueEvent(EventCatalog.ReverseDns.IssueCompleted, "proj_a", 1, "evt-completed"), CancellationToken.None);

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

    [Fact]
    public async Task Subscription_EnabledKind_CreatesItem()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueNumber: 1,
            title: "Enabled test");
        await InboxProjectionTestSupport.SeedSubscriptionAsync(database, "proj_a",
            workflowFailedEnabled: true);

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueCompleted,
            projectId: "proj_a",
            issueNumber: 1,
            eventId: "evt-enabled");

        await handler.HandleAsync(evt, CancellationToken.None);

        var item = Assert.Single(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Equal(NotificationKinds.IssueCompleted, item.NotificationKind);
    }

    [Fact]
    public async Task Subscription_DisabledKind_DoesNotCreateItem()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
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
            issueNumber: 1);

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
    }

    [Fact]
    public async Task Subscription_DifferentDisabledKinds_EachPreventsItsOwnKind()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueNumber: 1,
            title: "Multi disabled");
        await InboxProjectionTestSupport.SeedSubscriptionAsync(database, "proj_a",
            workflowFailedEnabled: false,
            approvalRequestedEnabled: false,
            issueStartedEnabled: false,
            issueCompletedEnabled: false);

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_fail", projectId: "proj_a", issueNumber: 1);
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_approve", projectId: "proj_a", issueNumber: 1);

        await handler.HandleAsync(InboxProjectionTestSupport.BuildWorkflowEvent(EventCatalog.ReverseDns.WorkflowRunFailed, "wf_fail", "evt-f"), CancellationToken.None);
        await handler.HandleAsync(InboxProjectionTestSupport.BuildWorkflowEvent(EventCatalog.ReverseDns.StageApprovalRequested, "wf_approve", "evt-a"), CancellationToken.None);
        await handler.HandleAsync(InboxProjectionTestSupport.BuildIssueEvent(EventCatalog.ReverseDns.IssueWorkStarted, "proj_a", 1, "evt-s"), CancellationToken.None);
        await handler.HandleAsync(InboxProjectionTestSupport.BuildIssueEvent(EventCatalog.ReverseDns.IssueCompleted, "proj_a", 1, "evt-c"), CancellationToken.None);

        Assert.Empty(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
    }

    [Fact]
    public async Task Subscription_MissingSubscription_CreatesItem()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueNumber: 1,
            title: "Missing sub");

        var handler = InboxProjectionTestSupport.CreateHandler(database);
        var evt = InboxProjectionTestSupport.BuildIssueEvent(
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            projectId: "proj_a",
            issueNumber: 1,
            eventId: "evt-no-sub");

        await handler.HandleAsync(evt, CancellationToken.None);

        var item = Assert.Single(await InboxProjectionTestSupport.GetInboxAsync(database, "proj_a"));
        Assert.Equal(NotificationKinds.IssueStarted, item.NotificationKind);
    }

    [Fact]
    public async Task Subscription_DisabledKind_LeavesExistingItemsUntouched()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueNumber: 1,
            title: "Existing item");
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_existing",
            projectId: "proj_a",
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

    [Fact]
    public async Task Subscription_ReEnabledKind_CreatesItemForSubsequentEvent()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueNumber: 1,
            title: "Re-enable test");
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_re",
            projectId: "proj_a",
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

    [Fact]
    public async Task Subscription_ReplayingDisabledKindEvent_RemainsNoOp()
    {
        await using var database = InboxProjectionTestSupport.CreateDatabase();
        await InboxProjectionTestSupport.SeedIssueAsync(database,
            projectId: "proj_a",
            issueNumber: 1,
            title: "Replay disabled");
        await InboxProjectionTestSupport.SeedWorkflowRunAsync(database,
            workflowRunId: "wf_replay_disabled",
            projectId: "proj_a",
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

}
