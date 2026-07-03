using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Epic.Services;
using Mohist.Server.Events.Subscriptions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Tests.Support;
using Orleans;
using System.Text.Json;
using Xunit;

namespace Mohist.Server.Tests.Specs.Events;

public class EpicAutoDoneHandlerSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task HandleAsync_LastIssueCompletes_TransitionsEpicToDone()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_1", issueNumber: 1);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicAutoDoneHandler(querier, grains, NullLogger<EpicAutoDoneHandler>.Instance);

        var evt = BuildCompletedEvent(projectId: "project_1", issueId: "issue_1");
        await handler.HandleAsync(evt, CancellationToken.None);

        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("done", stored.Status);
        Assert.Single(grains.Calls);
        Assert.Equal("project_1:epic_1", grains.Calls[0].GrainKey);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task HandleAsync_RehomedIssue_DispatchesToNonTerminalEpic()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_closed", number: 1, status: "closed");
        await SeedEpicAsync(database, epicId: "epic_running", number: 2, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        await SeedLinkAsync(database, epicId: "epic_closed", issueId: "issue_1", issueNumber: 1);
        await SeedLinkAsync(database, epicId: "epic_running", issueId: "issue_1", issueNumber: 1);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicAutoDoneHandler(querier, grains, NullLogger<EpicAutoDoneHandler>.Instance);

        await handler.HandleAsync(BuildCompletedEvent(projectId: "project_1", issueId: "issue_1"), CancellationToken.None);

        var call = Assert.Single(grains.Calls);
        Assert.Equal("project_1:epic_running", call.GrainKey);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task HandleAsync_IssueNotLinkedToAnyEpic_NoOpsAndDoesNotInvokeGrain()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicAutoDoneHandler(querier, grains, NullLogger<EpicAutoDoneHandler>.Instance);

        var evt = BuildCompletedEvent(projectId: "project_1", issueId: "issue_unlinked");
        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(grains.Calls);
        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("idle", stored.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task HandleAsync_EpicStillHasIncompleteIssues_StaysIdle()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_2", issueNumber: 2, status: Mohist.Server.Issue.Domain.IssueStatus.InProgress);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_1", issueNumber: 1);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_2", issueNumber: 2);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicAutoDoneHandler(querier, grains, NullLogger<EpicAutoDoneHandler>.Instance);

        var evt = BuildCompletedEvent(projectId: "project_1", issueId: "issue_1");
        await handler.HandleAsync(evt, CancellationToken.None);

        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("idle", stored.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task HandleAsync_PausedEpic_RemainsPausedNoAutoDone()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "paused", pauseReason: "on hold");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_1", issueNumber: 1);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicAutoDoneHandler(querier, grains, NullLogger<EpicAutoDoneHandler>.Instance);

        var evt = BuildCompletedEvent(projectId: "project_1", issueId: "issue_1");
        await handler.HandleAsync(evt, CancellationToken.None);

        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("paused", stored.Status);
        Assert.Equal("on hold", stored.PauseReason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task HandleAsync_DuplicateWorkCompletedEvents_ConvergeToDoneAndNoErrors()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_1", issueNumber: 1);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicAutoDoneHandler(querier, grains, NullLogger<EpicAutoDoneHandler>.Instance);

        var evt = BuildCompletedEvent(projectId: "project_1", issueId: "issue_1");
        await handler.HandleAsync(evt, CancellationToken.None);
        await handler.HandleAsync(evt, CancellationToken.None);
        await handler.HandleAsync(evt, CancellationToken.None);

        // The first reconcile marks the epic terminal and releases active
        // ownership; repeated terminal events then find no active owner.
        Assert.Single(grains.Calls);
        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("done", stored.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task HandleAsync_DeliveredThroughInMemoryBus_TypedHandlerDispatches()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_1", issueNumber: 1);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicAutoDoneHandler(querier, grains, NullLogger<EpicAutoDoneHandler>.Instance);

        var subscriptions = new List<Subscription>
        {
            new(EventCatalog.ReverseDns.IssueCompleted, handler, (h, e, ct) =>
                ((ICloudEventHandler<IssueCompleted>)h).HandleAsync(
                    new CloudEvent<IssueCompleted>(
                        e.Id, e.Source, e.Type, e.Time,
                        e.Data!.Value.Deserialize<IssueCompleted>(CloudEvent.JsonOptions)!,
                        e.DataContentType, e.Subject, e.SpecVersion, e.Extensions),
                    ct)),
        };
        var bus = new InMemoryEventBus(subscriptions, NullLogger<InMemoryEventBus>.Instance);

        var extensions = new Dictionary<string, string>
        {
            ["projectid"] = "project_1",
            ["issueid"] = "issue_1",
            ["issueno"] = "1",
        };
        await bus.PublishAsync(
            data: new IssueCompleted("wr_1"),
            type: EventCatalog.ReverseDns.IssueCompleted,
            source: "/mohist/issue/issue_1",
            subject: "1",
            extensions: extensions);

        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("done", stored.Status);
        Assert.Single(grains.Calls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task HandleAsync_MissingProjectIdExtension_NoOpsWithoutError()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicAutoDoneHandler(querier, grains, NullLogger<EpicAutoDoneHandler>.Instance);

        var evt = new CloudEvent<IssueCompleted>(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/issue/issue_1", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCompleted,
            time: DateTimeOffset.UtcNow,
            data: new IssueCompleted("wr_1"),
            subject: "1",
            extensions: new Dictionary<string, string> { ["issueid"] = "issue_1" });

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(grains.Calls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task HandleAsync_HasSubscriptionAttributeWithExpectedType()
    {
        var attr = (SubscriptionAttribute?)Attribute.GetCustomAttribute(
            typeof(EpicAutoDoneHandler), typeof(SubscriptionAttribute));
        Assert.NotNull(attr);
        Assert.Equal(EventCatalog.ReverseDns.IssueCompleted, attr!.Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task CancelledHandler_HasSubscriptionAttributeOnCancelledType()
    {
        // The IssueCancelled subscription is required: a cancelled in-progress
        // issue must trigger reconcile so the next startable issue is
        // advanced (otherwise the epic deadlocks on a cancelled slot).
        var attr = (SubscriptionAttribute?)Attribute.GetCustomAttribute(
            typeof(EpicCancelledReconcileHandler), typeof(SubscriptionAttribute));
        Assert.NotNull(attr);
        Assert.Equal(EventCatalog.ReverseDns.IssueCancelled, attr!.Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task CancelledHandler_CancelledIssue_InvokesReconcileOnOwningEpic()
    {
        // Both terminal events funnel through the same grain method;
        // this verifies the new subscription delivers the same call.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Cancelled);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_1", issueNumber: 1);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicCancelledReconcileHandler(querier, grains, NullLogger<EpicCancelledReconcileHandler>.Instance);

        var evt = BuildCancelledEvent(projectId: "project_1", issueId: "issue_1");
        await handler.HandleAsync(evt, CancellationToken.None);

        // The grain call itself is the wiring contract — ReconcileAfterTerminalAsync
        // advances the next startable issue via the EpicGrain (covered by
        // EpicProgressionSpecs.ReconcileAfterTerminalAsync_RunningEpicOnCancelledInProgressIssue_AdvancesNext).
        var call = Assert.Single(grains.Calls);
        Assert.Equal("project_1:epic_1", call.GrainKey);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task CancelledHandler_RehomedIssue_DispatchesToNonTerminalEpic()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_done", number: 1, status: "done");
        await SeedEpicAsync(database, epicId: "epic_running", number: 2, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Cancelled);
        await SeedLinkAsync(database, epicId: "epic_done", issueId: "issue_1", issueNumber: 1);
        await SeedLinkAsync(database, epicId: "epic_running", issueId: "issue_1", issueNumber: 1);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicCancelledReconcileHandler(querier, grains, NullLogger<EpicCancelledReconcileHandler>.Instance);

        await handler.HandleAsync(BuildCancelledEvent(projectId: "project_1", issueId: "issue_1"), CancellationToken.None);

        var call = Assert.Single(grains.Calls);
        Assert.Equal("project_1:epic_running", call.GrainKey);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task CancelledHandler_IssueNotLinkedToAnyEpic_NoOpsWithoutGrainCall()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicCancelledReconcileHandler(querier, grains, NullLogger<EpicCancelledReconcileHandler>.Instance);

        var evt = BuildCancelledEvent(projectId: "project_1", issueId: "issue_unlinked");
        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(grains.Calls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task CancelledHandler_DuplicateCancelledEvents_AreIdempotent()
    {
        // Duplicate terminal signals must converge to the same state
        // without erroring. After the terminal/open readiness change,
        // a running epic with only a cancelled linked issue has no open
        // linked issue and auto-marks done on the first reconcile;
        // subsequent duplicate events see a terminal epic and no-op.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Cancelled);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_1", issueNumber: 1);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicCancelledReconcileHandler(querier, grains, NullLogger<EpicCancelledReconcileHandler>.Instance);

        var evt = BuildCancelledEvent(projectId: "project_1", issueId: "issue_1");
        await handler.HandleAsync(evt, CancellationToken.None);
        await handler.HandleAsync(evt, CancellationToken.None);
        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Single(grains.Calls);
        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("done", stored.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task CancelledHandler_TerminalEpic_StaysTerminalNoError()
    {
        // Terminal epics must absorb the closed event without flipping
        // state or throwing. ReconcileAfterTerminalAsync short-circuits
        // on done/closed.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "done");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Cancelled);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_1", issueNumber: 1);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicCancelledReconcileHandler(querier, grains, NullLogger<EpicCancelledReconcileHandler>.Instance);

        var evt = BuildCancelledEvent(projectId: "project_1", issueId: "issue_1");
        await handler.HandleAsync(evt, CancellationToken.None);

        // Retained terminal memberships are historical only; without a
        // non-terminal owner there is no active epic to reconcile.
        Assert.Empty(grains.Calls);
        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("done", stored.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task CancelledHandler_MissingProjectIdExtension_NoOpsWithoutError()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicCancelledReconcileHandler(querier, grains, NullLogger<EpicCancelledReconcileHandler>.Instance);

        var evt = new CloudEvent<IssueCancelled>(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/issue/issue_1", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCancelled,
            time: DateTimeOffset.UtcNow,
            data: new IssueCancelled("cancel reason"),
            subject: "1",
            extensions: new Dictionary<string, string> { ["issueid"] = "issue_1" });

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(grains.Calls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task CancelledHandler_MissingIssueIdExtension_NoOpsWithoutError()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicCancelledReconcileHandler(querier, grains, NullLogger<EpicCancelledReconcileHandler>.Instance);

        var evt = new CloudEvent<IssueCancelled>(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/issue/issue_1", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCancelled,
            time: DateTimeOffset.UtcNow,
            data: new IssueCancelled("cancel reason"),
            subject: "1",
            extensions: new Dictionary<string, string> { ["projectid"] = "project_1" });

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(grains.Calls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task BothHandlers_FireOnOutOfOrderTerminalSignals_Converge()
    {
        // Out-of-order terminal signals (e.g. completed arrives
        // AFTER cancelled because the bus reordered them) must still end
        // at the correct epic state. Both handlers call the same
        // idempotent reconcile method; the grain absorbs the
        // reordering without double-transition or stuck state.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_2", issueNumber: 2, status: Mohist.Server.Issue.Domain.IssueStatus.Cancelled);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_1", issueNumber: 1);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_2", issueNumber: 2);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var completed = new EpicAutoDoneHandler(querier, grains, NullLogger<EpicAutoDoneHandler>.Instance);
        var cancelled = new EpicCancelledReconcileHandler(querier, grains, NullLogger<EpicCancelledReconcileHandler>.Instance);

        // Cancelled first, then completed (out of order).
        await cancelled.HandleAsync(BuildCancelledEvent("project_1", "issue_2"), CancellationToken.None);
        await completed.HandleAsync(BuildCompletedEvent("project_1", "issue_1"), CancellationToken.None);

        // The first flow reaches the grain and releases the active
        // membership; the reordered duplicate terminal signal then has
        // no active owner to dispatch to.
        Assert.Single(grains.Calls);
    }

    private static CloudEvent<IssueCompleted> BuildCompletedEvent(string projectId, string issueId) =>
        new(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/issue/{issueId}", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCompleted,
            time: DateTimeOffset.UtcNow,
            data: new IssueCompleted("wr_1"),
            subject: "1",
            extensions: new Dictionary<string, string>
            {
                ["projectid"] = projectId,
                ["issueid"] = issueId,
                ["issueno"] = "1",
            });

    private static CloudEvent<IssueCancelled> BuildCancelledEvent(string projectId, string issueId) =>
        new(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/issue/{issueId}", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCancelled,
            time: DateTimeOffset.UtcNow,
            data: new IssueCancelled("cancel reason"),
            subject: "1",
            extensions: new Dictionary<string, string>
            {
                ["projectid"] = projectId,
                ["issueid"] = issueId,
                ["issueno"] = "1",
            });

    private static async Task SeedEpicAsync(
        TestDatabase database,
        string projectId = "project_1",
        string epicId = "epic_1",
        int number = 1,
        string status = "idle",
        string? pauseReason = null)
    {
        await using var db = database.CreateDbContext();
        db.Epics.Add(new EpicRow
        {
            Id = epicId,
            ProjectId = projectId,
            Number = number,
            Title = $"Epic {epicId}",
            Description = "",
            Priority = "p2",
            Status = status,
            PauseReason = pauseReason,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedIssueAsync(
        TestDatabase database,
        string projectId,
        string issueId,
        int issueNumber,
        Mohist.Server.Issue.Domain.IssueStatus status)
    {
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = issueId,
            ProjectId = projectId,
            Number = issueNumber,
            Title = $"Issue {issueNumber}",
            Status = status,
        };
        var json = IssueStore.Serialize(issue);
        await using var db = database.CreateDbContext();
        db.Issues.Add(new IssueRow
        {
            IssueId = issueId,
            ProjectId = projectId,
            Number = issueNumber,
            State = json,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedLinkAsync(TestDatabase database, string epicId, string issueId, int issueNumber)
    {
        await using var db = database.CreateDbContext();
        db.EpicIssues.Add(new EpicIssueRow
        {
            EpicId = epicId,
            ProjectId = "project_1",
            IssueId = issueId,
            IssueNumber = issueNumber,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var epic = await db.Epics.AsNoTracking().FirstAsync(e => e.ProjectId == "project_1" && e.Id == epicId);
        if (epic.Status is not ("done" or "closed"))
        {
            db.EpicActiveIssues.Add(new EpicActiveIssueRow
            {
                ProjectId = "project_1",
                IssueId = issueId,
                EpicId = epicId,
                IssueNumber = issueNumber,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();
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
            GrainTestConfig.MigrateWithSchemaFix(db);
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
        public TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        {
            Options = options;
        }

        public DbContextOptions<MohistDbContext> Options { get; }

        public MohistDbContext CreateDbContext() => new(Options);
    }

    private sealed class TestEpicGrainFactory : IGrainFactory
    {
        private readonly IDbContextFactory<MohistDbContext> _dbFactory;
        public List<RecordedGrainCall> Calls { get; } = [];

        public TestEpicGrainFactory(IDbContextFactory<MohistDbContext> dbFactory)
        {
            _dbFactory = dbFactory;
        }

        public IEpicGrain GetEpicGrain(string grainKey)
        {
            Calls.Add(new RecordedGrainCall(grainKey));
            return new EpicGrain(
                _dbFactory,
                this,
                new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero)),
                NullLogger<EpicGrain>.Instance) { GrainKeyForTest = grainKey };
        }

        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            if (typeof(TGrainInterface) == typeof(IEpicGrain))
                return (TGrainInterface)(object)GetEpicGrain(primaryKey);
            throw new NotSupportedException(typeof(TGrainInterface).FullName);
        }

        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey => throw new NotSupportedException();
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey)
        {
            if (grainInterfaceType == typeof(IEpicGrain))
                return GetEpicGrain(grainPrimaryKey);
            throw new NotSupportedException(grainInterfaceType.FullName);
        }
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId) where TGrainInterface : IAddressable => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId) => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw new NotSupportedException();
        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey, string? grainClassNamePrefix = null) => throw new NotSupportedException();
        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey) => throw new NotSupportedException();
    }

    public sealed record RecordedGrainCall(string GrainKey);
}
