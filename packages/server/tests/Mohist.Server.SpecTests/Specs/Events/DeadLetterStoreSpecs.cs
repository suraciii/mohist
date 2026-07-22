using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

public class DeadLetterStoreSpecs : IAsyncLifetime
{
    private static readonly DateTimeOffset FirstTime = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SecondTime = new(2026, 7, 1, 12, 5, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ThirdTime = new(2026, 7, 1, 12, 10, 0, TimeSpan.Zero);

    private TestSqliteDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private DeadLetterStore _store = null!;
    private EventStore _events = null!;

    public ValueTask InitializeAsync()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        _factory = new TestDbContextFactory(_database.Options);
        _store = new DeadLetterStore(_factory);
        _events = new EventStore(_factory, NullLogger<EventStore>.Instance);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task WriteAsync_PersistsRow_AndGetAsyncReturnsIt()
    {
        var row = BuildRow(origin: "WorkflowRun", deadLetteredAt: FirstTime);

        await _store.WriteAsync(row);

        var fetched = await _store.GetAsync(row.DeadLetterId);
        Assert.NotNull(fetched);
        AssertRowEqual(row, fetched);
    }

    [Fact]
    public async Task WriteAsync_AssignsMonotonicDeadLetterId()
    {
        var first = BuildRow(origin: "Issue", deadLetteredAt: FirstTime, eventId: "evt_first");
        var second = BuildRow(origin: "Issue", deadLetteredAt: SecondTime, eventId: "evt_second");

        await _store.WriteAsync(first);
        await _store.WriteAsync(second);

        Assert.True(second.DeadLetterId > first.DeadLetterId);
    }

    [Fact]
    public async Task WriteAsync_PersistsAgentSessionOrigin()
    {
        var row = BuildRow(origin: "AgentSession", deadLetteredAt: FirstTime, eventId: "evt_agent");

        await _store.WriteAsync(row);

        var fetched = await _store.GetAsync(row.DeadLetterId);
        Assert.NotNull(fetched);
        Assert.Equal("AgentSession", fetched.Origin);
    }

    [Fact]
    public async Task QueryAsync_NoFilter_ReturnsRowsOrderedByDeadLetteredAt()
    {
        var earliest = BuildRow(origin: "WorkflowRun", deadLetteredAt: FirstTime, eventId: "evt_earliest");
        var middle = BuildRow(origin: "WorkflowRun", deadLetteredAt: SecondTime, eventId: "evt_middle");
        var latest = BuildRow(origin: "WorkflowRun", deadLetteredAt: ThirdTime, eventId: "evt_latest");

        await _store.WriteAsync(latest);
        await _store.WriteAsync(earliest);
        await _store.WriteAsync(middle);

        var listed = await _store.QueryAsync(failingHandler: null, limit: 100);

        Assert.Equal(new[] { earliest.DeadLetterId, middle.DeadLetterId, latest.DeadLetterId },
            listed.Select(r => r.DeadLetterId).ToArray());
    }

    [Fact]
    public async Task QueryAsync_NoFilter_AppliesLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            await _store.WriteAsync(BuildRow(origin: "Issue", deadLetteredAt: FirstTime.AddMinutes(i), eventId: $"evt_{i}"));
        }

        var listed = await _store.QueryAsync(failingHandler: null, limit: 3);

