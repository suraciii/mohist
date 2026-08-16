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
public class AgentSessionActivityVisibilitySpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public AgentSessionActivityVisibilitySpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    /// <summary>
    /// Single wire case for <c>GET /api/projects/{projectRef}/agent/activity</c>:
    /// the mixed generic/workflow card JSON shape. Generic cards carry
    /// <c>agentId</c>/<c>agentName</c>; workflow cards must not leak them.
    /// Card projection and attribution semantics are owned by
    /// <see cref="AgentActivityFeedAssemblerSpecs"/>; activeAgents selection
    /// by <see cref="AgentStatusHistoryBoundedSelectionSpecs"/>.
    /// </summary>
    [Fact]
    public async Task ActivityCards_MixedGenericAndWorkflowSessions_ExposeContractualJsonShape()
    {
        var project = await CreateProjectAsync("gen-wf-regression");
        var agentId = "agent_wfAgent";
        var agentName = "wf-agent";
        var genericSessionId = $"session-{Guid.NewGuid():N}";
        var workflowSessionId = $"session-{Guid.NewGuid():N}";
        var runnerId = $"runner-{Guid.NewGuid():N}";

        await InsertActiveGenericSessionAsync(project, genericSessionId, agentId, agentName, runnerId);
        await InsertWorkflowSessionAsync(project, workflowSessionId, runnerId);

        var activity = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{project}/agent/activity");
        var sessions = activity.GetProperty("sessions").EnumerateArray()
            .ToList();

        var wfCard = Assert.Single(sessions, s => s.GetProperty("sessionId").GetString() == workflowSessionId);
        Assert.False(wfCard.TryGetProperty("agentId", out _), "Workflow card must not carry agentId");
        Assert.False(wfCard.TryGetProperty("agentName", out _), "Workflow card must not carry agentName");

        var genericCard = Assert.Single(sessions, s => s.GetProperty("sessionId").GetString() == genericSessionId);
        Assert.Equal(agentId, genericCard.GetProperty("agentId").GetString());
        Assert.Equal(agentName, genericCard.GetProperty("agentName").GetString());
        Assert.Equal(0, genericCard.GetProperty("issueNumber").GetInt32());
    }

    /// <summary>
    /// Single wire case for the <c>activeAgents</c> array on
    /// <c>GET /api/projects/{projectRef}/agent/status</c>: a generic
    /// agent-launch session's entry shape. Candidate selection and stale
    /// exclusion are owned by <see cref="AgentStatusHistoryBoundedSelectionSpecs"/>.
    /// </summary>
    [Fact]
    public async Task ActiveAgents_GenericSession_ExposesContractualEntryShape()
    {
        var project = await CreateProjectAsync("gen-activeagents");
        var agentId = "agent_activeGenAgent";
        var agentName = "active-gen-agent";
        var sessionId = $"session-{Guid.NewGuid():N}";
        var runnerId = $"runner-{Guid.NewGuid():N}";

        await InsertActiveGenericSessionAsync(project, sessionId, agentId, agentName, runnerId);

        var status = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{project}/agent/status");
        var activeAgents = status.GetProperty("activeAgents").EnumerateArray()
            .ToList();

        var entry = Assert.Single(activeAgents, a => a.GetProperty("sessionId").GetString() == sessionId);
        Assert.Equal(agentId, entry.GetProperty("agentId").GetString());
        Assert.Equal(agentName, entry.GetProperty("agentName").GetString());
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

    private async Task InsertWorkflowSessionAsync(
        string projectId,
        string sessionId,
        string runnerId)
    {
        var startedAt = TestTime.UtcDateTime.AddMinutes(-10);
        var workflowRunId = $"wf-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";

        var session = new AgentSession
        {
            Id = sessionId,
            Runtime = new AgentSessionRuntime(runnerId, null),
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
            RunnerId = runnerId,
        });
        await db.SaveChangesAsync();
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
}
