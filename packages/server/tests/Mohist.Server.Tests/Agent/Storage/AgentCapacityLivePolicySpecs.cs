using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Capacity;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.TestSupport;
using Mohist.Server.Tests.Support;
using Xunit;
using DomainAgent = Mohist.Server.Agent.Domain.Agent;

namespace Mohist.Server.Tests.Agent.Storage;

[Trait("level", "L0")]
public sealed class AgentCapacityLivePolicySpecs : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly TestSqliteDatabase _database = TestSqliteDatabase.CreateModelSchema();
    private readonly FakeTimeProvider _time = new(Now);
    private AgentCapacityStore Store => new(new TestDbContextFactory(_database.Options), _time);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task UnclaimedJob_ObservesLiveChangeFromFullToUnlimited()
    {
        await AddAgentAsync("project", "agent", 1);
        await AddJobAsync("occupied", "project", "agent", claimedAt: Now);
        var queued = await AddJobAsync("queued", "project", "agent");

        var full = await Store.ClaimJobAsync("queued", queued.Revision);
        Assert.Equal(AgentCapacityClaimDisposition.CapacityFull, full.Disposition);

        await SetLimitAsync("project", "agent", null);
        var claimed = await Store.ClaimJobAsync("queued", queued.Revision);
        Assert.Equal(AgentCapacityClaimDisposition.Claimed, claimed.Disposition);
    }

    [Fact]
    public async Task OlderProvisionalJob_IsIneligibleAndDoesNotBlockVisibleContender()
    {
        await AddAgentAsync("visibility", "agent", 1);
        var provisional = await AddJobAsync(
            "a-provisional",
            "visibility",
            "agent",
            submittedAt: Now.AddMinutes(-1),
            visibility: AgentLaunchVisibility.Provisional);
        var visible = await AddJobAsync(
            "z-visible",
            "visibility",
            "agent",
            submittedAt: Now,
            visibility: AgentLaunchVisibility.Visible);

        Assert.Equal(AgentCapacityClaimDisposition.NotEligible,
            (await Store.ClaimJobAsync("a-provisional", provisional.Revision)).Disposition);
        Assert.Equal(AgentCapacityClaimDisposition.Claimed,
            (await Store.ClaimJobAsync("z-visible", visible.Revision)).Disposition);
    }

    [Fact]
    public async Task StaleJobRevision_DoesNotOverwriteUnrelatedOwnerState()
    {
        await AddAgentAsync("project", "agent", 1);
        var queued = await AddJobAsync("queued", "project", "agent");
        await using (var db = _database.CreateContext())
        {
            var row = await db.AgentJobs.SingleAsync(candidate => candidate.JobKey == "queued");
            var state = JSON.Deserialize<AgentJobState>(row.State)!;
            state.FailureReason = "independent-update";
            row.State = JSON.Serialize(state);
            row.Revision++;
            await db.SaveChangesAsync();
        }

        var conflict = await Store.ClaimJobAsync("queued", queued.Revision);

        Assert.Equal(AgentCapacityClaimDisposition.Conflict, conflict.Disposition);
        await using var verification = _database.CreateContext();
        var persisted = JSON.Deserialize<AgentJobState>((await verification.AgentJobs.SingleAsync()).State)!;
        Assert.Equal("independent-update", persisted.FailureReason);
        Assert.Null(persisted.CapacityClaimedAt);
    }

    [Fact]
    public async Task Occupancy_IsIsolatedByBothProjectAndAgent()
    {
        await AddAgentAsync("project", "agent-a", 1);
        await AddAgentAsync("project", "agent-b", 1);
        await AddAgentAsync("other-project", "agent-a", 1);
        await AddJobAsync("occupied", "project", "agent-a", claimedAt: Now);
        var otherAgent = await AddJobAsync("other-agent", "project", "agent-b");
        var otherProject = await AddJobAsync("other-project", "other-project", "agent-a");

        Assert.Equal(AgentCapacityClaimDisposition.Claimed,
            (await Store.ClaimJobAsync("other-agent", otherAgent.Revision)).Disposition);
        Assert.Equal(AgentCapacityClaimDisposition.Claimed,
            (await Store.ClaimJobAsync("other-project", otherProject.Revision)).Disposition);
    }

    private async Task AddAgentAsync(string projectId, string agentId, int? limit)
    {
        var agent = new DomainAgent
        {
            Id = agentId,
            ProjectId = projectId,
            Name = agentId,
            MaxConcurrentRuns = limit,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        await using var db = _database.CreateContext();
        db.Agents.Add(new AgentRow { Id = GrainKey.Agent(projectId, agentId), State = AgentStore.Serialize(agent) });
        await db.SaveChangesAsync();
    }

    private async Task SetLimitAsync(string projectId, string agentId, int? limit)
    {
        await using var db = _database.CreateContext();
        var row = await db.Agents.FindAsync(GrainKey.Agent(projectId, agentId));
        var agent = AgentStore.Deserialize(row!.State)!;
        agent.MaxConcurrentRuns = limit;
        row.State = AgentStore.Serialize(agent);
        await db.SaveChangesAsync();
    }

    private async Task<AgentJobLedgerRecord> AddJobAsync(
        string key,
        string projectId,
        string agentId,
        DateTimeOffset? claimedAt = null,
        DateTimeOffset? submittedAt = null,
        AgentLaunchVisibility visibility = AgentLaunchVisibility.Visible)
    {
        var state = new AgentJobState
        {
            Input = new AgentJobInput("prompt", ProjectId: projectId, AgentId: agentId),
            SubmittedAt = submittedAt ?? Now,
            CapacityClaimedAt = claimedAt,
            LaunchVisibility = visibility,
        };
        var store = new AgentJobStore(
            new TestDbContextFactory(_database.Options),
            NullLogger<AgentJobStore>.Instance,
            _time);
        return await store.InsertLedgerAsync(new AgentJobLedgerRecord(
            key, JSON.Serialize(state), 0, null, null, null, null, null,
            "agent-job", "agent", key, projectId, null, null, null, null,
            LaunchVisibility: visibility.ToString().ToLowerInvariant()));
    }
}
