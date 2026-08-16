using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.TestSupport;
using Mohist.Server.UnitTests.Support;
using Xunit;

namespace Mohist.Server.UnitTests.Sessions;

/// <summary>
/// Lower-owner coverage for the production <see cref="AgentSessionStore"/>
/// transactional save path with schedule-bearing state (#422): reload
/// equivalence, append ordering, rollback/cancellation atomicity and
/// cross-session isolation, observed against a real in-memory SQLite schema
/// (the established UnitTests pattern) with fake collaborators at the other
/// seams — a recording <see cref="IEventStore"/>, the null dispatcher grain
/// factory and a recording background-task launcher. SQLite serializes
/// concurrent writers, matching the production contract that callers
/// (session grains) serialize saves per key, so isolation is asserted as
/// key scoping plus last-committed-wins.
/// </summary>
public sealed class AgentSessionStoreSchedulePersistenceTests
{
    private static readonly DateTime FixedTime = new(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc);

    private sealed class RecordingEventStore : IEventStore
    {
        public List<CloudEvent> Appended { get; } = [];
        public bool FailStagedAppends { get; set; }

        public Task AppendAsync(CloudEvent envelope, CancellationToken ct = default)
        {
            Appended.Add(envelope);
            return Task.CompletedTask;
        }

        public Task AppendAsync(MohistDbContext db, CloudEvent envelope, CancellationToken ct = default)
        {
            Appended.Add(envelope);
            if (FailStagedAppends)
                throw new InvalidOperationException("event append failed inside the transaction");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StoredCloudEvent>> ListAsync(string workflowRunId, int limit = 200, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);
        public Task<IReadOnlyList<StoredCloudEvent>> ListIssueEventsAsync(string projectId, int issueNumber, int limit = 200, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);
        public Task<IReadOnlyList<StoredCloudEvent>> ListEpicEventsAsync(string projectId, int epicNumber, int limit = 200, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);
        public Task<IReadOnlyList<StoredCloudEvent>> ListAgentSessionEventsAsync(string sessionId, int limit = 200, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);
        public Task<IReadOnlyList<StoredCloudEvent>> ListAgentJobEventsAsync(string agentJobId, int limit = 200, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);
        public Task<IReadOnlyList<StoredCloudEvent>> ListWorkspaceEventsAsync(string projectId, string name, int limit = 200, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);
        public Task MarkDispatchedAsync(EventOrigin origin, string source, long id, DateTimeOffset dispatchedAt, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<IReadOnlyList<UndeliveredEvent>> ListUndeliveredAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UndeliveredEvent>>([]);
    }

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);

        public Task<MohistDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }

    private static AgentSession CreateSession(string id) =>
        AgentSession.Create(
            id, "runner-1", "/workdir",
            metadata: new AgentSessionMetadata()
                .WithLabel("mohist.io/project-id", $"project-{id}")
                .WithLabel("mohist.io/source-kind", "workflow")
                .WithLabel("mohist.io/source-id", "workflow-1")
                .WithLabel("mohist.io/session-name", "build"),
            now: FixedTime,
            runtime: "test-runtime");

    [Fact]
    public async Task SaveAsync_ScheduleBearingState_RoundTripsEveryStatusFieldExactly()
    {
        await using var keeper = await OpenSchemaAsync("schedule-roundtrip");
        var factory = new TestDbContextFactory(Options(keeper));
        var events = new RecordingEventStore();
        var store = NewStore(factory, events);

        var session = CreateSession("session-roundtrip");
        var scheduled = session.CreateSchedule("sch-1", "first", FixedTime.AddHours(1), "idem-1", FixedTime);
        session.Status = session.Status with
        {
            Schedules =
            [
                scheduled,
                MakeRecord("sch-p", SessionScheduleStatus.PendingDelivery, FixedTime),
                MakeRecord("sch-d", SessionScheduleStatus.Delivered, FixedTime, InputId: "input-d"),
                MakeRecord("sch-x", SessionScheduleStatus.Cancelled, FixedTime, CancelledAt: FixedTime.AddMinutes(5)),
            ],
        };

        await store.SaveAsync(
            "session-roundtrip",
            session,
            [
                new AgentSessionRuntimeBound("runtime-session-1"),
                new AgentSessionUsageRecorded(new AgentUsageSummary(InputTokens: 11, OutputTokens: 7, TotalTokens: 18)),
            ]);

        Assert.Equal(
            [AgentSessionEventSerializer.BusType(new AgentSessionRuntimeBound("runtime-session-1")),
             AgentSessionEventSerializer.BusType(new AgentSessionUsageRecorded(new AgentUsageSummary(InputTokens: 11)))],
            events.Appended.Select(e => e.Type).ToArray());

        await using var db = await factory.CreateDbContextAsync();
        var row = await db.AgentSessions.AsNoTracking().SingleAsync(r => r.Id == "session-roundtrip");
        var reloaded = AgentSessionJson.Deserialize(row);

        Assert.NotNull(reloaded);
        Assert.Equal(session.Id, reloaded!.Id);
        var schedules = (reloaded.Status.Schedules ?? []).ToList();
        Assert.Equal(4, schedules.Count);
        Assert.Equal(scheduled, schedules[0]);
        Assert.Equal(SessionScheduleStatus.PendingDelivery, schedules[1].Status);
        Assert.Null(schedules[1].CancelledAt);
        Assert.Equal(SessionScheduleStatus.Delivered, schedules[2].Status);
        Assert.Equal("input-d", schedules[2].InputId);
        Assert.Equal(SessionScheduleStatus.Cancelled, schedules[3].Status);
        Assert.Equal(FixedTime.AddMinutes(5), schedules[3].CancelledAt);
        Assert.Null(schedules[3].InputId);
    }

