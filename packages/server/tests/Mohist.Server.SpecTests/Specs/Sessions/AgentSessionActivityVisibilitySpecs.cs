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
    public async Task ActivityCard_ForGenericAgentLaunchSession_CarriesAgentIdAndAgentName()
    {
        var project = await CreateProjectAsync("gen-activity-agent");
        var agentId = "agent_testAgent1";
        var agentName = "test-agent-one";
        var sessionId = $"session-{Guid.NewGuid():N}";

        await InsertGenericSessionAsync(project, sessionId, agentId, agentName, issueNumber: null);

        var activity = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{project}/agent/activity");
        var sessions = activity.GetProperty("sessions").EnumerateArray()
            .ToList();

        var card = Assert.Single(sessions, s => s.GetProperty("sessionId").GetString() == sessionId);
        Assert.Equal(agentId, card.GetProperty("agentId").GetString());
        Assert.Equal(agentName, card.GetProperty("agentName").GetString());
    }

    [Fact]
    public async Task ActivityCard_ForGenericSessionWithoutIssueRef_ProducesNoSyntheticIssueCard()
    {
        var project = await CreateProjectAsync("gen-activity-noissue");
        var agentId = "agent_noIssueAgent";
        var agentName = "no-issue-agent";
        var sessionId = $"session-{Guid.NewGuid():N}";

        await InsertGenericSessionAsync(project, sessionId, agentId, agentName, issueNumber: null);

        var activity = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{project}/agent/activity");
        var sessions = activity.GetProperty("sessions").EnumerateArray()
            .ToList();

        var card = Assert.Single(sessions, s => s.GetProperty("sessionId").GetString() == sessionId);
        Assert.Equal(0, card.GetProperty("issueNumber").GetInt32());
        Assert.Equal(agentId, card.GetProperty("agentId").GetString());
        Assert.Equal(agentName, card.GetProperty("agentName").GetString());
    }

    [Fact]
    public async Task ActivityCard_ForGenericSessionWithIssueRef_IsAssociatedButAgentAttributed()
    {
        var project = await CreateProjectAsync("gen-activity-wissue");
        var agentId = "agent_withIssueAgent";
        var agentName = "with-issue-agent";
        var sessionId = $"session-{Guid.NewGuid():N}";
        const int issueNumber = 42;

        await InsertGenericSessionAsync(project, sessionId, agentId, agentName, issueNumber);

        var activity = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{project}/agent/activity");
        var sessions = activity.GetProperty("sessions").EnumerateArray()
            .ToList();

        var card = Assert.Single(sessions, s => s.GetProperty("sessionId").GetString() == sessionId);
        Assert.Equal(issueNumber, card.GetProperty("issueNumber").GetInt32());
        Assert.Equal(agentId, card.GetProperty("agentId").GetString());
        Assert.Equal(agentName, card.GetProperty("agentName").GetString());
    }

    [Fact]
    public async Task ActiveAgents_GenericSession_AppearsDespiteBlankWorkflowRunId()
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

    [Fact]
    public async Task WorkflowActivityCard_DoesNotLeakAgentIdOrAgentName()
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
                LastDataAt: TestTime.UtcDateTime,
                AgentRuntimeSessionId: sessionId),
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
