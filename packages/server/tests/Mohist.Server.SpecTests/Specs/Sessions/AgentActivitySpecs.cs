using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

[Collection("IntegrationSessions")]
public class AgentActivitySpecs
{
    private static readonly DateTime PinnedNow = new(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc);

    private readonly MohistIntegrationFixture _fixture;

    public AgentActivitySpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    private AgentActivityFeedAssembler ResolveActivityFeed()
    {
        var scope = _fixture.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AgentActivityFeedAssembler>();
    }

    [Fact]
    public async Task Activity_WithNoSessions_ShowsEmptySummary()
    {
        var project = await CreateProjectAsync();

        var assembler = ResolveActivityFeed();
        var result = await assembler.GetActivityAsync(project.Id);

        Assert.Equal(0, result.Summary.Active);
        Assert.Equal(0, result.Summary.Waiting);
        Assert.Equal(0, result.Summary.Completed);
        Assert.Equal(0, result.Summary.Failed);
        Assert.Empty(result.Sessions);
        Assert.Empty(result.Waiting);
    }

    [Fact]
    public async Task Activity_WithGenericAgentSession_ShowsAgentAndIssueFallback()
    {
        var project = await CreateProjectAsync();
        var agentId = "agent_unitCard";
        var agentName = "unit-card-agent";
        var sessionId = $"session-{Guid.NewGuid():N}";

        await InsertGenericSessionAsync(project.Id, sessionId, agentId, agentName, issueNumber: 7, isActive: true);

        var assembler = ResolveActivityFeed();
        var result = await assembler.GetActivityAsync(project.Id, limit: 10);

        var card = Assert.Single(result.Sessions, c => c.SessionId == sessionId);
        Assert.Equal($"agent_{agentId}", card.IssueId);
        Assert.Equal(7, card.IssueNumber);
        Assert.Equal("Issue #7", card.IssueTitle);
        Assert.Equal(agentId, card.AgentId);
        Assert.Equal(agentName, card.AgentName);
        Assert.NotNull(card.Usage);
        Assert.Equal(1, result.Summary.Active);
        Assert.Empty(result.Waiting);
    }

    [Fact]
    public async Task Activity_WithWaitingWork_ShowsItSeparatelyFromSessions()
    {
        var project = await CreateProjectAsync();
        var sessionId = $"session-{Guid.NewGuid():N}";
        await InsertGenericSessionAsync(project.Id, sessionId, "agent_waiting", "waiting-agent", issueNumber: null, isActive: true);

        var waiting = new List<ActivityWaitingCardDto>
        {
            new(IssueId: "issue-1", IssueNumber: 1, IssueTitle: "Issue #1", Stage: "approval", Label: "Needs Approval", RequestedAt: "2026-06-30T00:00:00Z", Preview: null),
            new(IssueId: "issue-2", IssueNumber: 2, IssueTitle: "Issue #2", Stage: "approval", Label: "Needs Approval", RequestedAt: null, Preview: null),
        };

        var assembler = ResolveActivityFeed();
        var result = await assembler.GetActivityAsync(project.Id, limit: 10, waiting: waiting);

        Assert.Equal(2, result.Waiting.Count);
        Assert.Equal(2, result.Summary.Waiting);
        Assert.Equal(1, result.Summary.Active);
    }

    [Fact]
    public async Task Activity_WithWorkflowSession_DoesNotShowAgentIdentity()
    {
        var project = await CreateProjectAsync();
        var sessionId = $"session-{Guid.NewGuid():N}";
        var workflowRunId = $"wf-{Guid.NewGuid():N}";

        await InsertWorkflowSessionAsync(project.Id, sessionId, workflowRunId, issueNumber: 42);

        var assembler = ResolveActivityFeed();
        var result = await assembler.GetActivityAsync(project.Id, limit: 10);

        var card = Assert.Single(result.Sessions, c => c.SessionId == sessionId);
        Assert.Equal($"issue_{project.Id}_42", card.IssueId);
        Assert.Null(card.AgentId);
        Assert.Null(card.AgentName);
    }

    private async Task<ProjectDto> CreateProjectAsync()
    {
        var name = $"activity-spec-{Guid.NewGuid():N}";
        var project = await _fixture.Client.PostDataAsync<ProjectDto>("/api/projects", new { name });
        await _fixture.Client.PostOkAsync($"/api/projects/{project.Id}/repositories", new
        {
            name = "main",
            gitUrl = $"file://{Guid.NewGuid():N}",
            baseBranch = "main",
            isDefault = true,
        });
        return project;
    }

    private async Task InsertGenericSessionAsync(
        string projectId,
        string sessionId,
        string agentId,
        string agentName,
        int? issueNumber,
        DateTime? createdAt = null,
        bool isActive = false)
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

        var started = createdAt ?? PinnedNow;

        var session = new AgentSession
        {
            Id = sessionId,
            Runtime = new AgentSessionRuntime("test-runner", null),
            Settings = new AgentSessionSettings("test-model"),
            Status = new AgentSessionStatusSnapshot(
                CreatedAt: started,
                AgentRuntimeSessionId: sessionId,
                LastDataAt: isActive ? started.AddSeconds(1) : null),
            Metadata = new AgentSessionMetadata(labels),
        };

        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = session.Id,
            State = JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions),
            CreatedAt = started,
            Status = "opened",
            AgentSessionId = sessionId,
            RunnerId = "test-runner",
        });
        await db.SaveChangesAsync();
    }

    private async Task InsertWorkflowSessionAsync(
        string projectId,
        string sessionId,
        string workflowRunId,
        int issueNumber)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "workflow",
            [AgentSessionQueryMetadataKeys.WorkflowRunId] = workflowRunId,
            [AgentSessionQueryMetadataKeys.WorkId] = $"work-{Guid.NewGuid():N}",
            [AgentSessionQueryMetadataKeys.WorkType] = "task",
            [AgentSessionQueryMetadataKeys.Stage] = "Build",
            [AgentSessionQueryMetadataKeys.IssueNumber] = issueNumber.ToString(),
            [AgentSessionQueryMetadataKeys.SessionName] = "plan",
        };

        var session = new AgentSession
        {
            Id = sessionId,
            Runtime = new AgentSessionRuntime("runner-test", null),
            Settings = new AgentSessionSettings("test-model"),
            Status = new AgentSessionStatusSnapshot(
                CreatedAt: PinnedNow,
                AgentRuntimeSessionId: sessionId),
            Metadata = new AgentSessionMetadata(labels),
        };

        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = session.Id,
            State = JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions),
            CreatedAt = PinnedNow,
            Status = "bound",
            AgentSessionId = sessionId,
            RunnerId = "runner-test",
        });
        await db.SaveChangesAsync();
    }

    private sealed record ProjectDto(string Id, string Name, string Path, string BaseBranch);
}