        Assert.Equal(3, listed.Count);
    }

    [Fact]
    public async Task QueryAsync_WithHandlerFilter_NarrowsToMatchingHandler()
    {
        var handlerA = "Mohist.Server.Events.Workflow.CompletedHandler";
        var handlerB = "Mohist.Server.Events.Issue.AnotherHandler";

        await _store.WriteAsync(BuildRow(origin: "WorkflowRun", deadLetteredAt: FirstTime, failingHandler: handlerA, eventId: "evt_a1"));
        await _store.WriteAsync(BuildRow(origin: "Issue", deadLetteredAt: SecondTime, failingHandler: handlerB, eventId: "evt_b1"));
        await _store.WriteAsync(BuildRow(origin: "WorkflowRun", deadLetteredAt: ThirdTime, failingHandler: handlerA, eventId: "evt_a2"));

        var handlerAFiltered = await _store.QueryAsync(failingHandler: handlerA, limit: 100);

        Assert.Equal(2, handlerAFiltered.Count);
        Assert.All(handlerAFiltered, r => Assert.Equal(handlerA, r.FailingHandler));
        Assert.Equal(new[] { "evt_a1", "evt_a2" }, handlerAFiltered.Select(r => r.EventId).ToArray());
    }

    [Fact]
    public async Task QueryAsync_WithHandlerFilter_NoMatches_ReturnsEmpty()
    {
        await _store.WriteAsync(BuildRow(origin: "WorkflowRun", deadLetteredAt: FirstTime, eventId: "evt_present"));

        var listed = await _store.QueryAsync(failingHandler: "Mohist.Server.Events.MissingHandler", limit: 100);

        Assert.Empty(listed);
    }

    [Fact]
    public async Task GetAsync_UnknownId_ReturnsNull()
    {
        var fetched = await _store.GetAsync(deadLetterId: 999_999_999);

        Assert.Null(fetched);
    }

    [Fact]
    public async Task DeleteAsync_RemovesResolvedRow()
    {
        var row = BuildRow(origin: "Issue", deadLetteredAt: FirstTime);
        await _store.WriteAsync(row);

        await _store.DeleteAsync(row.DeadLetterId);

        Assert.Null(await _store.GetAsync(row.DeadLetterId));
        Assert.Empty(await _store.QueryAsync(failingHandler: null, limit: 100));
    }

    [Fact]
    public async Task SettleAsync_WritesOneHandlerRowAndMarksSourceInOneCommit()
    {
        var envelope = new CloudEvent(
            id: "evt_atomic",
            source: new Uri("/mohist/issues/issue_atomic", UriKind.Relative),
            type: "com.mohist.issue.completed",
            time: FirstTime,
            data: JsonSerializer.SerializeToElement(new { value = 1 }),
            extensions: IssueExtensions("issue_atomic"));
        await _events.AppendAsync(envelope);
        var sourceEvent = Assert.Single(await _events.ListUndeliveredAsync());
        var deadLetter = FromSource(
            BuildRow(nameof(EventOrigin.Issue), FirstTime, eventId: envelope.Id),
            sourceEvent);

        await _store.SettleAsync(sourceEvent, [deadLetter], SecondTime);

        Assert.Empty(await _events.ListUndeliveredAsync());
        var stored = Assert.Single(await _store.QueryAsync(failingHandler: null, limit: 100));
        Assert.Equal(DeadLetterStatus.Pending, stored.Status);

        deadLetter.ErrorMessage = "updated failure";
        await _store.SettleAsync(sourceEvent, [deadLetter], ThirdTime);

        stored = Assert.Single(await _store.QueryAsync(failingHandler: null, limit: 100));
        Assert.Equal("updated failure", stored.ErrorMessage);
    }

    [Fact]
    public async Task SettleAsync_SourceMarkFailureDoesNotCommitDeadLetter()
    {
        var missing = new UndeliveredEvent(
            EventOrigin.Issue,
            999,
            "/mohist/issues/missing",
            "evt_missing",
            "com.mohist.issue.completed",
            FirstTime,
            "1.0",
            null,
            "application/json",
            JsonSerializer.SerializeToElement(new { value = 1 }),
            "{}");
        var deadLetter = FromSource(
            BuildRow(nameof(EventOrigin.Issue), FirstTime, eventId: missing.EventId),
            missing);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.SettleAsync(missing, [deadLetter], SecondTime));

        Assert.Empty(await _store.QueryAsync(failingHandler: null, limit: 100));
    }

    [Fact]
    public async Task RedeliveryState_TracksAmbiguousAttemptAndResolution()
    {
        var row = BuildRow(origin: "Issue", deadLetteredAt: FirstTime);
        await _store.WriteAsync(row);

        var started = await _store.StartRedeliveryAsync(row.DeadLetterId, SecondTime);
        Assert.NotNull(started);
        Assert.Equal(DeadLetterStatus.Redelivering, started.Status);

        await _store.ResolveAsync(row.DeadLetterId, ThirdTime);

        var resolved = await _store.GetAsync(row.DeadLetterId);
        Assert.NotNull(resolved);
        Assert.Equal(DeadLetterStatus.Resolved, resolved.Status);
        Assert.Equal(ThirdTime, resolved.ResolvedAt);
        Assert.Empty(await _store.QueryAsync(failingHandler: null, limit: 100));
    }

    [Fact]
    public async Task RedeliveryFailure_ReturnsToPendingWithUpdatedDiagnostics()
    {
        var row = BuildRow(origin: "Issue", deadLetteredAt: FirstTime);
        await _store.WriteAsync(row);
        await _store.StartRedeliveryAsync(row.DeadLetterId, SecondTime);

        await _store.RecordRedeliveryFailureAsync(
            row.DeadLetterId,
            "replacement handler failure",
            "replacement stack",
            attemptCount: 5,
            attemptedAt: ThirdTime);

        var stored = await _store.GetAsync(row.DeadLetterId);
        Assert.NotNull(stored);
        Assert.Equal(DeadLetterStatus.Pending, stored.Status);
        Assert.Equal("replacement handler failure", stored.ErrorMessage);
        Assert.Equal("replacement stack", stored.ErrorStack);
        Assert.Equal(5, stored.AttemptCount);
        Assert.Equal(ThirdTime, stored.RedeliveryAttemptedAt);
        Assert.Contains(
            await _store.QueryAsync(failingHandler: null, limit: 100),
            candidate => candidate.DeadLetterId == row.DeadLetterId);
    }

    [Fact]
    public void NoopDeadLetterStore_IsUsableFake()
    {
        IDeadLetterStore fake = new NoopDeadLetterStore();
        Assert.NotNull(fake);
    }

    [Fact]
    public async Task ListByHandlerAsync_ReturnsRowsForHandler_OrderedByDeadLetteredAtDescending()
    {
        var handlerA = "Mohist.Server.Events.Workflow.HandlerA";
        var handlerB = "Mohist.Server.Events.Workflow.HandlerB";
        await _store.WriteAsync(BuildRow(
            origin: "WorkflowRun", deadLetteredAt: FirstTime,
            failingHandler: handlerA, eventId: "evt_a_first"));
        await _store.WriteAsync(BuildRow(
            origin: "WorkflowRun", deadLetteredAt: ThirdTime,
            failingHandler: handlerB, eventId: "evt_b_latest"));
        await _store.WriteAsync(BuildRow(
            origin: "WorkflowRun", deadLetteredAt: SecondTime,
            failingHandler: handlerA, eventId: "evt_a_middle"));

        var rows = await _store.ListByHandlerAsync(handlerA);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(handlerA, r.FailingHandler));
        Assert.Equal(new[] { "evt_a_middle", "evt_a_first" }, rows.Select(r => r.EventId).ToArray());
    }

    [Fact]
    public async Task ListByHandlerAsync_AppliesLimit()
    {
        var handler = "Mohist.Server.Events.Workflow.Handler";
        for (var i = 0; i < 5; i++)
        {
            await _store.WriteAsync(BuildRow(
                origin: "Issue", deadLetteredAt: FirstTime.AddMinutes(i),
                failingHandler: handler, eventId: $"evt_{i}"));
        }

        var rows = await _store.ListByHandlerAsync(handler, limit: 3);

        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task ListByHandlerAsync_NoMatches_ReturnsEmpty()
    {
        await _store.WriteAsync(BuildRow(
            origin: "WorkflowRun", deadLetteredAt: FirstTime, eventId: "evt_present"));

        var rows = await _store.ListByHandlerAsync("Mohist.Server.Events.Missing");

        Assert.Empty(rows);
    }

    [Fact]
    public async Task ListByHandlerAsync_EmptyHandler_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.ListByHandlerAsync(""));
    }

    [Fact]
    public async Task ListByTimeRangeAsync_ReturnsRowsInRange()
    {
        await _store.WriteAsync(BuildRow(
            origin: "Issue", deadLetteredAt: FirstTime, eventId: "evt_earliest"));
        await _store.WriteAsync(BuildRow(
            origin: "Issue", deadLetteredAt: SecondTime, eventId: "evt_middle"));
        await _store.WriteAsync(BuildRow(
            origin: "Issue", deadLetteredAt: ThirdTime, eventId: "evt_latest"));

        var rows = await _store.ListByTimeRangeAsync(
            FirstTime.AddMinutes(1), ThirdTime);

        Assert.Single(rows);
        Assert.Equal("evt_middle", rows[0].EventId);
    }

    [Fact]
    public async Task ListByTimeRangeAsync_RangeIsLowerInclusiveUpperExclusive()
    {
        await _store.WriteAsync(BuildRow(
            origin: "Issue", deadLetteredAt: FirstTime, eventId: "evt_lower"));
        await _store.WriteAsync(BuildRow(
            origin: "Issue", deadLetteredAt: SecondTime, eventId: "evt_upper"));

        var rows = await _store.ListByTimeRangeAsync(FirstTime, SecondTime);

        Assert.Single(rows);
        Assert.Equal("evt_lower", rows[0].EventId);
    }

    [Fact]
    public async Task ListByTimeRangeAsync_NoMatches_ReturnsEmpty()
    {
        await _store.WriteAsync(BuildRow(
            origin: "Issue", deadLetteredAt: FirstTime, eventId: "evt_present"));

        var rows = await _store.ListByTimeRangeAsync(
            FirstTime.AddHours(1), FirstTime.AddHours(2));

        Assert.Empty(rows);
    }

    [Fact]
    public async Task RetryAsync_ReNullsSourceRow_AndPreservesDeadLetter()
    {
        var envelope = new CloudEvent(
            id: "evt_retry_target",
            source: new Uri("/mohist/issues/issue_retry_target", UriKind.Relative),
            type: "com.mohist.issue.completed",
            time: FirstTime,
            data: JsonSerializer.SerializeToElement(new { value = 1 }),
            extensions: IssueExtensions("issue_retry_target"));
        await _events.AppendAsync(envelope);
        var sourceEvent = Assert.Single(await _events.ListUndeliveredAsync());

        var deadLetter = FromSource(
            BuildRow(nameof(EventOrigin.Issue), FirstTime, eventId: envelope.Id),
            sourceEvent);
        await _store.WriteAsync(deadLetter);

        await _events.MarkDispatchedAsync(
            sourceEvent.Origin, sourceEvent.Source, sourceEvent.Id, SecondTime);

        Assert.Empty(await _events.ListUndeliveredAsync());

        await _store.RetryAsync(deadLetter.DeadLetterId);

        var undelivered = await _events.ListUndeliveredAsync();
        var requeued = Assert.Single(undelivered);
        Assert.Equal(sourceEvent.Origin, requeued.Origin);
        Assert.Equal(sourceEvent.Source, requeued.Source);
        Assert.Equal(sourceEvent.Id, requeued.Id);
        Assert.Equal(sourceEvent.EventId, requeued.EventId);

        var stillStored = Assert.Single(await _store.QueryAsync(failingHandler: null, limit: 100));
        Assert.Equal(deadLetter.DeadLetterId, stillStored.DeadLetterId);
        Assert.Equal(deadLetter.FailingHandler, stillStored.FailingHandler);
        Assert.Equal(deadLetter.ErrorMessage, stillStored.ErrorMessage);
    }

    [Fact]
    public async Task RetryAsync_UnknownId_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.RetryAsync(deadLetterId: 999_999_999));
    }

    [Fact]
    public async Task RetryAsync_RoutesByOrigin()
    {
        var envelope = new CloudEvent(
            id: "evt_retry_origin",
            source: new Uri("/mohist/workflow-runs/wfr_retry_origin", UriKind.Relative),
            type: "com.mohist.workflow.task.completed",
            time: FirstTime,
            data: JsonSerializer.SerializeToElement(new { value = 1 }),
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.ProjectId] = "project_1",
                [EventCatalog.Lineage.WorkflowRunId] = "wfr_retry_origin",
                [EventCatalog.Lineage.Stage] = "test",
            });
        await _events.AppendAsync(envelope);
        var sourceEvent = Assert.Single(await _events.ListUndeliveredAsync());

        var deadLetter = FromSource(
            BuildRow(nameof(EventOrigin.WorkflowRun), FirstTime, eventId: envelope.Id),
            sourceEvent);
        await _store.WriteAsync(deadLetter);

        await _events.MarkDispatchedAsync(
            sourceEvent.Origin, sourceEvent.Source, sourceEvent.Id, SecondTime);
        Assert.Empty(await _events.ListUndeliveredAsync());

        await _store.RetryAsync(deadLetter.DeadLetterId);

        var requeued = Assert.Single(await _events.ListUndeliveredAsync());
        Assert.Equal(EventOrigin.WorkflowRun, requeued.Origin);
        Assert.Equal(sourceEvent.Source, requeued.Source);
        Assert.Equal(sourceEvent.Id, requeued.Id);
    }

    private static Dictionary<string, string> IssueExtensions(string issueId) => new(StringComparer.Ordinal)
    {
        [EventCatalog.Lineage.ProjectId] = "project_1",
        [EventCatalog.Lineage.Issue] = issueId,
        [EventCatalog.Lineage.Issue] = "1",
    };

    private static DeadLetterRow BuildRow(
        string origin,
        DateTimeOffset deadLetteredAt,
        string failingHandler = "Mohist.Server.Events.Workflow.CompletedHandler",
        string eventId = "evt_default") =>
        new()
        {
            Origin = origin,
            Id = 42,
            Source = $"/mohist/workflow-runs/{eventId}",
            EventId = eventId,
            Type = "com.mohist.workflow.task.completed",
            Time = FirstTime,
            SpecVersion = "1.0",
            Subject = "task-42",
            DataContentType = "application/json",
            Data = JsonDocument.Parse("{\"result\":\"ok\"}").RootElement,
            ExtensionsJson = "{\"tenant\":\"local\",\"traceId\":\"tr_42\"}",
            FailingHandler = failingHandler,
            ErrorMessage = "handler crashed",
            ErrorStack = "stack line 1\nstack line 2",
            AttemptCount = 3,
            DeadLetteredAt = deadLetteredAt,
        };

    private static DeadLetterRow FromSource(DeadLetterRow template, UndeliveredEvent sourceEvent) =>
        new()
        {
            Origin = sourceEvent.Origin.ToString(),
            Id = sourceEvent.Id,
            Source = sourceEvent.Source,
            EventId = sourceEvent.EventId,
            Type = sourceEvent.Type,
            Time = sourceEvent.Time,
            SpecVersion = sourceEvent.SpecVersion,
            Subject = sourceEvent.Subject,
            DataContentType = sourceEvent.DataContentType,
            Data = sourceEvent.Data,
            ExtensionsJson = sourceEvent.ExtensionsJson,
            FailingHandler = template.FailingHandler,
            ErrorMessage = template.ErrorMessage,
            ErrorStack = template.ErrorStack,
            AttemptCount = template.AttemptCount,
            DeadLetteredAt = template.DeadLetteredAt,
        };

    private static void AssertRowEqual(DeadLetterRow expected, DeadLetterRow actual)
    {
        Assert.Equal(expected.DeadLetterId, actual.DeadLetterId);
        Assert.Equal(expected.Origin, actual.Origin);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Source, actual.Source);
        Assert.Equal(expected.EventId, actual.EventId);
        Assert.Equal(expected.Type, actual.Type);
        Assert.Equal(expected.Time, actual.Time);
        Assert.Equal(expected.SpecVersion, actual.SpecVersion);
        Assert.Equal(expected.Subject, actual.Subject);
        Assert.Equal(expected.DataContentType, actual.DataContentType);
        Assert.Equal(expected.Data.GetRawText(), actual.Data.GetRawText());
        Assert.Equal(expected.ExtensionsJson, actual.ExtensionsJson);
        Assert.Equal(expected.FailingHandler, actual.FailingHandler);
        Assert.Equal(expected.ErrorMessage, actual.ErrorMessage);
        Assert.Equal(expected.ErrorStack, actual.ErrorStack);
        Assert.Equal(expected.AttemptCount, actual.AttemptCount);
        Assert.Equal(expected.DeadLetteredAt, actual.DeadLetteredAt);
    }
}
