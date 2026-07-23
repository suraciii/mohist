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
    public async Task ListAgentSessions_ByAgentId_ReturnsRecencyOrderedGenericSessions()
    {
        var project = await CreateProjectAsync("list-by-agent");
        var agentName = "list-by-agent-name";
        var agent = await CreateAgentAsync(project, agentName);
        var sessionIds = new[] { "sess-latest", "sess-mid", "sess-oldest" };

        await InsertGenericSessionAsync(project, sessionIds[2], agent.Id, agentName, createdAt: TestTime.UtcDateTime.AddHours(-3));
        await InsertGenericSessionAsync(project, sessionIds[1], agent.Id, agentName, createdAt: TestTime.UtcDateTime.AddHours(-2));
        await InsertGenericSessionAsync(project, sessionIds[0], agent.Id, agentName, createdAt: TestTime.UtcDateTime.AddHours(-1));

        var list = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{project}/agents/{agent.Id}/sessions");

        var items = list.EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal("sess-latest", items[0].GetProperty("sessionId").GetString());
        Assert.Equal("sess-mid", items[1].GetProperty("sessionId").GetString());
        Assert.Equal("sess-oldest", items[2].GetProperty("sessionId").GetString());

        foreach (var item in items)
        {
            Assert.Equal(agent.Id, item.GetProperty("agentId").GetString());
            Assert.Equal(agentName, item.GetProperty("agentName").GetString());
            Assert.NotNull(item.GetProperty("activity").GetString());
            Assert.NotNull(item.GetProperty("createdAt").GetString());
        }
    }

    [Fact]
    public async Task ListAgentSessions_ByAgentName_ResolvesToSameSet()
    {
        var project = await CreateProjectAsync("list-by-name");
        var agentName = "list-resolve-name";
        var agent = await CreateAgentAsync(project, agentName);
        var sessionId = $"sess-{Guid.NewGuid():N}";

        await InsertGenericSessionAsync(project, sessionId, agent.Id, agentName);

        var byId = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{project}/agents/{agent.Id}/sessions");
        var byName = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{project}/agents/{agentName}/sessions");

        var byIdCount = byId.EnumerateArray().Count();
        var byNameCount = byName.EnumerateArray().Count();
        Assert.Equal(byIdCount, byNameCount);
        Assert.True(byIdCount >= 1);
    }

    [Fact]
    public async Task ListAgentSessions_StatusFilter_ReturnsOnlyMatchingStatus()
    {
        var project = await CreateProjectAsync("list-status");
        var agentName = "list-status-agent";
        var agent = await CreateAgentAsync(project, agentName);
        var runningSession = $"sess-{Guid.NewGuid():N}";
        var failedSession = $"sess-{Guid.NewGuid():N}";

        await InsertActiveGenericSessionAsync(project, runningSession, agent.Id, agentName, "test-runner");
        await InsertFailedGenericSessionAsync(project, failedSession, agent.Id, agentName);

        // Under the activity model the ?status= filter matches the activity
        // vocabulary (idle/active/unknown); the legacy running/failed values
        // no longer match anything. The active helper seeds an `active`
        // session and the failed helper seeds an `idle` one, so the filter
        // narrows to exactly those rows.
        var running = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{project}/agents/{agent.Id}/sessions?status=active");
        Assert.Single(running.EnumerateArray(), r => r.GetProperty("sessionId").GetString() == runningSession);

        var failed = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{project}/agents/{agent.Id}/sessions?status=idle");
        Assert.Single(failed.EnumerateArray(), f => f.GetProperty("sessionId").GetString() == failedSession);

        var multi = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{project}/agents/{agent.Id}/sessions?status=active,idle");
        Assert.Equal(2, multi.EnumerateArray().Count());
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

    private async Task InsertGenericSessionAsync(
        string projectId,
        string sessionId,
        string agentId,
        string agentName,
        int? issueNumber = null,
        DateTime? createdAt = null)
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

        var created = createdAt ?? TestTime.UtcDateTime;
        var session = new AgentSession
        {
            Id = sessionId,
            Runtime = new AgentSessionRuntime("test-runner", null, "opencode"),
            Settings = new AgentSessionSettings("test-model"),
            Status = new AgentSessionStatusSnapshot(
                CreatedAt: created,
                AgentRuntimeSessionId: sessionId),
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

    private async Task InsertActiveGenericSessionAsync(
        string projectId,
        string sessionId,
        string agentId,
        string agentName,
        string runnerId)
    {
        var startedAt = TestTime.UtcDateTime.AddMinutes(-5);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            [GenericAgentSessionMetadata.AgentId] = agentId,
            [GenericAgentSessionMetadata.AgentName] = agentName,
        };

        var session = new AgentSession
        {
            Id = sessionId,
            Runtime = new AgentSessionRuntime(runnerId, null),
            Settings = new AgentSessionSettings("test-model"),
            Status = new AgentSessionStatusSnapshot(
                CreatedAt: startedAt,
                BoundAt: startedAt.AddSeconds(1),
                LastDataAt: TestTime.UtcDateTime,
                AgentRuntimeSessionId: sessionId,
                Activity: AgentSessionActivity.Active),
            Metadata = new AgentSessionMetadata(labels),
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
            RunnerId = runnerId,
        });
        await db.SaveChangesAsync();
    }

    private async Task InsertFailedGenericSessionAsync(
        string projectId,
        string sessionId,
        string agentId,
        string agentName)
    {
        var startedAt = TestTime.UtcDateTime.AddMinutes(-10);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            [GenericAgentSessionMetadata.AgentId] = agentId,
            [GenericAgentSessionMetadata.AgentName] = agentName,
        };

        var session = new AgentSession
        {
            Id = sessionId,
            Runtime = new AgentSessionRuntime("test-runner", null),
            Settings = new AgentSessionSettings("test-model"),
            Status = new AgentSessionStatusSnapshot(
                CreatedAt: startedAt,
                BoundAt: startedAt.AddSeconds(1),
                LastDataAt: startedAt.AddMinutes(5),
                AgentRuntimeSessionId: sessionId),
            Metadata = new AgentSessionMetadata(labels),
        };

        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();

        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = session.Id,
            State = JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions),
            CreatedAt = startedAt,
            Status = "closed",
            AgentSessionId = sessionId,
            RunnerId = "test-runner",
        });

        var turn = new AgentSessionTranscriptTurnRow
        {
            SessionId = sessionId,
            RuntimeSessionId = sessionId,
            Sequence = 1,
            StartedAt = startedAt,
            UpdatedAt = startedAt.AddMinutes(5),
        };
        db.AgentSessionTranscriptTurns.Add(turn);
        await db.SaveChangesAsync();

        db.AgentSessionTranscriptParts.Add(new AgentSessionTranscriptPartRow
        {
            TurnId = turn.Id,
            Sequence = 1,
            Type = TranscriptPartTypes.SessionClosed,
            CorrelationKey = $"session.closed_{Guid.NewGuid():N}",
            PayloadJson = $$"""{"status":"failed","ts":"{{startedAt.AddMinutes(5):O}}"}""",
            LastSeenAt = startedAt.AddMinutes(5),
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
