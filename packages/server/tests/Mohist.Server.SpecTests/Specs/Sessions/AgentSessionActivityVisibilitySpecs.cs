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

/// <summary>
/// Route contract for the active-agent readout at
/// <c>/api/projects/{projectRef}/agent/status</c>. The per-session
/// activity-card projection (generic sessions carry
/// <c>agentId</c>/<c>agentName</c>; workflow sessions do not) lives in
/// <c>AgentActivityCardQuerierSpecs</c>.
/// </summary>
[Collection("IntegrationSessions")]
public class AgentSessionActivityVisibilitySpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public AgentSessionActivityVisibilitySpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task ActiveAgents_GenericSession_AppearsDespiteBlankWorkflowRunId()
    {
        var project = await CreateProjectAsync($"gen-activeagents-{Guid.NewGuid():N}");
        var agentId = "agent_activeGenAgent";
        var agentName = "active-gen-agent";
        var sessionId = $"session-{Guid.NewGuid():N}";
        var runnerId = $"runner-{Guid.NewGuid():N}";

        await InsertActiveGenericSessionAsync(project, sessionId, agentId, agentName, runnerId);

        var status = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{project}/agent/status");
        var activeAgents = status.GetProperty("activeAgents").EnumerateArray().ToList();

        var entry = Assert.Single(activeAgents, a => a.GetProperty("sessionId").GetString() == sessionId);
        Assert.Equal(agentId, entry.GetProperty("agentId").GetString());
        Assert.Equal(agentName, entry.GetProperty("agentName").GetString());
    }

    [Fact]
    public async Task ActiveAgents_StaleGenericSession_IsNotReported()
    {
        var project = await CreateProjectAsync($"gen-stale-activeagents-{Guid.NewGuid():N}");
        var sessionId = $"session-{Guid.NewGuid():N}";

        await InsertGenericSessionAsync(project, sessionId, "agent_stale", "stale-agent", issueNumber: null);

        var status = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{project}/agent/status");
        var activeAgents = status.GetProperty("activeAgents").EnumerateArray().ToList();

        Assert.DoesNotContain(activeAgents, agent => agent.GetProperty("sessionId").GetString() == sessionId);
    }

    [Fact]
    public async Task ActiveAgents_GenericSession_PresentWithoutIssueRef()
    {
        var project = await CreateProjectAsync($"gen-noissue-active-{Guid.NewGuid():N}");
        var sessionId = $"session-{Guid.NewGuid():N}";
        var runnerId = $"runner-{Guid.NewGuid():N}";

        await InsertActiveGenericSessionAsync(project, sessionId, "agent_noIssueRef", "no-issue-ref-agent", runnerId);

        var status = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{project}/agent/status");
        var activeAgents = status.GetProperty("activeAgents").EnumerateArray().ToList();

        Assert.Contains(activeAgents, agent => agent.GetProperty("sessionId").GetString() == sessionId);
    }

    private async Task InsertGenericSessionAsync(
        string projectId,
        string sessionId,
        string agentId,
        string agentName,
        int? issueNumber)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            [GenericAgentSessionMetadata.AgentId] = agentId,
            [GenericAgentSessionMetadata.AgentName] = agentName,
        };
        if (issueNumber.HasValue)
            labels[AgentSessionQueryMetadataKeys.IssueNumber] = issueNumber.Value.ToString();

        var session = new AgentSession
        {
            Id = sessionId,
            Runtime = new AgentSessionRuntime("test-runner", null),
            Settings = new AgentSessionSettings("test-model"),
            Status = new AgentSessionStatusSnapshot(
                CreatedAt: TestTime.UtcDateTime,
                AgentRuntimeSessionId: sessionId),
            Metadata = new AgentSessionMetadata(labels),
        };

        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = session.Id,
            State = JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions),
            CreatedAt = TestTime.UtcDateTime,
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

        var session = new AgentSession
        {
            Id = sessionId,
            Runtime = new AgentSessionRuntime(runnerId, null),
            Settings = new AgentSessionSettings("test-model"),
            Status = new AgentSessionStatusSnapshot(
                CreatedAt: startedAt,
                BoundAt: startedAt.AddSeconds(1),
                LastDataAt: _fixture.TimeProvider.GetUtcNow().UtcDateTime,
                AgentRuntimeSessionId: sessionId,
                Activity: AgentSessionActivity.Active),
            Metadata = new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = agentId,
                [GenericAgentSessionMetadata.AgentName] = agentName,
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
            RunnerId = runnerId,
        });
        await db.SaveChangesAsync();
    }

    private async Task<string> CreateProjectAsync(string name)
    {
        using var response = await _client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("id").GetString()!;
    }
}