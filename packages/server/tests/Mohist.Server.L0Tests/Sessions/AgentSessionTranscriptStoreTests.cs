using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.L0Tests.Support;
using Xunit;

namespace Mohist.Server.L0Tests.Sessions;

/// <summary>
/// Owner coverage for the production <see cref="AgentSessionTranscriptStore"/>
/// persistence rules that the former full-host transcript specs proved only
/// through the running application (#676): turn creation vs append, part
/// sequence assignment, idempotent redelivery, raw-event count merge, and the
/// sequence-order read path shared by the summary projector. Observed against
/// real in-memory SQLite with the model schema; no application host.
/// </summary>
public sealed class AgentSessionTranscriptStoreTests : IDisposable
{
    private static readonly DateTime FixedTime = new(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc);

    private readonly TestSqliteDatabase _db = TestSqliteDatabase.CreateModelSchema();

    private AgentSessionTranscriptStore CreateStore() =>
        new(new TestDbContextFactory(_db.Options));

    public void Dispose() => _db.Dispose();

    private static AgentSessionTranscriptTurnUpsert Turn(string sessionId, string prompt = "plan the refactor") =>
        new(sessionId, 0, prompt, "task", FixedTime, FixedTime);

    private static AgentSessionTranscriptPartDelta Part(
        string type,
        string correlationKey,
        string? textDelta = null,
        string payloadJson = "{}",
        int rawEventCount = 1,
        bool isIdempotent = false) =>
        new(type, correlationKey, null, textDelta, payloadJson, FixedTime, FixedTime, rawEventCount, isIdempotent);

    [Fact]
    public async Task Save_NewTurn_CreatesTurnAndPersistsPartsInGivenOrder()
    {
        var store = CreateStore();
        const string sessionId = "session-new-turn";

        await store.SaveAsync(new AgentSessionTranscriptFlush(
            StartNewTurn: true,
            Turn(sessionId),
            [
                Part(TranscriptPartTypes.Text, "msg-1", textDelta: "first second", rawEventCount: 2),
                Part(TranscriptPartTypes.Reasoning, "reason-1", textDelta: "thinking"),
                Part(TranscriptPartTypes.Reasoning, "reason-2", textDelta: "deeper"),
                Part(TranscriptPartTypes.Tool, "tool-1", payloadJson: """{"toolCallId":"tool-1","status":"completed"}""", rawEventCount: 2),
            ]));

        await using var db = _db.CreateContext();
        var turn = await db.AgentSessionTranscriptTurns.SingleAsync(t => t.SessionId == sessionId);
        Assert.Equal(1, turn.Sequence);
        Assert.Equal("plan the refactor", turn.PromptText);
        Assert.Equal("task", turn.PromptKind);

        var parts = await db.AgentSessionTranscriptParts
            .Where(p => p.TurnId == turn.Id)
            .OrderBy(p => p.Sequence)
            .ToListAsync();
        Assert.Equal([TranscriptPartTypes.Text, TranscriptPartTypes.Reasoning, TranscriptPartTypes.Reasoning, TranscriptPartTypes.Tool], parts.Select(p => p.Type));
        Assert.Equal([1L, 2L, 3L, 4L], parts.Select(p => p.Sequence));
        Assert.Equal("first second", parts[0].Text);
        Assert.Equal(2, parts[0].RawEventCount);
        Assert.Equal(2, parts[3].RawEventCount);
    }

    [Fact]
    public async Task Save_AppendFlush_ContinuesSameTurnWithoutNewSequenceGap()
    {
        var store = CreateStore();
        const string sessionId = "session-append";

        await store.SaveAsync(new AgentSessionTranscriptFlush(
            StartNewTurn: true,
            Turn(sessionId),
            [Part(TranscriptPartTypes.Text, "msg-1", textDelta: "first")]));
        await store.SaveAsync(new AgentSessionTranscriptFlush(
            StartNewTurn: false,
            Turn(sessionId, prompt: "plan the refactor"),
            [Part(TranscriptPartTypes.Reasoning, "reason-1", textDelta: "thinking")]));

        await using var db = _db.CreateContext();
        var turns = await db.AgentSessionTranscriptTurns.Where(t => t.SessionId == sessionId).ToListAsync();
        var turn = Assert.Single(turns);
        Assert.Equal("plan the refactor", turn.PromptText);

        var parts = await db.AgentSessionTranscriptParts
            .Where(p => p.TurnId == turn.Id)
            .OrderBy(p => p.Sequence)
            .ToListAsync();
        Assert.Equal(2, parts.Count);
        Assert.Equal([1L, 2L], parts.Select(p => p.Sequence));
        Assert.Equal([TranscriptPartTypes.Text, TranscriptPartTypes.Reasoning], parts.Select(p => p.Type));
    }

