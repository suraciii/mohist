using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Storage;

public class AgentJobStoreSpecs : IAsyncLifetime
{
    private readonly TestSqliteDatabase _database;
    private readonly AgentJobStore _store;
    private readonly AgentJobQuerier _querier;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 29, 0, 0, 0, TimeSpan.Zero));

    public AgentJobStoreSpecs()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(_database.Options);
        _store = new AgentJobStore(factory, NullLogger<AgentJobStore>.Instance, _time);
        _querier = new AgentJobQuerier(factory);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SaveAsync_InsertsRow_AndComputedColumnsReflectState()
    {
        var key = $"job-{Guid.NewGuid():N}";
        var state = MakeState(
            AgentJobStatus.Pending,
            projectId: "proj-1",
            agentId: "agent-1",
            submittedAt: new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.Zero));

        await _store.SaveAsync(key, Serialize(state));

        await using var db = new MohistDbContext(_database.Options);
        var row = await db.AgentJobs.SingleAsync(r => r.JobKey == key);
        Assert.Equal("proj-1", row.ProjectId);
        Assert.Equal("agent-1", row.AgentId);
        Assert.Equal("pending", row.Status);
        Assert.Contains("2026-07-25T10:00:00", row.SubmittedAt);
        Assert.Null(row.TerminalAt);
    }

    [Fact]
    public async Task SaveAsync_UpsertsExistingRow()
    {
        var key = $"job-{Guid.NewGuid():N}";
        var pending = MakeState(AgentJobStatus.Pending, projectId: "proj-1", agentId: "agent-1");
        await _store.SaveAsync(key, Serialize(pending));

        var running = MakeState(AgentJobStatus.Running, projectId: "proj-1", agentId: "agent-1");
        await _store.SaveAsync(key, Serialize(running));

        await using var db = new MohistDbContext(_database.Options);
        var rows = await db.AgentJobs.Where(r => r.JobKey == key).ToListAsync();
        Assert.Single(rows);
        Assert.Equal("running", rows[0].Status);
    }

    [Fact]
    public async Task SaveAsync_TerminalTransitionPopulatesTerminalAt()
    {
        var key = $"job-{Guid.NewGuid():N}";
        var terminalAt = new DateTimeOffset(2026, 7, 25, 11, 30, 0, TimeSpan.Zero);
        var state = MakeState(
            AgentJobStatus.Failed,
            projectId: "proj-1",
            agentId: "agent-1",
            terminalAt: terminalAt);

        await _store.SaveAsync(key, Serialize(state));

        await using var db = new MohistDbContext(_database.Options);
        var row = await db.AgentJobs.SingleAsync(r => r.JobKey == key);
        Assert.Contains("2026-07-25T11:30:00", row.TerminalAt);
    }

    [Fact]
    public async Task LoadAsync_ReturnsNullForMissingKey()
    {
        var loaded = await _store.LoadAsync($"missing-{Guid.NewGuid():N}");
        Assert.Null(loaded);
    }

    [Fact]
    public async Task LoadAsync_RoundTripsStateJson()
    {
        var key = $"job-{Guid.NewGuid():N}";
        var state = MakeState(AgentJobStatus.Completed, projectId: "proj-2", agentId: "agent-2");
        state.TerminalResult = new AgentJobTerminalResult(
            AgentJobStatus.Completed, "ok", "{}", new[] { "artifact-1" }, null, 0);

        var json = Serialize(state);
        await _store.SaveAsync(key, json);
        var loaded = await _store.LoadAsync(key);

        Assert.NotNull(loaded);
        var deserialized = JsonSerializer.Deserialize<AgentJobState>(loaded!, JSON.Options);
        Assert.NotNull(deserialized);
        Assert.Equal(AgentJobStatus.Completed, deserialized!.Status);
        Assert.Equal("proj-2", deserialized.Input!.ProjectId);
        Assert.Equal("agent-2", deserialized.Input.AgentId);
        Assert.Equal(AgentJobStatus.Completed, deserialized.TerminalResult!.Status);
        Assert.Equal("ok", deserialized.TerminalResult.Message);
        Assert.Equal("{}", deserialized.TerminalResult.Output);
        Assert.Equal(new[] { "artifact-1" }, deserialized.TerminalResult.ArtifactUploadIds);
        Assert.Null(deserialized.TerminalResult.FailureReason);
        Assert.Equal(0, deserialized.TerminalResult.ExitCode);
    }

    [Fact]
    public async Task LoadAsync_RoundTripsFailedTerminalResultFields()
    {
        var key = $"job-failed-{Guid.NewGuid():N}";
        var state = MakeState(AgentJobStatus.Failed, projectId: "proj-failed", agentId: "agent-failed");
        state.TerminalResult = new AgentJobTerminalResult(
            AgentJobStatus.Failed,
            "failed message",
            "{\"error\":\"detail\"}",
            new[] { "artifact-failed" },
            "failure-reason",
            17);

        await _store.SaveAsync(key, Serialize(state));
        var loaded = await _store.LoadAsync(key);

        Assert.NotNull(loaded);
        var deserialized = JsonSerializer.Deserialize<AgentJobState>(loaded!, JSON.Options);
        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized!.TerminalResult);
        Assert.Equal(AgentJobStatus.Failed, deserialized.TerminalResult!.Status);
        Assert.Equal("failed message", deserialized.TerminalResult.Message);
        Assert.Equal("{\"error\":\"detail\"}", deserialized.TerminalResult.Output);
        Assert.Equal(new[] { "artifact-failed" }, deserialized.TerminalResult.ArtifactUploadIds);
        Assert.Equal("failure-reason", deserialized.TerminalResult.FailureReason);
        Assert.Equal(17, deserialized.TerminalResult.ExitCode);
    }

    [Fact]
    public async Task Querier_ListByAgent_AppliesLimit()
    {
        var agentId = $"agent-limit-{Guid.NewGuid():N}";
        var projectId = "proj-limit";
        await _store.SaveAsync($"old-{agentId}", Serialize(MakeState(AgentJobStatus.Pending, projectId, agentId,
            submittedAt: new DateTimeOffset(2026, 7, 25, 8, 0, 0, TimeSpan.Zero))));
        await _store.SaveAsync($"new-{agentId}", Serialize(MakeState(AgentJobStatus.Pending, projectId, agentId,
            submittedAt: new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero))));
        await _store.SaveAsync($"mid-{agentId}", Serialize(MakeState(AgentJobStatus.Pending, projectId, agentId,
            submittedAt: new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.Zero))));

        var result = await _querier.ListByAgentAsync(projectId, agentId, limit: 2);

        Assert.Equal(new[] { $"new-{agentId}", $"mid-{agentId}" },
            result.Select(r => r.JobKey).ToArray());
    }

    [Fact]
    public async Task Querier_ListByAgent_OrdersEqualSubmissionTimesByKey()
    {
        var agentId = $"agent-tie-{Guid.NewGuid():N}";
        var projectId = "proj-tie";
        var submittedAt = new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.Zero);
        await _store.SaveAsync($"a-{agentId}", Serialize(MakeState(AgentJobStatus.Pending, projectId, agentId, submittedAt)));
        await _store.SaveAsync($"z-{agentId}", Serialize(MakeState(AgentJobStatus.Pending, projectId, agentId, submittedAt)));

        var result = await _querier.ListByAgentAsync(projectId, agentId);

        Assert.Equal(new[] { $"z-{agentId}", $"a-{agentId}" },
            result.Select(r => r.JobKey).ToArray());
    }

    [Fact]
    public async Task Querier_ListByAgent_ReturnsJobsMostRecentFirst()
    {
        var agentId = $"agent-list-{Guid.NewGuid():N}";
        var projectId = "proj-list";
        await _store.SaveAsync($"old-{agentId}", Serialize(MakeState(AgentJobStatus.Completed, projectId, agentId,
            submittedAt: new DateTimeOffset(2026, 7, 25, 8, 0, 0, TimeSpan.Zero))));
        await _store.SaveAsync($"new-{agentId}", Serialize(MakeState(AgentJobStatus.Running, projectId, agentId,
            submittedAt: new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero))));
        await _store.SaveAsync($"mid-{agentId}", Serialize(MakeState(AgentJobStatus.Pending, projectId, agentId,
            submittedAt: new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.Zero))));

        var result = await _querier.ListByAgentAsync(projectId, agentId);

        Assert.Equal(new[] { $"new-{agentId}", $"mid-{agentId}", $"old-{agentId}" },
            result.Select(r => r.JobKey).ToArray());
    }

    [Fact]
    public async Task Querier_ListByAgent_FiltersByStatus()
    {
        var agentId = $"agent-filter-{Guid.NewGuid():N}";
        var projectId = "proj-filter";
        await _store.SaveAsync($"completed-{agentId}", Serialize(MakeState(AgentJobStatus.Completed, projectId, agentId)));
        await _store.SaveAsync($"failed-{agentId}", Serialize(MakeState(AgentJobStatus.Failed, projectId, agentId)));
        await _store.SaveAsync($"running-{agentId}", Serialize(MakeState(AgentJobStatus.Running, projectId, agentId)));

        var result = await _querier.ListByAgentAsync(projectId, agentId,
            statusSet: new[] { AgentJobStatus.Completed, AgentJobStatus.Failed });

        var keys = result.Select(r => r.JobKey).ToHashSet();
        Assert.Contains($"completed-{agentId}", keys);
        Assert.Contains($"failed-{agentId}", keys);
        Assert.DoesNotContain($"running-{agentId}", keys);
    }

    [Fact]
    public async Task Querier_ListByAgent_EmptyForAgentWithNoJobs()
    {
        var result = await _querier.ListByAgentAsync("proj-empty", $"no-jobs-{Guid.NewGuid():N}");
        Assert.Empty(result);
    }

    [Fact]
    public async Task Querier_ListByAgent_DoesNotLeakAcrossAgentsOrProjects()
    {
        var agentId = $"agent-scoped-{Guid.NewGuid():N}";
        await _store.SaveAsync($"in-{agentId}", Serialize(MakeState(AgentJobStatus.Pending, "proj-in", agentId)));
        await _store.SaveAsync($"other-agent-{agentId}", Serialize(MakeState(AgentJobStatus.Pending, "proj-in", "other-agent")));
        await _store.SaveAsync($"other-proj-{agentId}", Serialize(MakeState(AgentJobStatus.Pending, "proj-out", agentId)));

        var result = await _querier.ListByAgentAsync("proj-in", agentId);

        Assert.Single(result);
        Assert.Equal($"in-{agentId}", result[0].JobKey);
    }

    [Fact]
    public async Task Querier_GetByKey_ReturnsRow()
    {
        var key = $"job-get-{Guid.NewGuid():N}";
        await _store.SaveAsync(key, Serialize(MakeState(AgentJobStatus.Running, "proj-g", "agent-g")));

        var item = await _querier.GetByKeyAsync(key);

        Assert.NotNull(item);
        Assert.Equal(key, item!.JobKey);
        Assert.Equal("agent-g", item.AgentId);
        Assert.Equal("running", item.Status);
    }

    [Fact]
    public async Task Querier_GetByKey_ReturnsNullForMissingKey()
    {
        var item = await _querier.GetByKeyAsync($"missing-{Guid.NewGuid():N}");
        Assert.Null(item);
    }

    // --- Owner ledger (issue #531 T-001) ---
    // The AgentJobs row is the single durable AgentJob ledger. These specs
    // cover atomic insert/save, revision-fenced updates, the poll-time
    // claim transition, and the four scheduling projections.

    private static AgentJobLedgerRecord NewPendingRecord(
        string key,
        string? runnerId,
        DateTimeOffset readySince,
        string? projectId = null) =>
        new(
            JobKey: key,
            StateJson: Serialize(MakeState(AgentJobStatus.Pending, projectId ?? "proj-1", "agent-1")),
            Revision: 0,
            AssignedRunnerId: runnerId,
            WorkId: runnerId is null ? null : $"{key}-work",
            ReadySince: readySince,
            RunningSince: null,
            DispatchJson: runnerId is null ? null : """{"workId":"placeholder"}""",
            WorkType: "agent-job",
            Stage: "agent",
            Title: "Agent Job",
            IssueProjectId: projectId,
            IssueNumber: null,
            AgentSessionId: null,
            InitialInputId: null,
            InitialTurnId: null);

    [Fact]
    public async Task InsertLedger_RequiresRevisionZero_AndAssignsFirstRevision()
    {
        var key = $"ledger-insert-{Guid.NewGuid():N}";
        var record = NewPendingRecord(key, "runner-a", _time.GetUtcNow());

        var inserted = await _store.InsertLedgerAsync(record);

        Assert.Equal(1, inserted.Revision);
        var loaded = await _store.LoadLedgerAsync(key);
        Assert.NotNull(loaded);
        Assert.Equal("runner-a", loaded!.AssignedRunnerId);
        Assert.Equal($"{key}-work", loaded.WorkId);
        Assert.Equal(AgentJobStatus.Pending, JsonSerializer.Deserialize<AgentJobState>(loaded.StateJson, JSON.Options)!.Status);
    }

    [Fact]
    public async Task InsertLedger_RejectsNonZeroRevision()
    {
        var key = $"ledger-badrev-{Guid.NewGuid():N}";
        var record = NewPendingRecord(key, "runner-a", _time.GetUtcNow()) with { Revision = 3 };

        await Assert.ThrowsAsync<ArgumentException>(() => _store.InsertLedgerAsync(record));
    }

    [Fact]
    public async Task SaveLedger_RejectsStaleRevision_AndAdvancesOnMatch()
    {
        var key = $"ledger-save-{Guid.NewGuid():N}";
        var inserted = await _store.InsertLedgerAsync(NewPendingRecord(key, "runner-a", _time.GetUtcNow()));

        var stale = inserted with { AssignedRunnerId = "runner-b", Revision = inserted.Revision - 1 };
        await Assert.ThrowsAsync<AgentJobLedgerConflictException>(() => _store.SaveLedgerAsync(stale));

        var fresh = inserted with { AssignedRunnerId = "runner-b", WorkId = $"{key}-work-2" };
        var saved = await _store.SaveLedgerAsync(fresh);
        Assert.Equal(inserted.Revision + 1, saved.Revision);
        Assert.Equal("runner-b", saved.AssignedRunnerId);
    }

    [Fact]
    public async Task SaveLedger_RejectsAssignmentWithoutDispatchSnapshot()
    {
        var key = $"ledger-reconstruct-{Guid.NewGuid():N}";
        var inserted = await _store.InsertLedgerAsync(NewPendingRecord(key, "runner-a", _time.GetUtcNow()));

        var noSnapshot = inserted with { DispatchJson = null };
        await Assert.ThrowsAsync<AgentJobLedgerReconstructionException>(() => _store.SaveLedgerAsync(noSnapshot));
    }

    [Fact]
    public async Task Claim_AtomicTransitionToRunning_PersistsWorkIdentityAndClearsReadySince()
    {
        var key = $"ledger-claim-{Guid.NewGuid():N}";
        await _store.InsertLedgerAsync(NewPendingRecord(key, "runner-a", _time.GetUtcNow()));
        var runningSince = _time.GetUtcNow();

        var claimed = await _store.ClaimAsync(key, "runner-a", runningSince);

        Assert.Equal(AgentJobStatus.Running, JsonSerializer.Deserialize<AgentJobState>(claimed.StateJson, JSON.Options)!.Status);
        Assert.Equal("runner-a", claimed.AssignedRunnerId);
        Assert.Equal($"{key}-work", claimed.WorkId);
        Assert.Equal(runningSince, claimed.RunningSince);
        Assert.Null(claimed.ReadySince);

        var reloaded = await _store.LoadLedgerAsync(key);
        Assert.Null(reloaded!.ReadySince);
    }

    [Fact]
    public async Task Claim_RejectsWrongRunner_NonPending_AndMissingRow()
    {
        var key = $"ledger-claim-reject-{Guid.NewGuid():N}";
        var inserted = await _store.InsertLedgerAsync(NewPendingRecord(key, "runner-a", _time.GetUtcNow()));

        await Assert.ThrowsAsync<AgentJobLedgerConflictException>(
            () => _store.ClaimAsync(key, "runner-other", _time.GetUtcNow()));
        await Assert.ThrowsAsync<AgentJobLedgerConflictException>(
            () => _store.ClaimAsync($"missing-{Guid.NewGuid():N}", "runner-a", _time.GetUtcNow()));

        await _store.ClaimAsync(key, "runner-a", _time.GetUtcNow());
        await Assert.ThrowsAsync<AgentJobLedgerConflictException>(
            () => _store.ClaimAsync(key, "runner-a", _time.GetUtcNow()));
    }

    [Fact]
    public async Task ListEligiblePending_ReturnsUnassignedByReadySinceAscending()
    {
        var early = $"ledger-elig-early-{Guid.NewGuid():N}";
        var late = $"ledger-elig-late-{Guid.NewGuid():N}";
        var assigned = $"ledger-elig-assigned-{Guid.NewGuid():N}";
        await _store.InsertLedgerAsync(NewPendingRecord(early, null, _time.GetUtcNow()));
        await _store.InsertLedgerAsync(NewPendingRecord(late, null, _time.GetUtcNow().AddMinutes(1)));
        await _store.InsertLedgerAsync(NewPendingRecord(assigned, "runner-a", _time.GetUtcNow()));

        var result = await _store.ListEligiblePendingAsync(projectId: null, limit: 10);

        Assert.Equal(new[] { early, late }, result.Select(r => r.JobKey).ToArray());
        Assert.All(result, r => Assert.Null(r.AssignedRunnerId));
    }

    [Fact]
    public async Task ProvisionalLaunch_IsHiddenFromSchedulingAndDefaultJobQuery()
    {
        var key = $"ledger-provisional-{Guid.NewGuid():N}";
        var state = MakeState(AgentJobStatus.Pending, "proj-provisional", "agent-provisional");
        state.LaunchVisibility = AgentLaunchVisibility.Provisional;
        await _store.InsertLedgerAsync(
            NewPendingRecord(key, "runner-provisional", _time.GetUtcNow()) with
            {
                StateJson = Serialize(state),
                LaunchVisibility = "provisional",
            });

        Assert.Empty(await _store.ListEligiblePendingAsync("proj-provisional", 10));
        Assert.Empty(await _store.ListAssignedPendingForRunnerAsync("runner-provisional", 10));
        Assert.Null(await _querier.GetByKeyAsync(key));
    }

    [Fact]
    public async Task ListRunningForRunner_AndAssignedPending_ProjectByRunner()
    {
        var pendingKey = $"ledger-ap-{Guid.NewGuid():N}";
        var runningKey = $"ledger-run-{Guid.NewGuid():N}";
        await _store.InsertLedgerAsync(NewPendingRecord(pendingKey, "runner-a", _time.GetUtcNow()));
        await _store.InsertLedgerAsync(NewPendingRecord(runningKey, "runner-a", _time.GetUtcNow()));
        await _store.ClaimAsync(runningKey, "runner-a", _time.GetUtcNow());

        var pending = await _store.ListAssignedPendingForRunnerAsync("runner-a", limit: 10);
        var running = await _store.ListRunningForRunnerAsync("runner-a");

        Assert.Equal(new[] { pendingKey }, pending.Select(r => r.JobKey).ToArray());
        Assert.Equal(new[] { runningKey }, running.Select(r => r.JobKey).ToArray());
    }

    [Fact]
    public async Task ListAssignedPendingForRunner_AppliesLimit()
    {
        var runnerId = $"runner-limited-{Guid.NewGuid():N}";
        var first = $"ledger-limited-first-{Guid.NewGuid():N}";
        var second = $"ledger-limited-second-{Guid.NewGuid():N}";
        var third = $"ledger-limited-third-{Guid.NewGuid():N}";
        await _store.InsertLedgerAsync(NewPendingRecord(first, runnerId, _time.GetUtcNow()));
        await _store.InsertLedgerAsync(NewPendingRecord(second, runnerId, _time.GetUtcNow().AddMinutes(1)));
        await _store.InsertLedgerAsync(NewPendingRecord(third, runnerId, _time.GetUtcNow().AddMinutes(2)));

        var result = await _store.ListAssignedPendingForRunnerAsync(runnerId, limit: 2);

        Assert.Equal([first, second], result.Select(record => record.JobKey));
    }

    [Fact]
    public async Task ListPendingAtOrBeforeReadySince_ReturnsOnlyAgedPending()
    {
        var cutoff = _time.GetUtcNow().AddMinutes(5);
        var aged = $"ledger-aged-{Guid.NewGuid():N}";
        var fresh = $"ledger-fresh-{Guid.NewGuid():N}";
        await _store.InsertLedgerAsync(NewPendingRecord(aged, "runner-a", _time.GetUtcNow()));
        await _store.InsertLedgerAsync(NewPendingRecord(fresh, "runner-a", cutoff.AddMinutes(2)));

        var result = await _store.ListPendingAtOrBeforeReadySinceAsync(cutoff, limit: 10);

        Assert.Equal(new[] { aged }, result.Select(r => r.JobKey).ToArray());
    }

    private static string Serialize(AgentJobState state) =>
        JsonSerializer.Serialize(state, JSON.Options);

    private static AgentJobState MakeState(
        AgentJobStatus status,
        string projectId,
        string agentId,
        DateTimeOffset? submittedAt = null,
        DateTimeOffset? terminalAt = null)
    {
        return new AgentJobState
        {
            Status = status,
            Input = new AgentJobInput(
                Prompt: "test",
                ProjectId: projectId,
                AgentId: agentId),
            SubmittedAt = submittedAt ?? new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero),
            TerminalAt = terminalAt,
        };
    }
}
