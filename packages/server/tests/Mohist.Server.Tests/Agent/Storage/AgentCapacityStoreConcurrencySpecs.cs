using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Capacity;
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
    public async Task ConcurrentJobAndTurnClaims_KeepDeterministicSingleOccupant()
    {
        await AddAgentAsync("mixed", "agent");
        var job = await AddJobAsync("job", "mixed", "agent");
        var (session, exact) = await AddQueuedSessionAsync("session", "turn", "mixed", "agent");

        var results = await Task.WhenAll(
            AttemptAsync(async () => (await Store.ClaimJobAsync("job", job.Revision)).Disposition),
            AttemptAsync(async () => (await Store.ClaimTurnAsync(session, exact, "turn")).Disposition));

        Assert.DoesNotContain(AgentCapacityClaimDisposition.Claimed, results.Skip(1));
        var snapshot = (await Store.ReadAsync("mixed", ["agent"]))["agent"];
        Assert.InRange(snapshot.Occupied!.Value, 0, 1);
        if (snapshot.Occupied == 0)
            Assert.Equal(AgentCapacityClaimDisposition.Claimed,
                (await Store.ClaimJobAsync("job", job.Revision)).Disposition);
    }

    [Fact]
    public async Task ConcurrentTurnClaims_KeepSessionIdTieOrderAndSingleOccupant()
    {
        await AddAgentAsync("turns", "agent");
        var (firstId, firstExact) = await AddQueuedSessionAsync("a-session", "turn-a", "turns", "agent");
        var (secondId, secondExact) = await AddQueuedSessionAsync("b-session", "turn-b", "turns", "agent");

        var results = await Task.WhenAll(
            AttemptAsync(async () => (await Store.ClaimTurnAsync(firstId, firstExact, "turn-a")).Disposition),
            AttemptAsync(async () => (await Store.ClaimTurnAsync(secondId, secondExact, "turn-b")).Disposition));

        Assert.DoesNotContain(AgentCapacityClaimDisposition.Claimed, results.Skip(1));
        var snapshot = (await Store.ReadAsync("turns", ["agent"]))["agent"];
        Assert.InRange(snapshot.Occupied!.Value, 0, 1);
        if (snapshot.Occupied == 0)
            Assert.Equal(AgentCapacityClaimDisposition.Claimed,
                (await Store.ClaimTurnAsync(firstId, firstExact, "turn-a")).Disposition);
    }

    private static async Task<AgentCapacityClaimDisposition?> AttemptAsync(
        Func<Task<AgentCapacityClaimDisposition>> action)
    {
        try { return await action(); }
        catch (Microsoft.Data.Sqlite.SqliteException) { return null; }
    }

    private async Task AddAgentAsync(string projectId, string agentId)
    {
        var agent = new DomainAgent
        {
            Id = agentId,
            ProjectId = projectId,
            Name = agentId,
            MaxConcurrentRuns = 1,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        await using var db = _database.CreateContext();
        db.Agents.Add(new AgentRow { Id = GrainKey.Agent(projectId, agentId), State = AgentStore.Serialize(agent) });
        await db.SaveChangesAsync();
    }

    private async Task<AgentJobLedgerRecord> AddJobAsync(string key, string projectId, string agentId)
    {
        var state = new AgentJobState
        {
            Input = new AgentJobInput("prompt", ProjectId: projectId, AgentId: agentId),
            SubmittedAt = Now,
        };
        var store = new AgentJobStore(
            new TestDbContextFactory(_database.Options),
            NullLogger<AgentJobStore>.Instance,
            _time);
        return await store.InsertLedgerAsync(new AgentJobLedgerRecord(
            key, JSON.Serialize(state), 0, null, null, Now, null, null,
            "agent-job", "agent", key, projectId, null, null, null, null));
    }

    private async Task<(string SessionId, string ExactState)> AddQueuedSessionAsync(
        string sessionId,
        string turnId,
        string projectId,
        string agentId)
    {
        var metadata = new AgentSessionMetadata()
            .WithLabel("mohist.io/project-id", projectId)
            .WithLabel("mohist.io/source-kind", "agent-launch")
            .WithLabel("mohist.io/agent-id", agentId);
        var session = AgentSession.Create(sessionId, "runner", "/work", metadata, Now.UtcDateTime, "pi");
        session.Status = session.Status with
        {
            Activity = AgentSessionActivity.Active,
            Inputs = [new("input", 1, "prompt", "api", AgentSessionInputAcceptance.Accepted, Now.UtcDateTime)],
            Turns = [new(turnId, 1, ["input"], AgentTurnStatus.Queued, RecordedAt: Now.UtcDateTime)],
            PendingFollowups = [new("operation", "runtime", Accepted: true, AcceptedAt: Now.UtcDateTime,
                StartedAt: Now.UtcDateTime, InputId: "input", TurnId: turnId)],
        };
        await using var db = _database.CreateContext();
        db.AgentSessions.Add(AgentSessionJson.ToRow(session, Now.UtcDateTime));
        await db.SaveChangesAsync();
        var exact = await db.AgentSessions.Where(row => row.Id == sessionId).Select(row => row.State).SingleAsync();
        return (sessionId, exact);
    }
}