    [Fact]
    public async Task Save_IdempotentRedelivery_SkipsExistingPartWithoutDuplicating()
    {
        var store = CreateStore();
        const string sessionId = "session-idempotent";

        await store.SaveAsync(new AgentSessionTranscriptFlush(
            StartNewTurn: true,
            Turn(sessionId),
            [Part(TranscriptPartTypes.Tool, "tool-1", payloadJson: """{"status":"in_progress"}""")]));
        await store.SaveAsync(new AgentSessionTranscriptFlush(
            StartNewTurn: false,
            Turn(sessionId),
            [Part(TranscriptPartTypes.Tool, "tool-1", payloadJson: """{"status":"completed"}""", isIdempotent: true)]));

        await using var db = _db.CreateContext();
        var part = await db.AgentSessionTranscriptParts.SingleAsync(p => p.CorrelationKey == "tool-1");
        Assert.Equal("""{"status":"in_progress"}""", part.PayloadJson);
        Assert.Equal(1, part.RawEventCount);
    }

    [Fact]
    public async Task Save_NonIdempotentRedelivery_AppendsTextAndRawEventCount()
    {
        var store = CreateStore();
        const string sessionId = "session-merge";

        await store.SaveAsync(new AgentSessionTranscriptFlush(
            StartNewTurn: true,
            Turn(sessionId),
            [Part(TranscriptPartTypes.Reasoning, "reason-1", textDelta: "thinking", rawEventCount: 3)]));
        await store.SaveAsync(new AgentSessionTranscriptFlush(
            StartNewTurn: false,
            Turn(sessionId),
            [Part(TranscriptPartTypes.Reasoning, "reason-1", textDelta: " deeper", rawEventCount: 2)]));

        await using var db = _db.CreateContext();
        var part = await db.AgentSessionTranscriptParts.SingleAsync(p => p.CorrelationKey == "reason-1");
        Assert.Equal("thinking deeper", part.Text);
        Assert.Equal(5, part.RawEventCount);
    }

    [Fact]
    public async Task ReadPath_SummaryProjector_ResolvesFactsBySequenceNotInsertionOrder()
    {
        var store = CreateStore();
        const string sessionId = "session-order";
        await store.SaveAsync(new AgentSessionTranscriptFlush(
            StartNewTurn: true,
            Turn(sessionId),
            [Part(TranscriptPartTypes.Input, "input-1")]));

        // Insert later-sequence facts first so insertion order diverges from
        // sequence order; the read path must still resolve sequence-last.
        await using (var db = _db.CreateContext())
        {
            var turn = await db.AgentSessionTranscriptTurns.SingleAsync(t => t.SessionId == sessionId);
            db.AgentSessionTranscriptParts.AddRange(
                new AgentSessionTranscriptPartRow
                {
                    TurnId = turn.Id,
                    Sequence = 20,
                    Type = TranscriptPartTypes.Model,
                    CorrelationKey = "model-latest-by-sequence",
                    PayloadJson = """{"resolvedModel":"sequence-last-model"}""",
                    FirstSeenAt = FixedTime,
                    LastSeenAt = FixedTime.AddMinutes(20),
                },
                new AgentSessionTranscriptPartRow
                {
                    TurnId = turn.Id,
                    Sequence = 10,
                    Type = TranscriptPartTypes.Model,
                    CorrelationKey = "model-inserted-last",
                    PayloadJson = """{"resolvedModel":"inserted-last-model"}""",
                    FirstSeenAt = FixedTime,
                    LastSeenAt = FixedTime.AddMinutes(10),
                },
                new AgentSessionTranscriptPartRow
                {
                    TurnId = turn.Id,
                    Sequence = 30,
                    Type = TranscriptPartTypes.SessionActivity,
                    CorrelationKey = "activity-latest-by-sequence",
                    PayloadJson = """{"status":"failed","failureCategory":"sequence-last-failure"}""",
                    FirstSeenAt = FixedTime,
                    LastSeenAt = FixedTime.AddMinutes(30),
                },
                new AgentSessionTranscriptPartRow
                {
                    TurnId = turn.Id,
                    Sequence = 15,
                    Type = TranscriptPartTypes.SessionActivity,
                    CorrelationKey = "activity-inserted-last",
                    PayloadJson = """{"status":"failed","failureCategory":"inserted-last-failure"}""",
                    FirstSeenAt = FixedTime,
                    LastSeenAt = FixedTime.AddMinutes(15),
                });
            await db.SaveChangesAsync();
        }

        await using var readDb = _db.CreateContext();
        var loaded = await TranscriptPartLoader.LoadAsync(readDb, [sessionId]);
        var turnSequenceByTurnId = loaded.Turns.ToDictionary(t => t.Id, t => t.Sequence);

        var summary = TranscriptEventSummaryProjector.Summarize(
            loaded.Parts.Select(e => new TranscriptSummaryEvent(
                TurnSequence: turnSequenceByTurnId.GetValueOrDefault(e.TurnId, 0),
                Sequence: e.Sequence,
                PartId: e.Id.ToString(),
                Type: e.Type,
                PayloadJson: e.PayloadJson)));

        Assert.Equal("sequence-last-model", summary.ResolvedModel);
        Assert.Equal("sequence-last-failure", summary.FailureCategory);
    }
}
