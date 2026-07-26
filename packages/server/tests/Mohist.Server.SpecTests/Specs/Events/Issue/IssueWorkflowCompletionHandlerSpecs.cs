using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Subscriptions;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Events.Issue;

/// <summary>
/// Covers <see cref="IssueWorkflowCompletionHandler"/>: the
/// <c>com.mohist.workflow.run.completed</c> subscription that
/// transitions the owning in-progress issue to <c>Done</c> via
/// <see cref="IIssueGrain.CompleteWorkAsync"/>. Spec:
/// <c>openspec/changes/issue-307/specs/issue-workflow-completion/spec.md</c>.
/// </summary>
public class IssueWorkflowCompletionHandlerSpecs : IssueWorkflowCompletionHandlerTestSupport
{

    [Fact]
    public void HasSingleSubscriptionAttributeForCompleted()
    {
        var attr = (SubscriptionAttribute?)Attribute.GetCustomAttribute(
            typeof(IssueWorkflowCompletionHandler), typeof(SubscriptionAttribute));
        Assert.NotNull(attr);
        Assert.Equal(EventCatalog.ReverseDns.WorkflowRunCompleted, attr!.Type);
    }

    [Fact]
    public async Task Querier_GetIssueForWorkflowRunAsync_ReturnsInProgressIssueReference()
    {
        await using var database = CreateDatabase();
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1,
            status: IssueStatus.InProgress, workflowRunId: "wr_completed");

        var querier = NewIssueQuerier(new TestDbContextFactory(database.Options));

        var issue = await querier.GetIssueForWorkflowRunAsync("wr_completed");

