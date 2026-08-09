using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

[Collection("IntegrationSessions")]
public class AgentSessionReadApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public AgentSessionReadApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task ListAgentSessions_UnknownAgentRef_Returns404()
    {
        var project = await CreateProjectAsync("list-404");

        using var response = await _client.GetAsync(
            $"/api/projects/{project}/agents/agent_unknown/sessions");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Summary_GenericSession_ReturnsEnrichedDto()
    {
        var project = await CreateProjectAsync("summary-enriched");
        var agentId = "agent_summaryAgent";
        var agentName = "summary-agent";
        var sessionId = $"sess-{Guid.NewGuid():N}";

        await InsertGenericSessionAsync(project, sessionId, agentId, agentName, issueNumber: 7);

        var summary = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{project}/agent-sessions/{sessionId}");

        Assert.Equal(sessionId, summary.GetProperty("sessionId").GetString());
        Assert.Equal(agentId, summary.GetProperty("agentId").GetString());
        Assert.Equal(agentName, summary.GetProperty("agentName").GetString());
        Assert.NotNull(summary.GetProperty("activity").GetString());
        Assert.NotNull(summary.GetProperty("createdAt").GetString());
        Assert.NotEqual(JsonValueKind.Undefined, summary.GetProperty("usage").ValueKind);

        Assert.False(summary.TryGetProperty("workflowRunId", out _));
        Assert.False(summary.TryGetProperty("sessionName", out _));
        Assert.False(summary.TryGetProperty("workId", out _));
    }

    [Fact]
    public async Task Summary_UnknownSessionId_Returns404()
    {
        var project = await CreateProjectAsync("summary-404");

        using var response = await _client.GetAsync(
            $"/api/projects/{project}/agent-sessions/sess_unknown");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Summary_WorkflowSession_Returns404()
    {
        var project = await CreateProjectAsync("summary-wf-404");
        var sessionId = $"sess-{Guid.NewGuid():N}";

        await InsertWorkflowSessionAsync(project, sessionId);

        using var response = await _client.GetAsync(
            $"/api/projects/{project}/agent-sessions/{sessionId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Transcript_GenericSession_ReturnsTranscriptWithoutWorkflowRunId()
    {
        var project = await CreateProjectAsync("transcript-gen");
        var agentId = "agent_transcriptAgent";
        var agentName = "transcript-agent";
        var sessionId = $"sess-{Guid.NewGuid():N}";

        await InsertGenericSessionAsync(project, sessionId, agentId, agentName);

        var transcript = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{project}/agent-sessions/{sessionId}/transcript");

        Assert.True(transcript.TryGetProperty("turns", out _));
        Assert.False(transcript.TryGetProperty("workflowRunId", out _));
    }

    [Fact]
    public async Task Transcript_UnknownSessionId_Returns404()
    {
        var project = await CreateProjectAsync("transcript-404");

        using var response = await _client.GetAsync(
            $"/api/projects/{project}/agent-sessions/sess_unknown/transcript");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Transcript_WorkflowSession_Returns404()
    {
        var project = await CreateProjectAsync("transcript-wf-404");
        var sessionId = $"sess-{Guid.NewGuid():N}";

        await InsertWorkflowSessionAsync(project, sessionId);

        using var response = await _client.GetAsync(
            $"/api/projects/{project}/agent-sessions/{sessionId}/transcript");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListIsDistinctFromProjectWideList()
    {
        var project = await CreateProjectAsync("list-distinct");
        var agentName = "distinct-agent";
        var agent = await CreateAgentAsync(project, agentName);
        var genericSession = $"sess-{Guid.NewGuid():N}";
        var workflowSession = $"sess-{Guid.NewGuid():N}";

        await InsertGenericSessionAsync(project, genericSession, agent.Id, agentName);
        await InsertWorkflowSessionAsync(project, workflowSession);

        var agentScoped = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{project}/agents/{agent.Id}/sessions");
        var projectWide = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{project}/agent/sessions");

        var agentSessionIds = agentScoped.EnumerateArray().Select(s => s.GetProperty("sessionId").GetString()).ToHashSet();
        Assert.Contains(genericSession, agentSessionIds);
        Assert.DoesNotContain(workflowSession, agentSessionIds);

        var projectSessionIds = projectWide.EnumerateArray().Select(s => s.GetProperty("sessionId").GetString()).ToHashSet();
        Assert.Contains(genericSession, projectSessionIds);
        Assert.Contains(workflowSession, projectSessionIds);
    }

    [Fact]
    public async Task History_ReturnsCanonicalTurnSummaryAndPublicContext()
    {
        var project = await CreateProjectAsync("history-contract");
        var agent = await CreateAgentAsync(project, "history-agent");
        var sessionId = $"sess-{Guid.NewGuid():N}";
        var started = TestTime.UtcDateTime.AddMinutes(-1);
        var ended = started.AddSeconds(9);

        await InsertGenericSessionAsync(
            project,
            sessionId,
            agent.Id,
            agent.Name,
            issueNumber: 385,
            createdAt: started,
            inputs:
            [
                new AgentSessionInputRecord(
                    "input-history",
                    1,
                    "Review history contract",
                    "agent-launch",
                    AgentSessionInputAcceptance.Accepted,
                    started,
                    JobId: "job-history")
            ],
            turns:
            [
                new AgentTurnRecord(
                    "turn-history",
                    1,
                    ["input-history"],
                    AgentTurnStatus.Completed,
                    JobId: "job-history",
                    Result: new AgentTurnResult(Message: "complete", Output: "result"),
                    RecordedAt: started,
                    UpdatedAt: ended)
            ],
            usage: new AgentUsageSummary(CostAmount: 1.5, CostCurrency: "USD"),
            repository: "suraciii/mohist",
            workspaceName: "history",
            workspacePath: "/private/history-worktree",
            targetId: "target-history");

        var history = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{project}/agents/{agent.Id}/history?limit=10");

        var row = Assert.Single(history.EnumerateArray());
        Assert.Equal("turn-history", row.GetProperty("id").GetString());
        Assert.Equal(sessionId, row.GetProperty("sessionId").GetString());
        Assert.Equal("input-history", row.GetProperty("inputId").GetString());
        Assert.Equal("turn-history", row.GetProperty("turnId").GetString());
        Assert.Equal("job-history", row.GetProperty("jobId").GetString());
        Assert.Equal("Review history contract", row.GetProperty("task").GetString());
        Assert.Equal("completed", row.GetProperty("status").GetString());
        Assert.Equal("success", row.GetProperty("outcome").GetString());
        Assert.Equal(started.ToString("o"), row.GetProperty("startedAt").GetString());
        Assert.Equal(9_000, row.GetProperty("durationMs").GetInt64());
        Assert.Equal("test-model", row.GetProperty("model").GetString());
        Assert.Equal(1.5, row.GetProperty("cost").GetProperty("amount").GetDouble());
        Assert.Equal("USD", row.GetProperty("cost").GetProperty("currency").GetString());
        Assert.Equal("history", row.GetProperty("workspace").GetString());
        Assert.Equal("target-history", row.GetProperty("target").GetString());
        Assert.Equal("recent", row.GetProperty("bucket").GetString());

        var context = row.GetProperty("context");
        Assert.Equal(385, context.GetProperty("issueNumber").GetInt32());
        Assert.Equal("suraciii/mohist", context.GetProperty("repository").GetString());
        Assert.Equal("history", context.GetProperty("workspaceName").GetString());
        Assert.DoesNotContain("private/history-worktree", row.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("workspacePath", row.ToString(), StringComparison.Ordinal);
    }

    private async Task InsertGenericSessionAsync(
        string projectId,
        string sessionId,
        string agentId,
        string agentName,
        int? issueNumber = null,
        DateTime? createdAt = null,
        IReadOnlyList<AgentSessionInputRecord>? inputs = null,
        IReadOnlyList<AgentTurnRecord>? turns = null,
        AgentUsageSummary? usage = null,
        string? repository = null,
        string? workspaceName = null,
        string? workspacePath = null,
        string? targetId = null)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            [GenericAgentSessionMetadata.AgentId] = agentId,
            [GenericAgentSessionMetadata.AgentName] = agentName,
        };
        if (issueNumber.HasValue)
            labels[GenericAgentSessionMetadata.IssueNumber] = issueNumber.Value.ToString();
        if (!string.IsNullOrWhiteSpace(repository))
            labels[GenericAgentSessionMetadata.Repository] = repository;
        if (!string.IsNullOrWhiteSpace(workspaceName))
            labels[GenericAgentSessionMetadata.WorkspaceName] = workspaceName;
        if (!string.IsNullOrWhiteSpace(workspacePath))
            labels[GenericAgentSessionMetadata.WorkspacePath] = workspacePath;
        if (!string.IsNullOrWhiteSpace(targetId))
            labels[GenericAgentSessionMetadata.TargetId] = targetId;

        var created = createdAt ?? TestTime.UtcDateTime;
        var session = new AgentSession
        {
            Id = sessionId,
            Runtime = new AgentSessionRuntime("test-runner", null, "opencode"),
            Settings = new AgentSessionSettings("test-model"),
            Status = new AgentSessionStatusSnapshot(
                CreatedAt: created,
                AgentRuntimeSessionId: sessionId,
                UsageSummary: usage,
                Inputs: inputs,
                Turns: turns),
            Metadata = new AgentSessionMetadata(labels),
        };

        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = session.Id,
            State = JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions),
            CreatedAt = created,
            Status = "opened",
            AgentSessionId = sessionId,
            RunnerId = "test-runner",
        });
        await db.SaveChangesAsync();
    }

    

    private async Task InsertWorkflowSessionAsync(
        string projectId,
        string sessionId)
    {
        var startedAt = TestTime.UtcDateTime.AddMinutes(-10);
        var workflowRunId = $"wf-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";

        var session = new AgentSession
        {
            Id = sessionId,
            Runtime = new AgentSessionRuntime("test-runner", null),
            Settings = new AgentSessionSettings("test-model"),
            Status = new AgentSessionStatusSnapshot(
                CreatedAt: startedAt,
                BoundAt: startedAt.AddSeconds(1),
                LastDataAt: TestTime.UtcDateTime,
                AgentRuntimeSessionId: sessionId),
            Metadata = new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "workflow",
                [AgentSessionQueryMetadataKeys.WorkflowRunId] = workflowRunId,
                [AgentSessionQueryMetadataKeys.WorkId] = workId,
                [AgentSessionQueryMetadataKeys.WorkType] = "task",
                [AgentSessionQueryMetadataKeys.Stage] = "Build",
                [AgentSessionQueryMetadataKeys.IssueNumber] = "1",
                [AgentSessionQueryMetadataKeys.SessionName] = "plan",
            }),
        };

        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = session.Id,
            State = JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions),
            CreatedAt = startedAt,
            Status = "bound",
            AgentSessionId = sessionId,
            RunnerId = "test-runner",
        });
        await db.SaveChangesAsync();
    }

    private async Task<AgentRef> CreateAgentAsync(string projectId, string name)
    {
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name,
                description = $"description for {name}",
                instructions = $"instructions for {name}",
                agentConfig = new { model = "openai/gpt-5.6" },
                skills = new[] { "coding" },
                maxConcurrentRuns = 1,
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new AgentRef(body.GetProperty("data").GetProperty("id").GetString()!, name);
    }

    private async Task<string> CreateProjectAsync(string prefix)
    {
        var raw = $"{prefix}-{Guid.NewGuid():N}".ToLowerInvariant();
        var name = raw.Length > 63 ? raw[..63] : raw;
        using var response = await _client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("id").GetString()
            ?? throw new InvalidOperationException($"CreateProject '{name}' returned no id");
    }

    private sealed record AgentRef(string Id, string Name);
}
