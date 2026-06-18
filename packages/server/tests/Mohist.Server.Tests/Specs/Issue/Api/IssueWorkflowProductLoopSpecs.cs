using System.Text.Json;
using System.Net.Http.Json;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs.Issue.Api;

[Collection("MohistIntegration")]
public class IssueWorkflowProductLoopSpecs : IAsyncLifetime
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private string? _runnerId;
    private string? _projectId;
    private int _issueNumber;

    public IssueWorkflowProductLoopSpecs(MohistIntegrationFixture fixture)
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

        if (string.IsNullOrWhiteSpace(_projectId) || _issueNumber <= 0)
            return;

        using var _ = await _client.PostAsync($"/api/projects/{_projectId}/issues/{_issueNumber}/stop", null);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task IssueStart_RunnerCompletesWorkflow_IssueBecomesDone()
    {
        var projectName = $"project-{Guid.NewGuid():N}";
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName });
        await _client.PostOkAsync($"/api/projects/{ project.Id }/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        await UseNoArtifactTemplateAsync(project.Id);
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Ship product loop", body = "body", labels = Array.Empty<string>(), priority = "p1", model = "openai/gpt-4o", projectId = project.Id, isDraft = false });
        _projectId = project.Id;
        _issueNumber = issue.Number;

        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start");
        _runnerId = $"product-loop-runner-{Guid.NewGuid():N}";
        await _client.PostOkAsync($"/api/runner/{_runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host", projectId = project.Id });
        var startedIssue = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.False(string.IsNullOrWhiteSpace(startedIssue.WorkflowRunId));

        var startEvents = await _client.GetDataAsync<EventDto[]>($"/api/projects/{project.Id}/issues/{issue.Number}/events");
        Assert.Contains(startEvents, e => e.Type == "com.mohist.workflow.run.started");

        var initialStatus = await _client.GetDataAsync<IssueWorkflowStatusDto>($"/api/projects/{project.Id}/issues/{issue.Number}/workflow/status");
        Assert.Contains(initialStatus.Workflow!.Stages, s => s.Stage == "plan" && s.Tasks.Any(t => t.Id == "proposal"));

        await DrainUntilApprovalAsync(project.Id, issue.Number, "plan");

        var planStatus = await _client.GetDataAsync<IssueWorkflowStatusDto>($"/api/projects/{project.Id}/issues/{issue.Number}/workflow/status");
        var planStage = Assert.Single(planStatus.Workflow!.Stages, s => s.Stage == "plan");
        Assert.Contains(planStage.Tasks, t => t.Id.StartsWith("proposal", StringComparison.Ordinal) && t.Status == "completed");
        Assert.Null(planStage.ApprovalStatus?.Result);

        var planEvents = await _client.GetDataAsync<EventDto[]>($"/api/projects/{project.Id}/issues/{issue.Number}/events");
        Assert.Contains(planEvents, e => e.Type == "com.mohist.workflow.task.completed");
        Assert.Contains(planEvents, e => e.Type == "com.mohist.workflow.check.passed");

        var listedAtApproval = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.Equal("in_progress", listedAtApproval.Status);
        Assert.Equal("plan", listedAtApproval.WorkflowStage);
        Assert.Equal("awaiting-approval", listedAtApproval.WorkflowStatus);
        Assert.Equal("attention", listedAtApproval.Health);
        Assert.Equal("review_required", listedAtApproval.Attention?.Reason);
        Assert.Equal("awaiting", listedAtApproval.ApprovalState?.Status);

        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/approve");

        await DrainUntilApprovalAsync(project.Id, issue.Number, "check");
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/approve");

        await DrainUntilDoneAsync(project.Id, issue.Number);

        var completed = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.Equal("done", completed.Status);
        Assert.Equal("done", completed.Health);

        var events = await _client.GetDataAsync<EventDto[]>($"/api/projects/{project.Id}/issues/{issue.Number}/events");
        Assert.Contains(events, e => e.Type == "com.mohist.workflow.run.completed");

        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/archive");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task IssueWorkflowVariablesPatch_AppliesToFutureDispatches()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"variables-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{ project.Id }/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Patch workflow variables", body = "body", labels = Array.Empty<string>(), priority = "p1", model = "openai/gpt-4o", projectId = project.Id, isDraft = false });
        _projectId = project.Id;
        _issueNumber = issue.Number;

        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start");
        _runnerId = $"variable-patch-runner-{Guid.NewGuid():N}";
        await _client.PostOkAsync($"/api/runner/{_runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host", projectId = project.Id });
        var startedIssue = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");

        var patched = await _client.PatchDataAsync<ProjectVariablesDto>(
            $"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/variables",
            new { vars = new { agent = new { type = "opencode", model = "kimi/k2", timeout = 1200 } } });
        Assert.NotNull(patched.Vars);

        var firstWork = await PollWorkAnyAsync();

        Assert.NotNull(firstWork.Variables);
        using var doc = JsonDocument.Parse(firstWork.Variables!);
        var agent = doc.RootElement.GetProperty("vars").GetProperty("agent");
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("kimi/k2", agent.GetProperty("model").GetString());
        Assert.Equal(1200, agent.GetProperty("timeout").GetInt32());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task IssueWorkflowVariablesPatch_ProjectsModelSettingsOnIssueDetail()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"issue-model-profile-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{ project.Id }/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Configure issue model profile", body = "body", labels = Array.Empty<string>(), priority = "p1", projectId = project.Id, isDraft = false });
        _projectId = project.Id;
        _issueNumber = issue.Number;

        await _client.PatchDataAsync<ProjectVariablesDto>(
            $"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/variables",
            new
            {
                vars = new { agent = new { type = "opencode", model = "issue/default-model", timeout = 1200 } },
                stages = new { plan = new { vars = new { agent = new { type = "opencode", model = "issue/plan-model" } } } }
            });

        var detail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");

        Assert.Equal("issue/default-model", detail.Model);
        Assert.NotNull(detail.AgentConfig);
        Assert.Equal("issue/default-model", detail.AgentConfig["model"].GetString());
        Assert.NotNull(detail.StageModels);
        Assert.Equal("issue/plan-model", detail.StageModels["plan"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ProjectVariablesPatch_AppliesToNextTaskDispatch()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"project-variables-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{ project.Id }/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        await UseNoArtifactTemplateAsync(project.Id);
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Patch project variables", body = "body", labels = Array.Empty<string>(), priority = "p1", projectId = project.Id, isDraft = false });
        _projectId = project.Id;
        _issueNumber = issue.Number;

        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start");
        _runnerId = $"project-variable-patch-runner-{Guid.NewGuid():N}";
        await _client.PostOkAsync($"/api/runner/{_runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host", projectId = project.Id });

        var proposal = await PollWorkAnyAsync();
        Assert.StartsWith("proposal", proposal.WorkId);
        await ReportAsync(proposal.WorkflowRunId, proposal.WorkId, "completed");

        await _client.PatchDataAsync<ProjectVariablesDto>(
            $"/api/projects/{project.Id}/workflow-profile/variables",
            new { vars = new { agent = new { type = "opencode", model = "project/model-new", timeout = 1500 } } });
        await _client.PatchDataAsync<ProjectVariablesDto>(
            $"/api/projects/{project.Id}/workflow-profile/variables",
            new { stages = new { build = new { vars = new { agent = new { model = "project/build-model" } } } } });

        await DrainUntilApprovalAsync(project.Id, issue.Number, "plan");
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/approve");
        var tasks = await PollWorkAnyAsync();
        Assert.Equal("mohist/openspec-tasks", tasks.Uses);
        var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(tasks.WorkflowRunId);
        await workflow.AddTasksAsync(new AddTasksBatchRequest([
            new AddTasksBatchItem("build-1", "Build task", "mohist/acp-agent")
        ]));
        await ReportAsync(tasks.WorkflowRunId, tasks.WorkId, "completed");

        var build = await PollWorkAnyAsync();
        Assert.Equal("build", build.Stage);
        Assert.StartsWith("build-1", build.WorkId);
        Assert.NotNull(build.Variables);

        using var doc = JsonDocument.Parse(build.Variables!);
        var agent = doc.RootElement.GetProperty("vars").GetProperty("agent");
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("project/build-model", agent.GetProperty("model").GetString());
        Assert.Equal(1500, agent.GetProperty("timeout").GetInt32());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ProjectStageVariablesPatch_OverridesPersistedWorkflowStageAgent()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"project-stage-variables-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{ project.Id }/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        await UseNoArtifactTemplateAsync(project.Id);
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Patch project stage variables", body = "body", labels = Array.Empty<string>(), priority = "p1", projectId = project.Id, isDraft = false });
        _projectId = project.Id;
        _issueNumber = issue.Number;

        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start");
        _runnerId = $"project-stage-variable-patch-runner-{Guid.NewGuid():N}";
        await _client.PostOkAsync($"/api/runner/{_runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host", projectId = project.Id });

        var proposal = await PollWorkAnyAsync();
        Assert.StartsWith("proposal", proposal.WorkId);

        await ReportAsync(proposal.WorkflowRunId, proposal.WorkId, "completed");
        await _client.PatchDataAsync<ProjectVariablesDto>(
            $"/api/projects/{project.Id}/workflow-profile/variables",
            new { vars = new { agent = new { type = "opencode", model = "project/default-model", timeout = 1500 } } });
        await _client.PatchDataAsync<ProjectVariablesDto>(
            $"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/variables",
            new { stages = new { build = new { vars = new { agent = new { model = "minimax-coding-plan/MiniMax-M3" } } } } });

        await DrainUntilApprovalAsync(project.Id, issue.Number, "plan");
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/approve");
        var tasks = await PollWorkAnyAsync();
        var tasksWorkflow = _fixture.Grains.GetGrain<IWorkflowGrain>(tasks.WorkflowRunId);
        await tasksWorkflow.AddTasksAsync(new AddTasksBatchRequest([
            new AddTasksBatchItem("build-1", "Build task", "mohist/acp-agent")
        ]));
        await ReportAsync(tasks.WorkflowRunId, tasks.WorkId, "completed");

        var build = await PollWorkAnyAsync();
        Assert.Equal("build", build.Stage);
        Assert.NotNull(build.Variables);
        Assert.NotNull(build.With);

        using var doc = JsonDocument.Parse(build.Variables!);
        var agent = doc.RootElement.GetProperty("vars").GetProperty("agent");
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("minimax-coding-plan/MiniMax-M3", agent.GetProperty("model").GetString());
        Assert.Equal(1500, agent.GetProperty("timeout").GetInt32());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task IssueStart_GlobalRunnerClaimsProjectBacklogWork()
    {
        var projectName = $"global-runner-{Guid.NewGuid():N}";
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName });
        await _client.PostOkAsync($"/api/projects/{ project.Id }/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Dispatch to global runner", body = "body", labels = Array.Empty<string>(), priority = "p1", projectId = project.Id, isDraft = false });
        _projectId = project.Id;
        _issueNumber = issue.Number;

        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start");
        _runnerId = $"global-runner-{Guid.NewGuid():N}";
        await _client.PostOkAsync($"/api/runner/{_runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host", projectId = project.Id, maxWorkflowSlots = 16 });

        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var response = await _client.PostAsync($"/api/runner/{_runnerId}/poll", null);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                await Task.Delay(20);
                continue;
            }

            response.EnsureSuccessStatusCode();
            var work = await response.Content.ReadFromJsonAsync<WorkDispatchDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException("Empty work dispatch");
            if (work.ProjectId != project.Id || work.IssueNumber != issue.Number)
            {
                await _client.PostOkAsync(
                    $"/api/runner/{_runnerId}/report",
                    new { workId = work.WorkId, workflowRunId = work.WorkflowRunId, status = work.WorkType == "checks" ? "pass" : "completed", projectId = work.ProjectId });
                continue;
            }

            Assert.Equal(project.Id, work.ProjectId);
            Assert.Equal(issue.Number, work.IssueNumber);
            Assert.Equal("plan", work.Stage);
            return;
        }

        Assert.Fail("Global runner did not claim project backlog work");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task IssueWorkflowYaml_ReturnsActiveWorkflowDefinition()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"yaml-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{ project.Id }/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Show workflow yaml", body = "body", labels = Array.Empty<string>(), priority = "p1", projectId = project.Id, isDraft = false });
        _projectId = project.Id;
        _issueNumber = issue.Number;

        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start");

        var current = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.False(string.IsNullOrWhiteSpace(current.WorkflowRunId));
        var response = await _client.GetDataAsync<WorkflowYamlDto>($"/api/workflow-runs/{current.WorkflowRunId}/yaml");

        Assert.False(string.IsNullOrWhiteSpace(response.WorkflowRunId));
        Assert.Contains("stages:", response.Yaml);
        Assert.Contains("agent: ${{ vars.agent }}", response.Yaml);
        Assert.Contains("prompt: ${{ prompts.proposal }}", response.Yaml);
    }

    private const string NoArtifactTemplateYaml = """
        id: mohist-test-noartifacts
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
          - stage: check
            requiresApproval: true
            tasks:
              - id: ai-review
                title: AI review
                uses: mohist/acp-agent
                with:
                  session: check
                  prompt: ${{ prompts.review }}
                  agent: ${{ vars.agent }}
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
            new { templateId = "mohist-test-noartifacts" });
    }

    private async Task DrainUntilApprovalAsync(string projectId, int issueNumber, string stage)
    {
        for (var i = 0; i < 100; i++)
        {
            var status = await _client.GetDataAsync<IssueWorkflowStatusDto>($"/api/projects/{projectId}/issues/{issueNumber}/workflow/status");
            if (status.Workflow?.Status == "awaiting-approval" && status.Workflow.CurrentStage == stage)
                return;
            await CompleteNextWorkAsync();
        }

        Assert.Fail($"Workflow did not reach approval at stage {stage}");
    }

    private async Task DrainUntilDoneAsync(string projectId, int issueNumber)
    {
        for (var i = 0; i < 100; i++)
        {
            var status = await _client.GetDataAsync<IssueWorkflowStatusDto>($"/api/projects/{projectId}/issues/{issueNumber}/workflow/status");
            if (status.Workflow?.Status == "completed")
                return;
            await CompleteNextWorkAsync();
        }

        Assert.Fail("Workflow did not complete");
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
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var response = await _client.PostAsync($"/api/runner/{_runnerId}/poll", null);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
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
                    await ReportAsync(work.WorkflowRunId, work.WorkId, "completed");
                    continue;
                }
            }
            else if (!IsCurrentIssueWork(work))
            {
                await ReportAsync(work.WorkflowRunId, work.WorkId, work.WorkType == "checks" ? "pass" : "completed");
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

    private sealed record ProjectDto(string Id, string Name, string Path, string BaseBranch);
    private sealed record ProjectVariablesDto(JsonElement? Vars, Dictionary<string, ProjectStageVariablesDto?>? Stages);
    private sealed record ProjectStageVariablesDto(JsonElement? Vars);
    private sealed record IssueDto(
        int Number,
        string Title,
        string Status,
        string Health,
        ApprovalStateDto? ApprovalState,
        AttentionDto? Attention,
        string? WorkflowRunId,
        string? WorkflowStage,
        string? WorkflowStatus,
        string? Model,
        Dictionary<string, JsonElement>? AgentConfig,
        Dictionary<string, string>? StageModels);
    private sealed record ApprovalStateDto(string Stage, string Status);
    private sealed record AttentionDto(string Reason);
    private sealed record IssueWorkflowStatusDto(WorkflowStatusDto? Workflow);
    private sealed record WorkflowStatusDto(string Status, string? CurrentStage, WorkflowStageDto[] Stages);
    private sealed record EventDto(long Id, string Type, string Time);
    private sealed record WorkflowYamlDto(string WorkflowRunId, string Yaml);
    private sealed record WorkflowStageDto(string Stage, string Status, WorkflowTaskDto[] Tasks, ApprovalDto? ApprovalStatus);
    private sealed record WorkflowTaskDto(string Id, string Title, string? Uses, string Status);
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
