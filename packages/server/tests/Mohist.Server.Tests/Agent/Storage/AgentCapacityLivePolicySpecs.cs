using System.Data.Common;
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
    public async Task ReadJobHistory_FiltersTerminalRowsButRetainsUnknownOwnerEvidence()
    {
        await AddAgentAsync("history", "bounded", 1);
        await AddAgentAsync("history", "unknown", 1);
        await AddJobAsync("eligible", "history", "bounded");
        await AddMalformedTerminalHistoryAsync("history", "bounded", 200);
        await AddUnknownStatusRowsAsync("history", "unknown");
        var recorder = new JobSelectRecorder();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_database.ConnectionString)
            .AddInterceptors(recorder)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        var store = new AgentCapacityStore(new TestDbContextFactory(options), _time);

        var snapshots = await store.ReadAsync("history", ["bounded", "unknown"]);

        Assert.Equal(AgentCapacityEvidenceStatus.Complete, snapshots["bounded"].EvidenceStatus);
        Assert.Equal(0, snapshots["bounded"].Occupied);
        Assert.Single(snapshots["bounded"].Eligible);
        Assert.Equal(AgentCapacityEvidenceStatus.IncompleteOwnerEvidence, snapshots["unknown"].EvidenceStatus);
        Assert.Null(snapshots["unknown"].Occupied);
        Assert.Contains("Status", recorder.JobSelect, StringComparison.Ordinal);
        Assert.Contains("completed", recorder.JobSelect, StringComparison.Ordinal);
        Assert.Contains("failed", recorder.JobSelect, StringComparison.Ordinal);
        Assert.Contains("cancelled", recorder.JobSelect, StringComparison.Ordinal);
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

    private async Task AddMalformedTerminalHistoryAsync(string projectId, string agentId, int count)
    {
        var terminal = new AgentJobState
        {
            Status = AgentJobStatus.Completed,
            Input = new AgentJobInput("prompt", ProjectId: projectId, AgentId: agentId),
            SubmittedAt = Now,
            TerminalAt = Now,
            FailureReason = "malformed-marker",
        };
        var malformed = JSON.Serialize(terminal).Replace(
            "\"failureReason\":\"malformed-marker\"",
            "\"failureReason\":42",
            StringComparison.Ordinal);
        Assert.Contains("\"failureReason\":42", malformed, StringComparison.Ordinal);
        await using var db = _database.CreateContext();
        for (var index = 0; index < count; index++)
        {
            db.AgentJobs.Add(new AgentJobRow
            {
                JobKey = $"terminal-{index}",
                State = malformed,
                Revision = 1,
            });
        }
        await db.SaveChangesAsync();
    }

    private async Task AddUnknownStatusRowsAsync(string projectId, string agentId)
    {
        var pending = new AgentJobState
        {
            Status = AgentJobStatus.Pending,
            Input = new AgentJobInput("prompt", ProjectId: projectId, AgentId: agentId),
            SubmittedAt = Now,
        };
        var valid = JSON.Serialize(pending);
        var invalid = valid.Replace("\"status\":\"pending\"", "\"status\":\"unexpected\"", StringComparison.Ordinal);
        var missing = valid.Replace("\"status\":\"pending\",", string.Empty, StringComparison.Ordinal);
        Assert.NotEqual(valid, invalid);
        Assert.NotEqual(valid, missing);
        await using var db = _database.CreateContext();
        db.AgentJobs.AddRange(
            new AgentJobRow { JobKey = "invalid-status", State = invalid, Revision = 1 },
            new AgentJobRow { JobKey = "missing-status", State = missing, Revision = 1 });
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

    private sealed class JobSelectRecorder : DbCommandInterceptor
    {
        public string JobSelect { get; private set; } = string.Empty;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"AgentJobs\"", StringComparison.Ordinal))
                JobSelect = command.CommandText;
            return ValueTask.FromResult(result);
        }
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
