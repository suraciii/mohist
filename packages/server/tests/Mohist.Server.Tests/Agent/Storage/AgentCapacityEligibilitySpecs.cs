using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Agent;
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
public sealed class AgentCapacityEligibilitySpecs : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly TestSqliteDatabase _database = TestSqliteDatabase.CreateModelSchema();
    private AgentCapacityStore Store => new(
        new TestDbContextFactory(_database.Options),
        new FakeTimeProvider(Now));

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task MalformedOwnerDocument_MakesOccupancyIncompleteInsteadOfZero()
    {
        await AddAgentAsync("project", "agent");
        await using (var db = _database.CreateContext())
        {
            db.AgentSessions.Add(new AgentSessionRow
            {
                Id = "broken",
                State = """{"id":"broken","metadata":{"labels":{"mohist.io/project-id":"project","mohist.io/source-kind":"agent-launch","mohist.io/agent-id":"agent"}},"runtime":null}""",
            });
            await db.SaveChangesAsync();
        }

        var snapshot = (await Store.ReadAsync("project", ["agent"]))["agent"];

        Assert.Equal(AgentCapacityEvidenceStatus.IncompleteOwnerEvidence, snapshot.EvidenceStatus);
        Assert.Null(snapshot.Occupied);
        Assert.False(snapshot.IsUnlimited);
    }

    [Fact]
    public async Task UnknownExecution_AllowsOnlySpecializedManagerRecoveryHead()
    {
        await AddAgentAsync("project", "agent");
        await AddSessionAsync(NewUnknownWithQueued(
            "ordinary", "project", "agent", "turn", "api", provenance: null));
        await AddSessionAsync(NewUnknownWithQueued(
            "manager",
            BuiltInAgentCatalog.MohistSlackProjectId,
            $"builtin:{BuiltInAgentCatalog.MohistSlackName}",
            "manager-recovery-turn:job",
            "manager-recovery:runner-lost",
            new AgentSessionInputProvenance("slack", "workspace", "conversation", "thread", "member", "message", "connection", "thread")));

        var ordinary = (await Store.ReadAsync("project", ["agent"]))["agent"];
        var manager = (await Store.ReadAsync(
            BuiltInAgentCatalog.MohistSlackProjectId,
            [$"builtin:{BuiltInAgentCatalog.MohistSlackName}"]))[$"builtin:{BuiltInAgentCatalog.MohistSlackName}"];

        Assert.Empty(ordinary.Eligible);
        Assert.Equal("manager-recovery-turn:job", Assert.Single(manager.Eligible).TurnId);
    }

    private async Task AddAgentAsync(string projectId, string agentId)
    {
        var agent = new DomainAgent
        {
            Id = agentId,
            ProjectId = projectId,
            Name = agentId,
            MaxConcurrentRuns = null,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        await using var db = _database.CreateContext();
        db.Agents.Add(new AgentRow { Id = GrainKey.Agent(projectId, agentId), State = AgentStore.Serialize(agent) });
        await db.SaveChangesAsync();
    }

    private async Task AddSessionAsync(AgentSession session)
    {
        await using var db = _database.CreateContext();
        db.AgentSessions.Add(AgentSessionJson.ToRow(session, Now.UtcDateTime));
        await db.SaveChangesAsync();
    }

    private static AgentSession NewUnknownWithQueued(
        string id,
        string projectId,
        string agentId,
        string queuedTurnId,
        string queuedSource,
        AgentSessionInputProvenance? provenance)
    {
        var metadata = new AgentSessionMetadata()
            .WithLabel("mohist.io/project-id", projectId)
            .WithLabel("mohist.io/source-kind", "agent-launch")
            .WithLabel("mohist.io/agent-id", agentId);
        var session = AgentSession.Create(id, "runner", "/work", metadata, Now.UtcDateTime, "pi");
        session.Status = session.Status with
        {
            Activity = AgentSessionActivity.Active,
            Inputs = [new("recovery-input", 1, "inspect", queuedSource, AgentSessionInputAcceptance.Accepted,
                Now.UtcDateTime, Provenance: provenance)],
            Turns =
            [
                new("unknown", 1, ["initial"], AgentTurnStatus.Unknown, JobId: "job", RecordedAt: Now.UtcDateTime),
                new(queuedTurnId, 2, ["recovery-input"], AgentTurnStatus.Queued, RecordedAt: Now.UtcDateTime),
            ],
            PendingFollowups = [new($"system-turn:{queuedTurnId}", "runtime", Accepted: true,
                AcceptedAt: Now.UtcDateTime, StartedAt: Now.UtcDateTime, InputId: "recovery-input", TurnId: queuedTurnId)],
        };
        return session;
    }
}
