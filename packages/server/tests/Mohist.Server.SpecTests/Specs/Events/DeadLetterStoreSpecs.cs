using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

[Trait(Traits.Speed.Name, Traits.Speed.Service)]
[Trait(Traits.Sut.Name, Traits.Sut.System)]
public class DeadLetterStoreSpecs : IAsyncLifetime
{
    private static readonly DateTimeOffset FirstTime = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SecondTime = new(2026, 7, 1, 12, 5, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ThirdTime = new(2026, 7, 1, 12, 10, 0, TimeSpan.Zero);

    private SqliteConnection _keeper = null!;
    private DbContextOptions<MohistDbContext> _options = null!;
    private DeadLetterStore _store = null!;

    public Task InitializeAsync()
    {
        var connectionString = $"Data Source=dead-letter-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();
        MigratedSqliteTemplate.CopyTo(_keeper);
        _options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        _store = new DeadLetterStore(new Factory(_options));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _keeper.Dispose();
        return Task.CompletedTask;
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
    public void NoopDeadLetterStore_IsUsableFake()
    {
        IDeadLetterStore fake = new NoopDeadLetterStore();
        Assert.NotNull(fake);
    }

    private static DeadLetterRow BuildRow(
        string origin,
        DateTimeOffset deadLetteredAt,
        string failingHandler = "Mohist.Server.Events.Workflow.CompletedHandler",
        string eventId = "evt_default") =>
        new()
        {
            Origin = origin,
            Id = 42,
            Source = "/mohist/workflow-runs/wr_42",
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

    private sealed class Factory : IDbContextFactory<MohistDbContext>
    {
        private readonly DbContextOptions<MohistDbContext> _options;

        public Factory(DbContextOptions<MohistDbContext> options) => _options = options;

        public MohistDbContext CreateDbContext() => new(_options);
    }
}
