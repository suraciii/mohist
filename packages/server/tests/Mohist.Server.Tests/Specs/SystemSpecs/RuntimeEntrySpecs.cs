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
using Mohist.Server.Workflow.Services.Sessions;
using Xunit;

namespace Mohist.Server.Tests.Specs.SystemSpecs;

[Collection("MohistIntegration")]
public class RuntimeEntrySpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public RuntimeEntrySpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task AgentStatus_WhenRunnerRegisteredWithoutActiveWork_ReportsIdleRuntime()
    {
        var projectName = $"runtime-status-{Guid.NewGuid():N}";
        var project = await _fixture.Client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = Directory.GetCurrentDirectory(), baseBranch = "main" });

        try
        {
            await _fixture.Client.PostOkAsync("/api/runner/runtime-test-runner/register", new { capabilities = Array.Empty<string>(), hostname = "test-host", projectId = project.Id });

            // The runner-reported maxWorkflowSlots field is intentionally
            // ignored for dispatch capacity (issue-222 T-002). Register
            // defaults the runner to 1 slot; PATCH bumps it to 2 so the
            // capacity view reflects the new value.
            await _fixture.Client.PatchOkAsync("/api/runner/runtime-test-runner", new { slots = 2 });

            var status = await _fixture.Client.GetDataAsync<AgentStatusDto>($"/api/projects/{project.Id}/agent/status");

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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task AgentStatus_WhenGlobalRunnerRegistered_ReportsRunnerAvailableForProject()
    {
        var projectName = $"runtime-global-runner-{Guid.NewGuid():N}";
        var project = await _fixture.Client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var runnerId = $"runtime-global-runner-{Guid.NewGuid():N}";

        try
        {
            await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host" });

            var status = await _fixture.Client.GetDataAsync<AgentStatusDto>($"/api/projects/{project.Id}/agent/status");

            Assert.True(status.RunnerAvailable);
            Assert.Null(status.RunnerMessage);
            Assert.Contains(status.Runners, r => r.Id == runnerId);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task AgentStatus_WhenRunnerRegisteredButOffline_DoesNotReportAvailableCapacity()
    {
        var projectName = $"runtime-offline-runner-{Guid.NewGuid():N}";
        var project = await _fixture.Client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var runnerId = $"runtime-offline-runner-{Guid.NewGuid():N}";

        var registry = _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        try
        {
            await registry.RegisterAsync(new RunnerInfo(runnerId, [], "test-host", project.Id, MaxWorkflowSlots: 4));

            var status = await _fixture.Client.GetDataAsync<AgentStatusDto>($"/api/projects/{project.Id}/agent/status");

            Assert.DoesNotContain(status.Runners, r => r.Id == runnerId);
            Assert.DoesNotContain(status.Runners, r => r.Max == 4 && r.Id == runnerId);
        }
        finally
        {
            await registry.UnregisterAsync(runnerId);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task AgentStatus_WhenRunnerUnregisteredThenHeartbeats_ReportsRunnerAvailable()
    {
        var projectName = $"runtime-presence-repair-{Guid.NewGuid():N}";
        var project = await _fixture.Client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var runnerId = $"runtime-presence-repair-{Guid.NewGuid():N}";

        try
        {
            var registry = _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);

            // Global runners from earlier integration tests can leak into this test's
            // registry assertions; clear them so we start from a known empty state.
            var existingIds = await registry.ListRunnerIdsAsync();
            foreach (var id in existingIds)
                await registry.UnregisterAsync(id);

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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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
            var status = await _fixture.Client.GetDataAsync<AgentStatusDto>($"/api/projects/{project.Id}/agent/status");
            Assert.True(status.RunnerAvailable);
            Assert.Contains(status.Runners, r => r.Id == runnerId && r.Max == 1);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task AgentStatus_WhenNoRunnerConnected_ReportsUnavailableRuntime()
    {
        var status = AgentStatusResponse.Create([], [], new Dictionary<string, int>(StringComparer.Ordinal));

        Assert.False(status.Running);
        Assert.False(status.RunnerAvailable);
        Assert.False(status.EmbeddedRunnerEnabled);
        Assert.Equal(0, status.Capacity.Active);
        Assert.Equal(0, status.Capacity.Max);
        Assert.Equal("No runner is connected. Start the Mohist runner process.", status.RunnerMessage);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task AgentStatus_OnLegacyRoute_ReturnsNotFound()
    {
        using var response = await _fixture.Client.GetAsync("/api/agent/status");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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
            .WithLabel(AgentSessionQueryMetadataKeys.ProjectId, projectId)
            .WithLabel(AgentSessionQueryMetadataKeys.IssueNumber, issueNumber.ToString())
            .WithLabel(AgentSessionQueryMetadataKeys.SourceKind, "workflow")
            .WithLabel(AgentSessionQueryMetadataKeys.WorkflowRunId, workflowRunId)
            .WithLabel(AgentSessionQueryMetadataKeys.SessionName, workId);
        var session = AgentSession.Create(
            $"session-{Guid.NewGuid():N}",
            runnerId,
            null,
            metadata: metadata,
            now: now);
        session.AttachPhysicalSession($"acp-{Guid.NewGuid():N}", null, null, null, null, now);
        return AgentSessionJson.ToRow(session, now);
    }
}