        Assert.Equal(new IssueWorkflowRef("project_1", 1), issue);
    }

    [Fact]
    public async Task Querier_GetIssueForWorkflowRunAsync_DoneIssueWithPreservedReference_ReturnsNull()
    {
        // Done issues keep their workflowRunId as historical execution
        // data — the lookup must filter to in_progress so a stale
        // workflow reference doesn't drive a redundant transition.
        await using var database = CreateDatabase();
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1,
            status: IssueStatus.Done, workflowRunId: "wr_completed");

        var querier = NewIssueQuerier(new TestDbContextFactory(database.Options));

        var issue = await querier.GetIssueForWorkflowRunAsync("wr_completed");

        Assert.Null(issue);
    }

    [Fact]
    public async Task Querier_GetIssueForWorkflowRunAsync_MixedRows_ReturnsOnlyInProgressMatch()
    {
        await using var database = CreateDatabase();
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1,
            status: IssueStatus.Done, workflowRunId: "wr_completed");
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 2,
            status: IssueStatus.Done, workflowRunId: "wr_completed",
            archivedAt: new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc));
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 3,
            status: IssueStatus.InProgress, workflowRunId: "wr_completed");
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 4,
            status: IssueStatus.InProgress, workflowRunId: "wr_other");

        var querier = NewIssueQuerier(new TestDbContextFactory(database.Options));

        var issue = await querier.GetIssueForWorkflowRunAsync("wr_completed");

        Assert.Equal(new IssueWorkflowRef("project_1", 3), issue);
    }

    [Fact]
    public async Task Querier_GetIssueForWorkflowRunAsync_NoMatch_ReturnsNull()
    {
        await using var database = CreateDatabase();
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1,
            status: IssueStatus.InProgress, workflowRunId: "wr_other");

        var querier = NewIssueQuerier(new TestDbContextFactory(database.Options));

        var issue = await querier.GetIssueForWorkflowRunAsync("wr_completed");

        Assert.Null(issue);
    }

    [Fact]
    public async Task Querier_GetIssueForWorkflowRunAsync_NullOrEmpty_ReturnsNull()
    {
        await using var database = CreateDatabase();

        var querier = NewIssueQuerier(new TestDbContextFactory(database.Options));

        Assert.Null(await querier.GetIssueForWorkflowRunAsync(null!));
        Assert.Null(await querier.GetIssueForWorkflowRunAsync(""));
        Assert.Null(await querier.GetIssueForWorkflowRunAsync("   "));
    }

    [Fact]
    public async Task HandleAsync_CompletedEventForInProgressIssue_TransitionsIssueToDone()
    {
        await using var database = CreateDatabase();
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1,
            status: IssueStatus.InProgress, workflowRunId: "wr_completed");

        var grains = new RecordingIssueGrainFactory(new TestDbContextFactory(database.Options), new FakeTimeProvider(FixedNow));
        var handler = new IssueWorkflowCompletionHandler(grains,
            NullLogger<IssueWorkflowCompletionHandler>.Instance);

        var evt = BuildCompletedEvent(workflowRunId: "wr_completed");
        await handler.HandleAsync(evt, CancellationToken.None);

        var call = Assert.Single(grains.Calls);
        Assert.Equal(new IssueWorkflowRef("project_1", 1), call.Issue);
        Assert.Equal("wr_completed", call.WorkflowRunId);

        await using var verify = database.CreateContext();
        var stored = await verify.Issues.AsNoTracking().FirstAsync();
        // After the handler ran, the in-progress issue has been
        // transitioned to Done (driven entirely by the event
        // subscription — no sweep advancement, no read-path open).
        Assert.Equal("done", stored.Status);
        Assert.Equal(IssueStatus.Done, IssueStore.Deserialize(stored.State)!.Status);
    }

    [Fact]
    public async Task HandleAsync_EmptySource_NoOpsAndDoesNotInvokeGrain()
    {
        await using var database = CreateDatabase();
        var grains = new RecordingIssueGrainFactory(new TestDbContextFactory(database.Options), new FakeTimeProvider(FixedNow));
        var handler = new IssueWorkflowCompletionHandler(grains,
            NullLogger<IssueWorkflowCompletionHandler>.Instance);

        var evt = new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/other/whatever", UriKind.Relative),
            type: EventCatalog.ReverseDns.WorkflowRunCompleted,
            time: FixedNow,
            data: null);

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(grains.Calls);
    }

    [Fact]
    public async Task HandleAsync_MissingScopedIssueExtension_NoOpsAndDoesNotInvokeGrain()
    {
        // A completion event without the project-scoped issue reference
        // cannot identify an aggregate and must not invoke a grain.
        await using var database = CreateDatabase();
        var grains = new RecordingIssueGrainFactory(new TestDbContextFactory(database.Options), new FakeTimeProvider(FixedNow));
        var handler = new IssueWorkflowCompletionHandler(grains,
            NullLogger<IssueWorkflowCompletionHandler>.Instance);

        var evt = new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/workflow-runs/wr_orphan", UriKind.Relative),
            type: EventCatalog.ReverseDns.WorkflowRunCompleted,
            time: FixedNow,
            data: null);

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(grains.Calls);
    }

    [Fact]
    public async Task HandleAsync_DuplicateCompletedDelivery_OnlyFirstInvocationRunsGrainLogic()
    {
        // First delivery transitions the issue to Done; the second
        // delivery invokes CompleteWorkAsync again, but the aggregate
        // guard rejects it (issue is no longer in_progress), so only
        // one effective transition occurs. No throw, no field mutation.
        // This is the documented idempotent path.
        await using var database = CreateDatabase();
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1,
            status: IssueStatus.InProgress, workflowRunId: "wr_completed");

        var grains = new RecordingIssueGrainFactory(new TestDbContextFactory(database.Options), new FakeTimeProvider(FixedNow));
        var handler = new IssueWorkflowCompletionHandler(grains,
            NullLogger<IssueWorkflowCompletionHandler>.Instance);

        var evt1 = BuildCompletedEvent(workflowRunId: "wr_completed");
        var evt2 = BuildCompletedEvent(workflowRunId: "wr_completed");
        await handler.HandleAsync(evt1, CancellationToken.None);
        await handler.HandleAsync(evt2, CancellationToken.None);

        Assert.Equal(2, grains.Calls.Count);
        Assert.All(grains.Calls, c =>
        {
            Assert.Equal(new IssueWorkflowRef("project_1", 1), c.Issue);
            Assert.Equal("wr_completed", c.WorkflowRunId);
        });

        await using var verify = database.CreateContext();
        var stored = await verify.Issues.AsNoTracking().FirstAsync();
        Assert.Equal(IssueStatus.Done, IssueStore.Deserialize(stored.State)!.Status);
    }

    [Fact]
    public async Task HandleAsync_MismatchedWorkflowRunIdOnIssue_DoesNotMutateIssue()
    {
        // After the issue is Done with wr_completed preserved, a
        // second event for the same run id calls CompleteWorkAsync
        // again, but the aggregate guard rejects it (issue is no
        // longer in_progress). Verify no mutation happens even when a
        // *different* delivery path attempts to invoke CompleteWorkAsync
        // with a stale workflowRunId — the Issue.Complete guard rejects
        // it (no change).
        await using var database = CreateDatabase();
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1,
            status: IssueStatus.InProgress, workflowRunId: "wr_completed");

        var grains = new RecordingIssueGrainFactory(new TestDbContextFactory(database.Options), new FakeTimeProvider(FixedNow));
        var handler = new IssueWorkflowCompletionHandler(grains,
            NullLogger<IssueWorkflowCompletionHandler>.Instance);

        // Pre-flip the issue to Done with the matching id, then verify
        // a mismatched-id CompleteWorkAsync is guarded by the aggregate.
        // Use the handler's own grain to perform the initial transition so
        // the in-memory cache and DB stay consistent.
        var firstEvent = BuildCompletedEvent(workflowRunId: "wr_completed");
        await handler.HandleAsync(firstEvent, CancellationToken.None);
        var firstCall = grains.Calls.Single();
        Assert.Equal(new IssueWorkflowRef("project_1", 1), firstCall.Issue);
        Assert.Equal("wr_completed", firstCall.WorkflowRunId);
        grains.Calls.Clear();

        // Now drive the handler with the SAME run id; CompleteWorkAsync
        // is invoked again but the aggregate is already Done so the
        // transition is a no-op.
        var evt = BuildCompletedEvent(workflowRunId: "wr_completed");
        await handler.HandleAsync(evt, CancellationToken.None);

        var secondCall = Assert.Single(grains.Calls);
        Assert.Equal(new IssueWorkflowRef("project_1", 1), secondCall.Issue);
        Assert.Equal("wr_completed", secondCall.WorkflowRunId);
        grains.Calls.Clear();

        // Independently verify that CompleteWorkAsync with a
        // mismatched workflowRunId would be a no-op (Issue.Complete
        // guard): this is what the spec requirement
        // "Mismatched workflow run id is ignored" asserts. The
        // aggregate's Complete() returns false when the run id does
        // not match — the guard fires regardless of subscription
        // filtering.
        var staleGrain = grains.GetIssueGrain("project_1:1");
        await staleGrain.CompleteWorkAsync("wr_mismatch");
        var stillOneCall = Assert.Single(grains.Calls);
        Assert.Equal("wr_mismatch", stillOneCall.WorkflowRunId);

        await using var verify = database.CreateContext();
        var stored = await verify.Issues.AsNoTracking().FirstAsync();
        var final = IssueStore.Deserialize(stored.State)!;
        Assert.Equal(IssueStatus.Done, final.Status);
    }

    [Fact]
    public async Task HandleAsync_GrainThrows_PropagatesToDispatcher()
    {
        await using var database = CreateDatabase();

        var grains = new ThrowingIssueGrainFactory();
        var handler = new IssueWorkflowCompletionHandler(grains,
            NullLogger<IssueWorkflowCompletionHandler>.Instance);

        var evt = BuildCompletedEvent(workflowRunId: "wr_completed");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(evt, CancellationToken.None));

        Assert.Equal(1, grains.CallCount);
    }

    [Fact]
    public async Task Filter_FailedTerminalEvent_ReturnsFalse()
    {
        await using var database = CreateDatabase();
        var grains = new RecordingIssueGrainFactory(new TestDbContextFactory(database.Options), new FakeTimeProvider(FixedNow));
        var handler = new IssueWorkflowCompletionHandler(grains,
            NullLogger<IssueWorkflowCompletionHandler>.Instance);

        var evt = new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/workflow-runs/wr_failed", UriKind.Relative),
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            time: FixedNow,
            data: null);

        Assert.False(handler.Filter(evt));
    }

    [Fact]
    public async Task Filter_StoppedTerminalEvent_ReturnsFalse()
    {
        await using var database = CreateDatabase();
        var grains = new RecordingIssueGrainFactory(new TestDbContextFactory(database.Options), new FakeTimeProvider(FixedNow));
        var handler = new IssueWorkflowCompletionHandler(grains,
            NullLogger<IssueWorkflowCompletionHandler>.Instance);

        var evt = new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/workflow-runs/wr_stopped", UriKind.Relative),
            type: EventCatalog.ReverseDns.WorkflowRunStopped,
            time: FixedNow,
            data: null);

        Assert.False(handler.Filter(evt));
    }

    [Fact]
    public async Task Filter_CompletedEvent_ReturnsTrue()
    {
        await using var database = CreateDatabase();
        var grains = new RecordingIssueGrainFactory(new TestDbContextFactory(database.Options), new FakeTimeProvider(FixedNow));
        var handler = new IssueWorkflowCompletionHandler(grains,
            NullLogger<IssueWorkflowCompletionHandler>.Instance);

        var evt = BuildCompletedEvent(workflowRunId: "wr_completed");

        Assert.True(handler.Filter(evt));
    }

    [Fact]
    public async Task HandleAsync_CompletesIssue_FromScopedIssueExtensions()
    {
        // The event carries the project-scoped issue reference, so the
        // handler dispatches CompleteWorkAsync without a reverse lookup.
        await using var database = CreateDatabase();
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1,
            status: IssueStatus.InProgress, workflowRunId: "wr_completed");

        var grains = new RecordingIssueGrainFactory(new TestDbContextFactory(database.Options), new FakeTimeProvider(FixedNow));
        var handler = new IssueWorkflowCompletionHandler(grains,
            NullLogger<IssueWorkflowCompletionHandler>.Instance);

        await handler.HandleAsync(
            BuildCompletedEvent(workflowRunId: "wr_completed"),
            CancellationToken.None);

        var call = Assert.Single(grains.Calls);
        Assert.Equal(new IssueWorkflowRef("project_1", 1), call.Issue);
        Assert.Equal("wr_completed", call.WorkflowRunId);

        await using var verify = database.CreateContext();
        var stored = await verify.Issues.AsNoTracking().FirstAsync();
        Assert.Equal(IssueStatus.Done, IssueStore.Deserialize(stored.State)!.Status);
    }

}