    [Fact]
    public async Task SaveAsync_AppendFailureInsideTransaction_LeavesNoHalfWrittenState()
    {
        await using var keeper = await OpenSchemaAsync("schedule-rollback");
        var factory = new TestDbContextFactory(Options(keeper));
        var events = new RecordingEventStore { FailStagedAppends = true };
        var launcher = new RecordingBackgroundTaskLauncher();
        var store = new AgentSessionStore(
            factory, events, new NullEventDispatchGrainFactory(),
            NullLogger<AgentSessionStore>.Instance, launcher);

        var session = CreateSession("session-rollback");
        _ = session.CreateSchedule("sch-doomed", "doomed", FixedTime.AddHours(2), "idem-doomed", FixedTime);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveAsync("session-rollback", session, [new AgentSessionRuntimeBound("runtime-rollback")]));

        Assert.Single(events.Appended);
        Assert.Equal(0, launcher.LaunchCount);

        await using var db = await factory.CreateDbContextAsync();
        Assert.False(await db.AgentSessions.AnyAsync(r => r.Id == "session-rollback"));
        Assert.False(await db.AgentSessionEvents.AnyAsync());
    }

    [Fact]
    public async Task SaveAsync_CanceledBeforeStart_NoWriteNoAppendNoPoke()
    {
        await using var keeper = await OpenSchemaAsync("schedule-cancel");
        var factory = new TestDbContextFactory(Options(keeper));
        var events = new RecordingEventStore();
        var launcher = new RecordingBackgroundTaskLauncher();
        var store = new AgentSessionStore(
            factory, events, new NullEventDispatchGrainFactory(),
            NullLogger<AgentSessionStore>.Instance, launcher);

        var session = CreateSession("session-cancel");
        _ = session.CreateSchedule("sch-cancel", "cancelled", FixedTime.AddHours(1), "idem-cancel", FixedTime);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.SaveAsync("session-cancel", session, [new AgentSessionRuntimeBound("runtime-cancel")], cts.Token));

        Assert.Empty(events.Appended);
        Assert.Equal(0, launcher.LaunchCount);

        await using var db = await factory.CreateDbContextAsync();
        Assert.False(await db.AgentSessions.AnyAsync(r => r.Id == "session-cancel"));
        Assert.False(await db.AgentSessionEvents.AnyAsync());
    }

    [Fact]
    public async Task SaveAsync_SequentialSaves_KeepSessionsIsolatedWithLastCommittedSnapshot()
    {
        await using var keeper = await OpenSchemaAsync("schedule-isolation");
        var factory = new TestDbContextFactory(Options(keeper));
        var store = NewStore(factory, new RecordingEventStore());

        var first = CreateSession("session-a");
        _ = first.CreateSchedule("sch-a", "a", FixedTime.AddHours(1), "idem-a", FixedTime);
        var second = CreateSession("session-b");
        _ = second.CreateSchedule("sch-b", "b", FixedTime.AddHours(2), "idem-b", FixedTime);
        await store.SaveAsync("session-a", first, []);
        await store.SaveAsync("session-b", second, []);

        var firstOverwritten = CreateSession("session-a");
        _ = firstOverwritten.CreateSchedule("sch-a2", "a2", FixedTime.AddHours(3), "idem-a2", FixedTime);
        await store.SaveAsync("session-a", firstOverwritten, []);

        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.AgentSessions.AsNoTracking().ToListAsync();
        var byKey = rows.ToDictionary(r => r.Id, r => AgentSessionJson.Deserialize(r));

        Assert.Equal(2, byKey.Count);
        var a = byKey["session-a"]!;
        var b = byKey["session-b"]!;
        Assert.Equal(
            ["sch-a2"],
            (a.Status.Schedules ?? []).Select(s => s.ScheduleId).ToArray());
        Assert.Equal(
            ["sch-b"],
            (b.Status.Schedules ?? []).Select(s => s.ScheduleId).ToArray());
    }

    private static AgentSessionStore NewStore(
        TestDbContextFactory factory, RecordingEventStore events) =>
        new(factory, events, new NullEventDispatchGrainFactory(),
            NullLogger<AgentSessionStore>.Instance, new RecordingBackgroundTaskLauncher());

    private static DbContextOptions<MohistDbContext> Options(SqliteConnection keeper) =>
        new DbContextOptionsBuilder<MohistDbContext>().UseSqlite(keeper).Options;

    private static async Task<SqliteConnection> OpenSchemaAsync(string name)
    {
        var keeper = new SqliteConnection($"Data Source=schedule-persist-{name}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await keeper.OpenAsync();
        SqliteSchemaTemplate.CopyModelSchemaTo(keeper);
        return keeper;
    }

    private static SessionScheduleRecord MakeRecord(
        string id,
        SessionScheduleStatus status,
        DateTime now,
        DateTime? CancelledAt = null,
        string? InputId = null) => new(
        id, now.AddHours(1), $"text-{id}", status, $"idem-{id}", now, CancelledAt, InputId);
}
