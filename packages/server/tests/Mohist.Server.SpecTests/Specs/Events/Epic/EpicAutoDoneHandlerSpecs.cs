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
using Mohist.Server.SpecTests.Support;
using Orleans;
using System.Text.Json;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

public class EpicAutoDoneHandlerSpecs : EpicAutoDoneHandlerTestSupport
{

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
        Assert.Equal("project_1:1", grains.Calls[0].GrainKey);
    }

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
        Assert.Equal("project_1:2", call.GrainKey);
    }

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
            [EventCatalog.Lineage.Issue] = "1",
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
                [EventCatalog.Lineage.Issue] = "1",
            }));

        await dispatcher.DispatchAsync(CancellationToken.None);

        Assert.Equal(["project_1:2"], grains.IssueStartCalls);
        Assert.Equal(1, events.PendingCount);
        Assert.Empty(deadLetters.Written);

        time.Advance(TimeSpan.FromSeconds(1));
        await dispatcher.DispatchAsync(CancellationToken.None);

        Assert.Equal(["project_1:2", "project_1:2"], grains.IssueStartCalls);
        var deadLetter = Assert.Single(deadLetters.Written);
        Assert.Equal("evt_terminal_start_failure", deadLetter.EventId);
        Assert.Equal(2, deadLetter.AttemptCount);
        Assert.Contains(nameof(EpicAutoDoneHandler), deadLetter.FailingHandler);
        Assert.Contains("selected issue start failure", deadLetter.ErrorMessage);
        Assert.Equal(0, events.PendingCount);
    }

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
            extensions: new Dictionary<string, string> { [EventCatalog.Lineage.Issue] = "1" });

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(grains.Calls);
    }

    [Fact]
    public async Task HandleAsync_HasSubscriptionAttributeWithExpectedType()
    {
        var attr = (SubscriptionAttribute?)Attribute.GetCustomAttribute(
            typeof(EpicAutoDoneHandler), typeof(SubscriptionAttribute));
        Assert.NotNull(attr);
        Assert.Equal(EventCatalog.ReverseDns.IssueCompleted, attr!.Type);
    }

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
}
