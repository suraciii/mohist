using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.Sessions;

public class AgentSessionEventSerializerTests
{
    [Fact]
    public void BusType_RuntimeBound_ReturnsReverseDnsConstant()
    {
        var busType = AgentSessionEventSerializer.BusType(new AgentSessionRuntimeBound("runtime-1"));

        Assert.Equal(EventCatalog.ReverseDns.AgentSessionRuntimeBound, busType);
    }

    [Fact]
    public void ToData_RuntimeBound_ExposesRuntime()
    {
        var data = AgentSessionEventSerializer.ToData(
            new AgentSessionRuntimeBound("runtime-session-1", Runtime: "opencode"));

        Assert.Equal("opencode", data.GetProperty("runtime").GetString());
    }

    [Fact]
    public void ToData_LegacyRuntimeBoundWithoutRuntime_OmitsRuntime()
    {
        var data = AgentSessionEventSerializer.ToData(
            new AgentSessionRuntimeBound("legacy-runtime-session"));

        Assert.False(data.TryGetProperty("runtime", out _));
    }

    [Fact]
    public void EventCatalog_IncludesTranscriptRuntimeEventTypes()
    {
        Assert.Contains("message.delta", EventCatalog.TranscriptTypes);
        Assert.Contains("reasoning.delta", EventCatalog.TranscriptTypes);
        Assert.Contains("tool_call.started", EventCatalog.TranscriptTypes);
        Assert.Contains("session.closed", EventCatalog.TranscriptTypes);
        Assert.Contains("usage.updated", EventCatalog.TranscriptTypes);
        Assert.Contains("model.resolved", EventCatalog.TranscriptTypes);
    }

    [Fact]
    public void ToData_EventPayloadWithChineseContent_EmitsVerbatimCharacters()
    {
        // T-004 acceptance: runner event stream with Chinese content is
        // readable. AgentSessionEventSerializer uses JSON.Options (a thin
        // delegate), so non-ASCII characters in event payloads are emitted
        // verbatim and not \uXXXX-escaped.
        var payload = new AgentSessionRuntimeBound("acp-中文-会话-001");

        var data = AgentSessionEventSerializer.ToData(payload);

        Assert.Equal("acp-中文-会话-001", data.GetProperty("agentRuntimeSessionId").GetString());
    }

    [Fact]
    public void ToData_EventPayloadWithChineseFailureCategory_EmitsVerbatimCharacters()
    {
        var payload = new AgentSessionContextExhausted(
            FailureCategory: "上下文耗尽",
            ContextUsagePercent: 0.95,
            ContextWindowUsed: 9000,
            ContextWindowSize: 10000,
            RecordedAt: new DateTime(2026, 6, 21, 0, 0, 0, DateTimeKind.Utc));

        var data = AgentSessionEventSerializer.ToData(payload);

        Assert.Equal("上下文耗尽", data.GetProperty("failureCategory").GetString());
    }
}
