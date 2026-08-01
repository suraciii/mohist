using Mohist.Server.Api;
using Mohist.Server.Project.Services;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

public class UnifiedSessionSummarySpecs
{
    [Fact]
    public async Task Show_AgentLaunchSession_CarriesEnrichedFieldsFromTranscriptAndState()
    {
        var db = await UnifiedSessionSummaryFactory.BuildEnrichedDbAsync(seedActiveAgent: true);
        var result = await UnifiedSessionRoutes.HandleShowAsync(UnifiedSessionSummaryFactory.ProjectAInfo, UnifiedSessionSummaryFactory.AgentLaunchSession, UnifiedSessionSummaryFactory.CreateQuerier(db), CancellationToken.None);
        var data = await UnifiedSessionSummaryFactory.OkDataAsync(result);
        Assert.Equal("gpt-4o", data.GetProperty("resolvedModel").GetString());
        Assert.Equal("rate_limited", data.GetProperty("failureCategory").GetString());
        Assert.Equal("OpenCode provider rate limit", data.GetProperty("failureReason").GetString());
        Assert.Equal(2, data.GetProperty("toolCallCount").GetInt32());
        Assert.Equal(1, data.GetProperty("toolErrorCount").GetInt32());
        Assert.False(data.GetProperty("recoveryAvailable").GetBoolean());
        Assert.Equal("active", data.GetProperty("activity").GetString());
        Assert.Equal(UnifiedSessionSummaryFactory.EnrichedActiveTurnId, data.GetProperty("currentTurnId").GetString());
        Assert.Equal("rt-agent", data.GetProperty("runtimeSessionId").GetString());
        Assert.Equal("opencode", data.GetProperty("runtime").GetString());
        Assert.True(data.TryGetProperty("usage", out _));
        Assert.Equal(1, data.GetProperty("inputs").GetArrayLength());
        Assert.Equal("accepted", data.GetProperty("inputs")[0].GetProperty("acceptance").GetString());
        Assert.Equal(1, data.GetProperty("turns").GetArrayLength());
        Assert.Equal(UnifiedSessionSummaryFactory.EnrichedActiveTurnId, data.GetProperty("turns")[0].GetProperty("id").GetString());
        Assert.Equal("executing", data.GetProperty("turns")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Show_WorkflowSession_CarriesEnrichedFieldsFromTranscriptAndState()
    {
        var db = await UnifiedSessionSummaryFactory.BuildEnrichedDbAsync(seedActiveWorkflow: true);
        var result = await UnifiedSessionRoutes.HandleShowAsync(UnifiedSessionSummaryFactory.ProjectAInfo, UnifiedSessionSummaryFactory.WorkflowSession, UnifiedSessionSummaryFactory.CreateQuerier(db), CancellationToken.None);
        var data = await UnifiedSessionSummaryFactory.OkDataAsync(result);
        Assert.Equal("claude-3", data.GetProperty("resolvedModel").GetString());
        Assert.Equal("context_exhaustion", data.GetProperty("failureCategory").GetString());
        Assert.Equal("Runtime context window exhausted", data.GetProperty("failureReason").GetString());
        Assert.Equal(3, data.GetProperty("toolCallCount").GetInt32());
        Assert.Equal(2, data.GetProperty("toolErrorCount").GetInt32());
        Assert.False(data.GetProperty("recoveryAvailable").GetBoolean());
        Assert.Equal("active", data.GetProperty("activity").GetString());
        Assert.Equal(UnifiedSessionSummaryFactory.EnrichedActiveTurnId, data.GetProperty("currentTurnId").GetString());
        Assert.Equal("rt-workflow", data.GetProperty("runtimeSessionId").GetString());
        Assert.Equal("opencode", data.GetProperty("runtime").GetString());
        Assert.True(data.TryGetProperty("usage", out _));
        Assert.Equal(1, data.GetProperty("inputs").GetArrayLength());
        Assert.Equal(1, data.GetProperty("turns").GetArrayLength());
        Assert.Equal(UnifiedSessionSummaryFactory.EnrichedActiveTurnId, data.GetProperty("turns")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Show_AgentLaunchSession_RecoveryAvailableTrue_WhenActivityIdle()
    {
        var db = await UnifiedSessionSummaryFactory.BuildEnrichedDbAsync();
        var result = await UnifiedSessionRoutes.HandleShowAsync(UnifiedSessionSummaryFactory.ProjectAInfo, UnifiedSessionSummaryFactory.AgentLaunchSession, UnifiedSessionSummaryFactory.CreateQuerier(db), CancellationToken.None);
        var data = await UnifiedSessionSummaryFactory.OkDataAsync(result);
        Assert.True(data.GetProperty("recoveryAvailable").GetBoolean());
        Assert.Equal("idle", data.GetProperty("activity").GetString());
        Assert.False(data.TryGetProperty("currentTurnId", out _));
    }

    [Fact]
    public async Task Show_AgentLaunchSession_RecoveryAvailableFalse_WhenActivityActive()
    {
        var db = await UnifiedSessionSummaryFactory.BuildEnrichedDbAsync(seedActiveAgent: true);
        var result = await UnifiedSessionRoutes.HandleShowAsync(UnifiedSessionSummaryFactory.ProjectAInfo, UnifiedSessionSummaryFactory.AgentLaunchSession, UnifiedSessionSummaryFactory.CreateQuerier(db), CancellationToken.None);
        var data = await UnifiedSessionSummaryFactory.OkDataAsync(result);
        Assert.False(data.GetProperty("recoveryAvailable").GetBoolean());
        Assert.Equal("active", data.GetProperty("activity").GetString());
        Assert.Equal(UnifiedSessionSummaryFactory.EnrichedActiveTurnId, data.GetProperty("currentTurnId").GetString());
    }

    [Fact]
    public async Task Show_AgentLaunchSession_PrefersExecutingTurnOverLaterQueuedTurn()
    {
        var db = await UnifiedSessionSummaryFactory.BuildEnrichedDbAsync(seedActiveAgent: true, seedQueuedTurn: true);
        var result = await UnifiedSessionRoutes.HandleShowAsync(UnifiedSessionSummaryFactory.ProjectAInfo, UnifiedSessionSummaryFactory.AgentLaunchSession, UnifiedSessionSummaryFactory.CreateQuerier(db), CancellationToken.None);
        var data = await UnifiedSessionSummaryFactory.OkDataAsync(result);

        Assert.Equal(UnifiedSessionSummaryFactory.EnrichedActiveTurnId, data.GetProperty("currentTurnId").GetString());
        Assert.False(data.GetProperty("recoveryAvailable").GetBoolean());
    }

    [Fact]
    public async Task Show_AgentLaunchSession_QueuedTurnBlocksRecoveryEvenWhenActivityIsIdle()
    {
        var db = await UnifiedSessionSummaryFactory.BuildEnrichedDbAsync(seedQueuedIdleTurn: true);
        var result = await UnifiedSessionRoutes.HandleShowAsync(UnifiedSessionSummaryFactory.ProjectAInfo, UnifiedSessionSummaryFactory.AgentLaunchSession, UnifiedSessionSummaryFactory.CreateQuerier(db), CancellationToken.None);
        var data = await UnifiedSessionSummaryFactory.OkDataAsync(result);

        Assert.Equal(UnifiedSessionSummaryFactory.EnrichedQueuedTurnId, data.GetProperty("currentTurnId").GetString());
        Assert.False(data.GetProperty("recoveryAvailable").GetBoolean());
    }

    [Fact]
    public async Task Show_AgentLaunchSession_ExposesTerminalTurnResult()
    {
        var db = await UnifiedSessionSummaryFactory.BuildEnrichedDbAsync(seedTurnResult: true);
        var result = await UnifiedSessionRoutes.HandleShowAsync(UnifiedSessionSummaryFactory.ProjectAInfo, UnifiedSessionSummaryFactory.AgentLaunchSession, UnifiedSessionSummaryFactory.CreateQuerier(db), CancellationToken.None);
        var data = await UnifiedSessionSummaryFactory.OkDataAsync(result);
        var turn = data.GetProperty("turns")[0];
        var turnResult = turn.GetProperty("result");

        Assert.Equal("initial launch completed", turnResult.GetProperty("message").GetString());
        Assert.Equal("artifact output", turnResult.GetProperty("output").GetString());
        Assert.Equal(0, turnResult.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task Show_AgentLaunchSession_ExposesRecoveryHistory()
    {
        var db = await UnifiedSessionSummaryFactory.BuildEnrichedDbAsync(seedRecoveryHistory: true);
        var result = await UnifiedSessionRoutes.HandleShowAsync(UnifiedSessionSummaryFactory.ProjectAInfo, UnifiedSessionSummaryFactory.AgentLaunchSession, UnifiedSessionSummaryFactory.CreateQuerier(db), CancellationToken.None);
        var data = await UnifiedSessionSummaryFactory.OkDataAsync(result);
        var history = data.GetProperty("recoveryHistory");

        Assert.Equal(2, history.GetArrayLength());
        Assert.Equal("reset", history[0].GetProperty("type").GetString());
        Assert.Equal("compaction", history[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Show_WorkflowSession_RecoveryAvailableFalse_WhenActivityActive()
    {
        var db = await UnifiedSessionSummaryFactory.BuildEnrichedDbAsync(seedActiveWorkflow: true);
        var result = await UnifiedSessionRoutes.HandleShowAsync(UnifiedSessionSummaryFactory.ProjectAInfo, UnifiedSessionSummaryFactory.WorkflowSession, UnifiedSessionSummaryFactory.CreateQuerier(db), CancellationToken.None);
        var data = await UnifiedSessionSummaryFactory.OkDataAsync(result);
        Assert.False(data.GetProperty("recoveryAvailable").GetBoolean());
        Assert.Equal("active", data.GetProperty("activity").GetString());
        Assert.Equal(UnifiedSessionSummaryFactory.EnrichedActiveTurnId, data.GetProperty("currentTurnId").GetString());
    }

    [Fact]
    public async Task Show_AgentLaunchSession_FailureEvidenceScopedToCurrentRuntimeBinding()
    {
        var db = await UnifiedSessionSummaryFactory.BuildEnrichedDbAsync(seedPriorRuntimeFailure: true);
        var result = await UnifiedSessionRoutes.HandleShowAsync(UnifiedSessionSummaryFactory.ProjectAInfo, UnifiedSessionSummaryFactory.AgentLaunchSession, UnifiedSessionSummaryFactory.CreateQuerier(db), CancellationToken.None);
        var data = await UnifiedSessionSummaryFactory.OkDataAsync(result);
        Assert.Equal("rate_limited", data.GetProperty("failureCategory").GetString());
        Assert.Equal("OpenCode provider rate limit", data.GetProperty("failureReason").GetString());
    }

    [Fact]
    public async Task Show_AgentLaunchSession_DoesNotUseHistoricalFacts_WhenRuntimeBindingIsMissing()
    {
        var db = await UnifiedSessionSummaryFactory.BuildEnrichedDbAsync(seedPriorRuntimeFailure: true, agentRuntimeSessionId: null);
        var result = await UnifiedSessionRoutes.HandleShowAsync(UnifiedSessionSummaryFactory.ProjectAInfo, UnifiedSessionSummaryFactory.AgentLaunchSession, UnifiedSessionSummaryFactory.CreateQuerier(db), CancellationToken.None);
        var data = await UnifiedSessionSummaryFactory.OkDataAsync(result);

        Assert.Equal("gpt-4o", data.GetProperty("resolvedModel").GetString());
        Assert.False(data.TryGetProperty("failureCategory", out _));
        Assert.False(data.TryGetProperty("failureReason", out _));
        Assert.False(data.TryGetProperty("toolCallCount", out _));
        Assert.False(data.TryGetProperty("toolErrorCount", out _));
    }

    [Fact]
    public async Task Transcript_SessionWithoutRuntimeBinding_ReturnsEmptyTurns()
    {
        var db = await UnifiedSessionSummaryFactory.BuildEnrichedDbAsync(agentRuntimeSessionId: null);
        var result = await UnifiedSessionRoutes.HandleTranscriptAsync(
            UnifiedSessionSummaryFactory.ProjectAInfo, UnifiedSessionSummaryFactory.AgentLaunchSession,
            runtimeSessionId: null, UnifiedSessionSummaryFactory.CreateQuerier(db), CancellationToken.None);
        var data = await UnifiedSessionSummaryFactory.OkDataAsync(result);

        Assert.Equal(0, data.GetProperty("turns").GetArrayLength());
    }

    [Fact]
    public async Task Show_AgentLaunchSession_CarriesUsageFromSession()
    {
        var db = await UnifiedSessionSummaryFactory.BuildEnrichedDbAsync();
        var result = await UnifiedSessionRoutes.HandleShowAsync(UnifiedSessionSummaryFactory.ProjectAInfo, UnifiedSessionSummaryFactory.AgentLaunchSession, UnifiedSessionSummaryFactory.CreateQuerier(db), CancellationToken.None);
        var data = await UnifiedSessionSummaryFactory.OkDataAsync(result);
        var usage = data.GetProperty("usage");
        Assert.Equal(120, usage.GetProperty("inputTokens").GetInt64());
        Assert.Equal(60, usage.GetProperty("outputTokens").GetInt64());
        Assert.Equal(180, usage.GetProperty("totalTokens").GetInt64());
        Assert.Equal(0.42, usage.GetProperty("costAmount").GetDouble());
    }

    [Fact]
    public async Task Show_AgentLaunchSession_ExposesEventOnlyCompactionHistory()
    {
        var db = await UnifiedSessionSummaryFactory.BuildEnrichedDbAsync(seedCompactionEventOnly: true);
        var result = await UnifiedSessionRoutes.HandleShowAsync(UnifiedSessionSummaryFactory.ProjectAInfo, UnifiedSessionSummaryFactory.AgentLaunchSession, UnifiedSessionSummaryFactory.CreateQuerier(db), CancellationToken.None);
        var data = await UnifiedSessionSummaryFactory.OkDataAsync(result);
        var history = data.GetProperty("recoveryHistory");

        Assert.Equal(1, history.GetArrayLength());
        Assert.Equal("compaction", history[0].GetProperty("type").GetString());
        Assert.Equal("Earlier context retained", history[0].GetProperty("summary").GetString());
    }

    [Fact]
    public async Task Show_UnsupportedSourceKind_Returns404()
    {
        var db = await UnifiedSessionSummaryFactory.BuildEnrichedDbAsync();
        await UnifiedSessionSummaryFactory.SeedUnsupportedSourceSessionAsync(db.Factory);
        var result = await UnifiedSessionRoutes.HandleShowAsync(UnifiedSessionSummaryFactory.ProjectAInfo, UnifiedSessionSummaryFactory.UnsupportedSourceSession, UnifiedSessionSummaryFactory.CreateQuerier(db), CancellationToken.None);
        await UnifiedSessionSummaryFactory.AssertNotFoundAsync(result, UnifiedSessionSummaryFactory.UnsupportedSourceSession);
    }

    [Fact]
    public async Task Transcript_UnsupportedSourceKind_Returns404()
    {
        var db = await UnifiedSessionSummaryFactory.BuildEnrichedDbAsync();
        await UnifiedSessionSummaryFactory.SeedUnsupportedSourceSessionAsync(db.Factory);
        var result = await UnifiedSessionRoutes.HandleTranscriptAsync(
            UnifiedSessionSummaryFactory.ProjectAInfo, UnifiedSessionSummaryFactory.UnsupportedSourceSession, runtimeSessionId: null, UnifiedSessionSummaryFactory.CreateQuerier(db), CancellationToken.None);
        await UnifiedSessionSummaryFactory.AssertNotFoundAsync(result, UnifiedSessionSummaryFactory.UnsupportedSourceSession);
    }

    [Fact]
    public async Task Show_AgentLaunchSession_OmitsNullableFailureFields_WhenTranscriptHasNoTerminalFact()
    {
        var db = await UnifiedSessionSummaryFactory.BuildBareDbAsync();
        var result = await UnifiedSessionRoutes.HandleShowAsync(UnifiedSessionSummaryFactory.ProjectAInfo, UnifiedSessionSummaryFactory.AgentLaunchSession, UnifiedSessionSummaryFactory.CreateQuerier(db), CancellationToken.None);
        var data = await UnifiedSessionSummaryFactory.OkDataAsync(result);
        var json = data.GetRawText();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("failureCategory", out _));
        Assert.False(doc.RootElement.TryGetProperty("failureReason", out _));
        Assert.False(doc.RootElement.TryGetProperty("toolCallCount", out _));
        Assert.False(doc.RootElement.TryGetProperty("toolErrorCount", out _));
        Assert.False(doc.RootElement.TryGetProperty("currentTurnId", out _));
        Assert.True(doc.RootElement.GetProperty("recoveryAvailable").GetBoolean());
    }
}
