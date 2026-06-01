using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Infrastructure;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class IssueWorkflowProfileApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private string? _runnerId;
    private string? _projectId;
    private int _issueNumber;

    public IssueWorkflowProfileApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task GetWorkflowProfileYaml_ReturnsNormalizedYaml_ForBacklogIssue()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"profile-get-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Backlog issue for profile get", projectId = project.Id });

        var response = await _client.GetAsync($"/api/issues/{issue.Number}/workflow/profile/yaml?projectId={project.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = result.GetProperty("data");
        Assert.Equal(issue.Number, data.GetProperty("issueNumber").GetInt32());
        Assert.Equal(project.Id, data.GetProperty("projectId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("updatedAt").GetString()));
    }

    [Fact]
    public async Task SaveWorkflowProfileYaml_UpdatesIssueProfile_WithoutMutatingProjectProfile()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"profile-save-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Backlog issue for profile save", projectId = project.Id });

        var customYaml = """
            id: custom-issue-workflow
            stages:
              - stage: plan
                tasks:
                  - id: custom-task
                    title: Custom Task
                checks: []
            """;
        var saveResponse = await _client.PutAsJsonAsync($"/api/issues/{issue.Number}/workflow/profile/yaml?projectId={project.Id}", new { yaml = customYaml });

        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);
        var saved = await saveResponse.Content.ReadFromJsonAsync<JsonElement>();
        var savedData = saved.GetProperty("data");
        var savedYaml = savedData.GetProperty("yaml").GetString();
        Assert.NotNull(savedYaml);
        Assert.Contains("custom-issue-workflow", savedYaml);
        Assert.Contains("custom-task", savedYaml);
        Assert.Equal("Custom", savedData.GetProperty("updateMode").GetString());
        Assert.Equal("custom-issue-workflow", savedData.GetProperty("profileId").GetString());

        var projectProfilesResponse = await _client.GetAsync("/api/workflow-profiles");
        var projectProfilesJson = await projectProfilesResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectProfilesData = projectProfilesJson.GetProperty("data");
        var projectProfiles = projectProfilesData.EnumerateArray()
            .Select(p => new WorkflowProfileDto(
                p.GetProperty("id").GetString()!,
                p.GetProperty("displayName").GetString()!,
                p.GetProperty("description").GetString()!,
                p.GetProperty("isDefault").GetBoolean()))
            .ToList();
        var defaultProfile = projectProfiles?.FirstOrDefault(p => p.Id == "mohist/default");
        Assert.NotNull(defaultProfile);

        var getResponse = await _client.GetAsync($"/api/issues/{issue.Number}/workflow/profile/yaml?projectId={project.Id}");
        var reloaded = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        var reloadedData = reloaded.GetProperty("data");
        var reloadedYaml = reloadedData.GetProperty("yaml").GetString();
        Assert.Equal(savedYaml, reloadedYaml);
        Assert.Equal(savedData.GetProperty("profileId").GetString(), reloadedData.GetProperty("profileId").GetString());
        Assert.Equal(savedData.GetProperty("updateMode").GetString(), reloadedData.GetProperty("updateMode").GetString());
        Assert.Equal(savedData.GetProperty("updatedAt").GetString(), reloadedData.GetProperty("updatedAt").GetString());
    }

    [Fact]
    public async Task SaveWorkflowProfileYaml_RejectsInvalidYamlSyntax()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"profile-yaml-err-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Backlog issue for yaml error", projectId = project.Id });

        var invalidYaml = """
            id: broken
            stages:
              - stage: plan
                tasks:
                  - id: bad
                    title: Bad Task
                    uses: spec/task
            invalid yaml structure here: [unclosed
            """;
        var response = await _client.PutAsJsonAsync($"/api/issues/{issue.Number}/workflow/profile/yaml?projectId={project.Id}", new { yaml = invalidYaml });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("yaml_syntax", payload.GetProperty("code").GetString());
        Assert.Contains("YAML", payload.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);

        var getResponse = await _client.GetAsync($"/api/issues/{issue.Number}/workflow/profile/yaml?projectId={project.Id}");
        var state = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        var stateData = state.GetProperty("data");
        var yaml = stateData.GetProperty("yaml").GetString();
        Assert.DoesNotContain("broken", yaml ?? "");
    }

    [Fact]
    public async Task SaveWorkflowProfileYaml_RejectsInvalidWorkflowShape()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"profile-shape-err-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Backlog issue for shape error", projectId = project.Id });

        var invalidShapeYaml = """
            id: no-stages-workflow
            stages: []
            """;
        var response = await _client.PutAsJsonAsync($"/api/issues/{issue.Number}/workflow/profile/yaml?projectId={project.Id}", new { yaml = invalidShapeYaml });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("workflow_shape", payload.GetProperty("code").GetString());
        Assert.Contains("stage", payload.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);

        var getResponse = await _client.GetAsync($"/api/issues/{issue.Number}/workflow/profile/yaml?projectId={project.Id}");
        var state = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        var stateData = state.GetProperty("data");
        Assert.Equal("Reference", stateData.GetProperty("updateMode").GetString());
    }

    [Fact]
    public async Task SaveWorkflowProfileYaml_SynchronizesActiveRunProfile_AndPreservesInitializedStageWork()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"profile-sync-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Active run profile sync issue", projectId = project.Id });
        await StartWorkflowWithRunnerAsync(project.Id, issue.Number, $"profile-sync-runner-{Guid.NewGuid():N}");

        await DrainUntilApprovalAsync(project.Id, issue.Number, "plan");
        var timelineBeforeSave = await _client.GetDataAsync<WorkflowTimelineDto>($"/api/issues/{issue.Number}/workflow/timeline?projectId={project.Id}");
        var planStageBeforeSave = Assert.Single(timelineBeforeSave.Stages, s => s.Stage == "plan");
        var planTaskIdsBeforeSave = planStageBeforeSave.Tasks.Select(t => t.Id).ToArray();
        var planCheckNamesBeforeSave = planStageBeforeSave.Checks.Select(c => c.Name).ToArray();
        Assert.NotEmpty(planTaskIdsBeforeSave);
        Assert.NotEmpty(planCheckNamesBeforeSave);

        var customYaml = """
            id: synced-workflow
            stages:
              - stage: plan
                tasks:
                  - id: replacement-plan-task
                    title: Replacement Plan Task
                    uses: mohist/acp-agent
                checks:
                  - name: replacement-plan-check
                    title: Replacement Plan Check
                    uses: mohist/check-typecheck
              - stage: build
                tasks:
                  - id: new-synced-task
                    title: New Synced Task
                    uses: mohist/acp-agent
                checks:
                  - name: new-build-check
                    title: New Build Check
                    uses: mohist/check-typecheck
            """;
        var saveResponse = await _client.PutAsJsonAsync($"/api/issues/{issue.Number}/workflow/profile/yaml?projectId={project.Id}", new { yaml = customYaml });
        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);

        var saved = await saveResponse.Content.ReadFromJsonAsync<JsonElement>();
        var savedData = saved.GetProperty("data");
        var savedYaml = savedData.GetProperty("yaml").GetString();
        Assert.Contains("synced-workflow", savedYaml!);

        var timelineAfterSave = await _client.GetDataAsync<WorkflowTimelineDto>($"/api/issues/{issue.Number}/workflow/timeline?projectId={project.Id}");
        var planStageAfterSave = Assert.Single(timelineAfterSave.Stages, s => s.Stage == "plan");
        Assert.Equal(planTaskIdsBeforeSave, planStageAfterSave.Tasks.Select(t => t.Id).ToArray());
        Assert.Equal(planCheckNamesBeforeSave, planStageAfterSave.Checks.Select(c => c.Name).ToArray());
        Assert.DoesNotContain(planStageAfterSave.Tasks, t => t.Id == "replacement-plan-task");
        Assert.DoesNotContain(planStageAfterSave.Checks, c => c.Name == "replacement-plan-check");

        var updatedRunYamlResponse = await _client.GetAsync($"/api/issues/{issue.Number}/workflow/yaml?projectId={project.Id}");
        updatedRunYamlResponse.EnsureSuccessStatusCode();
        var updatedRunYaml = await updatedRunYamlResponse.Content.ReadFromJsonAsync<JsonElement>();
        var updatedRunData = updatedRunYaml.GetProperty("data");
        var updatedYamlStr = updatedRunData.GetProperty("yaml").GetString();
        var updatedDefinition = WorkflowYamlSerializer.FromYaml(updatedYamlStr!);
        var buildStage = updatedDefinition.Stages.FirstOrDefault(s => s.Stage == "build");
        Assert.NotNull(buildStage);
        Assert.Contains(buildStage.Tasks, t => t.Id == "new-synced-task");
        Assert.Contains(buildStage.Checks, c => c.Name == "new-build-check");
    }

    [Fact]
    public async Task NextStageInitialization_UsesUpdatedDefinition_AfterProfileSave()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"profile-next-stage-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Next stage init issue", projectId = project.Id });
        await StartWorkflowWithRunnerAsync(project.Id, issue.Number, $"profile-next-stage-runner-{Guid.NewGuid():N}");

        await DrainUntilApprovalAsync(project.Id, issue.Number, "plan");

        var customYaml = """
            id: updated-next-stage
            stages:
              - stage: plan
                tasks:
                  - id: plan-only-task
                    title: Plan Only Task
                    uses: mohist/acp-agent
                checks: []
              - stage: build
                tasks:
                  - id: brand-new-build-task
                    title: Brand New Build Task
                    uses: mohist/acp-agent
                checks:
                  - name: build-definition-check
                    title: Build Definition Check
                    uses: mohist/check-typecheck
            """;
        var saveResponse = await _client.PutAsJsonAsync($"/api/issues/{issue.Number}/workflow/profile/yaml?projectId={project.Id}", new { yaml = customYaml });
        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);

        await _client.PostOkAsync($"/api/issues/{issue.Number}/approve?projectId={project.Id}");
        for (var i = 0; i < 100; i++)
        {
            var buildTimelineAttempt = await _client.GetDataAsync<WorkflowTimelineDto>($"/api/issues/{issue.Number}/workflow/timeline?projectId={project.Id}");
            var buildStageAttempt = buildTimelineAttempt.Stages.FirstOrDefault(s => s.Stage == "build");
            if (buildStageAttempt is not null && buildStageAttempt.Tasks.Any(t => t.Id == "brand-new-build-task"))
                break;
            await Task.Delay(20);
        }

        var buildTimeline = await _client.GetDataAsync<WorkflowTimelineDto>($"/api/issues/{issue.Number}/workflow/timeline?projectId={project.Id}");
        var buildStage = Assert.Single(buildTimeline.Stages, s => s.Stage == "build");
        Assert.Contains(buildStage.Tasks, t => t.Id == "brand-new-build-task");
        Assert.Contains(buildStage.Checks, c => c.Name == "build-definition-check");
    }

    private async Task StartWorkflowWithRunnerAsync(string projectId, int issueNumber, string runnerId)
    {
        _projectId = projectId;
        _issueNumber = issueNumber;
        _runnerId = runnerId;

        await _client.PostOkAsync($"/api/issues/{issueNumber}/start?projectId={projectId}", null);
        await _client.PostOkAsync($"/api/runner/{runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host" });
        var startedIssue = await _client.GetDataAsync<IssueWithWorkflowDto>($"/api/issues/{issueNumber}?projectId={projectId}");
        Assert.False(string.IsNullOrWhiteSpace(startedIssue.WorkflowRunId));
        await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).AssignWorkflowAsync(startedIssue.WorkflowRunId!);
    }

    private async Task DrainUntilApprovalAsync(string projectId, int issueNumber, string stage)
    {
        for (var i = 0; i < 100; i++)
        {
            var status = await _client.GetDataAsync<IssueWorkflowEnvelopeDto>($"/api/issues/{issueNumber}/workflow/status?projectId={projectId}");
            if (status.Workflow?.Status == "AwaitingApproval" && status.Workflow.CurrentStage == stage)
                return;
            await CompleteNextWorkAsync();
        }

        Assert.Fail($"Workflow did not reach approval at stage {stage}");
    }

    private async Task CompleteNextWorkAsync()
    {
        var work = await PollWorkAnyAsync();
        switch (work.WorkType)
        {
            case "task":
                if (work.Uses == "mohist/openspec-tasks")
                {
                    var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(work.WorkflowRunId);
                    await workflow.AddTasksAsync(new AddTasksBatchRequest([
                        new AddTasksBatchItem("build-1", "Build task", "mohist/acp-agent")
                    ]));
                }
                await ReportAsync(work.WorkId, "completed");
                break;
            case "checks":
                var checkNames = ParseCheckNames(work.With);
                await ReportAsync(work.WorkId, "pass", output: JsonSerializer.Serialize(checkNames.Select(name => new { name, status = "pass" })));
                break;
            default:
                await ReportAsync(work.WorkId, "completed");
                break;
        }
    }

    private async Task<WorkDispatchDto> PollWorkAnyAsync()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var response = await _client.PostAsync($"/api/runner/{_runnerId}/poll", null);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                await Task.Delay(20);
                continue;
            }

            response.EnsureSuccessStatusCode();
            var work = await response.Content.ReadFromJsonAsync<WorkDispatchDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException("Empty work dispatch");
            if (work.Stage == "plan" && work.Uses == "mohist/acp-agent")
            {
                if (!IsCurrentIssueWork(work))
                {
                    await ReportAsync(work.WorkId, "completed");
                    continue;
                }
            }
            else if (!IsCurrentIssueWork(work))
            {
                await ReportAsync(work.WorkId, work.WorkType == "checks" ? "pass" : "completed");
                continue;
            }

            return work;
        }

        Assert.Fail($"Runner '{_runnerId}' has no work");
        return default!;
    }

    private bool IsCurrentIssueWork(WorkDispatchDto work)
    {
        return work.ProjectId == _projectId && work.IssueNumber == _issueNumber;
    }

    private Task ReportAsync(string workId, string status, string? message = null, string? output = null, int? exitCode = null) =>
        _client.PostOkAsync($"/api/runner/{_runnerId}/report", new { workId, status, message, output, exitCode });

    private static string[] ParseCheckNames(string? with)
    {
        if (string.IsNullOrWhiteSpace(with))
            return [];
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(with);
        if (payload is null || !payload.TryGetValue("checks", out var checks) || checks is null)
            return [];
        return checks.Value.EnumerateArray()
            .Select(item => item.GetProperty("name").GetString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToArray();
    }

    private sealed record ProjectDto(string Id);
    private sealed record IssueDto(int Number, string Id);
    private sealed record IssueWithWorkflowDto(int Number, string Id, string? WorkflowRunId);
    private sealed record WorkflowProfileDto(string Id, string DisplayName, string Description, bool IsDefault);
    private sealed record IssueWorkflowEnvelopeDto(WorkflowStatusDto? Workflow);
    private sealed record WorkflowStatusDto(string Status, string? CurrentStage);
    private sealed record WorkflowTimelineDto(string WorkflowRunId, string Status, string? CurrentStage, WorkflowStageDto[] Stages);
    private sealed record WorkflowStageDto(string Stage, string Status, WorkflowTaskDto[] Tasks, WorkflowCheckDto[] Checks, ApprovalDto? ApprovalStatus);
    private sealed record WorkflowTaskDto(string Id, string Title, string? Uses, string Status);
    private sealed record WorkflowCheckDto(string Name, string Title, string? Uses, string Status, string? Message);
    private sealed record ApprovalDto(string? Result);
    private sealed record WorkDispatchDto(
        string WorkflowRunId,
        string WorkId,
        string? Uses,
        string? With,
        string? Variables,
        string WorkType,
        string? Stage,
        string? Title,
        string? ProjectId,
        int? IssueNumber);
}
