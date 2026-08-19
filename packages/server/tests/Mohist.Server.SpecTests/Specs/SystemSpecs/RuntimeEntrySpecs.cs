using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.SystemSpecs;

[Collection("IsolatedIntegration")]
public class RuntimeEntrySpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public RuntimeEntrySpecs(IsolatedMohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WebRoot_WhenConfigured_ServesIndexAndSpaFallback()
    {
        var root = await _fixture.Client.GetStringAsync("/");
        var route = await _fixture.Client.GetStringAsync("/issues/1");
        var workflowSession = await _fixture.Client.GetStringAsync("/issues/1/workflow/sessions/plan");
        var dottedSession = await _fixture.Client.GetStringAsync("/issues/12/workflow/sessions/T-001.1");

        Assert.Contains("Mohist Test Web", root);
        Assert.Contains("Mohist Test Web", route);
        Assert.Contains("Mohist Test Web", workflowSession);
        Assert.Contains("Mohist Test Web", dottedSession);
    }

    [Fact]
    public async Task SpaFallback_WhenDottedSessionDeepLink_ReturnsHtmlEntryPoint()
    {
        using var response = await _fixture.Client.GetAsync("/issues/12/workflow/sessions/T-001.1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Mohist Test Web", body);
    }

    [Fact]
    public async Task SpaFallback_WhenOtelV1Path_ReturnsNotFound()
    {
        using var response = await _fixture.Client.GetAsync("/otel/v1/traces");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SpaFallback_WhenRealStaticAsset_ServedAheadOfFallback()
    {
        using var response = await _fixture.Client.GetAsync("/assets/app.css");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("body{color:red}", body);
    }

    [Fact]
    public async Task SpaFallback_WhenFileLikePathMissing_ServesEntryPoint()
    {
        using var response = await _fixture.Client.GetAsync("/assets/missing.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Mohist Test Web", body);
    }

    [Fact]
    public async Task AgentStatus_WhenRunnerRegisteredWithoutActiveWork_ReportsIdleRuntime()
    {
        var projectName = $"runtime-status-{Guid.NewGuid():N}";
        var project = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", projectName);

        // Capacity is summed across every online runner in the global registry,
        // which is shared across the integration collection. Drain it so the
        // active/max assertions below reflect only this test's runner.
        var registry = _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        foreach (var staleId in await registry.ListRunnerIdsAsync())
            await registry.UnregisterAsync(staleId);

        try
        {
            await _fixture.Client.PostOkAsync("/api/runner/runtime-test-runner/register", new { capabilities = Array.Empty<string>(), hostname = "test-host", projectId = project.Id });

            // Register defaults the runner to 1 slot; PATCH bumps it to 2
            // so the capacity view reflects the new value.
            await _fixture.Client.PatchOkAsync("/api/runner/runtime-test-runner", new { slots = 2 });

            var status = await _fixture.Client.GetDataAsync<AgentStatusDto>($"/api/projects/{project.Id}/agent/status");

            Assert.False(status.Running);
            Assert.True(status.RunnerAvailable);
            Assert.False(status.EmbeddedRunnerEnabled);
            Assert.Null(status.RunnerMessage);
            Assert.Equal(0, status.Capacity.Active);
            Assert.Equal(2, status.Capacity.Max);
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
        var project = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", projectName);
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

    [Fact]
    public async Task AgentStatus_WhenRunnerRegisteredButOffline_DoesNotReportAvailableCapacity()
    {
        var projectName = $"runtime-offline-runner-{Guid.NewGuid():N}";
        var project = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", projectName);
        var runnerId = $"runtime-offline-runner-{Guid.NewGuid():N}";

        var registry = _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        try
        {
            await registry.RegisterAsync(new RunnerInfo(runnerId, [], "test-host", project.Id));

            var status = await _fixture.Client.GetDataAsync<AgentStatusDto>($"/api/projects/{project.Id}/agent/status");

            Assert.DoesNotContain(status.Runners, r => r.Id == runnerId);
            Assert.DoesNotContain(status.Runners, r => r.Max == 4 && r.Id == runnerId);
        }
        finally
        {
            await registry.UnregisterAsync(runnerId);
        }
    }

    [Fact]
    public async Task AgentStatus_WhenRunnerUnregistered_HeartbeatRestoresPresence()
    {
        var projectName = $"runtime-presence-repair-{Guid.NewGuid():N}";
        var project = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", projectName);
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
            Assert.Contains(runnersAfterHeartbeat, r => r.RunnerId == runnerId);

            using var poll = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/poll", content: null);
            Assert.True(poll.IsSuccessStatusCode);

            var runnersAfterPoll = await registry.ListRunnersAsync();
            Assert.Contains(runnersAfterPoll, r => r.RunnerId == runnerId);
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
        var project = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", projectName);
        var runnerId = $"runtime-empty-heartbeat-{Guid.NewGuid():N}";

        try
        {
            await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host", projectId = project.Id });

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

    [Fact]
    public async Task AgentStatus_WhenNoRunnerConnected_ReportsUnavailableRuntime()
    {
        var status = AgentStatusResponse.Create(
            activeAgents: [],
            runners: Array.Empty<RunnerStatusView>(),
            capacity: new RunnerCapacityView(0, 0),
            amplification: new AgentAmplificationDto(0, 0, 0, 0, 0));

        Assert.False(status.Running);
        Assert.False(status.RunnerAvailable);
        Assert.False(status.EmbeddedRunnerEnabled);
        Assert.Equal(0, status.Capacity.Active);
        Assert.Equal(0, status.Capacity.Max);
        Assert.Equal("No runner is connected. Start the Mohist runner process.", status.RunnerMessage);
    }

    [Fact]
    public async Task AgentStatus_WhenRunnerActiveWorksExceedVisibleSessions_CapacityReflectsRunner()
    {
        // Divergence proof required by issue-300/T-001: the runner grain carries
        // more active workflow works than there are visible AgentSessions, so
        // /agent/status.capacity.active must follow the runner active-works
        // count, not the (smaller) AgentSession count.
        var projectName = $"runtime-divergence-{Guid.NewGuid():N}";
        var project = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", projectName);
        var registry = _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        foreach (var staleId in await registry.ListRunnerIdsAsync())
            await registry.UnregisterAsync(staleId);

        var runnerId = $"runtime-divergence-{Guid.NewGuid():N}";

        try
        {
            await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host", projectId = project.Id });
            await _fixture.Client.PatchOkAsync($"/api/runner/{runnerId}", new { slots = 4 });

            var workflowA = $"wf-div-a-{Guid.NewGuid():N}";
            var workflowB = $"wf-div-b-{Guid.NewGuid():N}";
            var workflowProjectId = $"wf-div-project-{Guid.NewGuid():N}";
            await SeedRuntimeDivergenceTemplateAsync(workflowProjectId);

            var workflowAGrain = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowA);
            var workflowBGrain = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowB);
            var startInput = new WorkflowStartInput(Metadata: new WorkflowRunMetadata(
                Name: null,
                CreatedAt: TestTime.UtcNow,
                 ProjectId: workflowProjectId));
            await workflowAGrain.StartAsync(startInput);
            await workflowBGrain.StartAsync(startInput);
            await workflowAGrain.AssignWorkerAsync(runnerId);
            await workflowBGrain.AssignWorkerAsync(runnerId);

            var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
            var first = await runner.PollAsync(_fixture.Services);
            Assert.NotNull(first);
            var second = await runner.PollAsync(_fixture.Services);
            Assert.NotNull(second);

            var httpStatus = await _fixture.Client.GetDataAsync<AgentStatusDto>($"/api/projects/{project.Id}/agent/status");

            // capacity.active reflects the runner active-works count (2
            // distinct workflow owners), NOT the AgentSession visibility count
            // (no AgentSessions were persisted in this scenario).
            Assert.Equal(2, httpStatus.Capacity.Active);
            Assert.Equal(4, httpStatus.Capacity.Max);
            Assert.True(httpStatus.ActiveAgents is null || httpStatus.ActiveAgents.Value.GetArrayLength() == 0);
            Assert.False(httpStatus.Running);

            var runnerView = Assert.Single(httpStatus.Runners, r => r.Id == runnerId);
            Assert.Equal(2, runnerView.Active);
            Assert.Equal(4, runnerView.Max);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    private async Task SeedRuntimeDivergenceTemplateAsync(string projectId)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;
        await using var db = new MohistDbContext(options);
        var templateId = "spec/workflow";
        var templateJson = WorkflowGrainTestHelpers.SerializeProfile(new WorkflowDefinition(
            [
                new StageDefinition("build",
                    [new TaskDefinition("task-1", "Task 1", "spec/task")],
                    [])
            ]));

        var existing = await db.ProjectWorkflowTemplates.FindAsync(projectId, templateId);
        if (existing is null)
        {
            db.ProjectWorkflowTemplates.Add(new Mohist.Server.Infrastructure.Data.Workflow.ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = templateId,
                Template = templateJson,
            });
        }
        else
        {
            existing.Template = templateJson;
            existing.UpdatedAt = TestTime.UtcNow;
        }
        if (await db.ProjectWorkflowProfiles.FindAsync(projectId) is null)
        {
            db.ProjectWorkflowProfiles.Add(new Mohist.Server.Infrastructure.Data.Workflow.ProjectWorkflowProfile
            {
                ProjectId = projectId,
                DefaultWorkflowProfileId = templateId,
            });
        }
        if (await db.WorkflowProfileRecords.FindAsync(projectId, templateId) is null)
        {
            db.WorkflowProfileRecords.Add(new Mohist.Server.Infrastructure.Data.Workflow.WorkflowProfileRecordRow
            {
                ProjectId = projectId,
                ProfileId = templateId,
                Name = templateId,
                DefinitionSource = WorkflowYamlSerializer.ToYaml(new WorkflowDefinition(
                    [new StageDefinition("build", [new TaskDefinition("task-1", "Task 1", "spec/task")], [])])),
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AgentStatus_OnLegacyRoute_WithoutSelectorReturnsBadRequest()
    {
        using var response = await _fixture.Client.GetAsync("/api/agent/status");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ApiFallback_WhenUnknownApiPath_ReturnsNotFound()
    {
        using var response = await _fixture.Client.GetAsync("/api/missing-route");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed record AgentStatusDto(bool Running, bool RunnerAvailable, bool EmbeddedRunnerEnabled, string? RunnerMessage, RunnerDto[] Runners, AgentCapacityDto Capacity, System.Text.Json.JsonElement? ActiveAgents = null);
    private sealed record AgentCapacityDto(int Active, int Max);
    private sealed record RunnerDto(string Id, string? Kind = null, int Active = 0, int Max = 0);
    private sealed record ProjectDto(string Id, string Name);
    private sealed record IssueDto(int Number, string Title);
    private sealed record ApiErrorDto(bool Success, string? Error);

    private static AgentSessionRow CreateRunningSessionRow(string projectId, int issueNumber, string workflowRunId, string workId, string runnerId, string title)
    {
        var now = TestTime.UtcDateTime;
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
        session.AttachPhysicalSession($"runtime-{Guid.NewGuid():N}", null, null, null, null, now);
        return AgentSessionJson.ToRow(session, now);
    }
}
