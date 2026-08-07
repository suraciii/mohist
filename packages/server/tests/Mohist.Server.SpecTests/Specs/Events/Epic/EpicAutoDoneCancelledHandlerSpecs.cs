using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Epic.Domain.Events;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Epic.Services;
using Mohist.Server.Epic.Subscriptions;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.TestSupport;
using Orleans;
using System.Text.Json;
using Xunit;
namespace Mohist.Server.SpecTests.Specs.Events;

public class EpicAutoDoneCancelledHandlerSpecs : EpicAutoDoneHandlerTestSupport
{

    [Fact]
    public async Task CancelledHandler_CancelledIssue_InvokesRecomputeOnOwningEpic()
    {
        // Both terminal events funnel through the same grain method;
        // this verifies the new subscription delivers the same call.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Cancelled);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_1", issueNumber: 1);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicCancelledHandler(querier, grains, NullLogger<EpicCancelledHandler>.Instance);

        var evt = BuildCancelledEvent(projectId: "project_1", issueId: "issue_1");
        await handler.HandleAsync(evt, CancellationToken.None);

        // The grain call itself is the wiring contract — RecomputeProgressAsync
        // advances the next startable issue via the EpicGrain (covered by
        // EpicProgressionSpecs.RecomputeProgressAsync_RunningEpicOnCancelledInProgressIssue_AdvancesNext).
        var call = Assert.Single(grains.Calls);
        Assert.Equal("project_1:1", call.GrainKey);
    }

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
        var handler = new EpicCancelledHandler(querier, grains, NullLogger<EpicCancelledHandler>.Instance);

        await handler.HandleAsync(BuildCancelledEvent(projectId: "project_1", issueId: "issue_1"), CancellationToken.None);

