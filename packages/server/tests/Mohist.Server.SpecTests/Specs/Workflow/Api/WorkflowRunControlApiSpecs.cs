using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Api;

[Collection("IntegrationWorkflow")]
public partial class WorkflowRunControlApiSpecs
{
    private static readonly JsonSerializerOptions ReadJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private readonly IGrainFactory _grains;
    private readonly IServiceProvider _services;
    private readonly string _connectionString;

    public WorkflowRunControlApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
        _grains = fixture.Grains;
        _services = fixture.Services;
        _connectionString = fixture.ConnectionString;
    }

    [Fact]
    public async Task Stop_OnActiveRun_TransitionsToStoppedAndReturnsOk()
    {
        var (projectId, issueNumber, _, wrId) = await SeedActiveWorkflowAsync();

        var response = await _client.PostAsync($"/api/workflow-runs/{wrId}/stop", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await LoadRunAsync(wrId);
        Assert.NotNull(run);
        Assert.Equal(WorkflowRunStatus.Stopped, run!.Status);

        var issueStatus = await GetIssueStatusAsync(projectId, issueNumber);
        Assert.Equal("in_progress", issueStatus);
    }

    [Fact]
    public async Task Approve_OnAwaitingApprovalRun_ApprovesStageAndAdvances()
    {
        var (_, _, _, wrId) = await SeedActiveWorkflowAsync();
        await ForceAwaitingApprovalAsync(wrId);

        var response = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{wrId}/approve",
            new { author = "supervisor" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await LoadRunAsync(wrId);
        var plan = run!.Stages.Single(s => s.Id == "plan");
        Assert.Equal("approved", plan.ApprovalStatus?.Result);
        Assert.Equal("supervisor", plan.ApprovalStatus?.DecidedBy);
        Assert.Equal(StageRunStatus.Completed, plan.Status);
        Assert.Equal("build", run.CurrentStageId);
    }

    [Fact]
    public async Task Reject_OnAwaitingApprovalRun_RecordsFeedbackAndSchedulesFeedbackTask()
    {
        var (_, _, _, wrId) = await SeedActiveWorkflowAsync();
        await ForceAwaitingApprovalAsync(wrId);

        var response = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{wrId}/reject",
            new { author = "supervisor", message = "add more detail" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await LoadRunAsync(wrId);
        Assert.Single(run!.Feedback);
        Assert.Equal("add more detail", run.Feedback[0].Body);
        var plan = run.Stages.Single(s => s.Id == "plan");
        Assert.Equal(StageRunStatus.Running, plan.Status);
        Assert.Equal("supervisor", plan.ApprovalStatus?.DecidedBy);
        Assert.Contains(plan.Tasks, t => t.DefinitionId == WorkflowRunExtensions.DefaultFeedbackTaskId);
    }

    [Fact]
    public async Task Approve_WithOverlongAuthor_Returns400AndDoesNotCallGrain()
    {
        var (_, _, _, wrId) = await SeedActiveWorkflowAsync();
        await ForceAwaitingApprovalAsync(wrId);

        var response = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{wrId}/approve",
            new { author = new string('a', 101) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var run = await LoadRunAsync(wrId);
        Assert.NotEqual(StageRunStatus.Completed, run!.Stages.Single(s => s.Id == "plan").Status);
    }

    [Fact]
    public async Task Approve_TrimsSurroundingWhitespaceFromAuthor()
    {
        var (_, _, _, wrId) = await SeedActiveWorkflowAsync();
        await ForceAwaitingApprovalAsync(wrId);

        var response = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{wrId}/approve",
            new { author = "  supervisor  " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await LoadRunAsync(wrId);
        Assert.Equal("supervisor", run!.Stages.Single(s => s.Id == "plan").ApprovalStatus?.DecidedBy);
    }

    [Fact]
    public async Task Reject_WithOverlongAuthor_Returns400AndDoesNotCallGrain()
    {
        var (_, _, _, wrId) = await SeedActiveWorkflowAsync();
        await ForceAwaitingApprovalAsync(wrId);

        var response = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{wrId}/reject",
            new { author = new string('a', 101), message = "needs more detail" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var run = await LoadRunAsync(wrId);
        Assert.Null(run!.Feedback.FirstOrDefault());
    }

    [Fact]
    public async Task Reject_TrimsSurroundingWhitespaceFromAuthor()
    {
        var (_, _, _, wrId) = await SeedActiveWorkflowAsync();
        await ForceAwaitingApprovalAsync(wrId);

        var response = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{wrId}/reject",
            new { author = "  supervisor  ", message = "needs more detail" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await LoadRunAsync(wrId);
        Assert.Equal("supervisor", run!.Stages.Single(s => s.Id == "plan").ApprovalStatus?.DecidedBy);
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
    public async Task Reject_WithMissingMessage_Returns400AndDoesNotCallGrain()
    {
        var (_, _, _, wrId) = await SeedActiveWorkflowAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{wrId}/reject",
            new { author = "supervisor" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Reject reason is required", payload.GetProperty("error").GetString());
        var run = await LoadRunAsync(wrId);
        Assert.Equal(WorkflowRunStatus.Pending, run!.Status);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task ActiveOnly_OnPendingRun_AdmittedByGuard_NotRejectedAsNotActive(string verb)
    {
        var (_, _, _, wrId) = await SeedActiveWorkflowAsync();

        var content = verb == "reject"
            ? JsonContent.Create(new { author = "supervisor", message = "needs more detail" })
            : JsonContent.Create(new { author = "supervisor" });
        var response = await _client.PostAsync($"/api/workflow-runs/{wrId}/{verb}", content);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (payload.TryGetProperty("error", out var errorEl))
        {
            Assert.NotEqual("Workflow is not active for this run", errorEl.GetString());
        }
    }

    [Fact]
    public async Task Retry_OnActiveRun_AdmitAndInvokesRetryAsync()
    {
        var (_, _, _, wrId) = await SeedActiveWorkflowAsync();

        var response = await _client.PostAsync($"/api/workflow-runs/{wrId}/retry", content: null);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (payload.TryGetProperty("error", out var errorEl))
        {
            Assert.NotEqual("Workflow is not active for this run", errorEl.GetString());
        }
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
    public async Task RerunFromStage_WithUnknownStage_Returns400WithUnknownStageCode()
    {
        var (_, _, _, wrId) = await SeedActiveWorkflowAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{wrId}/rerun-from-stage",
            new { stage = "no-such-stage" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unknown_stage", payload.GetProperty("code").GetString());
        Assert.Contains("no-such-stage", payload.GetProperty("error").GetString());
    }

    [Fact]
    public async Task NotFound_OnUnknownWorkflowRun_Returns404()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/wr_does_not_exist/approve",
            new { author = "supervisor" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("not_found", payload.GetProperty("code").GetString());
        Assert.Contains("wr_does_not_exist", payload.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("resume")]
    [InlineData("pause")]
    [InlineData("stop")]
    public async Task ActiveOnly_OnStoppedRun_Returns409AndDoesNotMutate(string verb)
    {
        var (_, _, _, wrId) = await SeedActiveWorkflowAsync();
        await _grains.GetGrain<IWorkflowGrain>(wrId).StopAsync("seeded-stopped");

        HttpContent? content = verb switch
        {
            "reject" => JsonContent.Create(new { author = "supervisor", message = "rejected via spec" }),
            "approve" => JsonContent.Create(new { author = "supervisor" }),
            _ => null,
        };
        var response = content is null
            ? await _client.PostAsync($"/api/workflow-runs/{wrId}/{verb}", content: null)
            : await _client.PostAsync($"/api/workflow-runs/{wrId}/{verb}", content);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("conflict", payload.GetProperty("code").GetString());
        Assert.Contains("not active", payload.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        var run = await LoadRunAsync(wrId);
        Assert.Equal(WorkflowRunStatus.Stopped, run!.Status);
    }

    [Theory]
    [InlineData("retry")]
    [InlineData("rerun")]
    public async Task RetryOrRerun_OnFailedRun_IsAdmitted(string verb)
    {
        var (_, _, _, wrId) = await SeedActiveWorkflowAsync();
        await ForceFailedStatusAsync(wrId);

        var response = await _client.PostAsync($"/api/workflow-runs/{wrId}/{verb}", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await LoadRunAsync(wrId);
        Assert.NotEqual(WorkflowRunStatus.Failed, run!.Status);
        Assert.Null(run.Failure);
        var plan = run.Stages.Single(s => s.Id == "plan");
        Assert.Null(plan.Failure);
    }

    [Fact]
    public async Task RerunFromStage_WithUnreachedStage_Returns400WithStageNotReachedCode()
    {
        var (_, _, _, wrId) = await SeedActiveWorkflowAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{wrId}/rerun-from-stage",
            new { stage = "build" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("stage_not_reached", payload.GetProperty("code").GetString());
    }

    [Fact]
    public async Task RerunFromStage_WithActiveWorkInRange_Returns409WithActiveWorkCode()
    {
        var (_, _, _, wrId) = await SeedActiveWorkflowAsync();

        // Claim the plan task so it is genuinely Running (active work).
        var wf = _grains.GetGrain<IWorkflowGrain>(wrId);
        await wf.AssignWorkerAsync("spec-runner");
        await wf.ClaimNextAsync("spec-runner");

        var response = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{wrId}/rerun-from-stage",
            new { stage = "plan" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("active_work_in_range", payload.GetProperty("code").GetString());
    }

    [Fact]
    public async Task RetryOrRerun_OnFailedRun_StoppedRunStillRejected()
    {
        var (_, _, _, wrId) = await SeedActiveWorkflowAsync();
        await _grains.GetGrain<IWorkflowGrain>(wrId).StopAsync("seeded-stopped");

        var response = await _client.PostAsync($"/api/workflow-runs/{wrId}/retry", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("approve", "approve")]
    [InlineData("resume", "resume")]
    [InlineData("pause", "force-stop")]
    public async Task CrossPath_ActiveOnlyAdmitDecisionMatchesIssueScopedRoute(string runVerb, string issueVerb)
    {
        var (projectId, issueNumber, _, wrId) = await SeedActiveWorkflowAsync();
        await _grains.GetGrain<IWorkflowGrain>(wrId).StopAsync("seeded-stopped");

        HttpContent? issueContent = issueVerb == "approve"
            ? JsonContent.Create(new { author = "supervisor" })
            : null;
        HttpContent? runContent = runVerb == "approve"
            ? JsonContent.Create(new { author = "supervisor" })
            : null;

        var issueResponse = await _client.PostAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/{issueVerb}", issueContent);
        var runResponse = await _client.PostAsync(
            $"/api/workflow-runs/{wrId}/{runVerb}", runContent);

        Assert.Equal(HttpStatusCode.Conflict, issueResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, runResponse.StatusCode);
    }

    [Fact]
    public async Task CrossPath_RerunOnFailedRun_AdmitDecisionMatchesIssueScopedRoute()
    {
        var (projectId, issueNumber, _, wrId) = await SeedActiveWorkflowAsync();
        await ForceFailedStatusAsync(wrId);

        var issueResponse = await _client.PostAsync($"/api/projects/{projectId}/issues/{issueNumber}/rerun", content: null);
        var runResponse = await _client.PostAsync($"/api/workflow-runs/{wrId}/rerun", content: null);

        Assert.Equal(HttpStatusCode.OK, issueResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, runResponse.StatusCode);
    }

    [Theory]
    [InlineData("rerun", false)]
    [InlineData("rerun-from-stage", true)]
    public async Task CrossPath_RerunWithCorruptedState_RecoversByStartingNewWorkflow(string verb, bool fromStage)
    {
        var (issueProjectId, issueNumber, _, issueWrId) = await SeedActiveWorkflowAsync();
        await CorruptWorkflowRunStateAsync(issueWrId);

        var issueResponse = fromStage
            ? await _client.PostAsJsonAsync($"/api/projects/{issueProjectId}/issues/{issueNumber}/{verb}", new { stage = "plan" })
            : await _client.PostAsync($"/api/projects/{issueProjectId}/issues/{issueNumber}/{verb}", content: null);

        Assert.Equal(HttpStatusCode.OK, issueResponse.StatusCode);
        await DispatchEventsAsync();
        var recoveredIssueWrId = await GetIssueWorkflowRunIdAsync(issueProjectId, issueNumber);
        Assert.NotNull(recoveredIssueWrId);
        Assert.NotEqual(issueWrId, recoveredIssueWrId);
        Assert.NotNull(await LoadRunAsync(recoveredIssueWrId!));

        var (runProjectId, runIssueNumber, _, runWrId) = await SeedActiveWorkflowAsync();
        await CorruptWorkflowRunStateAsync(runWrId);

        var runResponse = fromStage
            ? await _client.PostAsJsonAsync($"/api/workflow-runs/{runWrId}/{verb}", new { stage = "plan" })
            : await _client.PostAsync($"/api/workflow-runs/{runWrId}/{verb}", content: null);

        Assert.Equal(HttpStatusCode.OK, runResponse.StatusCode);
        await DispatchEventsAsync();
        var recoveredRunWrId = await GetIssueWorkflowRunIdAsync(runProjectId, runIssueNumber);
        Assert.NotNull(recoveredRunWrId);
        Assert.NotEqual(runWrId, recoveredRunWrId);
        Assert.NotNull(await LoadRunAsync(recoveredRunWrId!));
    }

    [Theory]
    [InlineData("retry")]
    [InlineData("stop")]
    public async Task CrossPath_FailedRun_AdmitsRecoveryAndStop(string verb)
    {
        var (issueProjectId, issueNumber, _, issueWrId) = await SeedActiveWorkflowAsync();
        await ForceFailedStatusAsync(issueWrId);

        var issueResponse = await _client.PostAsync($"/api/projects/{issueProjectId}/issues/{issueNumber}/{verb}", content: null);
        Assert.Equal(HttpStatusCode.OK, issueResponse.StatusCode);

        var (_, _, _, runWrId) = await SeedActiveWorkflowAsync();
        await ForceFailedStatusAsync(runWrId);

        var runResponse = await _client.PostAsync($"/api/workflow-runs/{runWrId}/{verb}", content: null);
        Assert.Equal(HttpStatusCode.OK, runResponse.StatusCode);
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
            new { author = "supervisor" });
        var runResponse = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{wrId}/reject",
            new { author = "supervisor" });

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

    private async Task<(string issueKey, int number)> CreateIssueInBacklogAsync(string projectId)
    {
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueKey = GrainKey.Issue(new IssueKey(projectId, number));
        var grain = _grains.GetGrain<IIssueGrain>(issueKey);
        await grain.CreateAsync(projectId, number, "Workflow control test", null, null, null, isDraft: false);
        return (issueKey, number);
    }

    private Task DispatchEventsAsync() =>
        _grains.GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global).DispatchNowAsync();

    private async Task SeedWorkflowTemplateAsync(string projectId)
    {
        var definition = new WorkflowDefinition(
        [
            new StageDefinition("plan", [new("draft", "Draft", "spec/task")], []),
            new StageDefinition("build", [new("compile", "Compile", "spec/task")], []),
        ]);

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        const string templateId = "spec/workflow";
        var existingTemplate = await db.ProjectWorkflowTemplates.FindAsync(projectId, templateId);
        if (existingTemplate is null)
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = templateId,
                Template = WorkflowGrainTestHelpers.SerializeProfile(definition),
            });
        }
        else
        {
            existingTemplate.Template = WorkflowGrainTestHelpers.SerializeProfile(definition);
            existingTemplate.UpdatedAt = TestTime.UtcNow;
        }

        var profile = await db.ProjectWorkflowProfiles.FindAsync(projectId);
        if (profile is null)
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = projectId,
                DefaultTemplateId = templateId,
            });
        }
        else
        {
            profile.DefaultTemplateId = templateId;
            profile.UpdatedAt = TestTime.UtcNow;
        }
        await db.SaveChangesAsync();
    }

    private async Task<WorkflowRun> LoadRunAsync(string wrId)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        return await store.LoadAsync(wrId) ?? throw new InvalidOperationException($"Workflow run '{wrId}' not found");
    }

    private async Task<string?> GetIssueStatusAsync(string projectId, int issueNumber)
    {
        using var scope = _services.CreateScope();
        var issues = scope.ServiceProvider.GetRequiredService<Mohist.Server.Issue.Services.IssueQuerier>();
        var info = await issues.GetInfoAsync(projectId, issueNumber);
        return info?.Status;
    }

    private async Task<string?> GetIssueWorkflowRunIdAsync(string projectId, int issueNumber)
    {
        using var scope = _services.CreateScope();
        var issues = scope.ServiceProvider.GetRequiredService<Mohist.Server.Issue.Services.IssueQuerier>();
        var info = await issues.GetInfoAsync(projectId, issueNumber);
        return info?.WorkflowRunId;
    }

    private async Task CorruptWorkflowRunStateAsync(string wrId)
    {
        await DeactivateWorkflowAsync(wrId);

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        await using var db = new MohistDbContext(options);
        var row = await db.WorkflowRuns.FindAsync(wrId)
            ?? throw new InvalidOperationException($"Workflow run {wrId} not found in store");
        row.State = "{}";
        await db.SaveChangesAsync();

        await _grains.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero);
    }

    private async Task ForceFailedStatusAsync(string wrId)
    {
        await DeactivateWorkflowAsync(wrId);

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        await using var db = new MohistDbContext(options);
        var row = await db.WorkflowRuns.FindAsync(wrId)
            ?? throw new InvalidOperationException($"Workflow run {wrId} not found in store");
        using var doc = JsonDocument.Parse(row.State);
        var state = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(doc.RootElement.GetRawText())!;
        state["status"] = JsonSerializer.SerializeToElement("Failed", JSON.Options);
        var failure = JsonSerializer.SerializeToElement(new
        {
            reason = "TaskFailed",
            stageId = "plan",
            taskId = "draft",
            message = "spec-forced failure",
        }, JSON.Options);
        state["failure"] = failure;
        if (state.TryGetValue("stages", out var stagesEl) && stagesEl.ValueKind == JsonValueKind.Array)
        {
            var stages = stagesEl.EnumerateArray().ToList();
            for (var i = 0; i < stages.Count; i++)
            {
                var stage = stages[i].Deserialize<Dictionary<string, JsonElement>>()!;
                stage["status"] = JsonSerializer.SerializeToElement("Failed", JSON.Options);
                stage["failure"] = failure;
                if (i == 0)
                {
                    stage["tasks"] = JsonSerializer.SerializeToElement(new[]
                    {
                        new
                        {
                            id = "draft",
                            definitionId = "draft",
                            attempt = 1,
                            title = "Draft task",
                            status = "Failed",
                            classification = "UserFacing",
                        }
                    }, JSON.Options);
                }
                stages[i] = JsonSerializer.SerializeToElement(stage, JSON.Options);
            }
            state["stages"] = JsonSerializer.SerializeToElement(stages, JSON.Options);
        }
        row.State = JsonSerializer.Serialize(state, JSON.Options);
        await db.SaveChangesAsync();
    }

    private async Task DeactivateWorkflowAsync(string workflowRunId)
    {
        await _grains.GetGrain<IWorkflowGrain>(workflowRunId).DeactivateForTestAsync();
        var management = _grains.GetGrain<IManagementGrain>(0);
        await management.ForceActivationCollection(TimeSpan.Zero);
        await TestWait.ForAsync(
            async () => await management.GetDetailedGrainStatistics(),
            activations => !activations.Any(stat =>
                stat.GrainType.Contains(nameof(WorkflowGrain), StringComparison.Ordinal)
                && stat.GrainId.ToString()!.Contains(workflowRunId, StringComparison.Ordinal)),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(50),
            $"Workflow grain '{workflowRunId}' to deactivate",
            () => management.ForceActivationCollection(TimeSpan.Zero));
    }

    private async Task ForceAwaitingApprovalAsync(string wrId)
    {
        var wfGrain = _grains.GetGrain<IWorkflowGrain>(wrId);
        await DeactivateWorkflowAsync(wrId);

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        await using var db = new MohistDbContext(options);
        var row = await db.WorkflowRuns.FindAsync(wrId)
            ?? throw new InvalidOperationException($"Workflow run {wrId} not found in store");
        using var doc = JsonDocument.Parse(row.State);
        var state = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(doc.RootElement.GetRawText())!;
        state["status"] = JsonSerializer.SerializeToElement("AwaitingApproval", JSON.Options);
        state["currentStageId"] = JsonSerializer.SerializeToElement("plan", JSON.Options);
        if (state.TryGetValue("stages", out var stagesEl) && stagesEl.ValueKind == JsonValueKind.Array)
        {
            var stages = stagesEl.EnumerateArray().ToList();
            for (var i = 0; i < stages.Count; i++)
            {
                var stage = stages[i].Deserialize<Dictionary<string, JsonElement>>()!;
                if (string.Equals(stage["id"].GetString(), "plan", StringComparison.Ordinal))
                {
                    stage["status"] = JsonSerializer.SerializeToElement("AwaitingApproval", JSON.Options);
                    stage["initialized"] = JsonSerializer.SerializeToElement(true, JSON.Options);
                    stage["requiresApproval"] = JsonSerializer.SerializeToElement(true, JSON.Options);
                    stage["tasks"] = JsonSerializer.SerializeToElement(Array.Empty<object>(), JSON.Options);
                    stage["checks"] = JsonSerializer.SerializeToElement(Array.Empty<object>(), JSON.Options);
                    stage["approvalStatus"] = JsonSerializer.SerializeToElement(new
                    {
                        result = (string?)null,
                        requestedAt = TestTime.UtcNow.ToString("O"),
                        respondedAt = (string?)null,
                    }, JSON.Options);
                }
                stages[i] = JsonSerializer.SerializeToElement(stage, JSON.Options);
            }
            state["stages"] = JsonSerializer.SerializeToElement(stages, JSON.Options);
        }
        row.State = JsonSerializer.Serialize(state, JSON.Options);
        await db.SaveChangesAsync();
        await wfGrain.DeactivateForTestAsync();
    }
}
