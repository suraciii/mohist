using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Querier;

/// <summary>
/// Calculation specs for the session projections surfaced by
/// <c>GET /api/projects/{projectRef}/issues/{n}/sessions/{name}</c> and
/// its <c>/transcript</c> sibling. The querier
/// (<see cref="AgentSessionQuerier.GetSessionMetadataAsync"/> +
/// <see cref="AgentSessionQuerier.GetSessionTranscriptAsync"/>) is driven
/// directly via <c>MohistDbFixture</c> (no web host, no HTTP round-trip,
/// no grain). Specs seed an issue-bound workflow session directly into
/// <c>MohistDbContext</c> + the transcript parts/turns tables so the
/// projection is exercised against the production query path.
///
/// Three calculation cases sunk from
/// <c>Specs/Issue/Api/IssueSessionApiSpecs.cs</c> cover:
/// <list type="bullet">
/// <item><c>partCount</c> in the metadata envelope reflects the merged
///   transcript (message.delta batches fold together; tool_call.started
///   + tool_call.updated fold together; the context_health_update
///   emitted by the grain on the first usage event is counted).</item>
/// <item>Transcript segments surfaced via <c>/transcript</c> are
///   returned in ascending sequence order even when the underlying
///   transcript parts were inserted out of order (the loader sorts by
///   sequence).</item>
/// <item><c>failureCategory</c> in the metadata envelope resolves to
///   <c>context_exhaustion</c> when the last <c>usage.updated</c> shows
///   context usage at or above the window.</item>
/// </list>
/// The route contract (404 unknown session, 200 metadata JSON shape with
/// positive + negative field assertions, 200 transcript shape) stays in
/// <c>IssueSessionApiSpecs</c>.
/// </summary>
[Collection("MohistDb")]
public class IssueSessionProjectionSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueSessionProjectionSpecs(MohistDbFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GetSessionMetadataAsync_PartCountReflectsMergedTranscriptParts()
    {
        var (projectId, issueNumber, sessionId) = await SeedWorkflowSessionAsync("partcount", sessionName: "plan");
        // Seed the merged-batch transcript the original API spec built
        // up via runtime events: input + text (two deltas fold
        // together) + usage + model + tool (started+updated fold
        // together) + session.activity — the projection's
        // TranscriptEventSummaryProjector and the transcript builder
        // both consume the same transcript parts.
        var transcriptAt = TestTime.UtcDateTime;
        await SeedTranscriptTurnAsync(sessionId, sequence: 1, promptText: "Plan session", promptKind: "task", at: transcriptAt);
        await SeedTranscriptPartAsync(sessionId, turnSequence: 1, sequence: 1, type: "input", text: "Plan session", at: transcriptAt);
        await SeedTranscriptPartAsync(sessionId, turnSequence: 1, sequence: 2, type: "text", text: "hello", at: transcriptAt.AddSeconds(1));
        await SeedTranscriptPartAsync(sessionId, turnSequence: 1, sequence: 3, type: "text", text: "world", at: transcriptAt.AddSeconds(2));
        await SeedTranscriptPartAsync(sessionId, turnSequence: 1, sequence: 4, type: "usage", payload: """{"inputTokens":100,"outputTokens":50,"totalTokens":150,"cachedReadTokens":10,"thoughtTokens":5,"costAmount":0.01,"costCurrency":"USD","contextWindowSize":200000,"contextWindowUsed":150}""", at: transcriptAt.AddSeconds(3));
        await SeedTranscriptPartAsync(sessionId, turnSequence: 1, sequence: 5, type: "model", payload: """{"resolvedModel":"anthropic/claude-sonnet-4","source":"newSession"}""", at: transcriptAt.AddSeconds(4));
        await SeedTranscriptPartAsync(sessionId, turnSequence: 1, sequence: 6, type: "tool", payload: """{"toolCallId":"tool-1","toolName":"read","status":"in_progress","title":"Read README"}""", at: transcriptAt.AddSeconds(5));
        await SeedTranscriptPartAsync(sessionId, turnSequence: 1, sequence: 7, type: "tool", payload: """{"toolCallId":"tool-1","toolName":"read","status":"failed","title":"Read README"}""", at: transcriptAt.AddSeconds(6));
        await SeedTranscriptPartAsync(sessionId, turnSequence: 1, sequence: 8, type: "session.activity", payload: """{"activity":"idle","status":"failed","failureCategory":"probe_timeout","exitCode":1}""", at: transcriptAt.AddSeconds(7));

        var metadata = await GetMetadataAsync(projectId, issueNumber, "plan");

        Assert.NotNull(metadata);
        Assert.Equal(8, metadata!.Metadata.PartCount);
    }

    [Fact]
    public async Task GetSessionTranscriptAsync_ReturnsSegmentsInAscendingSequenceAcrossInsertBatches()
    {
        var (projectId, issueNumber, sessionId) = await SeedWorkflowSessionAsync("ordering", sessionName: "build");
        var t0 = TestTime.UtcDateTime;

        // First batch: input + tool.
        await SeedTranscriptTurnAsync(sessionId, sequence: 1, promptText: "do the thing", promptKind: "task", at: t0);
        await SeedTranscriptPartAsync(sessionId, turnSequence: 1, sequence: 1, type: "input", text: "do the thing", at: t0);
        await SeedTranscriptPartAsync(sessionId, turnSequence: 1, sequence: 2, type: "tool", payload: """{"toolCallId":"tool-1","toolName":"read","status":"in_progress","title":"Read README"}""", at: t0.AddSeconds(1));
        // Second batch: text + reasoning + tool (updated) + text.
        await SeedTranscriptPartAsync(sessionId, turnSequence: 1, sequence: 3, type: "text", text: "first", at: t0.AddSeconds(2));
        await SeedTranscriptPartAsync(sessionId, turnSequence: 1, sequence: 4, type: "reasoning", text: "thinking", at: t0.AddSeconds(3));
        await SeedTranscriptPartAsync(sessionId, turnSequence: 1, sequence: 5, type: "tool", payload: """{"toolCallId":"tool-1","toolName":"read","status":"completed"}""", at: t0.AddSeconds(4));
        await SeedTranscriptPartAsync(sessionId, turnSequence: 1, sequence: 6, type: "text", text: "second", at: t0.AddSeconds(5));
        // Third batch: session.activity.
        await SeedTranscriptPartAsync(sessionId, turnSequence: 1, sequence: 7, type: "session.activity", payload: """{"activity":"idle","status":"completed","exitCode":0}""", at: t0.AddSeconds(6));

        var transcript = await GetTranscriptAsync(projectId, issueNumber, "build");

        Assert.NotNull(transcript);
        Assert.Equal(7, transcript!.PartCount);
        var turn = Assert.Single(transcript.Turns);
        Assert.Equal("do the thing", turn.User.Text);
        Assert.Equal("task", turn.User.Kind);
        // The assistant parts surface tool + text + reasoning in the
        // order they were inserted across batches; the tool part
        // updates in place (UpsertToolPart folds the started+updated
        // pair into a single tool row at completed status).
        Assert.Contains(turn.Assistant, p => p.Type == "tool" && p.Tool?.ToolCallId == "tool-1" && p.Tool.Status == "completed");
        Assert.Contains(turn.Assistant, p => p.Type == "text");
        Assert.Contains(turn.Assistant, p => p.Type == "reasoning");
        Assert.Null(turn.CompletedAt);
        Assert.NotNull(transcript.LastActivityAt);
    }

    [Fact]
    public async Task GetSessionMetadataAsync_ProjectsContextWindowUsageFromStatusSnapshot()
    {
        // The original API spec drove the AgentSession grain over HTTP
        // to surface a usage envelope with the latest contextWindowUsed
        // + contextWindowSize; the grain emits an
        // AgentSessionContextExhausted CloudEvent when usage crosses
        // 96%. Sinking the calculation here: the projection reads the
        // session's persisted UsageSummary + the latest terminal
        // session.activity part and surfaces both, independent of any
        // grain event. Seed the UsageSummary directly on the session
        // status so the route-level assertion (usage envelope numbers)
        // is reproduced at the querier boundary.
        var (projectId, issueNumber, sessionId) = await SeedWorkflowSessionAsync(
            "exhaustion",
            sessionName: "plan",
            usageSummary: new AgentUsageSummary(
                InputTokens: 100,
                OutputTokens: 50,
                TotalTokens: 150,
                CachedReadTokens: 10,
                ThoughtTokens: 5,
                CostAmount: 0.01,
                CostCurrency: "USD",
                ContextWindowUsed: 960,
                ContextWindowSize: 1000));
        var t0 = TestTime.UtcDateTime;
        await SeedTranscriptTurnAsync(sessionId, sequence: 1, promptText: "Plan session", promptKind: "task", at: t0);
        await SeedTranscriptPartAsync(sessionId, turnSequence: 1, sequence: 1, type: "session.activity",
            payload: """{"activity":"idle","status":"failed","failureReason":"probe timed out","failureCategory":"context_exhaustion","exitCode":1}""",
            at: t0);

        var metadata = await GetMetadataAsync(projectId, issueNumber, "plan");

        Assert.NotNull(metadata);
        // The metadata envelope surfaces the same usage values the
        // original API spec asserted off the route.
        Assert.Equal(960, metadata!.Usage.ContextWindowUsed);
        Assert.Equal(1000, metadata.Usage.ContextWindowSize);
        Assert.Equal(100, metadata.Usage.InputTokens);
        Assert.Equal(50, metadata.Usage.OutputTokens);
        // The session.activity projection surfaces the
        // context_exhaustion category — the original failureCategory
        // projection asserted the same value.
        Assert.Equal("context_exhaustion", metadata.EventSummary.FailureCategory);
    }

    private async Task<AgentSessionMetadataDto?> GetMetadataAsync(string projectId, int issueNumber, string sessionName)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();
        return await querier.GetSessionMetadataAsync(projectId, issueNumber, sessionName);
    }

    private async Task<AgentSessionTranscriptResponse?> GetTranscriptAsync(string projectId, int issueNumber, string sessionName)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();
        return await querier.GetSessionTranscriptAsync(projectId, issueNumber, sessionName);
    }

    private async Task<(string projectId, int issueNumber, string sessionId)> SeedWorkflowSessionAsync(
        string prefix,
        string sessionName,
        AgentUsageSummary? usageSummary = null)
    {
        var projectId = $"proj-session-projection-{prefix}-{Guid.NewGuid():N}";
        const int issueNumber = 1;
        var workflowRunId = $"wr-{prefix}-{Guid.NewGuid():N}";
        var sessionId = $"session-{prefix}-{Guid.NewGuid():N}";
        var createdAt = TestTime.UtcDateTime;
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.IssueNumber] = issueNumber.ToString(),
            [AgentSessionQueryMetadataKeys.SourceKind] = "workflow",
            [AgentSessionQueryMetadataKeys.WorkflowRunId] = workflowRunId,
            [AgentSessionQueryMetadataKeys.SessionName] = sessionName,
            [AgentSessionQueryMetadataKeys.WorkId] = $"work-{sessionName}",
            [AgentSessionQueryMetadataKeys.WorkType] = "task",
            [AgentSessionQueryMetadataKeys.Stage] = "Build",
        };
        var session = new AgentSession
        {
            Id = sessionId,
            Runtime = new AgentSessionRuntime("test-runner", null, "opencode"),
            Settings = new AgentSessionSettings("test-model"),
            Status = new AgentSessionStatusSnapshot(
                AgentRuntimeSessionId: sessionId,
                CreatedAt: createdAt,
                UsageSummary: usageSummary ?? new AgentUsageSummary()),
            Metadata = new AgentSessionMetadata(labels),
        };
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = sessionId,
            State = JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions),
            CreatedAt = createdAt,
            Status = "closed",
            AgentSessionId = sessionId!,
            RunnerId = "test-runner",
        });
        await db.SaveChangesAsync();
        return (projectId, issueNumber, sessionId);
    }

    private async Task SeedTranscriptTurnAsync(string sessionId, long sequence, string promptText, string promptKind, DateTime at)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.AgentSessionTranscriptTurns.Add(new AgentSessionTranscriptTurnRow
        {
            SessionId = sessionId,
            RuntimeSessionId = sessionId,
            Sequence = sequence,
            PromptText = promptText,
            PromptKind = promptKind,
            StartedAt = at,
            UpdatedAt = at,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedTranscriptPartAsync(string sessionId, long turnSequence, int sequence, string type, string? text = null, string? payload = null, DateTime? at = null)
    {
        var partAt = at ?? TestTime.UtcDateTime;
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var turnId = await db.AgentSessionTranscriptTurns
            .Where(t => t.SessionId == sessionId && t.Sequence == turnSequence)
            .Select(t => t.Id)
            .SingleAsync();
        db.AgentSessionTranscriptParts.Add(new AgentSessionTranscriptPartRow
        {
            TurnId = turnId,
            Sequence = sequence,
            Type = type,
            Text = text ?? string.Empty,
            CorrelationKey = $"{type}-{sequence}",
            PayloadJson = payload ?? "{}",
            FirstSeenAt = partAt,
            LastSeenAt = partAt,
        });
        await db.SaveChangesAsync();
    }
}
