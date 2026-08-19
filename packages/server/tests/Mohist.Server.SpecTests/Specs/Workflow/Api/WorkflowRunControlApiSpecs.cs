using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Events.Grains;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Api;

public partial class WorkflowRunControlApiSpecs
{
    private readonly HttpClient _client;
    private readonly IGrainFactory _grains;
    private readonly IServiceProvider _services;
    private readonly string _connectionString;

    public WorkflowRunControlApiSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
        _grains = fixture.Grains;
        _services = fixture.Services;
        _connectionString = fixture.ConnectionString;
    }

    [Fact]
    public async Task Pause_OnActiveRun_TransitionsToPaused()
    {
        var (_, _, _, wrId) = await SeedActiveWorkflowAsync();

        var response = await _client.PostAsync($"/api/workflow-runs/{wrId}/pause", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await LoadRunAsync(wrId);
        Assert.NotNull(run);
        Assert.Equal(WorkflowRunStatus.Paused, run!.Status);
    }

    [Fact]
    public async Task Resume_OnPausedRun_LeavesRunActive()
    {
        var (_, _, _, wrId) = await SeedActiveWorkflowAsync();
        await _grains.GetGrain<IWorkflowGrain>(wrId).PauseAsync("seeded-for-resume");

        var response = await _client.PostAsync($"/api/workflow-runs/{wrId}/resume", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await LoadRunAsync(wrId);
        Assert.NotNull(run);
        Assert.NotEqual(WorkflowRunStatus.Paused, run!.Status);
    }

    [Fact]
    public async Task RerunFromStage_WithBlankStage_Returns400()
    {
        var (_, _, _, wrId) = await SeedActiveWorkflowAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{wrId}/rerun-from-stage",
            new { stage = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Stage is required", payload.GetProperty("error").GetString());
    }

    [Fact]
    public async Task NotFound_OnUnknownWorkflowRun_Returns404()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/wr_does_not_exist/approve",
            new { displayName = "supervisor" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("not_found", payload.GetProperty("code").GetString());
        Assert.Contains("wr_does_not_exist", payload.GetProperty("error").GetString());
    }

    [Fact]
    public async Task TasksBatch_PostWithExpect_PropagatesExpectIntoMaterializedTaskRun()
    {
        // Spec scenario "A dynamically generated task uses the canonical
        // declaration": the runner posts a generated task with both `with`
        // and `expect` to /api/workflow-runs/{wrId}/tasks/batch; the HTTP
        // DTO MUST carry `expect` through to AddTasksBatchItem.Expect and
        // the materialized TaskRun MUST observe ExpectInput.
        var (_, _, _, wrId) = await SeedActiveWorkflowAsync();

        var body = new
        {
            tasks = new[]
            {
                new
                {
                    id = "T-dynamic",
                    title = "Dynamic task with completion contract",
                    uses = "mohist/opencode",
                    @with = new { prompt = "do work" },
                    expect = new
                    {
                        files = new[] { new { path = "src/FeatureFlags.cs" } },
                        markers = new[]
                        {
                            new { path = "review.md", oneOf = new[] { "<promise>PASS</promise>", "<promise>FAIL</promise>" } },
                        },
                    },
                },
            },
        };

        var response = await _client.PostAsJsonAsync($"/api/workflow-runs/{wrId}/tasks/batch", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The materialized TaskRun must carry the `expect` declaration so
        // the executor's completion evaluator can read it from the dispatch
        // envelope (workflow-task-contract spec).
        var run = await LoadRunAsync(wrId);
        var stage = run.Stages.Single(s => s.Id == "plan");
        var dynamicTask = stage.Tasks.Single(t => t.DefinitionId == "T-dynamic");
        Assert.NotNull(dynamicTask.ExpectInput);
        Assert.True(dynamicTask.ExpectInput!.ContainsKey("files"));
        Assert.True(dynamicTask.ExpectInput.ContainsKey("markers"));
    }

    [Fact]
    public async Task CrossPath_RejectWithoutMessage_FailsBothRoutesWith400()
    {
        var (projectId, issueNumber, _, wrId) = await SeedActiveWorkflowAsync();

        var issueResponse = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/reject",
            new { displayName = "supervisor" });
        var runResponse = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{wrId}/reject",
            new { displayName = "supervisor" });

        Assert.Equal(HttpStatusCode.BadRequest, issueResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, runResponse.StatusCode);
    }

    [Fact]
    public async Task CrossPath_RerunFromStage_UnknownStageReturnsSameStructuredError()
    {
        var (projectId, issueNumber, _, wrId) = await SeedActiveWorkflowAsync();

        var issueResponse = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/rerun-from-stage",
            new { stage = "no-such-stage" });
        var runResponse = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{wrId}/rerun-from-stage",
            new { stage = "no-such-stage" });

        Assert.Equal(HttpStatusCode.BadRequest, issueResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, runResponse.StatusCode);
        var issuePayload = await issueResponse.Content.ReadFromJsonAsync<JsonElement>();
        var runPayload = await runResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unknown_stage", issuePayload.GetProperty("code").GetString());
        Assert.Equal("unknown_stage", runPayload.GetProperty("code").GetString());
    }

    [Fact]
    public async Task IssueScoped_ExistingEndpoints_RegressionUnchanged()
    {
        var (projectId, issueNumber, _, wrId) = await SeedActiveWorkflowAsync();

        var stopResponse = await _client.PostAsync($"/api/projects/{projectId}/issues/{issueNumber}/stop", content: null);
        Assert.Equal(HttpStatusCode.OK, stopResponse.StatusCode);
        var run = await LoadRunAsync(wrId);
        Assert.Equal(WorkflowRunStatus.Stopped, run!.Status);

        var resumeResponse = await _client.PostAsync($"/api/projects/{projectId}/issues/{issueNumber}/resume", content: null);
        Assert.Equal(HttpStatusCode.Conflict, resumeResponse.StatusCode);
    }

    private async Task<(string projectId, int issueNumber, string issueKey, string wrId)> SeedActiveWorkflowAsync()
    {
        var (projectId, _) = await SeedProjectAsync();
        var (issueKey, issueNumber) = await CreateIssueInBacklogAsync(projectId);
        await SeedWorkflowTemplateAsync(projectId);
        var grain = _grains.GetGrain<IIssueGrain>(issueKey);
        var wrId = await grain.StartWorkAsync();
        await DispatchEventsAsync();
        return (projectId, issueNumber, issueKey, wrId);
    }

    private async Task<(string projectId, int issueNumber, string issueKey, string wrId)> SeedAwaitingApprovalWorkflowAsync()
    {
        var (projectId, _) = await SeedProjectAsync();
        var (issueKey, issueNumber) = await CreateIssueInBacklogAsync(projectId);
        var definition = new WorkflowDefinition(
        [
            new StageDefinition("plan", [], [], RequiresApproval: true),
        ]);
        await WorkflowApiTestSupport.SeedWorkflowProfileAsync(_connectionString, projectId, definition);
        var wrId = await _grains.GetGrain<IIssueGrain>(issueKey).StartWorkAsync();
        await DispatchEventsAsync();
        var run = await LoadRunAsync(wrId);
        Assert.Equal(WorkflowRunStatus.AwaitingApproval, run.Status);
        return (projectId, issueNumber, issueKey, wrId);
    }

    private async Task<(string projectId, string projectName)> SeedProjectAsync()
    {
        var id = $"proj_{Guid.NewGuid():N}";
        var name = $"wr-control-{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(id);
        await projectGrain.CreateAsync(name, new Mohist.Server.Project.Domain.RepositoryInfo
        {
            Name = "origin",
            GitUrl = "git@example.com:test.git",
            BaseBranch = "main",
            IsDefault = true,
        });
        return (id, name);
    }

    private Task<(string issueKey, int number)> CreateIssueInBacklogAsync(string projectId) =>
        WorkflowApiTestSupport.CreateIssueInBacklogAsync(_grains, projectId);

    private Task DispatchEventsAsync() =>
        WorkflowApiTestSupport.DispatchEventsAsync(_grains);

    private Task SeedWorkflowTemplateAsync(string projectId) =>
        WorkflowApiTestSupport.SeedWorkflowTemplateAsync(_connectionString, projectId);

    private Task<WorkflowRun> LoadRunAsync(string wrId) =>
        WorkflowApiTestSupport.LoadRunAsync(_services, wrId);

}
