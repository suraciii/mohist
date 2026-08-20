using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.UnitTests.Sessions;

[Collection("MohistDb")]
public sealed class GenericAgentSessionFollowupServiceSpecs
{
    private readonly MohistDbFixture _fixture;

    public GenericAgentSessionFollowupServiceSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ResolveGenericFollowupTarget_ReadsRunnerIdAndActiveStateFromSession()
    {
        var sessionId = $"followup-service-active-{Guid.NewGuid():N}";
        await InsertSessionAsync(
            ProjectId,
            sessionId,
            sourceKind: "agent-launch",
            runnerId: "runner-active",
            runtimeSessionId: "runtime-active",
            activity: AgentSessionActivity.Active);

        var target = await Querier.ResolveGenericFollowupTargetAsync(ProjectId, sessionId);

        Assert.NotNull(target);
        Assert.Equal("runner-active", target!.RunnerId);
        Assert.Equal(sessionId, target.SessionId);
        Assert.True(target.IsActive);
    }

    [Fact]
    public async Task ResolveGenericFollowupTarget_NoRunnerOpened_ReturnsQueuedTargetWithEmptyRunner()
    {
        var sessionId = $"followup-service-no-runner-{Guid.NewGuid():N}";
        await InsertSessionAsync(
            ProjectId,
            sessionId,
            sourceKind: "agent-launch",
            runnerId: string.Empty,
            runtimeSessionId: null,
            activity: AgentSessionActivity.Active,
            rowStatus: "opened");

        var target = await Querier.ResolveGenericFollowupTargetAsync(ProjectId, sessionId);

        Assert.NotNull(target);
        Assert.Equal(string.Empty, target!.RunnerId);
        Assert.Equal(sessionId, target.SessionId);
        Assert.True(target.IsActive);
    }

    [Fact]
    public async Task ResolveGenericFollowupTarget_UnknownOrCrossProjectSession_ReturnsNull()
    {
        var sessionId = $"followup-service-cross-{Guid.NewGuid():N}";
        await InsertSessionAsync(
            ProjectId,
            sessionId,
            sourceKind: "agent-launch",
            runnerId: "runner-cross",
            runtimeSessionId: "runtime-cross",
            activity: AgentSessionActivity.Active);

        Assert.Null(await Querier.ResolveGenericFollowupTargetAsync(ProjectId, $"missing-{Guid.NewGuid():N}"));
        Assert.Null(await Querier.ResolveGenericFollowupTargetAsync(OtherProjectId, sessionId));
    }

    [Fact]
    public async Task ResolveCanonicalFollowupTarget_WorkflowSession_UsesWorkflowTargetAndBinding()
    {
        var sessionId = $"followup-service-workflow-{Guid.NewGuid():N}";
        await InsertSessionAsync(
            ProjectId,
            sessionId,
            sourceKind: "workflow",
            runnerId: "runner-workflow",
            runtimeSessionId: "runtime-workflow",
            activity: AgentSessionActivity.Idle,
            workflowRunId: "workflow-1",
            sessionName: "build",
            workDir: "/work/project-1");

        var target = await Querier.ResolveCanonicalFollowupTargetAsync(ProjectId, sessionId);

        Assert.NotNull(target);
        Assert.Equal(sessionId, target!.SessionId);
        Assert.Equal("workflow", target.SourceKind);
        Assert.Equal("workflow-1", target.WorkflowRunId);
        Assert.Equal("build", target.SessionName);
        Assert.Equal("runner-workflow", target.RunnerId);
        Assert.Equal("opencode", target.Runtime);
        Assert.Equal("runtime-workflow", target.RuntimeSessionId);
        Assert.Equal("/work/project-1", target.WorkDir);
    }

    [Fact]
    public async Task ResolveCanonicalFollowupTarget_AfterResetUsesReplacementRuntimeBinding()
    {
        var sessionId = $"followup-service-reset-{Guid.NewGuid():N}";
        await InsertSessionAsync(
            ProjectId,
            sessionId,
            sourceKind: "agent-launch",
            runnerId: "runner-reset",
            runtimeSessionId: "runtime-replacement",
            activity: AgentSessionActivity.Idle,
            workDir: "/work/project-1");

        var target = await Querier.ResolveCanonicalFollowupTargetAsync(ProjectId, sessionId);

        Assert.NotNull(target);
        Assert.Equal("agent-launch", target!.SourceKind);
        Assert.Equal("runtime-replacement", target.RuntimeSessionId);
        Assert.Equal("runner-reset", target.RunnerId);
    }

    [Theory]
    [InlineData("workflow", null, "build")]
    [InlineData("workflow", "workflow-1", null)]
    [InlineData("unsupported", null, null)]
    public async Task ResolveCanonicalFollowupTarget_InvalidSourceShape_ReturnsNull(
        string sourceKind,
        string? workflowRunId,
        string? sessionName)
    {
        var sessionId = $"followup-service-invalid-{Guid.NewGuid():N}";
        await InsertSessionAsync(
            ProjectId,
            sessionId,
            sourceKind,
            runnerId: "runner-invalid",
            runtimeSessionId: "runtime-invalid",
            activity: AgentSessionActivity.Idle,
            workflowRunId: workflowRunId,
            sessionName: sessionName);

        Assert.Null(await Querier.ResolveCanonicalFollowupTargetAsync(ProjectId, sessionId));
    }

    private const string ProjectId = "project-followup-service";
    private const string OtherProjectId = "project-followup-other";
    private static readonly DateTime CreatedAt = TestTime.UtcDateTime;

    private AgentSessionQuerier Querier => _fixture.Services.GetRequiredService<AgentSessionQuerier>();

    private async Task InsertSessionAsync(
        string projectId,
        string sessionId,
        string sourceKind,
        string runnerId,
        string? runtimeSessionId,
        AgentSessionActivity activity,
        string? workflowRunId = null,
        string? sessionName = null,
        string? workDir = "/work/project",
        string rowStatus = "bound")
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = sourceKind,
        };
        if (workflowRunId is not null)
            labels[AgentSessionQueryMetadataKeys.WorkflowRunId] = workflowRunId;
        if (sessionName is not null)
            labels[AgentSessionQueryMetadataKeys.SessionName] = sessionName;
        if (sourceKind == "agent-launch")
        {
            labels[GenericAgentSessionMetadata.AgentId] = $"agent-{sessionId}";
            labels[GenericAgentSessionMetadata.AgentName] = "Followup Agent";
        }

        var session = new AgentSession
        {
            Id = sessionId,
            Runtime = new AgentSessionRuntime(runnerId, workDir, "opencode"),
            Settings = new AgentSessionSettings("test-model"),
            Status = new AgentSessionStatusSnapshot(
                AgentRuntimeSessionId: runtimeSessionId,
                CreatedAt: CreatedAt,
                BoundAt: runtimeSessionId is null ? null : CreatedAt.AddSeconds(1),
                LastDataAt: CreatedAt.AddMinutes(1),
                Activity: activity),
            Metadata = new AgentSessionMetadata(labels),
        };

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = sessionId,
            State = JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions),
            RunnerId = string.IsNullOrWhiteSpace(runnerId) ? null : runnerId,
            AgentSessionId = runtimeSessionId,
            Status = rowStatus,
            CreatedAt = CreatedAt,
        });
        await db.SaveChangesAsync();
    }
}
