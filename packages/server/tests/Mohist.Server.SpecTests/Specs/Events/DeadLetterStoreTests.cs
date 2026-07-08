using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

[Trait(Traits.Speed.Name, Traits.Speed.Unit)]
[Trait(Traits.Sut.Name, Traits.Sut.System)]
public sealed class DeadLetterStoreTests : IAsyncLifetime
{
    private static readonly DateTimeOffset FirstTime = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SecondTime = new(2026, 7, 1, 12, 5, 0, TimeSpan.Zero);

    private readonly DbContextOptions<MohistDbContext> _options;
    private readonly SqliteConnection _keeper;
    private readonly DeadLetterStore _store;

    public DeadLetterStoreTests()
    {
        var connectionString = $"Data Source=dead-letter-store-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();
        _options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connectionString)
            .Options;

        using var db = new MohistDbContext(_options);
        db.Database.EnsureCreated();

        _store = new DeadLetterStore(new Factory(_options));
    }

    [Fact]
    public async Task WriteThenList_RoundTripsFullSnapshot()
    {
        var record = BuildRecord(deadLetteredAt: FirstTime);

        await _store.WriteAsync(record);

        var listed = await _store.ListAsync();
        var actual = Assert.Single(listed);
        AssertRecord(actual, record);

        await using var db = new MohistDbContext(_options);
        Assert.Empty(await db.WorkflowRunEvents.ToListAsync());
        Assert.Empty(await db.IssueEvents.ToListAsync());
        Assert.Empty(await db.EpicEvents.ToListAsync());
        Assert.Single(await db.DeadLetters.ToListAsync());
    }

    [Fact]
    public async Task WriteAsync_AppendsInsteadOfOverwriting()
    {
        var first = BuildRecord(deadLetteredAt: FirstTime);
        var second = BuildRecord(deadLetteredAt: SecondTime);

        await _store.WriteAsync(first);
        await _store.WriteAsync(second);

        var listed = await _store.ListAsync();
        Assert.Equal(2, listed.Count);
        AssertRecord(listed[0], first);
        AssertRecord(listed[1], second);
    }

    [Fact]
    public async Task ListAsync_WithMoreThanLimit_ReturnsEarliestSliceInAppendOrder()
    {
        var records = Enumerable.Range(0, 5)
            .Select(i => BuildRecord(deadLetteredAt: FirstTime.AddMinutes(i)))
            .ToList();

        foreach (var record in records)
        {
            await _store.WriteAsync(record);
        }

        var listed = await _store.ListAsync(limit: 3);
        Assert.Equal(3, listed.Count);
        AssertRecord(listed[0], records[0]);
        AssertRecord(listed[1], records[1]);
        AssertRecord(listed[2], records[2]);
    }

    [Fact]
    public async Task ListAsync_DefaultLimit_ReturnsFirstHundredRows()
    {
        var records = Enumerable.Range(0, 101)
            .Select(i => BuildRecord(deadLetteredAt: FirstTime.AddMinutes(i)))
            .ToList();

        foreach (var record in records)
        {
            await _store.WriteAsync(record);
        }

        var listed = await _store.ListAsync();
        Assert.Equal(100, listed.Count);

        for (int i = 0; i < 100; i++)
        {
            AssertRecord(listed[i], records[i]);
        }
    }

    [Fact]
    public void NoopDeadLetterStore_IsUsableFake()
    {
        IDeadLetterStore store = new NoopDeadLetterStore();
        Assert.NotNull(store);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _keeper.Dispose();
        return Task.CompletedTask;
    }

    private static DeadLetterRecord BuildRecord(DateTimeOffset deadLetteredAt) =>
        new(
            Origin: EventOrigin.WorkflowRun,
            Id: 42,
            Source: "/mohist/workflow-runs/wr_42",
            EventId: "evt_42",
            Type: "com.mohist.workflow.task.completed",
            Time: FirstTime,
            SpecVersion: "1.0",
            Subject: "task-42",
            DataContentType: "application/json",
            Data: JsonDocument.Parse("{" + "\"result\":\"ok\",\"attempt\":2}").RootElement,
            ExtensionsJson: "{\"tenant\":\"local\",\"traceId\":\"tr_42\"}",
            FailingHandler: "Mohist.Server.Events.Workflow.CompletedHandler",
            ErrorMessage: "handler crashed",
            ErrorStack: "stack line 1\nstack line 2",
            AttemptCount: 3,
            DeadLetteredAt: deadLetteredAt);

    private static void AssertRecord(DeadLetterRecord actual, DeadLetterRecord expected)
    {
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
