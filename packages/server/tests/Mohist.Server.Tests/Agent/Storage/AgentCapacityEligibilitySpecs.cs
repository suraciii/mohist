using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Capacity;
using Mohist.Server.Infrastructure.Data.AgentJobs;
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
    private readonly FakeTimeProvider _time = new(Now);
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
    public async Task UnownedWorkflowSession_DoesNotPoisonAgentCapacityEvidence()
    {
        await AddAgentAsync("project", "agent");
        await using (var db = _database.CreateContext())
        {
            db.AgentSessions.Add(new AgentSessionRow
            {
                Id = "legacy-workflow",
                State = """{"Id":"legacy-workflow","Metadata":{"Labels":{"mohist.io/project-id":"project","mohist.io/source-kind":"workflow"}},"Runtime":{"RunnerId":"runner","AgentRuntime":"opencode"},"Settings":{},"Status":{"Phase":"Created","CreatedAt":"2026-09-21T12:00:00Z","UsageSummary":{}}}""",
            });
            await db.SaveChangesAsync();
        }

        var snapshot = (await Store.ReadAsync("project", ["agent"]))["agent"];

        Assert.Equal(AgentCapacityEvidenceStatus.Complete, snapshot.EvidenceStatus);
        Assert.Equal(0, snapshot.Occupied);
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

    [Fact]
    public async Task ManagerRecoveryHead_ClaimsBesideTheUnresolvedJobOccupancy()
    {
        var agentId = $"builtin:{BuiltInAgentCatalog.MohistSlackName}";
        var exact = await AddSessionAsync(NewUnknownWithQueued(
            "manager",
            BuiltInAgentCatalog.MohistSlackProjectId,
            agentId,
            "manager-recovery-turn:job",
            "manager-recovery:runner-lost",
            new AgentSessionInputProvenance("slack", "workspace", "conversation", "thread", "member", "message", "connection", "thread")));
        // The unresolved original Job keeps occupying its own slot while the
        // inspection Turn claims its own beside it.
        var unresolved = await AddJobAsync(
            "unknown-job",
            BuiltInAgentCatalog.MohistSlackProjectId,
            agentId,
            Now,
            status: AgentJobStatus.Running);
        var snapshot = (await Store.ReadAsync(
            BuiltInAgentCatalog.MohistSlackProjectId,
            [agentId]))[agentId];

        Assert.Equal(AgentCapacityEvidenceStatus.Complete, snapshot.EvidenceStatus);
        Assert.Equal(1, snapshot.Occupied);
        var claim = await Store.ClaimTurnAsync("manager", exact, "manager-recovery-turn:job");

        Assert.Equal(AgentCapacityClaimDisposition.Claimed, claim.Disposition);
        await using var db = _database.CreateContext();
        var jobState = JSON.Deserialize<AgentJobState>(
            (await db.AgentJobs.SingleAsync(row => row.JobKey == unresolved.JobKey)).State)!;
        Assert.Equal(AgentJobStatus.Running, jobState.Status);
    }

    [Fact]
    public async Task OrdinaryHeadBeforeJobTurn_OnlyTheOrdinaryHeadCompetesUntilTheJobSettles()
    {
        await AddAgentAsync("project", "agent", 1);
        var session = NewLocalOrderSession("session", "project", "agent", Now.UtcDateTime,
            ("ordinary-input", "ordinary-turn", null),
            ("job-input", "job-turn", "job"));
        var exact = await AddSessionAsync(session);
        var job = await AddJobAsync(
            "job", "project", "agent", Now.AddMinutes(1),
            agentSessionId: "session", initialInputId: "job-input", initialTurnId: "job-turn");

        var snapshot = (await Store.ReadAsync("project", ["agent"]))["agent"];
        Assert.Equal(AgentCapacityEvidenceStatus.Complete, snapshot.EvidenceStatus);
        var entry = Assert.Single(snapshot.Eligible);
        Assert.Equal(AgentCapacityOwnerKind.Turn, entry.Kind);
        Assert.Equal("ordinary-turn", entry.TurnId);

        // Local ineligibility is order evidence, not a capacity conclusion.
        var blocked = await Store.ClaimJobAsync("job", job.Revision);
        Assert.Equal(AgentCapacityClaimDisposition.NotEligible, blocked.Disposition);
        await AssertJobClaimUntouchedAsync("job");

        Assert.Equal(AgentCapacityClaimDisposition.Claimed,
            (await Store.ClaimTurnAsync("session", exact, "ordinary-turn")).Disposition);
        // The claimed head is still the Turn that may advance, so the Job
        // stays locally behind it rather than reading the occupied slot as
        // its own verdict.
        Assert.Equal(AgentCapacityClaimDisposition.NotEligible,
            (await Store.ClaimJobAsync("job", job.Revision)).Disposition);
        await AssertJobClaimUntouchedAsync("job");

        await SetTurnTerminalAsync("session", "ordinary-turn");
        Assert.Equal(AgentCapacityClaimDisposition.Claimed,
            (await Store.ClaimJobAsync("job", job.Revision)).Disposition);
        await using var db = _database.CreateContext();
        var state = JSON.Deserialize<AgentJobState>(
            (await db.AgentJobs.SingleAsync(row => row.JobKey == "job")).State)!;
        Assert.Equal(Now, state.CapacityClaimedAt);
        Assert.Equal(Now, state.ReadySince);
    }

    [Fact]
    public async Task JobTurnBeforeOrdinaryHead_OnlyTheJobCompetesUntilItSettles()
    {
        await AddAgentAsync("project", "agent", 1);
        var session = NewLocalOrderSession("session", "project", "agent", Now.UtcDateTime,
            ("job-input", "job-turn", "job"),
            ("ordinary-input", "ordinary-turn", null));
        var exact = await AddSessionAsync(session);
        var job = await AddJobAsync(
            "job", "project", "agent", Now,
            agentSessionId: "session", initialInputId: "job-input", initialTurnId: "job-turn");

        var snapshot = (await Store.ReadAsync("project", ["agent"]))["agent"];
        var entry = Assert.Single(snapshot.Eligible);
        Assert.Equal(AgentCapacityOwnerKind.Job, entry.Kind);
        Assert.Equal("job", entry.OwnerId);

        Assert.Equal(AgentCapacityClaimDisposition.NotEligible,
            (await Store.ClaimTurnAsync("session", exact, "ordinary-turn")).Disposition);
        Assert.Equal(AgentCapacityClaimDisposition.Claimed,
            (await Store.ClaimJobAsync("job", job.Revision)).Disposition);
        // The Job claim does not move the Session's own Turn: the Job-owned
        // Turn stays the local head, so the later ordinary Turn stays out of
        // order rather than reading the occupied slot as its own verdict.
        Assert.Equal(AgentCapacityClaimDisposition.NotEligible,
            (await Store.ClaimTurnAsync("session", exact, "ordinary-turn")).Disposition);

        await SetJobTerminalAsync("job");
        await SetTurnTerminalAsync("session", "job-turn");
        var settled = await SessionStateAsync("session");
        Assert.Equal(AgentCapacityClaimDisposition.Claimed,
            (await Store.ClaimTurnAsync("session", settled, "ordinary-turn")).Disposition);
    }

    [Fact]
    public async Task UnlimitedLimit_StillHonorsSessionLocalOrderInBothDirections()
    {
        await AddAgentAsync("project", "agent", null);
        var ordinaryFirst = await AddSessionAsync(NewLocalOrderSession("session-a", "project", "agent", Now.UtcDateTime,
            ("ordinary-input", "ordinary-turn", null),
            ("job-input", "job-turn", "job")));
        var jobBehindHead = await AddJobAsync(
            "job-a", "project", "agent", Now.AddMinutes(1),
            agentSessionId: "session-a", initialInputId: "job-input", initialTurnId: "job-turn");
        var jobFirst = await AddSessionAsync(NewLocalOrderSession("session-b", "project", "agent", Now.UtcDateTime,
            ("job-input", "job-turn", "job-b"),
            ("ordinary-input", "ordinary-turn", null)));
        var jobAhead = await AddJobAsync(
            "job-b", "project", "agent", Now,
            agentSessionId: "session-b", initialInputId: "job-input", initialTurnId: "job-turn");

        Assert.Equal(AgentCapacityClaimDisposition.NotEligible,
            (await Store.ClaimJobAsync("job-a", jobBehindHead.Revision)).Disposition);
        await AssertJobClaimUntouchedAsync("job-a");
        Assert.Equal(AgentCapacityClaimDisposition.Claimed,
            (await Store.ClaimTurnAsync("session-a", ordinaryFirst, "ordinary-turn")).Disposition);

        Assert.Equal(AgentCapacityClaimDisposition.NotEligible,
            (await Store.ClaimTurnAsync("session-b", jobFirst, "ordinary-turn")).Disposition);
        Assert.Equal(AgentCapacityClaimDisposition.Claimed,
            (await Store.ClaimJobAsync("job-b", jobAhead.Revision)).Disposition);
    }

    [Fact]
    public async Task StopAndResetFences_BlockBothTheOrdinaryHeadAndTheJob()
    {
        await AddAgentAsync("project", "agent", null);
        var stopped = NewLocalOrderSession("stopped", "project", "agent", Now.UtcDateTime,
            ("ordinary-input", "ordinary-turn", null));
        stopped.Status = stopped.Status with
        {
            PendingStop = new AgentSessionStopClaim("ordinary-turn", "stop-operation"),
        };
        var stoppedExact = await AddSessionAsync(stopped);
        var reset = NewLocalOrderSession("reset", "project", "agent", Now.UtcDateTime,
            ("job-input", "job-turn", "job"),
            ("ordinary-input", "ordinary-turn", null));
        reset.Status = reset.Status with
        {
            PendingReset = new AgentSessionResetReservation("reset-operation", null, "pi", Now.UtcDateTime),
        };
        var resetExact = await AddSessionAsync(reset);
        var job = await AddJobAsync(
            "job", "project", "agent", Now,
            agentSessionId: "reset", initialInputId: "job-input", initialTurnId: "job-turn");

        var snapshot = (await Store.ReadAsync("project", ["agent"]))["agent"];

        Assert.Empty(snapshot.Eligible);
        Assert.Equal(AgentCapacityClaimDisposition.NotEligible,
            (await Store.ClaimTurnAsync("stopped", stoppedExact, "ordinary-turn")).Disposition);
        Assert.Equal(AgentCapacityClaimDisposition.NotEligible,
            (await Store.ClaimTurnAsync("reset", resetExact, "ordinary-turn")).Disposition);
        Assert.Equal(AgentCapacityClaimDisposition.NotEligible,
            (await Store.ClaimJobAsync("job", job.Revision)).Disposition);
        await AssertJobClaimUntouchedAsync("job");
    }

    [Fact]
    public async Task JobReferencedMissingOrMalformedSession_FailsClosedAsIncomplete()
    {
        await AddAgentAsync("project", "agent", 1);
        var ghost = await AddJobAsync(
            "ghost-job", "project", "agent", Now,
            agentSessionId: "ghost", initialInputId: "job-input", initialTurnId: "job-turn");
        await using (var db = _database.CreateContext())
        {
            db.AgentSessions.Add(new AgentSessionRow
            {
                Id = "broken",
                State = """{"id":"broken","metadata":{"labels":{"mohist.io/project-id":"project","mohist.io/source-kind":"agent-launch","mohist.io/agent-id":"agent"}},"runtime":null}""",
            });
            await db.SaveChangesAsync();
        }
        var broken = await AddJobAsync(
            "broken-job", "project", "agent", Now.AddMinutes(1),
            agentSessionId: "broken", initialInputId: "job-input", initialTurnId: "job-turn");

        var snapshot = (await Store.ReadAsync("project", ["agent"]))["agent"];

        Assert.Equal(AgentCapacityEvidenceStatus.IncompleteOwnerEvidence, snapshot.EvidenceStatus);
        Assert.Null(snapshot.Occupied);
        Assert.Empty(snapshot.Eligible);
        Assert.Equal(AgentCapacityClaimDisposition.Incomplete,
            (await Store.ClaimJobAsync("ghost-job", ghost.Revision)).Disposition);
        Assert.Equal(AgentCapacityClaimDisposition.Incomplete,
            (await Store.ClaimJobAsync("broken-job", broken.Revision)).Disposition);
        await AssertJobClaimUntouchedAsync("ghost-job");
        await AssertJobClaimUntouchedAsync("broken-job");
    }

    [Fact]
    public async Task JobReferencedSessionWithoutAgentLabel_IsIncompleteEvidenceNotInvisible()
    {
        await AddAgentAsync("project", "agent", 1);
        var session = NewLocalOrderSession("unlabeled", "project", "agent", Now.UtcDateTime,
            ("job-input", "job-turn", "job"));
        var row = AgentSessionJson.ToRow(session, Now.UtcDateTime);
        var state = JsonNode.Parse(row.State)!.AsObject();
        state["metadata"]!["labels"]!.AsObject().Remove("mohist.io/agent-id");
        row.State = state.ToJsonString();
        await using (var db = _database.CreateContext())
        {
            db.AgentSessions.Add(row);
            await db.SaveChangesAsync();
        }
        var job = await AddJobAsync(
            "job", "project", "agent", Now,
            agentSessionId: "unlabeled", initialInputId: "job-input", initialTurnId: "job-turn");

        var snapshot = (await Store.ReadAsync("project", ["agent"]))["agent"];

        Assert.Equal(AgentCapacityEvidenceStatus.IncompleteOwnerEvidence, snapshot.EvidenceStatus);
        Assert.Null(snapshot.Occupied);
        Assert.Equal(AgentCapacityClaimDisposition.Incomplete,
            (await Store.ClaimJobAsync("job", job.Revision)).Disposition);
        await AssertJobClaimUntouchedAsync("job");
    }

    [Fact]
    public async Task JobReferencedSessionWithContradictoryIdentity_IsIncompleteEvidence()
    {
        await AddAgentAsync("project", "agent", 1);
        await AddSessionAsync(NewLocalOrderSession("foreign", "project", "other-agent", Now.UtcDateTime,
            ("job-input", "job-turn", "job")));
        var job = await AddJobAsync(
            "job", "project", "agent", Now,
            agentSessionId: "foreign", initialInputId: "job-input", initialTurnId: "job-turn");

        var snapshot = (await Store.ReadAsync("project", ["agent"]))["agent"];

        Assert.Equal(AgentCapacityEvidenceStatus.IncompleteOwnerEvidence, snapshot.EvidenceStatus);
        Assert.Null(snapshot.Occupied);
        Assert.Equal(AgentCapacityClaimDisposition.Incomplete,
            (await Store.ClaimJobAsync("job", job.Revision)).Disposition);
        await AssertJobClaimUntouchedAsync("job");
    }

    [Fact]
    public async Task DefinitiveReferenceMismatches_AreNotEligibleAndNeverFull()
    {
        await AddAgentAsync("project", "agent", 1);
        await AddSessionAsync(NewLocalOrderSession("absent", "project", "agent", Now.UtcDateTime,
            ("job-input", "job-turn", "other-job")));
        var foreign = await AddJobAsync(
            "foreign", "project", "agent", Now,
            agentSessionId: "absent", initialInputId: "job-input", initialTurnId: "job-turn");

        var foreignClaim = await Store.ClaimJobAsync("foreign", foreign.Revision);
        Assert.Equal(AgentCapacityClaimDisposition.NotEligible, foreignClaim.Disposition);
        Assert.Equal(AgentCapacityEvidenceStatus.Complete, foreignClaim.Capacity!.EvidenceStatus);
        Assert.Equal(0, foreignClaim.Capacity.Occupied);
        await AssertJobClaimUntouchedAsync("foreign");

        await AddSessionAsync(NewLocalOrderSession("turnless", "project", "agent", Now.UtcDateTime,
            ("ordinary-input", "ordinary-turn", null)));
        var turnless = await AddJobAsync(
            "turnless", "project", "agent", Now.AddMinutes(1),
            agentSessionId: "turnless", initialInputId: "job-input", initialTurnId: "job-turn");

        Assert.Equal(AgentCapacityClaimDisposition.NotEligible,
            (await Store.ClaimJobAsync("turnless", turnless.Revision)).Disposition);
        await AssertJobClaimUntouchedAsync("turnless");
    }

    [Fact]
    public async Task BlankJobReferenceTuple_IsIncompleteNotUnconstrained()
    {
        await AddAgentAsync("project", "agent", 1);
        await AddSessionAsync(NewLocalOrderSession("session", "project", "agent", Now.UtcDateTime,
            ("job-input", "job-turn", "job")));
        var job = await AddJobAsync(
            "job", "project", "agent", Now,
            agentSessionId: "session", initialInputId: "job-input");

        Assert.Equal(AgentCapacityClaimDisposition.Incomplete,
            (await Store.ClaimJobAsync("job", job.Revision)).Disposition);
        await AssertJobClaimUntouchedAsync("job");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task BlankSessionIdReference_IsIncompleteNotSessionless(string sessionId)
    {
        await AddAgentAsync("project", "agent", 1);
        var job = await AddJobAsync("job", "project", "agent", Now, agentSessionId: sessionId);

        var snapshot = (await Store.ReadAsync("project", ["agent"]))["agent"];
        Assert.Equal(AgentCapacityEvidenceStatus.IncompleteOwnerEvidence, snapshot.EvidenceStatus);
        Assert.Null(snapshot.Occupied);
        Assert.Equal(AgentCapacityClaimDisposition.Incomplete,
            (await Store.ClaimJobAsync("job", job.Revision)).Disposition);
        await AssertJobClaimUntouchedAsync("job");
    }

    [Fact]
    public async Task NullSessionIdReference_RemainsDirectJob()
    {
        await AddAgentAsync("project", "agent", 1);
        var job = await AddJobAsync("job", "project", "agent", Now);

        Assert.Equal(AgentCapacityClaimDisposition.Claimed,
            (await Store.ClaimJobAsync("job", job.Revision)).Disposition);
    }

    private async Task AddAgentAsync(string projectId, string agentId, int? limit = null)
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
        string? agentSessionId = null,
        string? initialInputId = null,
        string? initialTurnId = null,
        AgentJobStatus status = AgentJobStatus.Pending)
    {
        var state = new AgentJobState
        {
            Status = status,
            Input = new AgentJobInput(
                "prompt",
                ProjectId: projectId,
                AgentId: agentId,
                AgentSessionId: agentSessionId,
                InitialInputId: initialInputId,
                InitialTurnId: initialTurnId),
            SubmittedAt = submittedAt,
        };
        var store = new AgentJobStore(
            new TestDbContextFactory(_database.Options),
            NullLogger<AgentJobStore>.Instance,
            _time);
        return await store.InsertLedgerAsync(new AgentJobLedgerRecord(
            key, JSON.Serialize(state), 0, null, null, null, null, null,
            "agent-job", "agent", key, projectId, null, null, null, null));
    }

    private async Task<string> AddSessionAsync(AgentSession session)
    {
        await using var db = _database.CreateContext();
        db.AgentSessions.Add(AgentSessionJson.ToRow(session, Now.UtcDateTime));
        await db.SaveChangesAsync();
        return await db.AgentSessions.Where(candidate => candidate.Id == session.Id)
            .Select(candidate => candidate.State).SingleAsync();
    }

    private async Task<string> SessionStateAsync(string sessionId)
    {
        await using var db = _database.CreateContext();
        return await db.AgentSessions.AsNoTracking()
            .Where(candidate => candidate.Id == sessionId)
            .Select(candidate => candidate.State).SingleAsync();
    }

    private async Task SetTurnTerminalAsync(string sessionId, string turnId)
    {
        await using var db = _database.CreateContext();
        var row = await db.AgentSessions.SingleAsync(candidate => candidate.Id == sessionId);
        var session = AgentSessionJson.Deserialize(row)
            ?? throw new InvalidOperationException("Seed session became unreadable.");
        var turns = session.Status.Turns!.ToList();
        var index = turns.FindIndex(turn => string.Equals(turn.Id, turnId, StringComparison.Ordinal));
        turns[index] = turns[index] with { Status = AgentTurnStatus.Completed, UpdatedAt = Now.UtcDateTime };
        session.Status = session.Status with { Turns = turns };
        row.State = System.Text.Json.JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions);
        await db.SaveChangesAsync();
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

    private async Task AssertJobClaimUntouchedAsync(string key)
    {
        await using var db = _database.CreateContext();
        var row = await db.AgentJobs.AsNoTracking().SingleAsync(candidate => candidate.JobKey == key);
        var state = JSON.Deserialize<AgentJobState>(row.State)!;
        Assert.Null(state.CapacityClaimedAt);
        Assert.Null(state.ReadySince);
    }

    private static AgentSession NewLocalOrderSession(
        string sessionId,
        string projectId,
        string agentId,
        DateTime recordedAt,
        params (string InputId, string TurnId, string? JobId)[] turns)
    {
        var metadata = new AgentSessionMetadata()
            .WithLabel("mohist.io/project-id", projectId)
            .WithLabel("mohist.io/source-kind", "agent-launch")
            .WithLabel("mohist.io/agent-id", agentId);
        var session = AgentSession.Create(sessionId, "runner", "/work", metadata, recordedAt, "pi");
        var inputs = new List<AgentSessionInputRecord>();
        var turnRecords = new List<AgentTurnRecord>();
        var leases = new List<AgentSessionFollowupLease>();
        for (var index = 0; index < turns.Length; index++)
        {
            var (inputId, turnId, jobId) = turns[index];
            var sequence = index + 1L;
            inputs.Add(new(inputId, sequence, "prompt", jobId is null ? "api" : "workflow",
                AgentSessionInputAcceptance.Accepted, recordedAt.AddSeconds(index), JobId: jobId, ContextGeneration: 1));
            turnRecords.Add(new(turnId, sequence, [inputId], AgentTurnStatus.Queued, jobId,
                RecordedAt: recordedAt.AddSeconds(index), ContextGeneration: 1));
            if (jobId is null)
                leases.Add(new($"system-turn:{turnId}", "runtime", Accepted: true,
                    AcceptedAt: recordedAt, StartedAt: recordedAt, InputId: inputId, TurnId: turnId));
        }
        session.Status = session.Status with
        {
            Activity = AgentSessionActivity.Active,
            ContextGeneration = 1,
            Inputs = inputs,
            Turns = turnRecords,
            PendingFollowups = leases,
        };
        return session;
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
