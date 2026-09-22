using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Capacity;
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
public sealed class AgentCapacityStoreSpecs : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly TestSqliteDatabase _database = TestSqliteDatabase.CreateModelSchema();
    private readonly FakeTimeProvider _time = new(Now);
    private AgentCapacityStore Store => new(new TestDbContextFactory(_database.Options), _time);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task ClaimJob_AtomicallyRestampsReadySinceAndRetryDoesNotRewriteOwner()
    {
        await AddAgentAsync("project", "agent", 1);
        var inserted = await AddJobAsync("job", "project", "agent", Now, readySince: Now.AddHours(-2));
        await using (var seedDb = _database.CreateContext())
        {
            var seedRow = await seedDb.AgentJobs.SingleAsync(candidate => candidate.JobKey == "job");
            seedRow.ReadySince = AgentJobStore.FormatTimestamp(Now.AddHours(-1));
            await seedDb.SaveChangesAsync();
        }

        var result = await Store.ClaimJobAsync("job", inserted.Revision);

        Assert.Equal(AgentCapacityClaimDisposition.Claimed, result.Disposition);
        Assert.Equal(inserted.Revision + 1, result.Job!.Revision);
        Assert.Equal(Now, result.Job.ReadySince);
        var state = JSON.Deserialize<AgentJobState>(result.Job.StateJson)!;
        Assert.Equal(Now, state.CapacityClaimedAt);
        Assert.Equal(Now, state.ReadySince);
        await using var db = _database.CreateContext();
        var row = await db.AgentJobs.SingleAsync(candidate => candidate.JobKey == "job");
        Assert.Equal(Now, AgentJobStore.ParseTimestamp(row.ReadySince));
        Assert.Equal(row.Revision, row.DirectApiProjectionRevision);
        Assert.NotNull(row.DirectApiProjectionJson);
        var projection = JSON.Deserialize<DirectApiAgentJobProjection>(row.DirectApiProjectionJson)!;
        Assert.Equal(Now, projection.ObservedAt);
        Assert.Equal(row.Revision, projection.SourceRevision);
        var committedState = row.State;
        var committedProjection = row.DirectApiProjectionJson;

        _time.Advance(TimeSpan.FromMinutes(5));
        var retry = await Store.ClaimJobAsync("job", inserted.Revision);

        Assert.Equal(AgentCapacityClaimDisposition.AlreadyClaimed, retry.Disposition);
        Assert.Equal(result.Job.Revision, retry.Job!.Revision);
        Assert.Equal(Now, retry.Job.ReadySince);
        await db.Entry(row).ReloadAsync();
        Assert.Equal(result.Job.Revision, row.Revision);
        Assert.Equal(Now, AgentJobStore.ParseTimestamp(row.ReadySince));
        Assert.Equal(committedState, row.State);
        Assert.Equal(committedProjection, row.DirectApiProjectionJson);
    }

    [Fact]
    public async Task ClaimedJobRetry_IsIdempotentAfterDefinitionDisappearsAndLimitDecreases()
    {
        await AddAgentAsync("project", "agent", 2);
        var inserted = await AddJobAsync("job", "project", "agent", Now);
        var claimed = await Store.ClaimJobAsync("job", inserted.Revision);
        await using (var db = _database.CreateContext())
        {
            var row = await db.Agents.SingleAsync();
            var agent = AgentStore.Deserialize(row.State)!;
            agent.MaxConcurrentRuns = 1;
            row.State = AgentStore.Serialize(agent);
            await db.SaveChangesAsync();
        }
        var lowerLimitRetry = await Store.ClaimJobAsync("job", inserted.Revision);
        Assert.Equal(AgentCapacityClaimDisposition.AlreadyClaimed, lowerLimitRetry.Disposition);

        await using (var db = _database.CreateContext())
        {
            db.Agents.Remove(await db.Agents.SingleAsync());
            await db.SaveChangesAsync();
        }
        var missingDefinitionRetry = await Store.ClaimJobAsync("job", inserted.Revision);

        Assert.Equal(AgentCapacityClaimDisposition.AlreadyClaimed, missingDefinitionRetry.Disposition);
        Assert.Equal(claimed.Job!.Revision, missingDefinitionRetry.Job!.Revision);
        Assert.Equal(AgentCapacityEvidenceStatus.MissingDefinition, missingDefinitionRetry.Capacity!.EvidenceStatus);
    }

    [Fact]
    public async Task Read_DerivesIntrinsicExecutionOccupancyWithoutCountingUnclaimedQueues()
    {
        await AddAgentAsync("project", "agent", 4);
        await AddJobAsync("running-job", "project", "agent", Now, AgentJobStatus.Running);
        await AddJobAsync("unknown-job", "project", "agent", Now.AddSeconds(1), AgentJobStatus.Unknown);
        var pending = await AddJobAsync("pending-job", "project", "agent", Now.AddSeconds(2));
        await AddSessionAsync(NewSession("executing", "project", "agent", AgentSessionActivity.Idle,
            [Turn("executing", 1, AgentTurnStatus.Executing, generation: 1)]));
        await AddSessionAsync(NewSession("unknown", "project", "agent", AgentSessionActivity.Idle,
            [Turn("unknown", 1, AgentTurnStatus.Unknown, generation: 1)]));
        await AddSessionAsync(NewSession("excluded", "project", "agent", AgentSessionActivity.Active,
        [
            Turn("queued-unclaimed", 1, AgentTurnStatus.Queued, generation: 1),
            Turn("initial", 2, AgentTurnStatus.Executing, generation: 1, jobId: "job"),
            Turn("old", 3, AgentTurnStatus.Executing, generation: 0),
            Turn("superseded", 4, AgentTurnStatus.Unknown, generation: 1, supersededAt: Now.UtcDateTime),
            Turn("terminal", 5, AgentTurnStatus.Completed, generation: 1, claimedAt: Now),
        ]));

        var snapshot = (await Store.ReadAsync("project", ["agent"]))["agent"];
        var blocked = await Store.ClaimJobAsync("pending-job", pending.Revision);

        Assert.Equal(AgentCapacityEvidenceStatus.Complete, snapshot.EvidenceStatus);
        Assert.Equal(4, snapshot.Occupied);
        Assert.Equal(AgentCapacityClaimDisposition.CapacityFull, blocked.Disposition);
    }

    [Fact]
    public async Task FiniteCapacity_UsesEligibleFifoWithJobBeforeTurnAtEqualTime()
    {
        await AddAgentAsync("project", "agent", 1);
        var job = await AddJobAsync("job", "project", "agent", Now);
        var session = NewQueuedSession("session", "turn", "project", "agent", Now.UtcDateTime);
        var exact = await AddSessionAsync(session);

        var earlyTurn = await Store.ClaimTurnAsync("session", exact, "turn");
        Assert.Equal(AgentCapacityClaimDisposition.NotInOrder, earlyTurn.Disposition);
        var jobClaim = await Store.ClaimJobAsync("job", job.Revision);
        Assert.Equal(AgentCapacityClaimDisposition.Claimed, jobClaim.Disposition);
        var full = await Store.ClaimTurnAsync("session", exact, "turn");
        Assert.Equal(AgentCapacityClaimDisposition.CapacityFull, full.Disposition);

        await SetJobTerminalAsync("job");
        var turnClaim = await Store.ClaimTurnAsync("session", exact, "turn");
        Assert.Equal(AgentCapacityClaimDisposition.Claimed, turnClaim.Disposition);
    }

    [Fact]
    public async Task ClaimTurn_RequiresExactDocumentAndReturnsCompleteCommittedSession()
    {
        await AddAgentAsync("project", "agent", 1);
        var session = NewQueuedSession("session", "turn", "project", "agent", Now.UtcDateTime);
        session.Runtime = new AgentSessionRuntime("runner", "/work", "pi");
        session.Status = session.Status with
        {
            AgentRuntimeSessionId = "runtime-session",
            PendingTranscriptEvidence = [new("evidence", "runtime-session", "text", "{}", Now.UtcDateTime, "followup")],
        };
        var exact = await AddSessionAsync(session);

        var conflict = await Store.ClaimTurnAsync("session", exact + " ", "turn");
        Assert.Equal(AgentCapacityClaimDisposition.Conflict, conflict.Disposition);
        var claimed = await Store.ClaimTurnAsync("session", exact, "turn");

        Assert.Equal(AgentCapacityClaimDisposition.Claimed, claimed.Disposition);
        Assert.Equal("runtime-session", claimed.Session!.Status.AgentRuntimeSessionId);
        Assert.Equal("/work", claimed.Session.Runtime.WorkDir);
        Assert.Single(claimed.Session.Status.Inputs!);
        Assert.Single(claimed.Session.Status.PendingTranscriptEvidence!);
        Assert.Equal(Now, Assert.Single(claimed.Session.Status.Turns!).CapacityClaimedAt);
    }

    [Fact]
    public async Task ClaimTurn_OnWorkflowSessionWithoutAgentIdentity_ReturnsIncompleteWithoutWrite()
    {
        await AddAgentAsync("project", "agent", 1);
        var session = NewQueuedSession("session", "turn", "project", "agent", Now.UtcDateTime);
        session.Metadata = new AgentSessionMetadata()
            .WithLabel("mohist.io/project-id", "project")
            .WithLabel("mohist.io/source-kind", "workflow")
            .WithLabel("mohist.io/source-id", "workflow-run-1")
            .WithLabel("mohist.io/session-name", "build")
            .WithLabel("mohist.io/agent-id", "agent");
        var row = AgentSessionJson.ToRow(session, Now.UtcDateTime);
        var state = JsonNode.Parse(row.State)!.AsObject();
        // Persisted Workflow owner evidence missing the canonical agent
        // label: incomplete, never guessed into an attribution. The strip is
        // written straight into the row document, never through the creation
        // API, and the document must stay structurally readable so the claim
        // is refused for its blank identity, not for an unreadable row.
        state["metadata"]!["labels"]!.AsObject().Remove("mohist.io/agent-id");
        row.State = state.ToJsonString();
        await using (var db = _database.CreateContext())
        {
            db.AgentSessions.Add(row);
            await db.SaveChangesAsync();
        }

        string incompleteState;
        await using (var readDb = _database.CreateContext())
        {
            var persisted = await readDb.AgentSessions
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == "session");
            incompleteState = persisted.State;
            var readable = AgentSessionJson.Deserialize(persisted);
            Assert.NotNull(readable);
            Assert.Null(readable!.Metadata.Label("mohist.io/agent-id"));
            Assert.Equal("workflow", readable.Metadata.Label("mohist.io/source-kind"));
            Assert.Equal("project", readable.Metadata.Label("mohist.io/project-id"));
            Assert.Equal("workflow-run-1", readable.Metadata.Label("mohist.io/source-id"));
            Assert.Equal("build", readable.Metadata.Label("mohist.io/session-name"));
            var input = Assert.Single(readable.Status.Inputs!);
            Assert.Equal(AgentSessionInputAcceptance.Accepted, input.Acceptance);
            var turn = Assert.Single(readable.Status.Turns!);
            Assert.Equal("turn", turn.Id);
            Assert.Equal(AgentTurnStatus.Queued, turn.Status);
            Assert.Equal("project", persisted.LabelProjectId);
            Assert.Equal("workflow", persisted.LabelSourceKind);
            Assert.Null(persisted.LabelAgentId);
        }

        var claim = await Store.ClaimTurnAsync("session", incompleteState, "turn");

        Assert.Equal(AgentCapacityClaimDisposition.Incomplete, claim.Disposition);
        Assert.Null(claim.Capacity);
        await using var verifyDb = _database.CreateContext();
        var after = await verifyDb.AgentSessions
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == "session");
        Assert.Equal(incompleteState, after.State);
        Assert.Equal("project", after.LabelProjectId);
        Assert.Equal("workflow", after.LabelSourceKind);
        Assert.Null(after.LabelAgentId);
    }

    [Fact]
    public async Task MissingMalformedAndArbitraryBuiltinDefinitions_FailClosed()
    {
        await AddJobAsync("missing", "project", "missing-agent", Now);
        await AddJobAsync("prefix", "project", "builtin:not-real", Now.AddMinutes(1));
        await using (var db = _database.CreateContext())
        {
            db.Agents.Add(new AgentRow { Id = GrainKey.Agent("project", "bad"), State = "{}" });
            await db.SaveChangesAsync();
        }

        var snapshots = await Store.ReadAsync("project", ["missing-agent", "builtin:not-real", "bad", "builtin:mohist/planner"]);

        Assert.Equal(AgentCapacityEvidenceStatus.MissingDefinition, snapshots["missing-agent"].EvidenceStatus);
        Assert.Equal(AgentCapacityEvidenceStatus.MissingDefinition, snapshots["builtin:not-real"].EvidenceStatus);
        Assert.Equal(AgentCapacityEvidenceStatus.MalformedDefinition, snapshots["bad"].EvidenceStatus);
        Assert.True(snapshots["builtin:mohist/planner"].IsUnlimited);
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

    private async Task<AgentJobLedgerRecord> AddJobAsync(
        string key,
        string projectId,
        string agentId,
        DateTimeOffset submittedAt,
        AgentJobStatus status = AgentJobStatus.Pending,
        DateTimeOffset? claimedAt = null,
        DateTimeOffset? readySince = null)
    {
        var state = new AgentJobState
        {
            Status = status,
            Input = new AgentJobInput("prompt", ProjectId: projectId, AgentId: agentId),
            SubmittedAt = submittedAt,
            CapacityClaimedAt = claimedAt,
            ReadySince = readySince,
        };
        var store = new AgentJobStore(
            new TestDbContextFactory(_database.Options),
            NullLogger<AgentJobStore>.Instance,
            _time);
        return await store.InsertLedgerAsync(new AgentJobLedgerRecord(
            key, JSON.Serialize(state), 0, null, null, readySince, null, null,
            "agent-job", "agent", key, projectId, null, null, null, null));
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

    private async Task SetJobTerminalAsync(string key)
    {
        await using var db = _database.CreateContext();
        var row = await db.AgentJobs.SingleAsync(candidate => candidate.JobKey == key);
        var state = JSON.Deserialize<AgentJobState>(row.State)!;
        state.Status = AgentJobStatus.Completed;
        state.TerminalAt = Now.AddMinutes(1);
        row.State = JSON.Serialize(state);
        row.Revision++;
        await db.SaveChangesAsync();
    }

    private static AgentSession NewQueuedSession(
        string sessionId,
        string turnId,
        string projectId,
        string agentId,
        DateTime recordedAt)
    {
        var session = NewSession(sessionId, projectId, agentId, AgentSessionActivity.Active,
            [Turn(turnId, 1, AgentTurnStatus.Queued, 1, recordedAt: recordedAt)]);
        session.Status = session.Status with
        {
            Inputs = [new("input", 1, "prompt", "api", AgentSessionInputAcceptance.Accepted, recordedAt, ContextGeneration: 1)],
            PendingFollowups = [new("operation", "runtime", Accepted: true, AcceptedAt: recordedAt, StartedAt: recordedAt, InputId: "input", TurnId: turnId)],
        };
        return session;
    }

    private static AgentSession NewSession(
        string sessionId,
        string projectId,
        string agentId,
        AgentSessionActivity activity,
        IReadOnlyList<AgentTurnRecord> turns)
    {
        var metadata = new AgentSessionMetadata()
            .WithLabel("mohist.io/project-id", projectId)
            .WithLabel("mohist.io/source-kind", "agent-launch")
            .WithLabel("mohist.io/agent-id", agentId);
        var session = AgentSession.Create(sessionId, "runner", "/work", metadata, Now.UtcDateTime, "pi");
        session.Status = session.Status with { Activity = activity, Turns = turns, ContextGeneration = 1 };
        return session;
    }

    private static AgentTurnRecord Turn(
        string id,
        long sequence,
        AgentTurnStatus status,
        long generation,
        DateTimeOffset? claimedAt = null,
        string? jobId = null,
        DateTime? supersededAt = null,
        DateTime? recordedAt = null) =>
        new(id, sequence, ["input"], status, jobId, RecordedAt: recordedAt ?? Now.UtcDateTime,
            ContextGeneration: generation, SupersededAt: supersededAt, CapacityClaimedAt: claimedAt);
}
