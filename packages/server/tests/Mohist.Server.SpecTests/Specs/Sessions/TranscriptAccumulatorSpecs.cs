using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport.TestData;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

public class TranscriptAccumulatorSpecs
{
    private static AgentSession CreateSession() => AgentSessionTestData.CreateRunning().Session;

    [Fact]
    public void Accept_TextDeltas_AccumulatesIntoPendingAndBuildFlushEmitsPart()
    {
        var session = CreateSession();
        var accumulator = new TranscriptAccumulator();
        var now = new DateTime(2026, 6, 14, 12, 0, 0, DateTimeKind.Utc);

        accumulator.Accept(session, new[]
        {
            new RuntimeEventEnvelope { Type = "message.delta", PayloadJson = "{\"text\":\"hello \"}", CreatedAt = now },
            new RuntimeEventEnvelope { Type = "message.delta", PayloadJson = "{\"text\":\"world\"}", CreatedAt = now }
        }, now);

        var flush = accumulator.BuildFlush(session, now);

        Assert.NotNull(flush);
        var part = Assert.Single(flush.Parts);
        Assert.Equal("text", part.Type);
        Assert.Equal("hello world", part.TextDelta);
        Assert.False(flush.StartNewTurn);
    }

    [Fact]
    public void Accept_PartDeltas_AccumulateAndCombineWithPendingText()
    {
        var session = CreateSession();
        var accumulator = new TranscriptAccumulator();
        var now = new DateTime(2026, 6, 14, 12, 0, 0, DateTimeKind.Utc);

        accumulator.Accept(session, new[]
        {
            new RuntimeEventEnvelope { Type = "message.delta", PayloadJson = "{\"text\":\"hello\"}", CreatedAt = now },
            new RuntimeEventEnvelope { Type = "tool_call.started", PayloadJson = "{\"toolCallId\":\"t1\",\"kind\":\"read\"}", CreatedAt = now }
        }, now);

        var flush = accumulator.BuildFlush(session, now);

        Assert.NotNull(flush);
        Assert.Equal(2, flush.Parts.Count);
        Assert.Equal("text", flush.Parts[0].Type);
        Assert.Equal("tool", flush.Parts[1].Type);
        Assert.Equal("t1", flush.Parts[1].CorrelationKey);
    }

    [Fact]
    public void BuildFlush_DoesNotClearAccumulatedPartsOrInputTracking()
    {
        var session = CreateSession();
        var accumulator = new TranscriptAccumulator();
        var now = new DateTime(2026, 6, 14, 12, 0, 0, DateTimeKind.Utc);

        accumulator.Accept(session, new[]
        {
            new RuntimeEventEnvelope { Type = "session.input", PayloadJson = "{\"text\":\"do it\",\"kind\":\"task\"}", CreatedAt = now },
            new RuntimeEventEnvelope { Type = "message.delta", PayloadJson = "{\"text\":\"ok\"}", CreatedAt = now }
        }, now);

        var first = accumulator.BuildFlush(session, now);
        var second = accumulator.BuildFlush(session, now.AddMilliseconds(1));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Parts.Count, second.Parts.Count);
        Assert.Equal(first.StartNewTurn, second.StartNewTurn);
        Assert.Equal(first.Turn.PromptText, second.Turn.PromptText);
    }

    [Fact]
    public void CommitFlush_ClearsPendingPartsAndInputTracking()
    {
        var session = CreateSession();
        var accumulator = new TranscriptAccumulator();
        var now = new DateTime(2026, 6, 14, 12, 0, 0, DateTimeKind.Utc);

        accumulator.Accept(session, new[]
        {
            new RuntimeEventEnvelope { Type = "session.input", PayloadJson = "{\"text\":\"do it\",\"kind\":\"task\"}", CreatedAt = now },
            new RuntimeEventEnvelope { Type = "message.delta", PayloadJson = "{\"text\":\"ok\"}", CreatedAt = now }
        }, now);

        var flush = accumulator.BuildFlush(session, now);
        Assert.NotNull(flush);
        accumulator.CommitFlush();

        var afterCommit = accumulator.BuildFlush(session, now.AddMilliseconds(1));
        Assert.Null(afterCommit);
        Assert.False(accumulator.HasPending);
    }

    [Fact]
    public void Accept_SessionInput_CapturesPromptForNextFlush()
    {
        var session = CreateSession();
        var accumulator = new TranscriptAccumulator();
        var now = new DateTime(2026, 6, 14, 12, 0, 0, DateTimeKind.Utc);

        accumulator.Accept(session, new[]
        {
            new RuntimeEventEnvelope { Type = "session.input", PayloadJson = "{\"text\":\"plan the work\",\"kind\":\"task\"}", CreatedAt = now }
        }, now);

        var flush = accumulator.BuildFlush(session, now);

        Assert.NotNull(flush);
        Assert.True(flush.StartNewTurn);
        Assert.Equal("plan the work", flush.Turn.PromptText);
        Assert.Equal("task", flush.Turn.PromptKind);
        Assert.Equal(now, flush.Turn.StartedAt);
    }

    [Fact]
    public void Accept_TerminalSessionActivity_UsesDeliveryIdForCorrelation()
    {
        var session = CreateSession();
        var accumulator = new TranscriptAccumulator();
        var now = new DateTime(2026, 6, 14, 12, 0, 0, DateTimeKind.Utc);
        const string deliveryId = "agent-job:job-1:terminal";

        var accepted = accumulator.Accept(session, [
            new RuntimeEventEnvelope
            {
                Type = RuntimeEventTypes.SessionActivity,
                PayloadJson = $$"""{"activity":"idle","deliveryId":"{{deliveryId}}","status":"failed"}""",
                CreatedAt = now,
            },
        ], now);

        var part = Assert.Single(accepted);
        Assert.Equal(deliveryId, part.CorrelationKey);
        Assert.Equal(deliveryId, part.CorrelationId);
    }

    [Fact]
    public void Accept_ContinuousTextDeltas_AcrossCalls_CombineIntoSinglePart()
    {
        var session = CreateSession();
        var accumulator = new TranscriptAccumulator();
        var now = new DateTime(2026, 6, 14, 12, 0, 0, DateTimeKind.Utc);

        accumulator.Accept(session, new[]
        {
            new RuntimeEventEnvelope { Type = "message.delta", PayloadJson = "{\"text\":\"first \"}", CreatedAt = now }
        }, now);

        accumulator.Accept(session, new[]
        {
            new RuntimeEventEnvelope { Type = "message.delta", PayloadJson = "{\"text\":\"second\"}", CreatedAt = now.AddSeconds(1) }
        }, now.AddSeconds(1));

        var flush = accumulator.BuildFlush(session, now.AddSeconds(2));

        Assert.NotNull(flush);
        var part = Assert.Single(flush.Parts);
        Assert.Equal("first second", part.TextDelta);
    }
}