        var call = Assert.Single(grains.Calls);
        Assert.Equal("project_1:2", call.GrainKey);
    }

    [Fact]
    public async Task CancelledHandler_IssueNotLinkedToAnyEpic_NoOpsWithoutGrainCall()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicCancelledHandler(querier, grains, NullLogger<EpicCancelledHandler>.Instance);

        var evt = BuildCancelledEvent(projectId: "project_1", issueId: "issue_unlinked");
        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(grains.Calls);
    }

    [Fact]
    public async Task CancelledHandler_DuplicateCancelledEvents_AreIdempotent()
    {
        // Duplicate terminal signals must converge to the same state
        // without erroring. After the terminal/open readiness change,
        // a running epic with only a cancelled linked issue has no open
        // linked issue and auto-marks done on the first recompute;
        // subsequent duplicate events see a terminal epic and no-op.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Cancelled);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_1", issueNumber: 1);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicCancelledHandler(querier, grains, NullLogger<EpicCancelledHandler>.Instance);

        var evt = BuildCancelledEvent(projectId: "project_1", issueId: "issue_1");
        await handler.HandleAsync(evt, CancellationToken.None);
        await handler.HandleAsync(evt, CancellationToken.None);
        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Single(grains.Calls);
        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("done", stored.Status);
    }

    [Fact]
    public async Task CancelledHandler_TerminalEpic_StaysTerminalNoError()
    {
        // Terminal epics must absorb the closed event without flipping
        // state or throwing. RecomputeProgressAsync short-circuits
        // on done/closed.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "done");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Cancelled);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_1", issueNumber: 1);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicCancelledHandler(querier, grains, NullLogger<EpicCancelledHandler>.Instance);

        var evt = BuildCancelledEvent(projectId: "project_1", issueId: "issue_1");
        await handler.HandleAsync(evt, CancellationToken.None);

        // Retained terminal memberships are historical only; without a
        // non-terminal owner there is no active epic to recompute.
        Assert.Empty(grains.Calls);
        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("done", stored.Status);
    }

    [Fact]
    public async Task CancelledHandler_MissingProjectIdExtension_NoOpsWithoutError()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicCancelledHandler(querier, grains, NullLogger<EpicCancelledHandler>.Instance);

        var evt = new CloudEvent<IssueCancelled>(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/issue/issue_1", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCancelled,
            time: TestTime.UtcNow,
            data: new IssueCancelled("cancel reason"),
            subject: "1",
            extensions: new Dictionary<string, string> { [EventCatalog.Lineage.Issue] = "1" });

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(grains.Calls);
    }

    [Fact]
    public async Task CancelledHandler_MissingIssueExtension_NoOpsWithoutError()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicCancelledHandler(querier, grains, NullLogger<EpicCancelledHandler>.Instance);

        var evt = new CloudEvent<IssueCancelled>(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/issue/issue_1", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCancelled,
            time: TestTime.UtcNow,
            data: new IssueCancelled("cancel reason"),
            subject: "1",
            extensions: new Dictionary<string, string> { ["projectid"] = "project_1" });

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(grains.Calls);
    }

    [Fact]
    public async Task BothHandlers_FireOnOutOfOrderTerminalSignals_Converge()
    {
        // Out-of-order terminal signals (e.g. completed arrives
        // AFTER cancelled because the bus reordered them) must still end
        // at the correct epic state. Both handlers call the same
        // idempotent recompute-progress method; the grain absorbs the
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
        var cancelled = new EpicCancelledHandler(querier, grains, NullLogger<EpicCancelledHandler>.Instance);

        // Cancelled first, then completed (out of order).
        await cancelled.HandleAsync(BuildCancelledEvent("project_1", "issue_2"), CancellationToken.None);
        await completed.HandleAsync(BuildCompletedEvent("project_1", "issue_1"), CancellationToken.None);

        // The first flow reaches the grain and releases the active
        // membership; the reordered duplicate terminal signal then has
        // no active owner to dispatch to.
        Assert.Single(grains.Calls);
    }

    // --- Fix C-1: EpicDraftChangedHandler (undraft triggers recompute) ---

    [Fact]
    public async Task DraftChangedHandler_HasSubscriptionAttributeOnDraftChangedType()
    {
        var attr = (SubscriptionAttribute?)Attribute.GetCustomAttribute(
            typeof(EpicDraftChangedHandler), typeof(SubscriptionAttribute));
        Assert.NotNull(attr);
        Assert.Equal(EventCatalog.ReverseDns.IssueDraftChanged, attr!.Type);
    }

    [Fact]
    public async Task DraftChangedHandler_Undraft_InvokesRecomputeOnOwningEpic()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Backlog);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_1", issueNumber: 1);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicDraftChangedHandler(querier, grains, NullLogger<EpicDraftChangedHandler>.Instance);

        // OldIsDraft=true, NewIsDraft=false — undraft to ready
        var evt = BuildDraftChangedEvent(projectId: "project_1", issueId: "issue_1", oldIsDraft: true, newIsDraft: false);
        await handler.HandleAsync(evt, CancellationToken.None);

        var call = Assert.Single(grains.Calls);
        Assert.Equal("project_1:1", call.GrainKey);
    }

    [Fact]
    public async Task DraftChangedHandler_Drafting_IgnoresEvent()
    {
        // Drafting a ready issue (NewIsDraft=true) has no epic-progress
        // effect; the handler's Filter rejects it.
        var handler = new EpicDraftChangedHandler(
            new EpicQuerier(CreateDatabase().Factory, null!),
            new TestEpicGrainFactory(CreateDatabase().Factory),
            NullLogger<EpicDraftChangedHandler>.Instance);

        var evt = BuildDraftChangedEvent(projectId: "project_1", issueId: "issue_1", oldIsDraft: false, newIsDraft: true);
        Assert.False(handler.Filter(evt));
    }

    // --- Fix item-4: EpicPrerequisiteRemovedHandler ---

    [Fact]
    public async Task PrerequisiteRemovedHandler_HasSubscriptionAttribute()
    {
        var attr = (SubscriptionAttribute?)Attribute.GetCustomAttribute(
            typeof(EpicPrerequisiteRemovedHandler), typeof(SubscriptionAttribute));
        Assert.NotNull(attr);
        Assert.Equal(EventCatalog.ReverseDns.IssuePrerequisiteRemoved, attr!.Type);
    }

    [Fact]
    public async Task PrerequisiteRemovedHandler_RemovedPrereq_InvokesRecomputeOnOwningEpic()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Backlog);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_1", issueNumber: 1);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicPrerequisiteRemovedHandler(querier, grains, NullLogger<EpicPrerequisiteRemovedHandler>.Instance);

        var evt = BuildPrerequisiteRemovedEvent(projectId: "project_1", issueId: "issue_1", prereqNumber: 10);
        await handler.HandleAsync(evt, CancellationToken.None);

        var call = Assert.Single(grains.Calls);
        Assert.Equal("project_1:1", call.GrainKey);
    }

    [Fact]
    public async Task IssueReopenedHandler_HasSubscriptionAttribute()
    {
        var attr = (SubscriptionAttribute?)Attribute.GetCustomAttribute(
            typeof(EpicIssueReopenedHandler), typeof(SubscriptionAttribute));
        Assert.NotNull(attr);
        Assert.Equal(EventCatalog.ReverseDns.IssueReopened, attr!.Type);
    }

    [Fact]
    public async Task IssueReopenedHandler_ReopenedIssue_InvokesRecomputeOnOwningEpic()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Backlog);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_1", issueNumber: 1);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicIssueReopenedHandler(querier, grains, NullLogger<EpicIssueReopenedHandler>.Instance);

        var evt = new CloudEvent<IssueReopened>(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/issue/issue_1", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueReopened,
            time: EventTime,
            data: new IssueReopened(),
            subject: "1",
            extensions: new Dictionary<string, string>
            {
                ["projectid"] = "project_1",
                [EventCatalog.Lineage.Issue] = "1",
            });
        await handler.HandleAsync(evt, CancellationToken.None);

        var call = Assert.Single(grains.Calls);
        Assert.Equal("project_1:1", call.GrainKey);
    }

    // --- Fix C-2: External prerequisite reverse lookup ---

}
