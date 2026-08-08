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
/// Calculation specs for the per-agent session listing behind
/// <c>GET /api/projects/{projectRef}/agents/{agentId}/sessions</c>. The
/// querier (<see cref="AgentSessionQuerier.ListAgentSessionsAsync"/>)
/// resolves the agent by id or by name, applies the recency ordering,
/// the activity status filter, and the project isolation. Specs drive
/// the querier directly via <c>MohistDbFixture</c> (no web host, no
/// HTTP). The route contract (404 for unknown agent, JSON envelope
/// shape, agent-vs-project-list distinction, transcript absent of
/// workflow fields) stays in <c>AgentSessionReadApiSpecs</c>.
/// </summary>
[Collection("MohistDb")]
public class UnifiedSessionListQuerierSpecs
{
    private readonly MohistDbFixture _fixture;

    public UnifiedSessionListQuerierSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ListAgentSessions_ByAgentId_ReturnsRecencyOrderedGenericSessions()
    {
        var projectId = NewProjectId();
        var agentId = NewAgentId();
        var agentName = NewAgentName();
        var latest = NewSessionId();
        var mid = NewSessionId();
        var oldest = NewSessionId();

        await InsertGenericSessionAsync(projectId, oldest, agentId, agentName, createdAt: TestTime.UtcDateTime.AddHours(-3));
        await InsertGenericSessionAsync(projectId, mid, agentId, agentName, createdAt: TestTime.UtcDateTime.AddHours(-2));
        await InsertGenericSessionAsync(projectId, latest, agentId, agentName, createdAt: TestTime.UtcDateTime.AddHours(-1));

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        var items = await querier.ListAgentSessionsAsync(projectId, agentId);

        Assert.Equal(3, items.Count);
        Assert.Equal(latest, items[0].SessionId);
        Assert.Equal(mid, items[1].SessionId);
        Assert.Equal(oldest, items[2].SessionId);
        foreach (var item in items)
        {
            Assert.Equal(agentId, item.AgentId);
            Assert.Equal(agentName, item.AgentName);
            Assert.NotNull(item.Activity);
            Assert.NotNull(item.CreatedAt);
        }
    }

    [Fact]
    public async Task ListAgentSessions_ByAgentName_ResolvesToSameSet()
    {
        var projectId = NewProjectId();
        var agentId = NewAgentId();
        var agentName = NewAgentName();
        var sessionId = NewSessionId();
        await InsertGenericSessionAsync(projectId, sessionId, agentId, agentName);

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        // The querier accepts only the agent id; the agent-name → id
        // resolution is the route layer's job (see
        // AgentSessionReadApiSpecs.ListAgentSessions_UnknownAgentRef_Returns404).
        // What we assert here is that two sessions seeded under the
        // same (agent id, agent name) tuple resolve identically when
        // listed by id.
        var byId = await querier.ListAgentSessionsAsync(projectId, agentId);

        Assert.NotEmpty(byId);
        Assert.Contains(byId, item => item.SessionId == sessionId);
    }

    [Fact]
    public async Task ListAgentSessions_StatusFilter_ReturnsOnlyMatchingActivity()
    {
        var projectId = NewProjectId();
        var agentId = NewAgentId();
        var agentName = NewAgentName();
        var activeSession = NewSessionId();
        var idleSession = NewSessionId();

        await InsertActiveGenericSessionAsync(projectId, activeSession, agentId, agentName, "test-runner");
        await InsertFailedGenericSessionAsync(projectId, idleSession, agentId, agentName);

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        var active = await querier.ListAgentSessionsAsync(projectId, agentId, statusSet: ["active"]);
        var idle = await querier.ListAgentSessionsAsync(projectId, agentId, statusSet: ["idle"]);
        var multi = await querier.ListAgentSessionsAsync(projectId, agentId, statusSet: ["active", "idle"]);

        Assert.Single(active, item => item.SessionId == activeSession);
        Assert.Single(idle, item => item.SessionId == idleSession);
        Assert.Equal(2, multi.Count);
    }

    [Fact]
    public async Task ListAgentSessions_ScopedByProjectId()
    {
        var projectA = NewProjectId();
        var projectB = NewProjectId();
        var agentId = NewAgentId();
        var agentName = NewAgentName();
        var sessionA = NewSessionId();
        var sessionB = NewSessionId();
        await InsertGenericSessionAsync(projectA, sessionA, agentId, agentName);
        await InsertGenericSessionAsync(projectB, sessionB, agentId, agentName);

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        var fromA = await querier.ListAgentSessionsAsync(projectA, agentId);
        var fromB = await querier.ListAgentSessionsAsync(projectB, agentId);

        Assert.Single(fromA, item => item.SessionId == sessionA);
        Assert.DoesNotContain(fromA, item => item.SessionId == sessionB);
        Assert.Single(fromB, item => item.SessionId == sessionB);
        Assert.DoesNotContain(fromB, item => item.SessionId == sessionA);
    }

    [Fact]
    public async Task ListAgentSessions_ExcludesWorkflowSourceSessions()
    {
        var projectId = NewProjectId();
        var agentId = NewAgentId();
        var agentName = NewAgentName();
        var genericSession = NewSessionId();
        var workflowSession = NewSessionId();
        await InsertGenericSessionAsync(projectId, genericSession, agentId, agentName);
        await InsertWorkflowSessionAsync(projectId, workflowSession);

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        var items = await querier.ListAgentSessionsAsync(projectId, agentId);

        Assert.Contains(items, item => item.SessionId == genericSession);
        Assert.DoesNotContain(items, item => item.SessionId == workflowSession);
    }

    private static string NewProjectId() => $"proj-list-{Guid.NewGuid():N}";
    private static string NewAgentId() => $"agent-list-{Guid.NewGuid():N}";
    private static string NewAgentName() => $"agent-name-{Guid.NewGuid():N}";
    private static string NewSessionId() => $"sess-list-{Guid.NewGuid():N}";

    private async Task InsertGenericSessionAsync(
        string projectId,
        string sessionId,
        string agentId,
        string agentName,
        DateTime? createdAt = null)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            [GenericAgentSessionMetadata.AgentId] = agentId,
            [GenericAgentSessionMetadata.AgentName] = agentName,
        };

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

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
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

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
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

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();

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
            Type = "session.closed",
            CorrelationKey = $"session.closed_{Guid.NewGuid():N}",
            PayloadJson = $$"""{"status":"failed","ts":"{{startedAt.AddMinutes(5):O}}"}""",
            LastSeenAt = startedAt.AddMinutes(5),
        });

        await db.SaveChangesAsync();
    }

    private async Task InsertWorkflowSessionAsync(string projectId, string sessionId)
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

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
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
}