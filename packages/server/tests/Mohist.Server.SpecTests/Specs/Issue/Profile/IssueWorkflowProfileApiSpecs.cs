using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Issue.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Profile;

[Collection("IssueProfile")]
public class IssueWorkflowProfileApiSpecs : IAsyncLifetime
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

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (!string.IsNullOrWhiteSpace(_runnerId))
        {
            using var __ = await _client.PostAsync($"/api/runner/{_runnerId}/unregister", null);
        }

        if (!string.IsNullOrWhiteSpace(_projectId) && _issueNumber > 0)
        {
            using var _ = await _client.PostAsync($"/api/projects/{_projectId}/issues/{_issueNumber}/stop", null);
        }
    }

    [Fact]
    public async Task GetWorkflowProfileYaml_ReturnsNormalizedYaml_ForBacklogIssue()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"profile-get-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Backlog issue for profile get", projectId = project.Id });

        var response = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = result.GetProperty("data");
        Assert.Equal(issue.Number, data.GetProperty("issueNumber").GetInt32());
        Assert.Equal(project.Id, data.GetProperty("projectId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("updatedAt").GetString()));
    }

    [Fact]
    public async Task GetWorkflowProfileYaml_ExposesTemplateSourceLabel_ForInheritedProjectAndCustomModes()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"profile-source-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Backlog issue for template source", projectId = project.Id });

        var initial = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        var initialResult = await initial.Content.ReadFromJsonAsync<JsonElement>();
        var initialData = initialResult.GetProperty("data");
        Assert.Equal("system", initialData.GetProperty("templateSource").GetString());
        Assert.Equal("reference", initialData.GetProperty("updateMode").GetString());
        Assert.False(initialData.GetProperty("hasCustomTemplate").GetBoolean());
        Assert.False(initialData.TryGetProperty("yaml", out _));

        var projectRefResponse = await _client.PutAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/template",
            new { projectTemplateId = "project-template-marker" });
        Assert.Equal(HttpStatusCode.OK, projectRefResponse.StatusCode);
        var projectRefData = (await projectRefResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("project", projectRefData.GetProperty("templateSource").GetString());
        Assert.Equal("reference", projectRefData.GetProperty("updateMode").GetString());
        Assert.False(projectRefData.GetProperty("hasCustomTemplate").GetBoolean());
        Assert.Equal("project-template-marker", projectRefData.GetProperty("sourceTemplateId").GetString());
        Assert.False(projectRefData.TryGetProperty("yaml", out _));

        var customYaml = """
            id: source-label-custom-workflow
            stages:
              - stage: plan
                tasks:
                  - id: source-label-task
                    title: Source Label Task
                checks: []
            """;
        var customResponse = await _client.PutAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/template",
            new { yaml = customYaml });
        Assert.Equal(HttpStatusCode.OK, customResponse.StatusCode);
        var customData = (await customResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("custom", customData.GetProperty("templateSource").GetString());
        Assert.Equal("custom", customData.GetProperty("updateMode").GetString());
        Assert.True(customData.GetProperty("hasCustomTemplate").GetBoolean());
        Assert.False(customData.TryGetProperty("sourceTemplateId", out _));

        var deleteResponse = await _client.DeleteAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/template");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        var clearedData = (await deleteResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("system", clearedData.GetProperty("templateSource").GetString());
        Assert.False(clearedData.GetProperty("hasCustomTemplate").GetBoolean());
        Assert.False(clearedData.TryGetProperty("yaml", out _));
    }

    [Fact]
    public async Task SaveWorkflowProfileYaml_UpdatesIssueProfile_WithoutMutatingProjectProfile()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"profile-save-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Backlog issue for profile save", projectId = project.Id });

        var customYaml = """
            id: custom-issue-workflow
            stages:
              - stage: plan
                tasks:
                  - id: custom-task
                    title: Custom Task
                checks: []
            """;
        var saveResponse = await _client.PutAsJsonAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/template", new { yaml = customYaml });

        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);
        var saved = await saveResponse.Content.ReadFromJsonAsync<JsonElement>();
        var savedData = saved.GetProperty("data");
        var savedYaml = savedData.GetProperty("yaml").GetString();
        Assert.NotNull(savedYaml);
        Assert.Contains("custom-issue-workflow", savedYaml);
        Assert.Contains("custom-task", savedYaml);
        Assert.Equal("custom", savedData.GetProperty("updateMode").GetString());
        // The PUT /workflow-profile/template path is an advanced override and
        // does NOT rewrite the issue-level selection. The unified profileId
        // therefore stays at the inherited default for an issue with no
        // selection; the override is surfaced via updateMode/hasCustomTemplate
        // and (after issue-workflow-profile consistency) the same profileId
        // every other read surface reports.
        Assert.Equal("mohist/local", savedData.GetProperty("profileId").GetString());

        var projectProfilesResponse = await _client.GetAsync("/api/workflow-templates/system");
        var projectProfilesJson = await projectProfilesResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectProfilesData = projectProfilesJson.GetProperty("data");
        Assert.Contains(projectProfilesData.EnumerateArray(), p => p.GetProperty("id").GetString() == "mohist/local");

        var getResponse = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile");
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
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"profile-yaml-err-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Backlog issue for yaml error", projectId = project.Id });

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
        var response = await _client.PutAsJsonAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/template", new { yaml = invalidYaml });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("yaml_syntax", payload.GetProperty("code").GetString());
        Assert.Contains("YAML", payload.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);

        var getResponse = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile");
        var state = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        var stateData = state.GetProperty("data");
        var yaml = stateData.TryGetProperty("yaml", out var yamlElement) ? yamlElement.GetString() : null;
        Assert.DoesNotContain("broken", yaml ?? "");
    }

    [Fact]
    public async Task SaveWorkflowProfileYaml_RejectsInvalidWorkflowShape()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"profile-shape-err-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Backlog issue for shape error", projectId = project.Id });

        var invalidShapeYaml = """
            id: no-stages-workflow
            stages: []
            """;
        var response = await _client.PutAsJsonAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/template", new { yaml = invalidShapeYaml });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("workflow_shape", payload.GetProperty("code").GetString());
        Assert.Contains("stage", payload.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);

        var getResponse = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile");
        var state = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        var stateData = state.GetProperty("data");
        Assert.Equal("reference", stateData.GetProperty("updateMode").GetString());
    }

    [Fact]
    public async Task SaveWorkflowProfileYaml_SynchronizesActiveRunProfile_AndPreservesInitializedStageWork()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"profile-sync-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        await UseNoArtifactTemplateAsync(project.Id);
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Active run profile sync issue", projectId = project.Id, isDraft = false });
        await StartWorkflowWithRunnerAsync(project.Id, issue.Number, $"profile-sync-runner-{Guid.NewGuid():N}");

        await DrainUntilApprovalAsync(project.Id, issue.Number, "plan");
        var statusBeforeSave = await _client.GetDataAsync<IssueWorkflowEnvelopeDto>($"/api/projects/{project.Id}/issues/{issue.Number}/workflow/status");
        var planStageBeforeSave = Assert.Single(statusBeforeSave.Workflow!.Stages, s => s.Stage == "plan");
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
        var saveResponse = await _client.PutAsJsonAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/template", new { yaml = customYaml });
        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);

        var saved = await saveResponse.Content.ReadFromJsonAsync<JsonElement>();
        var savedData = saved.GetProperty("data");
        var savedYaml = savedData.GetProperty("yaml").GetString();
        Assert.Contains("synced-workflow", savedYaml!);

        var statusAfterSave = await _client.GetDataAsync<IssueWorkflowEnvelopeDto>($"/api/projects/{project.Id}/issues/{issue.Number}/workflow/status");
        var planStageAfterSave = Assert.Single(statusAfterSave.Workflow!.Stages, s => s.Stage == "plan");
        Assert.Equal(planTaskIdsBeforeSave, planStageAfterSave.Tasks.Select(t => t.Id).ToArray());
        Assert.Equal(planCheckNamesBeforeSave, planStageAfterSave.Checks.Select(c => c.Name).ToArray());
        Assert.DoesNotContain(planStageAfterSave.Tasks, t => t.Id == "replacement-plan-task");
        Assert.DoesNotContain(planStageAfterSave.Checks, c => c.Name == "replacement-plan-check");

        var activeIssue = await _client.GetDataAsync<IssueWithWorkflowDto>($"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.False(string.IsNullOrWhiteSpace(activeIssue.WorkflowRunId));
        var updatedRunYamlResponse = await _client.GetAsync($"/api/workflow-runs/{activeIssue.WorkflowRunId}/yaml");
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

    private const string NoArtifactTemplateYaml = """
        id: mohist-test-noartifacts-profile
        variables:
          agent:
            type: opencode
        stages:
          - stage: plan
            requiresApproval: true
            tasks:
              - id: proposal
                title: Generate proposal
                uses: mohist/acp-agent
                with:
                  session: plan
                  prompt: ${{ prompts.proposal }}
                  agent: ${{ vars.agent }}
              - id: specs
                title: Write specs
                uses: mohist/acp-agent
                with:
                  session: plan
                  prompt: ${{ prompts.specs }}
                  agent: ${{ vars.agent }}
              - id: design
                title: Create design
                uses: mohist/acp-agent
                with:
                  session: plan
                  prompt: ${{ prompts.design }}
                  agent: ${{ vars.agent }}
              - id: tasks
                title: Generate tasks
                uses: mohist/acp-agent
                with:
                  session: plan
                  prompt: ${{ prompts.tasks }}
                  agent: ${{ vars.agent }}
              - id: self-review
                title: Self review
                uses: mohist/acp-agent
                with:
                  session: plan
                  prompt: ${{ prompts.self-review }}
                  agent: ${{ vars.agent }}
            checks:
              - name: health
                title: Health
                uses: core/script
                with:
                  run: git diff --check
          - stage: build
            tasks:
              - id: load-tasks
                title: Load tasks from plan
                uses: mohist/openspec-tasks
                with:
                  path: ${{ openspecChangeDir }}/tasks.json
                  task:
                    uses: mohist/acp-agent
                    with:
                      agent: ${{ vars.agent }}
                      prompt:
                        uses: mohist/openspec-task-prompt
                        with:
                          file: ${{ openspecChangeDir }}/tasks.json
                          items: tasks
                          base: ${{ prompts.build }}
            checks:
              - name: health
                title: Health
                uses: core/script
                with:
                  run: git diff --check
        """;

    private async Task UseNoArtifactTemplateAsync(string projectId)
    {
        await _client.PostOkAsync(
            $"/api/projects/{projectId}/workflow-templates",
            new { yaml = NoArtifactTemplateYaml });
        await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/workflow-profile/default-template",
            new { templateId = "mohist-test-noartifacts-profile" });
    }

    private async Task StartWorkflowWithRunnerAsync(string projectId, int issueNumber, string runnerId)
    {
        _projectId = projectId;
        _issueNumber = issueNumber;
        _runnerId = runnerId;

        await _client.PostOkAsync($"/api/projects/{projectId}/issues/{issueNumber}/start", null);
        await _client.PostOkAsync($"/api/runner/{runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host", projectId });
        var startedIssue = await _client.GetDataAsync<IssueWithWorkflowDto>($"/api/projects/{projectId}/issues/{issueNumber}");
        Assert.False(string.IsNullOrWhiteSpace(startedIssue.WorkflowRunId));
    }

    private async Task DrainUntilApprovalAsync(string projectId, int issueNumber, string stage)
    {
        for (var i = 0; i < 100; i++)
        {
            var status = await _client.GetDataAsync<IssueWorkflowEnvelopeDto>($"/api/projects/{projectId}/issues/{issueNumber}/workflow/status");
            if (status.Workflow?.Status == "awaiting-approval" && status.Workflow.CurrentStage == stage)
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
                await ReportAsync(work.WorkflowRunId, work.WorkId, "completed");
                break;
            case "checks":
                var checkNames = ParseCheckNames(work.With);
                await ReportAsync(work.WorkflowRunId, work.WorkId, "pass", output: JsonSerializer.Serialize(checkNames.Select(name => new { name, status = "pass" })));
                break;
            default:
                await ReportAsync(work.WorkflowRunId, work.WorkId, "completed");
                break;
        }
    }

    private async Task<WorkDispatchDto> PollWorkAnyAsync()
    {
        var work = await TestWait.ForAsync(
            async () => await PollMatchingWorkAsync(IsCurrentIssueWork),
            value => value is not null,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(20),
            $"Runner '{_runnerId}' to receive work");
        return work!;
    }

    private async Task<WorkDispatchDto?> PollMatchingWorkAsync(Func<WorkDispatchDto, bool> matches)
    {
        using var response = await _client.PostAsync($"/api/runner/{_runnerId}/poll", null);
        var work = await response.ReadFirstDispatchAsync<WorkDispatchDto>();
        if (work is null)
            return null;

        if (matches(work))
            return work;

        await ReportAsync(work.WorkflowRunId, work.WorkId, work.WorkType == "checks" ? "pass" : "completed");
        return null;
    }

    private bool IsCurrentIssueWork(WorkDispatchDto work)
    {
        return work.ProjectId == _projectId && work.IssueNumber == _issueNumber;
    }

    private Task ReportAsync(string workflowRunId, string workId, string status, string? message = null, string? output = null, int? exitCode = null) =>
        _client.PostOkAsync($"/api/runner/{_runnerId}/report", new { workflowRunId, workId, status, message, output, exitCode });

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
    private sealed record IssueWorkflowEnvelopeDto(WorkflowStatusDto? Workflow);
    private sealed record WorkflowStatusDto(string Status, string? CurrentStage, WorkflowStageDto[] Stages);
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
