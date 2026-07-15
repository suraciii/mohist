using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Epic.Domain.Events;
using Mohist.Server.Epic.Services;
using Mohist.Server.Events.Grains;
using Mohist.Server.Events.Subscriptions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.SpecTests.Support;
using Orleans;
using System.Text.Json;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

public class EpicAutoDoneHandlerSpecs
{
    private static readonly DateTimeOffset EventTime = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
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

        // The first recompute marks the epic terminal and releases active
        // ownership; repeated terminal events then find no active owner.
        Assert.Single(grains.Calls);
        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("done", stored.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task HandleAsync_DispatchedByBus_AppendsRowButDoesNotInvokeHandler()
    {
        // After issue-361 T-002 the bus is write-only: PublishAsync
        // delegates to IEventStore.AppendAsync and never invokes the
        // registered handlers. Driving the handler directly (as the
        // future dispatcher will) is what produces the state change.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_1", issueNumber: 1);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicAutoDoneHandler(querier, grains, NullLogger<EpicAutoDoneHandler>.Instance);

        var store = new RecordingEventStore();
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
        var bus = new InMemoryEventBus(subscriptions, store, new FakeTimeProvider(EventTime), NullLogger<InMemoryEventBus>.Instance);

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

        // Bus no longer fans out — no grain call, epic stays idle.
        Assert.Empty(grains.Calls);
        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("idle", stored.Status);

        // The row was persisted for the future dispatcher to pick up.
        var row = Assert.Single(store.Appended);
        Assert.Equal(EventCatalog.ReverseDns.IssueCompleted, row.Envelope.Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task DispatchAsync_CompletedEventSelectedIssueStartFailure_RetriesThenDeadLetters()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_completed", issueNumber: 1, status: IssueStatus.Done);
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_next", issueNumber: 2, status: IssueStatus.Backlog);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_completed", issueNumber: 1);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_next", issueNumber: 2);

        var grains = new TestEpicGrainFactory(database.Factory) { ThrowOnIssueStart = true };
        var handler = new EpicAutoDoneHandler(
            new EpicQuerier(database.Factory, null!),
            grains,
            NullLogger<EpicAutoDoneHandler>.Instance);
        var events = new CapturingEventStore();
        var deadLetters = new CapturingDeadLetterStore(events);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var dispatcher = new EventDispatcherService(
            events,
            [new Subscription(
                EventCatalog.ReverseDns.IssueCompleted,
                handler,
                (rawHandler, rawEvent, ct) =>
                {
                    var typedHandler = (ICloudEventHandler<IssueCompleted>)rawHandler;
                    var typedEvent = new CloudEvent<IssueCompleted>(
                        rawEvent.Id,
                        rawEvent.Source,
                        rawEvent.Type,
                        rawEvent.Time,
                        rawEvent.Data!.Value.Deserialize<IssueCompleted>(CloudEvent.JsonOptions)!,
                        rawEvent.DataContentType,
                        rawEvent.Subject,
                        rawEvent.SpecVersion,
                        rawEvent.Extensions);
                    return typedHandler.HandleAsync(typedEvent, ct);
                })],
            deadLetters,
            time,
            Options.Create(new EventDispatcherOptions
            {
                BatchSize = 10,
                MaxAttempts = 2,
                BaseBackoff = TimeSpan.FromSeconds(1),
                MaxBackoff = TimeSpan.FromSeconds(1),
            }),
            NullLogger<EventDispatcherService>.Instance);

        await events.AppendAsync(new CloudEvent(
            id: "evt_terminal_start_failure",
            source: new Uri("/mohist/issues/issue_completed", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCompleted,
            time: time.GetUtcNow(),
            data: JsonSerializer.SerializeToElement(new IssueCompleted("wr_1"), CloudEvent.JsonOptions),
            subject: "1",
            extensions: new Dictionary<string, string>
            {
                ["projectid"] = "project_1",
                ["issueid"] = "issue_completed",
            }));

        await dispatcher.DispatchAsync(CancellationToken.None);

        Assert.Equal(["issue_next"], grains.IssueStartCalls);
        Assert.Equal(1, events.PendingCount);
        Assert.Empty(deadLetters.Written);

        time.Advance(TimeSpan.FromSeconds(1));
        await dispatcher.DispatchAsync(CancellationToken.None);

        Assert.Equal(["issue_next", "issue_next"], grains.IssueStartCalls);
        var deadLetter = Assert.Single(deadLetters.Written);
        Assert.Equal("evt_terminal_start_failure", deadLetter.EventId);
        Assert.Equal(2, deadLetter.AttemptCount);
        Assert.Contains(nameof(EpicAutoDoneHandler), deadLetter.FailingHandler);
        Assert.Contains("selected issue start failure", deadLetter.ErrorMessage);
        Assert.Equal(0, events.PendingCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
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
            time: TestTime.UtcNow,
            data: new IssueCompleted("wr_1"),
            subject: "1",
            extensions: new Dictionary<string, string> { ["issueid"] = "issue_1" });

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(grains.Calls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task HandleAsync_HasSubscriptionAttributeWithExpectedType()
    {
        var attr = (SubscriptionAttribute?)Attribute.GetCustomAttribute(
            typeof(EpicAutoDoneHandler), typeof(SubscriptionAttribute));
        Assert.NotNull(attr);
        Assert.Equal(EventCatalog.ReverseDns.IssueCompleted, attr!.Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task CancelledHandler_HasSubscriptionAttributeOnCancelledType()
    {
        // The IssueCancelled subscription is required: a cancelled in-progress
        // issue must trigger recompute progress so the next startable issue is
        // advanced (otherwise the epic deadlocks on a cancelled slot).
        var attr = (SubscriptionAttribute?)Attribute.GetCustomAttribute(
            typeof(EpicCancelledHandler), typeof(SubscriptionAttribute));
        Assert.NotNull(attr);
        Assert.Equal(EventCatalog.ReverseDns.IssueCancelled, attr!.Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
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
        Assert.Equal("project_1:epic_1", call.GrainKey);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
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
        var handler = new EpicCancelledHandler(querier, grains, NullLogger<EpicCancelledHandler>.Instance);

        await handler.HandleAsync(BuildCancelledEvent(projectId: "project_1", issueId: "issue_1"), CancellationToken.None);

        var call = Assert.Single(grains.Calls);
        Assert.Equal("project_1:epic_running", call.GrainKey);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
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
            extensions: new Dictionary<string, string> { ["issueid"] = "issue_1" });

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(grains.Calls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task CancelledHandler_MissingIssueIdExtension_NoOpsWithoutError()
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

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
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

    // --- Fix B: EpicIssueLinkedHandler (durable convergence for link) ---

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task IssueLinkedHandler_HasSubscriptionAttributeOnLinkedType()
    {
        var attr = (SubscriptionAttribute?)Attribute.GetCustomAttribute(
            typeof(EpicIssueLinkedHandler), typeof(SubscriptionAttribute));
        Assert.NotNull(attr);
        Assert.Equal(EventCatalog.ReverseDns.EpicIssueLinked, attr!.Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task IssueLinkedHandler_LinkedEvent_InvokesRecomputeOnOwningEpic()
    {
        // Epic events carry projectid + epicid on the envelope (stamped by
        // PersistEpicEventsAsync), so no reverse lookup is needed.
        var database = CreateDatabase();
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicIssueLinkedHandler(grains, database.Factory, NullLogger<EpicIssueLinkedHandler>.Instance);

        var evt = BuildEpicIssueLinkedEvent(projectId: "project_1", epicId: "epic_1", issueId: "issue_1", issueNumber: 1);
        await handler.HandleAsync(evt, CancellationToken.None);

        var call = Assert.Single(grains.Calls);
        Assert.Equal("project_1:epic_1", call.GrainKey);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task IssueLinkedHandler_MissingProjectIdExtension_NoOps()
    {
        var database = CreateDatabase();
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicIssueLinkedHandler(grains, database.Factory, NullLogger<EpicIssueLinkedHandler>.Instance);

        var evt = new CloudEvent<EpicIssueLinked>(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/epic/epic_1", UriKind.Relative),
            type: EventCatalog.ReverseDns.EpicIssueLinked,
            time: TestTime.UtcNow,
            data: new EpicIssueLinked("issue_1", 1),
            subject: "1",
            extensions: new Dictionary<string, string> { ["epicid"] = "epic_1" });

        await handler.HandleAsync(evt, CancellationToken.None);
        Assert.Empty(grains.Calls);
    }

    // --- Fix C-1: EpicDraftChangedHandler (undraft triggers recompute) ---

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task DraftChangedHandler_HasSubscriptionAttributeOnDraftChangedType()
    {
        var attr = (SubscriptionAttribute?)Attribute.GetCustomAttribute(
            typeof(EpicDraftChangedHandler), typeof(SubscriptionAttribute));
        Assert.NotNull(attr);
        Assert.Equal(EventCatalog.ReverseDns.IssueDraftChanged, attr!.Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
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
        Assert.Equal("project_1:epic_1", call.GrainKey);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task PrerequisiteRemovedHandler_HasSubscriptionAttribute()
    {
        var attr = (SubscriptionAttribute?)Attribute.GetCustomAttribute(
            typeof(EpicPrerequisiteRemovedHandler), typeof(SubscriptionAttribute));
        Assert.NotNull(attr);
        Assert.Equal(EventCatalog.ReverseDns.IssuePrerequisiteRemoved, attr!.Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
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
        Assert.Equal("project_1:epic_1", call.GrainKey);
    }

    // --- Fix: EpicIssueUnlinkedHandler + EpicIssueReopenedHandler ---

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task IssueUnlinkedHandler_HasSubscriptionAttribute()
    {
        var attr = (SubscriptionAttribute?)Attribute.GetCustomAttribute(
            typeof(EpicIssueUnlinkedHandler), typeof(SubscriptionAttribute));
        Assert.NotNull(attr);
        Assert.Equal(EventCatalog.ReverseDns.EpicIssueUnlinked, attr!.Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task IssueUnlinkedHandler_UnlinkedEvent_InvokesRecomputeOnOwningEpic()
    {
        var database = CreateDatabase();
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicIssueUnlinkedHandler(grains, database.Factory, NullLogger<EpicIssueUnlinkedHandler>.Instance);

        var evt = BuildEpicIssueUnlinkedEvent(projectId: "project_1", epicId: "epic_1", issueId: "issue_1", issueNumber: 1);
        await handler.HandleAsync(evt, CancellationToken.None);

        var call = Assert.Single(grains.Calls);
        Assert.Equal("project_1:epic_1", call.GrainKey);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task IssueReopenedHandler_HasSubscriptionAttribute()
    {
        var attr = (SubscriptionAttribute?)Attribute.GetCustomAttribute(
            typeof(EpicIssueReopenedHandler), typeof(SubscriptionAttribute));
        Assert.NotNull(attr);
        Assert.Equal(EventCatalog.ReverseDns.IssueReopened, attr!.Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
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
                ["issueid"] = "issue_1",
                ["issueno"] = "1",
            });
        await handler.HandleAsync(evt, CancellationToken.None);

        var call = Assert.Single(grains.Calls);
        Assert.Equal("project_1:epic_1", call.GrainKey);
    }

    // --- Fix C-2: External prerequisite reverse lookup ---

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task HandleAsync_ExternalPrerequisiteCompletes_DispatchesToDependentEpic()
    {
        // An external prerequisite (issue 10) is NOT a member of the epic,
        // but issue 2 (a member) depends on it. When issue 10 completes,
        // the handler must reverse-look-up the dependent epic and recompute.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        // Member issue 2 depends on external prerequisite 10
        await SeedIssueWithPrereqsAsync(database, projectId: "project_1", issueId: "issue_2", issueNumber: 2, prereqNumbers: [10]);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_2", issueNumber: 2);
        // External prerequisite issue 10 — not linked to the epic
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_10", issueNumber: 10, status: Mohist.Server.Issue.Domain.IssueStatus.Done);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicAutoDoneHandler(querier, grains, NullLogger<EpicAutoDoneHandler>.Instance);

        // issue_10 completes — it has no direct membership, but the
        // prerequisite reverse lookup should find epic_1 via issue_2.
        var evt = BuildCompletedEvent(projectId: "project_1", issueId: "issue_10");
        evt = new CloudEvent<IssueCompleted>(
            id: evt.Id,
            source: evt.Source,
            type: evt.Type,
            time: evt.Time,
            data: evt.Data,
            subject: "10",
            extensions: new Dictionary<string, string>
            {
                ["projectid"] = "project_1",
                ["issueid"] = "issue_10",
                ["issueno"] = "10",
            });
        await handler.HandleAsync(evt, CancellationToken.None);

        var call = Assert.Single(grains.Calls);
        Assert.Equal("project_1:epic_1", call.GrainKey);
    }

    // --- T-007: issueno -> issue rename; dual-key read for historical rows ---

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task HandleAsync_ExternalPrerequisiteCompletes_DispatchesToDependentEpic_ViaUnifiedIssueKey()
    {
        // Post-change row stamped with the unified `issue` key. The
        // dispatcher's prerequisite reverse lookup must read `issue`
        // and dispatch the dependent epic — no more primary read of
        // `issueno`.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueWithPrereqsAsync(database, projectId: "project_1", issueId: "issue_2", issueNumber: 2, prereqNumbers: [10]);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_2", issueNumber: 2);
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_10", issueNumber: 10, status: Mohist.Server.Issue.Domain.IssueStatus.Done);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicAutoDoneHandler(querier, grains, NullLogger<EpicAutoDoneHandler>.Instance);

        var evt = new CloudEvent<IssueCompleted>(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/issue/issue_10", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCompleted,
            time: EventTime,
            data: new IssueCompleted("wr_1"),
            subject: "10",
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.ProjectId] = "project_1",
                [EventCatalog.Lineage.IssueId] = "issue_10",
                [EventCatalog.Lineage.Issue] = "10",
            });

        await handler.HandleAsync(evt, CancellationToken.None);

        var call = Assert.Single(grains.Calls);
        Assert.Equal("project_1:epic_1", call.GrainKey);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task HandleAsync_ExternalPrerequisiteCompletes_DispatchesToDependentEpic_ViaLegacyIssuenoFallback()
    {
        // Pre-change historical row stamped with the legacy `issueno`
        // key only. The Non-Goal forbids backfill, so the dual-key read
        // must still resolve and dispatch the dependent epic.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueWithPrereqsAsync(database, projectId: "project_1", issueId: "issue_2", issueNumber: 2, prereqNumbers: [10]);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_2", issueNumber: 2);
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_10", issueNumber: 10, status: Mohist.Server.Issue.Domain.IssueStatus.Done);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicAutoDoneHandler(querier, grains, NullLogger<EpicAutoDoneHandler>.Instance);

        var evt = new CloudEvent<IssueCompleted>(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/issue/issue_10", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCompleted,
            time: EventTime,
            data: new IssueCompleted("wr_1"),
            subject: "10",
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.ProjectId] = "project_1",
                [EventCatalog.Lineage.IssueId] = "issue_10",
                ["issueno"] = "10",
            });

        await handler.HandleAsync(evt, CancellationToken.None);

        var call = Assert.Single(grains.Calls);
        Assert.Equal("project_1:epic_1", call.GrainKey);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task HandleAsync_BothKeysPresent_DispatchesViaUnifiedIssueKey()
    {
        // When both keys are stamped, the unified `issue` value wins
        // (matching the unified-key contract).
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueWithPrereqsAsync(database, projectId: "project_1", issueId: "issue_2", issueNumber: 2, prereqNumbers: [10]);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_2", issueNumber: 2);
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_10", issueNumber: 10, status: Mohist.Server.Issue.Domain.IssueStatus.Done);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicAutoDoneHandler(querier, grains, NullLogger<EpicAutoDoneHandler>.Instance);

        var evt = new CloudEvent<IssueCompleted>(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/issue/issue_10", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCompleted,
            time: EventTime,
            data: new IssueCompleted("wr_1"),
            subject: "10",
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.ProjectId] = "project_1",
                [EventCatalog.Lineage.IssueId] = "issue_10",
                [EventCatalog.Lineage.Issue] = "10",
                ["issueno"] = "999",
            });

        await handler.HandleAsync(evt, CancellationToken.None);

        // Unified key wins -> real prereq 10 -> dependent epic dispatched.
        var call = Assert.Single(grains.Calls);
        Assert.Equal("project_1:epic_1", call.GrainKey);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task HandleAsync_NeitherIssueNorIssueno_SkipsPrerequisiteLookupAndStillDispatchesOwningEpic()
    {
        // No issue number on the envelope: the owning-epic lookup still
        // dispatches via the direct membership path; the prerequisite
        // reverse lookup is simply skipped (its input is null).
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_1", issueNumber: 1);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicAutoDoneHandler(querier, grains, NullLogger<EpicAutoDoneHandler>.Instance);

        var evt = new CloudEvent<IssueCompleted>(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/issue/issue_1", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCompleted,
            time: EventTime,
            data: new IssueCompleted("wr_1"),
            subject: "1",
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.ProjectId] = "project_1",
                [EventCatalog.Lineage.IssueId] = "issue_1",
            });

        await handler.HandleAsync(evt, CancellationToken.None);

        var call = Assert.Single(grains.Calls);
        Assert.Equal("project_1:epic_1", call.GrainKey);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task CancelledHandler_ExternalCancelledPrerequisite_DoesNotDispatchDependentEpic()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueWithPrereqsAsync(database, projectId: "project_1", issueId: "issue_2", issueNumber: 2, prereqNumbers: [10]);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_2", issueNumber: 2);
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_10", issueNumber: 10, status: Mohist.Server.Issue.Domain.IssueStatus.Cancelled);

        var querier = new EpicQuerier(database.Factory, null!);
        var grains = new TestEpicGrainFactory(database.Factory);
        var handler = new EpicCancelledHandler(querier, grains, NullLogger<EpicCancelledHandler>.Instance);
        var evt = new CloudEvent<IssueCancelled>(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/issues/issue_10", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCancelled,
            time: EventTime,
            data: new IssueCancelled("cancelled"),
            subject: "10",
            extensions: new Dictionary<string, string>
            {
                ["projectid"] = "project_1",
                ["issueid"] = "issue_10",
                ["issueno"] = "10",
            });

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(grains.Calls);
    }

    // --- Fix D: EpicStartRetryHandler (start-attempt-failed triggers recompute) ---

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task StartRetryHandler_HasSubscriptionAttributeOnStartAttemptFailedType()
    {
        var attr = (SubscriptionAttribute?)Attribute.GetCustomAttribute(
            typeof(EpicStartRetryHandler), typeof(SubscriptionAttribute));
        Assert.NotNull(attr);
        Assert.Equal(EventCatalog.ReverseDns.EpicStartAttemptFailed, attr!.Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task StartRetryHandler_StartAttemptFailedEvent_InvokesRecomputeOnOwningEpic()
    {
        var grains = new TestEpicGrainFactory(CreateDatabase().Factory);
        var handler = new EpicStartRetryHandler(grains, NullLogger<EpicStartRetryHandler>.Instance);

        var evt = BuildStartAttemptFailedEvent(projectId: "project_1", epicId: "epic_1", issueId: "issue_1", issueNumber: 1);
        await handler.HandleAsync(evt, CancellationToken.None);

        var call = Assert.Single(grains.Calls);
        Assert.Equal("project_1:epic_1", call.GrainKey);
    }

    private static CloudEvent<EpicIssueLinked> BuildEpicIssueLinkedEvent(
        string projectId, string epicId, string issueId, int issueNumber) =>
        new(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/epic/{epicId}", UriKind.Relative),
            type: EventCatalog.ReverseDns.EpicIssueLinked,
            time: EventTime,
            data: new EpicIssueLinked(issueId, issueNumber),
            subject: epicId,
            extensions: new Dictionary<string, string>
            {
                ["projectid"] = projectId,
                ["epicid"] = epicId,
                ["epicno"] = "1",
            });

    private static CloudEvent<EpicIssueUnlinked> BuildEpicIssueUnlinkedEvent(
        string projectId, string epicId, string issueId, int issueNumber) =>
        new(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/epic/{epicId}", UriKind.Relative),
            type: EventCatalog.ReverseDns.EpicIssueUnlinked,
            time: EventTime,
            data: new EpicIssueUnlinked(issueId, issueNumber),
            subject: epicId,
            extensions: new Dictionary<string, string>
            {
                ["projectid"] = projectId,
                ["epicid"] = epicId,
                ["epicno"] = "1",
            });

    private static CloudEvent<IssueDraftChanged> BuildDraftChangedEvent(
        string projectId, string issueId, bool oldIsDraft, bool newIsDraft) =>
        new(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/issue/{issueId}", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueDraftChanged,
            time: EventTime,
            data: new IssueDraftChanged(oldIsDraft, newIsDraft),
            subject: "1",
            extensions: new Dictionary<string, string>
            {
                ["projectid"] = projectId,
                ["issueid"] = issueId,
                ["issueno"] = "1",
            });

    private static CloudEvent<IssuePrerequisiteRemoved> BuildPrerequisiteRemovedEvent(
        string projectId, string issueId, int prereqNumber) =>
        new(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/issue/{issueId}", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssuePrerequisiteRemoved,
            time: EventTime,
            data: new IssuePrerequisiteRemoved(prereqNumber),
            subject: "1",
            extensions: new Dictionary<string, string>
            {
                ["projectid"] = projectId,
                ["issueid"] = issueId,
                ["issueno"] = "1",
            });

    private static CloudEvent<EpicStartAttemptFailed> BuildStartAttemptFailedEvent(
        string projectId, string epicId, string issueId, int issueNumber) =>
        new(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/epic/{epicId}", UriKind.Relative),
            type: EventCatalog.ReverseDns.EpicStartAttemptFailed,
            time: EventTime,
            data: new EpicStartAttemptFailed(issueId, issueNumber, "transient failure"),
            subject: epicId,
            extensions: new Dictionary<string, string>
            {
                ["projectid"] = projectId,
                ["epicid"] = epicId,
                ["epicno"] = "1",
            });

    private static async Task SeedIssueWithPrereqsAsync(
        TestDatabase database,
        string projectId,
        string issueId,
        int issueNumber,
        int[] prereqNumbers)
    {
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = issueId,
            ProjectId = projectId,
            Number = issueNumber,
            Title = $"Issue {issueNumber}",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
        };
        foreach (var prereq in prereqNumbers)
            issue.AddPrerequisite(prereq);
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

    private static CloudEvent<IssueCompleted> BuildCompletedEvent(string projectId, string issueId) =>
        new(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/issue/{issueId}", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCompleted,
            time: EventTime,
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
            time: EventTime,
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
            CreatedAt = TestTime.UtcNow,
            UpdatedAt = TestTime.UtcNow,
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
            CreatedAt = TestTime.UtcNow,
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
                CreatedAt = TestTime.UtcNow,
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
        MigratedSqliteTemplate.CopyTo(connection);
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
        public List<string> IssueStartCalls { get; } = [];
        public bool ThrowOnIssueStart { get; init; }

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
                new NoopEventStore(),
                NullLogger<EpicGrain>.Instance) { GrainKeyForTest = grainKey };
        }

        private IIssueGrain GetIssueGrain(string issueId) => new TestIssueGrain(this, issueId);

        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            if (typeof(TGrainInterface) == typeof(IEpicGrain))
                return (TGrainInterface)(object)GetEpicGrain(primaryKey);
            if (typeof(TGrainInterface) == typeof(IIssueGrain))
                return (TGrainInterface)(object)GetIssueGrain(primaryKey);
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
            if (grainInterfaceType == typeof(IIssueGrain))
                return GetIssueGrain(grainPrimaryKey);
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

    private sealed class TestIssueGrain : IIssueGrain
    {
        private readonly TestEpicGrainFactory _owner;
        private readonly string _issueId;

        public TestIssueGrain(TestEpicGrainFactory owner, string issueId)
        {
            _owner = owner;
            _issueId = issueId;
        }

        public Task<string> CreateAsync(string projectId, int number, string title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, string? repositoryRef = null, string? issueId = null, string? risk = null, bool isDraft = false, string[]? attachmentIds = null, string? workflowProfileId = null, int[]? prerequisiteNumbers = null) => throw new NotSupportedException();

        public Task<string> StartWorkAsync(WorkflowProjectContext? project = null)
        {
            _owner.IssueStartCalls.Add(_issueId);
            return _owner.ThrowOnIssueStart
                ? Task.FromException<string>(new InvalidOperationException("selected issue start failure"))
                : Task.FromResult("wr_test");
        }

        public Task EnsureWorkflowBindingAsync(string workflowRunId) => throw new NotSupportedException();

        public Task CompleteWorkAsync(string workflowRunId) => throw new NotSupportedException();
        public Task CancelAsync() => throw new NotSupportedException();
        public Task UpdateAsync(string title, string? body) => throw new NotSupportedException();
        public Task UpdateFullAsync(UpdateIssueData data) => throw new NotSupportedException();
        public Task ArchiveAsync() => throw new NotSupportedException();
        public Task UnarchiveAsync() => throw new NotSupportedException();
        public Task ReopenAsync() => throw new NotSupportedException();
        public Task<IssueWorkflowStatus?> GetWorkflowStatusAsync() => throw new NotSupportedException();
        public Task<IssuePrerequisiteResult> AddPrerequisiteAsync(int prerequisiteNumber) => throw new NotSupportedException();
        public Task RemovePrerequisiteAsync(int prerequisiteNumber) => throw new NotSupportedException();
        public Task<IssueStartReadiness> GetStartReadinessAsync() => throw new NotSupportedException();
        public Task<IssueCommentResult> AddCommentAsync(string body, string[]? attachmentIds = null) => throw new NotSupportedException();
        public Task DeactivateForTestAsync() => throw new NotSupportedException();
        public Task SetEpicAffiliationAsync(string? epicId) => Task.CompletedTask;
    }

    public sealed record RecordedGrainCall(string GrainKey);
}
