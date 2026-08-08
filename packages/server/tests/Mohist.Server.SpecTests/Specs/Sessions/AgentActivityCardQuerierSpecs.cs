using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.AgentOps.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

/// <summary>
/// Calculation specs for the per-session activity-card projection
/// (<see cref="AgentActivityFeedAssembler.GetActivityAsync"/>) —
/// generic <c>agent-launch</c> sessions carry <c>agentId</c>/
/// <c>agentName</c> and link the issue when present; workflow sessions
/// carry no agent attribution; active-session reconciliation surfaces
/// the most recent activity per runner. Specs drive the assembler
/// directly via <c>MohistDbFixture</c> (no web host, no HTTP). The
/// route contract stays in
/// <c>AgentSessionActivityVisibilitySpecs</c>.
/// </summary>
[Collection("MohistDb")]
public class AgentActivityCardQuerierSpecs
{
    private readonly MohistDbFixture _fixture;

    public AgentActivityCardQuerierSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ActivityCard_ForGenericAgentLaunchSession_CarriesAgentIdAndAgentName()
    {
        var projectId = $"proj-gen-activity-agent-{Guid.NewGuid():N}";
        var agentId = "agent_testAgent1";
        var agentName = "test-agent-one";
        var sessionId = await InsertGenericSessionAsync(projectId, agentId, agentName, issueNumber: null);

        using var scope = _fixture.Services.CreateScope();
        var assembler = scope.ServiceProvider.GetRequiredService<AgentActivityFeedAssembler>();

        var activity = await assembler.GetActivityAsync(projectId);

        var card = Assert.Single(activity.Sessions, s => s.SessionId == sessionId);
        Assert.Equal(agentId, card.AgentId);
        Assert.Equal(agentName, card.AgentName);
    }

    [Fact]
    public async Task ActivityCard_ForGenericSessionWithoutIssueRef_ProducesNoSyntheticIssueCard()
    {
        var projectId = $"proj-gen-activity-noissue-{Guid.NewGuid():N}";
        var sessionId = await InsertGenericSessionAsync(projectId, "agent_noIssueAgent", "no-issue-agent", issueNumber: null);

        using var scope = _fixture.Services.CreateScope();
        var assembler = scope.ServiceProvider.GetRequiredService<AgentActivityFeedAssembler>();

        var activity = await assembler.GetActivityAsync(projectId);

        var card = Assert.Single(activity.Sessions, s => s.SessionId == sessionId);
        Assert.Equal(0, card.IssueNumber);
        Assert.Equal("agent_noIssueAgent", card.AgentId);
        Assert.Equal("no-issue-agent", card.AgentName);
    }

    [Fact]
    public async Task ActivityCard_ForGenericSessionWithIssueRef_IsAssociatedButAgentAttributed()
    {
        var projectId = $"proj-gen-activity-wissue-{Guid.NewGuid():N}";
        const int issueNumber = 42;
        var sessionId = await InsertGenericSessionAsync(projectId, "agent_withIssueAgent", "with-issue-agent", issueNumber);

        using var scope = _fixture.Services.CreateScope();
        var assembler = scope.ServiceProvider.GetRequiredService<AgentActivityFeedAssembler>();

        var activity = await assembler.GetActivityAsync(projectId);

        var card = Assert.Single(activity.Sessions, s => s.SessionId == sessionId);
        Assert.Equal(issueNumber, card.IssueNumber);
        Assert.Equal("agent_withIssueAgent", card.AgentId);
        Assert.Equal("with-issue-agent", card.AgentName);
    }

    [Fact]
    public async Task WorkflowActivityCard_DoesNotLeakAgentIdOrAgentName()
    {
        var projectId = $"proj-gen-wf-regression-{Guid.NewGuid():N}";
        var genericSessionId = await InsertGenericSessionAsync(projectId, "agent_wfAgent", "wf-agent", issueNumber: null, active: true);
        var workflowSessionId = await InsertWorkflowSessionAsync(projectId);

        using var scope = _fixture.Services.CreateScope();
        var assembler = scope.ServiceProvider.GetRequiredService<AgentActivityFeedAssembler>();

        var activity = await assembler.GetActivityAsync(projectId);

        var wfCard = Assert.Single(activity.Sessions, s => s.SessionId == workflowSessionId);
        Assert.Null(wfCard.AgentId);
        Assert.Null(wfCard.AgentName);

        var genericCard = Assert.Single(activity.Sessions, s => s.SessionId == genericSessionId);
        Assert.Equal("agent_wfAgent", genericCard.AgentId);
        Assert.Equal("wf-agent", genericCard.AgentName);
    }

    [Fact]
    public async Task ActivitySummary_ActiveCount_ReflectsActiveSessions()
    {
        var projectId = $"proj-summary-active-{Guid.NewGuid():N}";
        await InsertGenericSessionAsync(projectId, "agent_active1", "active-agent-1", issueNumber: null, active: true);
        await InsertGenericSessionAsync(projectId, "agent_active2", "active-agent-2", issueNumber: null, active: true);
        await InsertGenericSessionAsync(projectId, "agent_idle", "idle-agent", issueNumber: null, active: false);

        using var scope = _fixture.Services.CreateScope();
        var assembler = scope.ServiceProvider.GetRequiredService<AgentActivityFeedAssembler>();

        var activity = await assembler.GetActivityAsync(projectId);

        Assert.Equal(2, activity.Summary.Active);
    }

    private async Task<string> InsertGenericSessionAsync(
        string projectId,
        string agentId,
        string agentName,
        int? issueNumber,
        bool active = false)
    {
        var sessionId = $"session-{Guid.NewGuid():N}";
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            [GenericAgentSessionMetadata.AgentId] = agentId,
            [GenericAgentSessionMetadata.AgentName] = agentName,
        };
        if (issueNumber.HasValue)
            labels[AgentSessionQueryMetadataKeys.IssueNumber] = issueNumber.Value.ToString();

        var createdAt = TestTime.UtcDateTime;
        var activity = active ? AgentSessionActivity.Active : AgentSessionActivity.Idle;
        var session = new AgentSession
        {
            Id = sessionId,
            Runtime = new AgentSessionRuntime("test-runner", null),
            Settings = new AgentSessionSettings("test-model"),
            Status = new AgentSessionStatusSnapshot(
                CreatedAt: createdAt,
                BoundAt: active ? createdAt : null,
                LastDataAt: active ? TestTime.UtcDateTime : null,
                AgentRuntimeSessionId: sessionId,
                Activity: activity),
            Metadata = new AgentSessionMetadata(labels),
        };

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = session.Id,
            State = JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions),
            CreatedAt = createdAt,
            Status = active ? "bound" : "opened",
            AgentSessionId = sessionId,
            RunnerId = "test-runner",
        });
        await db.SaveChangesAsync();
        return sessionId;
    }

    private async Task<string> InsertWorkflowSessionAsync(string projectId)
    {
        var sessionId = $"session-{Guid.NewGuid():N}";
        var runnerId = $"runner-{Guid.NewGuid():N}";
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
        return sessionId;
    }
}