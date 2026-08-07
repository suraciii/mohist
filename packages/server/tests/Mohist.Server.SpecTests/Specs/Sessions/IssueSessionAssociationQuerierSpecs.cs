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
/// Calculation specs for the issue/epic → agent-session association
/// lookup behind <c>GET /api/projects/{projectRef}/issues/{n}/agent-sessions</c>
/// and <c>GET .../epics/{n}/agent-sessions</c>. The querier reads the
/// <c>agent-launch/issue-number</c> / <c>agent-launch/epic-number</c>
/// context-reference labels and returns a lightweight association list
/// (session id, agent id/name, activity, created timestamp, session
/// link). Specs drive <see cref="AgentSessionQuerier.ListSessionsByContextRefAsync"/>
/// directly via <c>MohistDbFixture</c> (no web host, no HTTP). The
/// route contract (404 unknown issue, 404 unknown epic, 200 empty
/// array) stays in <c>AgentSessionContextAssociationApiSpecs</c>.
/// </summary>
[Collection("MohistDb")]
public class IssueSessionAssociationQuerierSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueSessionAssociationQuerierSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ListSessionsByContextRefAsync_ForIssue_ReturnsSessionReferencingThatIssue()
    {
        var projectId = $"proj-issue-assoc-{Guid.NewGuid():N}";
        var sessionId = await InsertGenericSessionAsync(projectId, issueNumber: 99);
        var otherSessionId = await InsertGenericSessionAsync(projectId, issueNumber: 100);

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        var result = await querier.ListSessionsByContextRefAsync(
            projectId, projectId, GenericAgentSessionMetadata.IssueNumber, "99");

        var entry = Assert.Single(result);
        Assert.Equal(sessionId, entry.SessionId);
        Assert.DoesNotContain(result, r => r.SessionId == otherSessionId);
        Assert.NotNull(entry.AgentId);
        Assert.NotNull(entry.AgentName);
        Assert.NotNull(entry.Activity);
        Assert.NotNull(entry.CreatedAt);
        Assert.Contains(sessionId, entry.SessionLink);
    }

    [Fact]
    public async Task ListSessionsByContextRefAsync_ForIssue_EmptyWhenNoMatch()
    {
        var projectId = $"proj-issue-empty-{Guid.NewGuid():N}";

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        var result = await querier.ListSessionsByContextRefAsync(
            projectId, projectId, GenericAgentSessionMetadata.IssueNumber, "999");

        Assert.Empty(result);
    }

    [Fact]
    public async Task ListSessionsByContextRefAsync_ForEpic_ReturnsSessionReferencingThatEpic()
    {
        var projectId = $"proj-epic-assoc-{Guid.NewGuid():N}";
        var sessionId = await InsertGenericSessionAsync(projectId, epicNumber: 7);

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        var result = await querier.ListSessionsByContextRefAsync(
            projectId, projectId, GenericAgentSessionMetadata.EpicNumber, "7");

        var entry = Assert.Single(result);
        Assert.Equal(sessionId, entry.SessionId);
        Assert.Contains(sessionId, entry.SessionLink);
    }

    [Fact]
    public async Task ListSessionsByContextRefAsync_ByEpicNumber_ResolvesCorrectly()
    {
        var projectId = $"proj-epic-by-id-{Guid.NewGuid():N}";
        var matched = await InsertGenericSessionAsync(projectId, epicNumber: 42);
        var unmatched = await InsertGenericSessionAsync(projectId, epicNumber: 43);

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        var result = await querier.ListSessionsByContextRefAsync(
            projectId, projectId, GenericAgentSessionMetadata.EpicNumber, "42");

        Assert.Single(result, e => e.SessionId == matched);
        Assert.DoesNotContain(result, e => e.SessionId == unmatched);
    }

    [Fact]
    public async Task ListSessionsByContextRefAsync_ForIssue_ScopedByProjectId()
    {
        var projectA = $"proj-a-{Guid.NewGuid():N}";
        var projectB = $"proj-b-{Guid.NewGuid():N}";
        var sessionA = await InsertGenericSessionAsync(projectA, issueNumber: 5);
        var sessionB = await InsertGenericSessionAsync(projectB, issueNumber: 5);

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        var fromA = await querier.ListSessionsByContextRefAsync(
            projectA, projectA, GenericAgentSessionMetadata.IssueNumber, "5");
        var fromB = await querier.ListSessionsByContextRefAsync(
            projectB, projectB, GenericAgentSessionMetadata.IssueNumber, "5");

        Assert.Single(fromA, e => e.SessionId == sessionA);
        Assert.Single(fromB, e => e.SessionId == sessionB);
        Assert.DoesNotContain(fromA, e => e.SessionId == sessionB);
        Assert.DoesNotContain(fromB, e => e.SessionId == sessionA);
    }

    [Fact]
    public async Task ListSessionsByContextRefAsync_FiltersByAgentLaunchSourceKind()
    {
        var projectId = $"proj-source-{Guid.NewGuid():N}";
        var agentLaunchId = await InsertGenericSessionAsync(projectId, issueNumber: 11, sourceKind: "agent-launch");
        var workflowId = await InsertGenericSessionAsync(projectId, issueNumber: 11, sourceKind: "workflow");

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        var result = await querier.ListSessionsByContextRefAsync(
            projectId, projectId, GenericAgentSessionMetadata.IssueNumber, "11");

        Assert.Single(result, e => e.SessionId == agentLaunchId);
        Assert.DoesNotContain(result, e => e.SessionId == workflowId);
    }

    private async Task<string> InsertGenericSessionAsync(
        string projectId,
        int? issueNumber = null,
        int? epicNumber = null,
        string sourceKind = "agent-launch")
    {
        var sessionId = $"sess-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        var agentName = $"name-{Guid.NewGuid():N}";
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = sourceKind,
            [GenericAgentSessionMetadata.AgentId] = agentId,
            [GenericAgentSessionMetadata.AgentName] = agentName,
        };
        if (issueNumber.HasValue)
            labels[GenericAgentSessionMetadata.IssueNumber] = issueNumber.Value.ToString();
        if (epicNumber.HasValue)
            labels[GenericAgentSessionMetadata.EpicNumber] = epicNumber.Value.ToString();

        var createdAt = TestTime.UtcDateTime;
        var session = new AgentSession
        {
            Id = sessionId,
            Runtime = new AgentSessionRuntime("test-runner", null),
            Settings = new AgentSessionSettings("test-model"),
            Status = new AgentSessionStatusSnapshot(
                CreatedAt: createdAt,
                AgentRuntimeSessionId: sessionId),
            Metadata = new AgentSessionMetadata(labels),
        };

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = session.Id,
            State = JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions),
            CreatedAt = createdAt,
            Status = "opened",
            AgentSessionId = sessionId,
            RunnerId = "test-runner",
        });
        await db.SaveChangesAsync();
        return sessionId;
    }
}