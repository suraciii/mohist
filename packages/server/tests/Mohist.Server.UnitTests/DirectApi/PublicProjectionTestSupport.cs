using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Api.DirectApi;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.PublicApi;
using Mohist.Server.Infrastructure.PublicApi;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.TestSupport;
using Mohist.Server.UnitTests.Support;

namespace Mohist.Server.UnitTests.DirectApi;

/// <summary>
/// Seeding and read helpers for the public execution projection specs.
/// Canonical facts are seeded through the real durable stores — the
/// AgentJob ledger, the AgentSession ledger, and the CloudEvents
/// journals — so the projector consumes exactly the durable inputs
/// production writes.
/// </summary>
public sealed class PublicProjectionTestSupport : IAsyncDisposable
{
    private static readonly DateTime FixedTime = new(2026, 8, 9, 10, 15, 0, DateTimeKind.Utc);

    public PublicProjectionTestSupport()
    {
        Database = TestSqliteDatabase.CreateModelSchema();
        DbFactory = new TestDbContextFactory(Database.Options);
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 9, 10, 15, 0, TimeSpan.Zero));
        EventStore = new EventStore(DbFactory, NullLogger<EventStore>.Instance);
        SessionStore = new AgentSessionStore(
            DbFactory,
            EventStore,
            new NullEventDispatchGrainFactory(),
            NullLogger<AgentSessionStore>.Instance,
            new BackgroundTaskLauncher());
        JobStore = new AgentJobStore(
            DbFactory,
            NullLogger<AgentJobStore>.Instance,
            Time);
        Engine = new PublicApiProjectionEngine(
            DbFactory,
            Time,
            NullLogger<PublicApiProjectionEngine>.Instance);
    }

    public TestSqliteDatabase Database { get; }
    public TestDbContextFactory DbFactory { get; }
    public FakeTimeProvider Time { get; }
    public EventStore EventStore { get; }
    public AgentSessionStore SessionStore { get; }
    public AgentJobStore JobStore { get; }
    public PublicApiProjectionEngine Engine { get; }

    public static string SerializeJobState(AgentJobState state) =>
        JsonSerializer.Serialize(state, JSON.Options);

    public async Task SeedJobAsync(
        string jobKey,
        string projectId,
        string agentId,
        string sessionId,
        string inputId,
        string turnId,
        AgentJobStatus status = AgentJobStatus.Pending,
        string? waitingReason = null,
        AgentJobTerminalResult? terminalResult = null,
        DateTimeOffset? submittedAt = null,
        DateTimeOffset? runningSince = null,
        DateTimeOffset? terminalAt = null)
    {
        var state = new AgentJobState
        {
            Status = status,
            WaitingReason = waitingReason,
            TerminalResult = terminalResult,
            SubmittedAt = submittedAt ?? new DateTimeOffset(FixedTime),
            RunningSince = runningSince,
            TerminalAt = terminalAt,
            Input = BuildJobInput(projectId, agentId, sessionId, inputId, turnId),
        };
        await JobStore.InsertLedgerAsync(new AgentJobLedgerRecord(
            JobKey: jobKey,
            StateJson: SerializeJobState(state),
            Revision: 0,
            AssignedRunnerId: null,
            WorkId: null,
            ReadySince: status == AgentJobStatus.Pending ? new DateTimeOffset(FixedTime.AddSeconds(1)) : null,
            RunningSince: runningSince,
            DispatchJson: null,
            WorkType: null,
            Stage: null,
            Title: null,
            IssueProjectId: null,
            IssueNumber: null,
            AgentSessionId: sessionId,
            InitialInputId: inputId,
            InitialTurnId: turnId));
    }

    public async Task SaveJobStatusAsync(
        string jobKey,
        AgentJobStatus status,
        string? waitingReason = null,
        AgentJobTerminalResult? terminalResult = null,
        DateTimeOffset? runningSince = null,
        DateTimeOffset? terminalAt = null)
    {
        var ledger = await JobStore.LoadLedgerAsync(jobKey)
            ?? throw new InvalidOperationException($"The seeded job {jobKey} disappeared.");
        var state = JsonSerializer.Deserialize<AgentJobState>(ledger.StateJson, JSON.Options)
            ?? throw new InvalidOperationException($"The seeded job {jobKey} state is unreadable.");
        state.Status = status;
        state.WaitingReason = waitingReason;
        state.TerminalResult = terminalResult;
        state.RunningSince = runningSince;
        state.TerminalAt = terminalAt;
        await JobStore.SaveLedgerAsync(ledger with
        {
            StateJson = SerializeJobState(state),
        });
    }

    private static AgentJobInput BuildJobInput(
        string projectId,
        string agentId,
        string sessionId,
        string inputId,
        string turnId) => new(
        Prompt: "Investigate the failed deployment",
        ProjectId: projectId,
        AgentId: agentId,
        AgentSessionId: sessionId,
        InitialInputId: inputId,
        InitialTurnId: turnId);

    public AgentSession BuildSession(
        string sessionId,
        string projectId,
        string agentId) => AgentSession.Create(
        sessionId,
        "runner-1",
        "/mohist-tests/work",
        new AgentSessionMetadata(Labels: new Dictionary<string, string>
        {
            ["mohist.io/project-id"] = projectId,
            ["mohist.io/source-kind"] = "agent-launch",
            ["mohist.io/agent-id"] = agentId,
        }),
        FixedTime);

    public static AgentSession WithFacts(
        AgentSession session,
        AgentSessionActivity activity,
        IReadOnlyList<AgentSessionInputRecord>? inputs,
        IReadOnlyList<AgentTurnRecord>? turns,
        AgentSessionStopClaim? pendingStop = null)
    {
        session.Status = session.Status with
        {
            Activity = activity,
            Inputs = inputs ?? [],
            Turns = turns ?? [],
            PendingStop = pendingStop,
            LastDataAt = FixedTime.AddMinutes(5),
        };
        return session;
    }

    public static AgentSessionInputRecord Input(
        string inputId,
        string? jobId,
        AgentSessionInputAcceptance acceptance = AgentSessionInputAcceptance.Accepted,
        DateTime? recordedAt = null) => new(
        Id: inputId,
        Sequence: 1,
        Text: "Investigate the failed deployment",
        Source: "direct-test",
        Acceptance: acceptance,
        RecordedAt: recordedAt ?? FixedTime,
        JobId: jobId);

    public static AgentTurnRecord Turn(
        string turnId,
        string inputId,
        string? jobId,
        AgentTurnStatus status,
        DateTime? recordedAt = null,
        DateTime? updatedAt = null,
        AgentTurnResult? result = null) => new(
        Id: turnId,
        Sequence: 1,
        InputIds: [inputId],
        Status: status,
        JobId: jobId,
        Result: result,
        RecordedAt: recordedAt ?? FixedTime,
        UpdatedAt: updatedAt);

    public Task SaveSessionAsync(AgentSession session) => SessionStore.SaveAsync(session.Id, session);

    public Task SaveSessionAsync(AgentSession session, IReadOnlyList<AgentSessionEvent> events) =>
        SessionStore.SaveAsync(session.Id, session, events);

    public async Task<List<PublicExecutionSnapshotRow>> SnapshotsAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        return await db.PublicExecutionSnapshots.AsNoTracking().OrderBy(row => row.AnchorType).ThenBy(row => row.AnchorId).ToListAsync();
    }

    public async Task<PublicExecutionSnapshotRow?> SnapshotAsync(string anchorType, string anchorId)
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        return await db.PublicExecutionSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(row => row.AnchorType == anchorType && row.AnchorId == anchorId);
    }

    public async Task<List<PublicSessionEventRow>> EventsAsync(string? sessionId = null, long? generation = null)
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        var query = db.PublicSessionEvents.AsNoTracking().AsQueryable();
        if (sessionId is not null)
        {
            query = query.Where(row => row.SessionId == sessionId);
        }

        if (generation is not null)
        {
            query = query.Where(row => row.Generation == generation);
        }

        return await query.OrderBy(row => row.SessionId).ThenBy(row => row.Sequence).ToListAsync();
    }

    public async Task<PublicStreamStateRow?> StreamStateAsync(string sessionId)
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        return await db.PublicStreamStates.AsNoTracking().FirstOrDefaultAsync(row => row.SessionId == sessionId);
    }

    public async Task<List<PublicProjectionCheckpointRow>> CheckpointsAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        return await db.PublicProjectionCheckpoints.AsNoTracking()
            .OrderBy(row => row.Feed).ThenBy(row => row.SourceKey).ToListAsync();
    }

    public async Task<int> CountAsync(Func<MohistDbContext, IQueryable<int>> selector)
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        return await selector(db).CountAsync();
    }

    public ValueTask DisposeAsync()
    {
        Database.Dispose();
        return ValueTask.CompletedTask;
    }
}
