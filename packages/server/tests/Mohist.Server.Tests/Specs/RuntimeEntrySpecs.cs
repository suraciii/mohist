using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class RuntimeEntrySpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public RuntimeEntrySpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WebRoot_WhenConfigured_ServesIndexAndSpaFallback()
    {
        var root = await _fixture.Client.GetStringAsync("/");
        var route = await _fixture.Client.GetStringAsync("/issues/1");
        var workflowSession = await _fixture.Client.GetStringAsync("/issues/1/workflow/sessions/plan");

        Assert.Contains("Mohist Test Web", root);
        Assert.Contains("Mohist Test Web", route);
        Assert.Contains("Mohist Test Web", workflowSession);
    }

    [Fact]
    public async Task AgentStatus_WhenRunnerRegisteredWithoutActiveWork_ReportsIdleRuntime()
    {
        var projectName = $"runtime-status-{Guid.NewGuid():N}";
        var project = await _fixture.Client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = Directory.GetCurrentDirectory(), baseBranch = "main" });

        try
        {
            await _fixture.Client.PostOkAsync("/api/runner/runtime-test-runner/register", new { capabilities = Array.Empty<string>(), hostname = "test-host", projectId = project.Id, maxWorkflowSlots = 2 });

            var status = await _fixture.Client.GetDataAsync<AgentStatusDto>($"/api/agent/status?projectId={project.Id}");

            Assert.False(status.Running);
            Assert.True(status.RunnerAvailable);
            Assert.False(status.EmbeddedRunnerEnabled);
            Assert.Null(status.RunnerMessage);
            Assert.Equal(0, status.Capacity.Active);
            Assert.True(status.Capacity.Max >= 2);
            var runner = Assert.Single(status.Runners, r => r.Id == "runtime-test-runner");
            Assert.Equal(0, runner.Active);
            Assert.Equal(2, runner.Max);
        }
        finally
        {
            await _fixture.Client.PostAsync("/api/runner/runtime-test-runner/unregister", null);
        }
    }

    [Fact]
    public async Task AgentStatus_WhenGlobalRunnerRegistered_ReportsRunnerAvailableForProject()
    {
        var projectName = $"runtime-global-runner-{Guid.NewGuid():N}";
        var project = await _fixture.Client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var runnerId = $"runtime-global-runner-{Guid.NewGuid():N}";

        try
        {
            await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host" });

            var status = await _fixture.Client.GetDataAsync<AgentStatusDto>($"/api/agent/status?projectId={project.Id}");

            Assert.True(status.RunnerAvailable);
            Assert.Null(status.RunnerMessage);
            Assert.Contains(status.Runners, r => r.Id == runnerId);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task AgentStatus_WhenRunnerRegisteredButOffline_DoesNotReportAvailableCapacity()
    {
        var projectName = $"runtime-offline-runner-{Guid.NewGuid():N}";
        var project = await _fixture.Client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var runnerId = $"runtime-offline-runner-{Guid.NewGuid():N}";

        var registry = _fixture.Grains.GetGrain<IRunnerRegistryGrain>(GrainKey.RunnerRegistry(project.Id));
        await registry.RegisterAsync(new RunnerInfo(runnerId, [], "test-host", project.Id, MaxWorkflowSlots: 4));

        var status = await _fixture.Client.GetDataAsync<AgentStatusDto>($"/api/agent/status?projectId={project.Id}");

        Assert.DoesNotContain(status.Runners, r => r.Id == runnerId);
        Assert.DoesNotContain(status.Runners, r => r.Max == 4 && r.Id == runnerId);
    }

    [Fact]
    public async Task AgentStatus_WhenRunnerUnregisteredThenHeartbeats_ReportsRunnerAvailable()
    {
        var projectName = $"runtime-presence-repair-{Guid.NewGuid():N}";
        var project = await _fixture.Client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var runnerId = $"runtime-presence-repair-{Guid.NewGuid():N}";

        try
        {
            var registry = _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.ForProject(project.Id));

            await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host", projectId = project.Id });
            await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/unregister", null);

            var runnersAfterUnregister = await registry.ListRunnersAsync();
            Assert.Empty(runnersAfterUnregister);

            await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/heartbeat", new { capabilities = Array.Empty<string>(), hostname = "test-host", projectId = project.Id });
            var runnersAfterHeartbeat = await registry.ListRunnersAsync();

            Assert.True(runnersAfterHeartbeat.Count > 0);
            Assert.Contains(runnersAfterHeartbeat, r => r.RunnerId == runnerId);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task RunnerHeartbeat_WithNoBody_RefreshesRegisteredRunner()
    {
        var projectName = $"runtime-empty-heartbeat-{Guid.NewGuid():N}";
        var project = await _fixture.Client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var runnerId = $"runtime-empty-heartbeat-{Guid.NewGuid():N}";

        try
        {
            await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host", projectId = project.Id, maxWorkflowSlots = 1 });

            using var response = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/heartbeat", content: null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var status = await _fixture.Client.GetDataAsync<AgentStatusDto>($"/api/agent/status?projectId={project.Id}");
            Assert.True(status.RunnerAvailable);
            Assert.Contains(status.Runners, r => r.Id == runnerId && r.Max == 1);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task AgentStatus_WhenNoRunnerConnected_ReportsUnavailableRuntime()
    {
        var status = AgentStatusResponse.Create([], []);

        Assert.False(status.Running);
        Assert.False(status.RunnerAvailable);
        Assert.False(status.EmbeddedRunnerEnabled);
        Assert.Equal(0, status.Capacity.Active);
        Assert.Equal(0, status.Capacity.Max);
        Assert.Equal("No runner is connected. Start the Mohist runner process.", status.RunnerMessage);
    }

    [Fact]
    public async Task AgentStatus_WhenLeaseOwnerDiffers_DoesNotCountStaleSessionAsActive()
    {
        var runnerId = $"runtime-lease-runner-{Guid.NewGuid():N}";
        var project = await _fixture.Client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"runtime-lease-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _fixture.Client.PostDataAsync<IssueDto>("/api/issues", new { title = "Lease-owned status", body = "status read consistency", labels = Array.Empty<string>(), priority = "p1", projectId = project.Id });
        var workflowRunId = $"wf-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";
        var staleRunnerId = $"stale-runner-{Guid.NewGuid():N}";

        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host", projectId = project.Id, maxWorkflowSlots = 2 });

        try
        {
            await using (var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync())
            {
                db.AgentSessions.Add(CreateRunningSessionRow(project.Id, issue.Number, workflowRunId, workId, staleRunnerId, "Lease-owned status"));

                db.WorkflowLeases.Add(new Mohist.Server.Infrastructure.Data.Workflow.WorkflowLeaseRow
                {
                    WorkflowRunId = workflowRunId,
                    State = JsonSerializer.Serialize(new WorkLease(workId, "task", "Build", workId, "Lease-owned status", runnerId), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                });

                await db.SaveChangesAsync();
            }

            var status = await _fixture.Client.GetDataAsync<AgentStatusDto>($"/api/agent/status?projectId={project.Id}");

            var runner = Assert.Single(status.Runners, r => r.Id == runnerId);
            Assert.Equal(0, runner.Active);
            Assert.False(status.Running);
            Assert.Equal(0, status.Capacity.Active);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task AgentStatus_WhenLeaseOwnerDiffers_DoesNotReportStaleRunnerAsActiveOwner()
    {
        var runnerId = $"runtime-status-owner-{Guid.NewGuid():N}";
        var project = await _fixture.Client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"runtime-owner-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _fixture.Client.PostDataAsync<IssueDto>("/api/issues", new { title = "Lease-owned status runner", body = "status owner consistency", labels = Array.Empty<string>(), priority = "p1", projectId = project.Id });
        var workflowRunId = $"wf-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";
        var staleRunnerId = $"stale-runner-{Guid.NewGuid():N}";

        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host", projectId = project.Id, maxWorkflowSlots = 2 });

        try
        {
            await using (var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync())
            {
                db.AgentSessions.Add(CreateRunningSessionRow(project.Id, issue.Number, workflowRunId, workId, staleRunnerId, "Lease-owned status runner"));

                db.WorkflowLeases.Add(new Mohist.Server.Infrastructure.Data.Workflow.WorkflowLeaseRow
                {
                    WorkflowRunId = workflowRunId,
                    State = JsonSerializer.Serialize(new WorkLease(workId, "task", "Build", workId, "Lease-owned status runner", runnerId), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                });

                await db.SaveChangesAsync();
            }

            var status = await _fixture.Client.GetDataAsync<AgentStatusDto>($"/api/agent/status?projectId={project.Id}");

            Assert.DoesNotContain(status.Runners, r => r.Id == staleRunnerId && r.Active > 0);
            var leaseOwner = Assert.Single(status.Runners, r => r.Id == runnerId);
            Assert.Equal(0, leaseOwner.Active);
            Assert.False(status.Running);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task AgentStatus_WhenProjectIdMissing_ReturnsJsonError()
    {
        using var response = await _fixture.Client.GetAsync("/api/agent/status");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiErrorDto>();
        Assert.NotNull(payload);
        Assert.False(payload!.Success);
        Assert.Equal("No active project", payload.Error);
    }

    [Fact]
    public async Task ApiFallback_WhenUnknownApiPath_ReturnsNotFound()
    {
        using var response = await _fixture.Client.GetAsync("/api/missing-route");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed record AgentStatusDto(bool Running, bool RunnerAvailable, bool EmbeddedRunnerEnabled, string? RunnerMessage, RunnerDto[] Runners, AgentCapacityDto Capacity);
    private sealed record AgentCapacityDto(int Active, int Max);
    private sealed record RunnerDto(string Id, string? Kind = null, int Active = 0, int Max = 0);
    private sealed record ProjectDto(string Id, string Name, string Path, string BaseBranch);
    private sealed record IssueDto(int Number, string Title);
    private sealed record ApiErrorDto(bool Success, string? Error);

    private static AgentSessionRow CreateRunningSessionRow(string projectId, int issueNumber, string workflowRunId, string workId, string runnerId, string title)
    {
        var now = DateTime.UtcNow;
        var metadata = new AgentSessionMetadata()
            .WithLabel(AgentSessionMetadataKeys.ProjectId, projectId)
            .WithLabel(AgentSessionMetadataKeys.IssueNumber, issueNumber.ToString())
            .WithLabel(AgentSessionMetadataKeys.SourceKind, "workflow")
            .WithLabel(AgentSessionMetadataKeys.SourceId, workflowRunId)
            .WithLabel(AgentSessionMetadataKeys.SessionName, workId)
            .WithAnnotation(AgentSessionMetadataKeys.TaskId, workId)
            .WithAnnotation(AgentSessionMetadataKeys.TaskKind, "task")
            .WithAnnotation(AgentSessionMetadataKeys.Phase, "Build")
            .WithAnnotation(AgentSessionMetadataKeys.Title, title);
        var session = AgentSession.Create(
            $"session-{Guid.NewGuid():N}",
            runnerId,
            "opencode",
            null,
            metadata: metadata,
            now: now);
        session.Start(null, now);
        return AgentSessionJson.ToRow(session, now);
    }
}
