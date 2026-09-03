using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.L0Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.L0Tests.Specs.Sessions;

[Collection("OrleansGrainL0")]
public sealed class AgentSessionRuntimeEventPersistenceSpecs
{
    private readonly OrleansL0WorkflowGrainFixture _fixture;
    private readonly string _runnerId = $"session-spec-runner-{Guid.NewGuid():N}";

    public AgentSessionRuntimeEventPersistenceSpecs(OrleansL0WorkflowGrainFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task RunnerAppendsSessionEvents_ConcurrentChunks_BuffersUntilFlush()
    {
        var session = await AgentSessionRuntimeEventTestSupport.CreateStartedSessionAsync(
            _fixture.Grains,
            _runnerId,
            "sequence");
        var persistence = _fixture.Persistence.Checkpoint(session.Id);

        await Task.WhenAll(
            AgentSessionRuntimeEventTestSupport.AppendAsync(
                session,
                ("message.delta", new { text = "first" })),
            AgentSessionRuntimeEventTestSupport.AppendAsync(
                session,
                ("message.delta", new { text = "second" })));

        await AgentSessionRuntimeEventTestSupport.AppendAsync(
            session,
            ("session.activity", new { activity = "idle", status = "completed", operationId = "op-flush" }));

        var dbFactory = new TestDbContextFactory(_fixture.DbOptions);
        await dbFactory.WaitForTranscriptPartsAsync(session.Id, 2, persistence);

        await using var db = await dbFactory.CreateDbContextAsync();
        var parts = await LoadTranscriptPartsAsync(db, session.Id);
        Assert.Equal([1L, 2L], parts.Select(e => e.Sequence).ToArray());
        Assert.Equal("text", parts[0].Type);
        Assert.Equal("session.activity", parts[1].Type);
    }

    [Fact]
    public async Task RunnerAppendsSessionEvents_StoresAggregateDomainEvents()
    {
        var session = await AgentSessionRuntimeEventTestSupport.CreateStartedSessionAsync(
            _fixture.Grains,
            _runnerId,
            "runner-events-store");
        var eventStore = _fixture.EventStore;
        var before = await eventStore.ListAgentSessionEventsAsync(session.Id);
        var lastExistingId = before.Count == 0 ? 0 : before.Max(e => e.Id);
        var persistence = session.Grain.PersistenceCheckpoint(_fixture.Persistence);

        await AgentSessionRuntimeEventTestSupport.AppendAsync(
            session,
            ("usage.updated", new { contextWindowUsed = 500, contextWindowSize = 1000 }));

        await persistence.WaitAsync();

        var stored = await eventStore.ListAgentSessionEventsAsync(session.Id);
        var appended = stored.Where(e => e.Id > lastExistingId).ToArray();

        Assert.Contains(appended, e => e.Envelope.Type == EventCatalog.ReverseDns.AgentSessionUsageRecorded);
        Assert.All(appended, e => Assert.Equal(session.Id, e.Envelope.Subject));
    }

    [Fact]
    public async Task RunnerAppendsResolvedModelEvent_UpdatesResolvedModel()
    {
        var session = await AgentSessionRuntimeEventTestSupport.CreateStartedSessionAsync(
            _fixture.Grains,
            _runnerId,
            "resolved-model");
        var persistence = session.Grain.PersistenceCheckpoint(_fixture.Persistence);

        await AgentSessionRuntimeEventTestSupport.AppendAsync(
            session,
            ("model.resolved", new
            {
                resolvedModel = "anthropic/claude-sonnet-4-20250514",
                source = "newSession"
            }));

        await persistence.WaitAsync();

        var grainSession = await session.Grain.GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("anthropic/claude-sonnet-4-20250514", grainSession.ResolvedModel);
    }

    [Fact]
    public async Task RunnerAppendsTerminalEvent_WithFailureCategory_PersistsCategory()
    {
        var session = await AgentSessionRuntimeEventTestSupport.CreateStartedSessionAsync(
            _fixture.Grains,
            _runnerId,
            "failure-category");
        var persistence = session.Grain.PersistenceCheckpoint(_fixture.Persistence);

        await AgentSessionRuntimeEventTestSupport.AppendAsync(
            session,
            ("session.activity", new
            {
                activity = "idle",
                status = "failed",
                failureReason = "probe timed out",
                failureCategory = "probe_timeout",
                exitCode = 1,
                operationId = "op-terminal"
            }));

        await persistence.WaitAsync();

        var grainSession = await session.Grain.GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("idle", grainSession.Status);
        Assert.Equal("probe_timeout", grainSession.FailureCategory);
    }

    [Fact]
    public async Task RunnerAppendsToolCallEvents_CountsCallsAndErrors()
    {
        var session = await AgentSessionRuntimeEventTestSupport.CreateStartedSessionAsync(
            _fixture.Grains,
            _runnerId,
            "tool-calls");
        var persistence = session.Grain.PersistenceCheckpoint(_fixture.Persistence);

        await AgentSessionRuntimeEventTestSupport.AppendAsync(
            session,
            ("tool_call.started", new { toolCallId = "tool-1", kind = "read", status = "in_progress", title = "Read file" }),
            ("tool_call.started", new { toolCallId = "tool-2", kind = "edit", status = "in_progress", title = "Edit file" }),
            ("tool_call.updated", new { toolCallId = "tool-1", kind = "read", status = "completed", title = "Read file" }),
            ("tool_call.updated", new { toolCallId = "tool-2", kind = "edit", status = "failed", title = "Edit file" }));

        await persistence.WaitAsync();

        var grainSession = await session.Grain.GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal(2, grainSession.ToolCallCount);
        Assert.Equal(1, grainSession.ToolErrorCount);
    }

    [Fact]
    public async Task RunnerAppendsUsageUpdate_TerminalSession_PersistsEventButDoesNotMutateCounters()
    {
        var session = await AgentSessionRuntimeEventTestSupport.CreateStartedSessionAsync(
            _fixture.Grains,
            _runnerId,
            "usage-terminal");
        var persistence = _fixture.Persistence.Checkpoint(session.Id);

        await AgentSessionRuntimeEventTestSupport.AppendAsync(
            session,
            ("session.activity", new { activity = "idle", status = "completed", operationId = "op-terminal" }));

        await AgentSessionRuntimeEventTestSupport.AppendAsync(
            session,
            ("usage.updated", new
            {
                inputTokens = 10,
                outputTokens = 5,
                costAmount = 0.001,
                costCurrency = "USD"
            }));

        var dbFactory = new TestDbContextFactory(_fixture.DbOptions);
        await dbFactory.WaitForTranscriptPartsAsync(session.Id, 2, persistence);

        var grainSession = await session.Grain.GetAsync();
        Assert.NotNull(grainSession);
        Assert.Equal("idle", grainSession.Status);
        Assert.Equal(10, grainSession.InputTokens);
        Assert.Equal(0.001, grainSession.CostAmount);

        await using var db = await dbFactory.CreateDbContextAsync();
        var runtimeEvents = (await LoadTranscriptPartsAsync(db, session.Id)).ToList();
        Assert.Equal(2, runtimeEvents.Count);
        Assert.Equal("session.activity", runtimeEvents[0].Type);
        Assert.Equal("usage", runtimeEvents[1].Type);
    }

    private static async Task<AgentSessionTranscriptPartRow[]> LoadTranscriptPartsAsync(
        MohistDbContext db,
        string sessionId)
    {
        var turnIds = await db.AgentSessionTranscriptTurns.AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .Select(e => e.Id)
            .ToArrayAsync();

        return await db.AgentSessionTranscriptParts.AsNoTracking()
            .Where(e => turnIds.Contains(e.TurnId))
            .OrderBy(e => e.Sequence)
            .ThenBy(e => e.Id)
            .ToArrayAsync();
    }
}
