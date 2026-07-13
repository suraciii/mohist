using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs;

/// <summary>
/// Unit spec for <see cref="DeadLetterStore"/> write/read round-trip via an
/// in-memory SQLite-backed <see cref="IDbContextFactory{TContext}"/> fake.
/// Verifies that the store persists a full event snapshot on
/// <see cref="DeadLetterStore.WriteAsync"/> and serves it back via
/// <see cref="DeadLetterStore.GetAsync"/> without losing any field, and that
/// the T-001-spec query paths return rows in the documented order.
/// </summary>
public class DeadLetterStoreRoundTripTests : IDisposable
{
    private static readonly DateTimeOffset FirstTime = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SecondTime = new(2026, 7, 1, 12, 5, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ThirdTime = new(2026, 7, 1, 12, 10, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<MohistDbContext> _factory;
    private readonly DeadLetterStore _store;

    public DeadLetterStoreRoundTripTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        using (var db = new MohistDbContext(options))
        {
            db.Database.EnsureCreated();
        }

        _factory = new InMemoryFactory(options);
        _store = new DeadLetterStore(_factory);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task WriteAsync_PersistsFullEventSnapshot_GetAsyncReturnsIt()
    {
        var row = BuildRow(FirstTime);

        await _store.WriteAsync(row);
        var fetched = await _store.GetAsync(row.DeadLetterId);

        Assert.NotNull(fetched);
        Assert.Equal(row.Origin, fetched.Origin);
        Assert.Equal(row.Source, fetched.Source);
        Assert.Equal(row.Id, fetched.Id);
        Assert.Equal(row.EventId, fetched.EventId);
        Assert.Equal(row.Type, fetched.Type);
        Assert.Equal(row.Time, fetched.Time);
        Assert.Equal(row.SpecVersion, fetched.SpecVersion);
        Assert.Equal(row.Subject, fetched.Subject);
        Assert.Equal(row.DataContentType, fetched.DataContentType);
        Assert.Equal(row.Data.GetRawText(), fetched.Data.GetRawText());
        Assert.Equal(row.ExtensionsJson, fetched.ExtensionsJson);
        Assert.Equal(row.FailingHandler, fetched.FailingHandler);
        Assert.Equal(row.ErrorMessage, fetched.ErrorMessage);
        Assert.Equal(row.ErrorStack, fetched.ErrorStack);
        Assert.Equal(row.AttemptCount, fetched.AttemptCount);
        Assert.Equal(row.DeadLetteredAt, fetched.DeadLetteredAt);
    }

    [Fact]
    public async Task WriteAsync_AssignsMonotonicDeadLetterId()
    {
        var first = BuildRow(FirstTime, eventId: "evt_first");
        var second = BuildRow(SecondTime, eventId: "evt_second");

        await _store.WriteAsync(first);
        await _store.WriteAsync(second);

        Assert.True(second.DeadLetterId > first.DeadLetterId);
    }

    [Fact]
    public async Task ListByHandlerAsync_ReturnsRowsForHandler_Descending()
    {
        var handlerA = "Mohist.Server.Events.A";
        var handlerB = "Mohist.Server.Events.B";

        await _store.WriteAsync(BuildRow(FirstTime, failingHandler: handlerA, eventId: "evt_a_first"));
        await _store.WriteAsync(BuildRow(ThirdTime, failingHandler: handlerB, eventId: "evt_b_latest"));
        await _store.WriteAsync(BuildRow(SecondTime, failingHandler: handlerA, eventId: "evt_a_middle"));

        var rows = await _store.ListByHandlerAsync(handlerA);

        Assert.Equal(new[] { "evt_a_middle", "evt_a_first" }, rows.Select(r => r.EventId).ToArray());
    }

    [Fact]
    public async Task ListByTimeRangeAsync_ReturnsRowsWithinRange()
    {
        await _store.WriteAsync(BuildRow(FirstTime, eventId: "evt_first"));
        await _store.WriteAsync(BuildRow(SecondTime, eventId: "evt_second"));
        await _store.WriteAsync(BuildRow(ThirdTime, eventId: "evt_third"));

        var rows = await _store.ListByTimeRangeAsync(SecondTime, ThirdTime);

        Assert.Single(rows);
        Assert.Equal("evt_second", rows[0].EventId);
    }

    private static DeadLetterRow BuildRow(
        DateTimeOffset deadLetteredAt,
        string failingHandler = "Mohist.Server.Events.DefaultHandler",
        string eventId = "evt_default") =>
        new()
        {
            Origin = "WorkflowRun",
            Id = 42,
            Source = $"/mohist/workflow-runs/{eventId}",
            EventId = eventId,
            Type = "com.mohist.workflow.task.completed",
            Time = FirstTime,
            SpecVersion = "1.0",
            Subject = "task-42",
            DataContentType = "application/json",
            Data = JsonDocument.Parse("{\"result\":\"ok\"}").RootElement,
            ExtensionsJson = "{\"tenant\":\"local\"}",
            FailingHandler = failingHandler,
            ErrorMessage = "handler crashed",
            ErrorStack = "stack line 1\nstack line 2",
            AttemptCount = 3,
            DeadLetteredAt = deadLetteredAt,
        };

    private sealed class InMemoryFactory : IDbContextFactory<MohistDbContext>
    {
        private readonly DbContextOptions<MohistDbContext> _options;

        public InMemoryFactory(DbContextOptions<MohistDbContext> options) => _options = options;

        public MohistDbContext CreateDbContext() => new(_options);
    }
}