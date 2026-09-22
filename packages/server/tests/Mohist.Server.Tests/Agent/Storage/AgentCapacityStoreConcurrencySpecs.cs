using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Capacity;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.TestSupport;
using Mohist.Server.Tests.Support;
using Xunit;
using DomainAgent = Mohist.Server.Agent.Domain.Agent;

namespace Mohist.Server.Tests.Agent.Storage;

[Trait("level", "L0")]
public sealed class AgentCapacityStoreConcurrencySpecs : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly TestSqliteDatabase _database = TestSqliteDatabase.CreateModelSchema();
    private readonly FakeTimeProvider _time = new(Now);
    private AgentCapacityStore Store => new(new TestDbContextFactory(_database.Options), _time);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task OverlappingJobClaims_AreSerializedBeforeOwnerReads()
    {
        await AddAgentAsync(1);
        var first = await AddJobAsync("job-a", Now);
        var contender = await AddJobAsync("job-b", Now.AddSeconds(1));

        await ProveImmediateWriteSerializationAsync(
            async store => (await store.ClaimJobAsync("job-a", first.Revision)).Disposition,
            async store => (await store.ClaimJobAsync("job-b", contender.Revision)).Disposition);

        var snapshot = (await Store.ReadAsync("project", ["agent"]))["agent"];
        Assert.Equal(1, snapshot.Occupied);
        Assert.Equal(Now, await ReadJobClaimAsync("job-a"));
        Assert.Null(await ReadJobClaimAsync("job-b"));
    }

    [Fact]
    public async Task OverlappingJobAndTurnClaims_AreSerializedBeforeOwnerReads()
    {
        await AddAgentAsync(1);
        var job = await AddJobAsync("job", Now);
        var contenderJson = await AddSessionAsync(NewSession("session", "turn", Now.UtcDateTime.AddSeconds(1)));

        await ProveImmediateWriteSerializationAsync(
            async store => (await store.ClaimJobAsync("job", job.Revision)).Disposition,
            async store => (await store.ClaimTurnAsync("session", contenderJson, "turn")).Disposition);

        var snapshot = (await Store.ReadAsync("project", ["agent"]))["agent"];
        Assert.Equal(1, snapshot.Occupied);
        Assert.Equal(Now, await ReadJobClaimAsync("job"));
        Assert.Null(await ReadTurnClaimAsync("session"));
    }

    [Fact]
    public async Task OverlappingTurnClaims_AreSerializedBeforeOwnerReads()
    {
        await AddAgentAsync(1);
        var firstJson = await AddSessionAsync(NewSession("session-a", "turn-a", Now.UtcDateTime));
        var contenderJson = await AddSessionAsync(NewSession("session-b", "turn-b", Now.UtcDateTime.AddSeconds(1)));

        await ProveImmediateWriteSerializationAsync(
            async store => (await store.ClaimTurnAsync("session-a", firstJson, "turn-a")).Disposition,
            async store => (await store.ClaimTurnAsync("session-b", contenderJson, "turn-b")).Disposition);

        var snapshot = (await Store.ReadAsync("project", ["agent"]))["agent"];
        Assert.Equal(1, snapshot.Occupied);
        Assert.Equal(Now, await ReadTurnClaimAsync("session-a"));
        Assert.Null(await ReadTurnClaimAsync("session-b"));
    }

    private async Task ProveImmediateWriteSerializationAsync(
        Func<AgentCapacityStore, Task<AgentCapacityClaimDisposition>> firstClaim,
        Func<AgentCapacityStore, Task<AgentCapacityClaimDisposition>> contenderClaim)
    {
        var probe = new ImmediateTransactionProbe();
        using var factory = new ObservedDbContextFactory(_database.ConnectionString, probe);
        var store = new AgentCapacityStore(factory, _time);
        var first = Task.Run(() => firstClaim(store));

        await probe.FirstWriteOwned.Task;
        var contender = Task.Run(() => contenderClaim(store));
        await probe.ContenderBeginAttempted.Task;
        Assert.DoesNotContain("2:owner-read", probe.Events);
        probe.ReleaseFirst();

        Assert.Equal(AgentCapacityClaimDisposition.Claimed, await first);
        Assert.Equal(AgentCapacityClaimDisposition.CapacityFull, await contender);

        var events = probe.Events;
        Assert.True(IndexOf(events, "1:write-owned") < IndexOf(events, "2:begin-attempt"));
        Assert.True(IndexOf(events, "2:begin-attempt") < IndexOf(events, "1:owner-read"));
        Assert.True(IndexOf(events, "1:owner-read") < IndexOf(events, "2:write-owned"));
        Assert.True(IndexOf(events, "2:write-owned") < IndexOf(events, "2:owner-read"));
    }

    private static int IndexOf(IReadOnlyList<string> events, string value)
    {
        var index = events.ToList().IndexOf(value);
        Assert.True(index >= 0, $"Missing transaction probe event '{value}'. Events: {string.Join(", ", events)}");
        return index;
    }

    private async Task AddAgentAsync(int limit)
    {
        var agent = new DomainAgent
        {
            Id = "agent",
            ProjectId = "project",
            Name = "agent",
            MaxConcurrentRuns = limit,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        await using var db = _database.CreateContext();
        db.Agents.Add(new AgentRow { Id = GrainKey.Agent("project", "agent"), State = AgentStore.Serialize(agent) });
        await db.SaveChangesAsync();
    }

    private async Task<AgentJobLedgerRecord> AddJobAsync(string key, DateTimeOffset submittedAt)
    {
        var state = new AgentJobState
        {
            Status = AgentJobStatus.Pending,
            Input = new AgentJobInput("prompt", ProjectId: "project", AgentId: "agent"),
            SubmittedAt = submittedAt,
        };
        var store = new AgentJobStore(
            new TestDbContextFactory(_database.Options),
            NullLogger<AgentJobStore>.Instance,
            _time);
        return await store.InsertLedgerAsync(new AgentJobLedgerRecord(
            key, JSON.Serialize(state), 0, null, null, null, null, null,
            "agent-job", "agent", key, "project", null, null, null, null));
    }

    private async Task<string> AddSessionAsync(AgentSession session)
    {
        var row = AgentSessionJson.ToRow(session, Now.UtcDateTime);
        await using var db = _database.CreateContext();
        db.AgentSessions.Add(row);
        await db.SaveChangesAsync();
        return await db.AgentSessions.Where(candidate => candidate.Id == session.Id)
            .Select(candidate => candidate.State).SingleAsync();
    }

    private async Task<DateTimeOffset?> ReadJobClaimAsync(string jobKey)
    {
        await using var db = _database.CreateContext();
        var json = await db.AgentJobs.Where(row => row.JobKey == jobKey).Select(row => row.State).SingleAsync();
        return JSON.Deserialize<AgentJobState>(json)!.CapacityClaimedAt;
    }

    private async Task<DateTimeOffset?> ReadTurnClaimAsync(string sessionId)
    {
        await using var db = _database.CreateContext();
        var row = await db.AgentSessions.SingleAsync(candidate => candidate.Id == sessionId);
        return AgentSessionJson.Deserialize(row)!.Status.Turns!.Single().CapacityClaimedAt;
    }

    private static AgentSession NewSession(string sessionId, string turnId, DateTime recordedAt)
    {
        var metadata = new AgentSessionMetadata()
            .WithLabel("mohist.io/project-id", "project")
            .WithLabel("mohist.io/source-kind", "agent-launch")
            .WithLabel("mohist.io/agent-id", "agent");
        var session = AgentSession.Create(sessionId, "runner", "/work", metadata, recordedAt, "pi");
        session.Status = session.Status with
        {
            Activity = AgentSessionActivity.Active,
            ContextGeneration = 1,
            Inputs = [new("input", 1, "prompt", "api", AgentSessionInputAcceptance.Accepted, recordedAt, ContextGeneration: 1)],
            PendingFollowups = [new("operation", "runtime", true, recordedAt, recordedAt, InputId: "input", TurnId: turnId)],
            Turns = [new(turnId, 1, ["input"], AgentTurnStatus.Queued, RecordedAt: recordedAt, ContextGeneration: 1)],
        };
        return session;
    }

    private sealed class ObservedDbContextFactory(string connectionString, ImmediateTransactionProbe probe)
        : IDbContextFactory<MohistDbContext>, IDisposable
    {
        private readonly ConcurrentBag<SqliteConnection> _connections = [];
        private int _connectionCount;

        public MohistDbContext CreateDbContext()
        {
            var ordinal = Interlocked.Increment(ref _connectionCount);
            var connection = new ObservedSqliteConnection(connectionString, ordinal, probe);
            connection.DefaultTimeout = 0;
            _connections.Add(connection);
            var options = new DbContextOptionsBuilder<MohistDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(new OwnerReadInterceptor(ordinal, probe))
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;
            return new MohistDbContext(options);
        }

        public Task<MohistDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());

        public void Dispose()
        {
            foreach (var connection in _connections)
                connection.Dispose();
        }
    }

    private sealed class ObservedSqliteConnection(
        string connectionString,
        int ordinal,
        ImmediateTransactionProbe probe) : SqliteConnection(connectionString)
    {
        public override SqliteTransaction BeginTransaction(bool deferred)
        {
            probe.Record($"{ordinal}:begin-attempt");
            Assert.False(deferred);
            var transaction = base.BeginTransaction(deferred);
            probe.Record($"{ordinal}:write-owned");
            if (ordinal == 1)
            {
                probe.FirstWriteOwned.TrySetResult();
                probe.WaitForFirstRelease();
            }
            return transaction;
        }
    }

    private sealed class OwnerReadInterceptor(int ordinal, ImmediateTransactionProbe probe) : DbCommandInterceptor
    {
        private int _recorded;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            RecordOwnerRead(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            RecordOwnerRead(command);
            return ValueTask.FromResult(result);
        }

        private void RecordOwnerRead(DbCommand command)
        {
            if (Interlocked.CompareExchange(ref _recorded, 1, 0) == 0
                && (command.CommandText.Contains("AgentJobs", StringComparison.Ordinal)
                    || command.CommandText.Contains("AgentSessions", StringComparison.Ordinal)
                    || command.CommandText.Contains("Agents", StringComparison.Ordinal)))
            {
                probe.Record($"{ordinal}:owner-read");
            }
        }
    }

    private sealed class ImmediateTransactionProbe
    {
        private readonly object _sync = new();
        private readonly List<string> _events = [];
        private readonly ManualResetEventSlim _releaseFirst = new(false);

        public TaskCompletionSource FirstWriteOwned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContenderBeginAttempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> Events
        {
            get
            {
                lock (_sync)
                    return [.. _events];
            }
        }

        public void Record(string value)
        {
            lock (_sync)
                _events.Add(value);
            if (value == "2:begin-attempt")
                ContenderBeginAttempted.TrySetResult();
        }

        public void WaitForFirstRelease() => _releaseFirst.Wait();
        public void ReleaseFirst() => _releaseFirst.Set();
    }
}
