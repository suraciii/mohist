using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events.Subscriptions;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

[Collection("IssueLifecycle")]
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

    private async Task StartIssueAsync(string projectId, int issueNumber)
    {
        await _client.PostOkAsync($"/api/projects/{projectId}/issues/{issueNumber}/start");
        await _fixture.Grains.GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global).DispatchNowAsync();
    }

    [Fact]
    public async Task IssueStart_RunnerCompletesWorkflow_IssueBecomesDone()
    {
        var projectName = $"project-{Guid.NewGuid():N}";
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProductLoopProjectDto>("/api/projects", projectName);
        await _client.PostOkAsync($"/api/projects/{ project.Id }/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        await UseNoArtifactTemplateAsync(project.Id);
var issue = await _client.PostDataAsync<ProductLoopIssueDto>($"/api/projects/{project.Id}/issues", new { title = "Ship product loop", body = "body", labels = new Dictionary<string, string>(StringComparer.Ordinal), priority = "p1", model = "openai/gpt-4o", projectId = project.Id, isDraft = false });
        _projectId = project.Id;
        _issueNumber = issue.Number;

        await StartIssueAsync(project.Id, issue.Number);
        _runnerId = $"product-loop-runner-{Guid.NewGuid():N}";
        await _client.PostOkAsync($"/api/runner/{_runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host", projectId = project.Id });
        var startedIssue = await _client.GetDataAsync<ProductLoopIssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.False(string.IsNullOrWhiteSpace(startedIssue.WorkflowRunId));

        var startEvents = await _client.GetDataAsync<ProductLoopEventDto[]>($"/api/projects/{project.Id}/issues/{issue.Number}/events");
        Assert.Contains(startEvents, e => e.Type == "com.mohist.workflow.run.started");

        var initialStatus = await _client.GetDataAsync<ProductLoopIssueWorkflowStatusDto>($"/api/projects/{project.Id}/issues/{issue.Number}/workflow/status");
        Assert.Contains(initialStatus.Workflow!.Stages, s => s.Stage == "plan" && s.Tasks.Any(t => t.Id.StartsWith("proposal")));

        await DrainUntilApprovalAsync(project.Id, issue.Number, "plan");

        var planStatus = await _client.GetDataAsync<ProductLoopIssueWorkflowStatusDto>($"/api/projects/{project.Id}/issues/{issue.Number}/workflow/status");
        var planStage = Assert.Single(planStatus.Workflow!.Stages, s => s.Stage == "plan");
        Assert.Contains(planStage.Tasks, t => t.Id.StartsWith("proposal", StringComparison.Ordinal) && t.Status == "completed");
        Assert.Null(planStage.ApprovalStatus?.Result);

        var planEvents = await _client.GetDataAsync<ProductLoopEventDto[]>($"/api/projects/{project.Id}/issues/{issue.Number}/events");
        Assert.Contains(planEvents, e => e.Type == "com.mohist.workflow.task.completed");
        Assert.Contains(planEvents, e => e.Type == "com.mohist.workflow.check.passed");

        var listedAtApproval = await _client.GetDataAsync<ProductLoopIssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");
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

        // issue-361 T-002: the bus is write-only, so the
        // IssueWorkflowCompletionHandler is no longer invoked by the
        // workflow-run.completed publish. Replay the persisted row
        // through the handler — that is the future dispatcher's job
        // and the only path that completes the issue end-to-end today.
        await DispatchWorkflowRunCompletedAsync(startedIssue.WorkflowRunId!);

        var completed = await _client.GetDataAsync<ProductLoopIssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.Equal("done", completed.Status);
        Assert.Equal("done", completed.Health);

        var events = await _client.GetDataAsync<ProductLoopEventDto[]>($"/api/projects/{project.Id}/issues/{issue.Number}/events");
        Assert.Contains(events, e => e.Type == "com.mohist.workflow.run.completed");

        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/archive");
    }

    [Fact]
    public async Task IssueWorkflowVariablesPatch_AppliesToFutureDispatches()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProductLoopProjectDto>("/api/projects", $"variables-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{ project.Id }/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
var issue = await _client.PostDataAsync<ProductLoopIssueDto>($"/api/projects/{project.Id}/issues", new { title = "Patch workflow variables", body = "body", labels = new Dictionary<string, string>(StringComparer.Ordinal), priority = "p1", model = "openai/gpt-4o", projectId = project.Id, isDraft = false });
        _projectId = project.Id;
        _issueNumber = issue.Number;

        await StartIssueAsync(project.Id, issue.Number);
        _runnerId = $"variable-patch-runner-{Guid.NewGuid():N}";
        await _client.PostOkAsync($"/api/runner/{_runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host", projectId = project.Id });
        var startedIssue = await _client.GetDataAsync<ProductLoopIssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");

        var patched = await _client.PatchDataAsync<ProductLoopProjectVariablesDto>(
            $"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/variables",
            new { vars = new { agent = new { model = "kimi/k2" } } });
        Assert.NotNull(patched.Vars);

        var firstWork = await PollWorkAnyAsync();

        Assert.NotNull(firstWork.Variables);
        using var doc = JsonDocument.Parse(firstWork.Variables!);
        var agent = doc.RootElement.GetProperty("vars").GetProperty("agent");
        Assert.False(agent.TryGetProperty("type", out _));
        Assert.Equal("kimi/k2", agent.GetProperty("model").GetString());
    }

    [Fact]
    public async Task IssueWorkflowVariablesPatch_ProjectsModelSettingsOnIssueDetail()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProductLoopProjectDto>("/api/projects", $"issue-model-profile-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{ project.Id }/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
var issue = await _client.PostDataAsync<ProductLoopIssueDto>($"/api/projects/{project.Id}/issues", new { title = "Configure issue model profile", body = "body", labels = new Dictionary<string, string>(StringComparer.Ordinal), priority = "p1", projectId = project.Id, isDraft = false });
        _projectId = project.Id;
        _issueNumber = issue.Number;

        await _client.PatchDataAsync<ProductLoopProjectVariablesDto>(
            $"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/variables",
            new
            {
                vars = new { agent = new { model = "issue/default-model" } },
                stages = new { plan = new { vars = new { agent = new { model = "issue/plan-model" } } } }
            });

        var detail = await _client.GetDataAsync<ProductLoopIssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");

        Assert.Equal("issue/default-model", detail.Model);
        Assert.NotNull(detail.AgentConfig);
        Assert.Equal("issue/default-model", detail.AgentConfig["model"].GetString());
        Assert.NotNull(detail.StageModels);
        Assert.Equal("issue/plan-model", detail.StageModels["plan"]);
    }

    [Fact]
    public async Task ProjectVariablesPatch_AppliesToNextTaskDispatch()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProductLoopProjectDto>("/api/projects", $"project-variables-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{ project.Id }/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        await UseNoArtifactTemplateAsync(project.Id);
var issue = await _client.PostDataAsync<ProductLoopIssueDto>($"/api/projects/{project.Id}/issues", new { title = "Patch project variables", body = "body", labels = new Dictionary<string, string>(StringComparer.Ordinal), priority = "p1", projectId = project.Id, isDraft = false });
        _projectId = project.Id;
        _issueNumber = issue.Number;

        await StartIssueAsync(project.Id, issue.Number);
        _runnerId = $"project-variable-patch-runner-{Guid.NewGuid():N}";
        await _client.PostOkAsync($"/api/runner/{_runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host", projectId = project.Id });

        var proposal = await PollWorkAnyAsync();
        Assert.StartsWith("proposal", proposal.WorkId);
        await ReportAsync(proposal.WorkflowRunId, proposal.WorkId, "completed");

        await _client.PatchDataAsync<ProductLoopProjectVariablesDto>(
            $"/api/projects/{project.Id}/workflow-profile/variables",
            new { vars = new { agent = new { model = "project/model-new" } } });
        await _client.PatchDataAsync<ProductLoopProjectVariablesDto>(
            $"/api/projects/{project.Id}/workflow-profile/variables",
            new { stages = new { build = new { vars = new { agent = new { model = "project/build-model" } } } } });

        await DrainUntilApprovalAsync(project.Id, issue.Number, "plan");
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/approve");
        var tasks = await PollWorkAnyAsync();
        Assert.Equal("mohist/openspec-tasks", tasks.Uses);
        var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(tasks.WorkflowRunId);
        await workflow.AddTasksAsync(new AddTasksBatchRequest([
            new AddTasksBatchItem(
                "build-1",
                "Build task",
                "mohist/opencode",
                JsonDocument.Parse("""{"options":"${{ vars.agent }}"}""").RootElement)
        ]));
        await ReportAsync(tasks.WorkflowRunId, tasks.WorkId, "completed");

        var build = await PollWorkAnyAsync();
        Assert.Equal("build", build.Stage);
        Assert.StartsWith("build-1", build.WorkId);
        Assert.NotNull(build.Variables);
        Assert.NotNull(build.With);

        using var doc = JsonDocument.Parse(build.Variables!);
        var agent = doc.RootElement.GetProperty("vars").GetProperty("agent");
        Assert.False(agent.TryGetProperty("type", out _));
        Assert.Equal("project/build-model", agent.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ProjectVariablesEdit_PropagatesToIssueCreatedWithPriorProjectConfig()
    {
        // Regression: the project had an agent model configured BEFORE the
        // issue was started (so the T1 snapshot would have baked it in under
        // the old design). After the issue is running, the project model is
        // changed. The next stage dispatch must use the NEW project model,
        // not the value that was live at issue creation.
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProductLoopProjectDto>("/api/projects", $"project-live-propagate-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{ project.Id }/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        await UseNoArtifactTemplateAsync(project.Id);

        // Project is configured with model A BEFORE the issue is started.
        await _client.PatchDataAsync<ProductLoopProjectVariablesDto>(
            $"/api/projects/{project.Id}/workflow-profile/variables",
            new { vars = new { agent = new { model = "old-coding/legacy" } } });

        var issue = await _client.PostDataAsync<ProductLoopIssueDto>($"/api/projects/{project.Id}/issues", new { title = "Live project config propagation", body = "body", labels = new Dictionary<string, string>(StringComparer.Ordinal), priority = "p1", projectId = project.Id, isDraft = false });
        _projectId = project.Id;
        _issueNumber = issue.Number;

        await StartIssueAsync(project.Id, issue.Number);
        _runnerId = $"live-propagate-runner-{Guid.NewGuid():N}";
        await _client.PostOkAsync($"/api/runner/{_runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host", projectId = project.Id });

        var proposal = await PollWorkAnyAsync();
        Assert.StartsWith("proposal", proposal.WorkId);
        await ReportAsync(proposal.WorkflowRunId, proposal.WorkId, "completed");

        // Project model changed to B AFTER the issue is already running.
        await _client.PatchDataAsync<ProductLoopProjectVariablesDto>(
            $"/api/projects/{project.Id}/workflow-profile/variables",
            new { vars = new { agent = new { model = "deepseek/deepseek-v4-pro" } } });

        await DrainUntilApprovalAsync(project.Id, issue.Number, "plan");
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/approve");
        var tasks = await PollWorkAnyAsync();
        Assert.Equal("mohist/openspec-tasks", tasks.Uses);
        var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(tasks.WorkflowRunId);
        await workflow.AddTasksAsync(new AddTasksBatchRequest([
            new AddTasksBatchItem(
                "build-1",
                "Build task",
                "mohist/opencode",
                JsonDocument.Parse("""{"options":"${{ vars.agent }}"}""").RootElement)
        ]));
        await ReportAsync(tasks.WorkflowRunId, tasks.WorkId, "completed");

        var build = await PollWorkAnyAsync();
        Assert.Equal("build", build.Stage);
        Assert.StartsWith("build-1", build.WorkId);
        Assert.NotNull(build.Variables);

        using var doc = JsonDocument.Parse(build.Variables!);
        var agent = doc.RootElement.GetProperty("vars").GetProperty("agent");
        Assert.Equal("deepseek/deepseek-v4-pro", agent.GetProperty("model").GetString());
        Assert.DoesNotContain("old-coding/legacy", build.Variables);
    }

    [Fact]
    public async Task ProjectStageVariablesPatch_OverridesPersistedWorkflowStageAgent()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProductLoopProjectDto>("/api/projects", $"project-stage-variables-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{ project.Id }/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        await UseNoArtifactTemplateAsync(project.Id);
var issue = await _client.PostDataAsync<ProductLoopIssueDto>($"/api/projects/{project.Id}/issues", new { title = "Patch project stage variables", body = "body", labels = new Dictionary<string, string>(StringComparer.Ordinal), priority = "p1", projectId = project.Id, isDraft = false });
        _projectId = project.Id;
        _issueNumber = issue.Number;

        await StartIssueAsync(project.Id, issue.Number);
        _runnerId = $"project-stage-variable-patch-runner-{Guid.NewGuid():N}";
        await _client.PostOkAsync($"/api/runner/{_runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host", projectId = project.Id });

        var proposal = await PollWorkAnyAsync();
        Assert.StartsWith("proposal", proposal.WorkId);

        await ReportAsync(proposal.WorkflowRunId, proposal.WorkId, "completed");
        await _client.PatchDataAsync<ProductLoopProjectVariablesDto>(
            $"/api/projects/{project.Id}/workflow-profile/variables",
            new { vars = new { agent = new { model = "project/default-model" } } });
        await _client.PatchDataAsync<ProductLoopProjectVariablesDto>(
            $"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/variables",
            new { stages = new { build = new { vars = new { agent = new { model = "minimax-coding-plan/MiniMax-M3" } } } } });

        await DrainUntilApprovalAsync(project.Id, issue.Number, "plan");
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/approve");
        var tasks = await PollWorkAnyAsync();
        var tasksWorkflow = _fixture.Grains.GetGrain<IWorkflowGrain>(tasks.WorkflowRunId);
        await tasksWorkflow.AddTasksAsync(new AddTasksBatchRequest([
            new AddTasksBatchItem(
                "build-1",
                "Build task",
                "mohist/opencode",
                JsonDocument.Parse("""{"options":"${{ vars.agent }}"}""").RootElement)
        ]));
        await ReportAsync(tasks.WorkflowRunId, tasks.WorkId, "completed");

        var build = await PollWorkAnyAsync();
        Assert.Equal("build", build.Stage);
        Assert.NotNull(build.Variables);
        Assert.NotNull(build.With);

        using var doc = JsonDocument.Parse(build.Variables!);
        var agent = doc.RootElement.GetProperty("vars").GetProperty("agent");
        Assert.False(agent.TryGetProperty("type", out _));
        Assert.Equal("minimax-coding-plan/MiniMax-M3", agent.GetProperty("model").GetString());
    }

    [Fact]
    public async Task IssueStart_GlobalRunnerAssignsProjectBacklogWork()
    {
        var projectName = $"global-runner-{Guid.NewGuid():N}";
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProductLoopProjectDto>("/api/projects", projectName);
        await _client.PostOkAsync($"/api/projects/{ project.Id }/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
var issue = await _client.PostDataAsync<ProductLoopIssueDto>($"/api/projects/{project.Id}/issues", new { title = "Dispatch to global runner", body = "body", labels = new Dictionary<string, string>(StringComparer.Ordinal), priority = "p1", projectId = project.Id, isDraft = false });
        _projectId = project.Id;
        _issueNumber = issue.Number;

        await StartIssueAsync(project.Id, issue.Number);
        _runnerId = $"global-runner-{Guid.NewGuid():N}";
        await _client.PostOkAsync($"/api/runner/{_runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host", projectId = project.Id });
        await _client.PatchOkAsync($"/api/runner/{_runnerId}", new { slots = 16 });

        var work = await TestWait.ForAsync(
            async () => await PollMatchingWorkAsync(candidate => candidate.ProjectId == project.Id && candidate.IssueNumber == issue.Number),
            value => value is not null,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(20),
            "global runner to assign project backlog work");

        Assert.Equal(project.Id, work!.ProjectId);
        Assert.Equal(issue.Number, work.IssueNumber);
        Assert.Equal("plan", work.Stage);
    }

    [Fact]
    public async Task IssueWorkflowYaml_ReturnsActiveWorkflowDefinition()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProductLoopProjectDto>("/api/projects", $"yaml-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{ project.Id }/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
var issue = await _client.PostDataAsync<ProductLoopIssueDto>($"/api/projects/{project.Id}/issues", new { title = "Show workflow yaml", body = "body", labels = new Dictionary<string, string>(StringComparer.Ordinal), priority = "p1", projectId = project.Id, isDraft = false });
        _projectId = project.Id;
        _issueNumber = issue.Number;

        await StartIssueAsync(project.Id, issue.Number);

        var current = await _client.GetDataAsync<ProductLoopIssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.False(string.IsNullOrWhiteSpace(current.WorkflowRunId));
        var response = await _client.GetDataAsync<ProductLoopWorkflowYamlDto>($"/api/workflow-runs/{current.WorkflowRunId}/yaml");

        Assert.False(string.IsNullOrWhiteSpace(response.WorkflowRunId));
        Assert.Contains("stages:", response.Yaml);
        Assert.Contains("options: ${{ vars.agent }}", response.Yaml);
        Assert.Contains("prompt: ${{ prompts.proposal }}", response.Yaml);
    }

    private const string NoArtifactTemplateYaml = """
        id: mohist-test-noartifacts
        variables:
          agent: {}
        stages:
          - stage: plan
            requiresApproval: true
            tasks:
              - id: proposal
                title: Generate proposal
                uses: mohist/opencode
                with:
                  session: plan
                  prompt: ${{ prompts.proposal }}
                  options: ${{ vars.agent }}
              - id: specs
                title: Write specs
                uses: mohist/opencode
                with:
                  session: plan
                  prompt: ${{ prompts.specs }}
                  options: ${{ vars.agent }}
              - id: design
                title: Create design
                uses: mohist/opencode
                with:
                  session: plan
                  prompt: ${{ prompts.design }}
                  options: ${{ vars.agent }}
              - id: tasks
                title: Generate tasks
                uses: mohist/opencode
                with:
                  session: plan
                  prompt: ${{ prompts.tasks }}
                  options: ${{ vars.agent }}
              - id: self-review
                title: Self review
                uses: mohist/opencode
                with:
                  session: plan
                  prompt: ${{ prompts.self-review }}
                  options: ${{ vars.agent }}
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
                    uses: mohist/opencode
                    with:
                      options: ${{ vars.agent }}
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
                uses: mohist/opencode
                with:
                  session: check
                  prompt: ${{ prompts.review }}
                  options: ${{ vars.agent }}
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
            var status = await _client.GetDataAsync<ProductLoopIssueWorkflowStatusDto>($"/api/projects/{projectId}/issues/{issueNumber}/workflow/status");
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
            var status = await _client.GetDataAsync<ProductLoopIssueWorkflowStatusDto>($"/api/projects/{projectId}/issues/{issueNumber}/workflow/status");
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
                        new AddTasksBatchItem("build-1", "Build task", "mohist/opencode")
                    ]));
                }
                await ReportAsync(work.WorkflowRunId, work.WorkId, "completed");
                break;
            case "checks":
                var checkNames = ParseCheckNames(work.With);
                await ReportAsync(work.WorkflowRunId, work.WorkId, "pass", output: checkNames.Select(name => new { name, status = "pass" }).ToArray());
                break;
            default:
                await ReportAsync(work.WorkflowRunId, work.WorkId, "completed");
                break;
        }
    }

    private async Task<ProductLoopWorkDispatchDto> PollWorkAnyAsync()
    {
        var work = await TestWait.ForAsync(
            async () => await PollMatchingWorkAsync(IsCurrentIssueWork),
            value => value is not null,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(20),
            $"Runner '{_runnerId}' to receive work");
        return work!;
    }

    private async Task<ProductLoopWorkDispatchDto?> PollMatchingWorkAsync(Func<ProductLoopWorkDispatchDto, bool> matches)
    {
        using var response = await _client.PostAsync($"/api/runner/{_runnerId}/poll", null);
        var work = await response.ReadFirstDispatchAsync<ProductLoopWorkDispatchDto>();
        if (work is null)
            return null;

        if (matches(work))
            return work;

        await ReportAsync(work.WorkflowRunId, work.WorkId, work.WorkType == "checks" ? "pass" : "completed");
        return null;
    }

    private bool IsCurrentIssueWork(ProductLoopWorkDispatchDto work)
    {
        return work.ProjectId == _projectId && work.IssueNumber == _issueNumber;
    }

    private Task ReportAsync(string workflowRunId, string workId, string status, string? message = null, object? output = null, int? exitCode = null) =>
        _client.PostOkAsync($"/api/runner/{_runnerId}/report", new { workflowRunId, workId, status, message, output, exitCode });

    private async Task DispatchWorkflowRunCompletedAsync(string workflowRunId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var handler = scope.ServiceProvider.GetRequiredService<IssueWorkflowCompletionHandler>();

        var stored = await TestWait.ForAsync(
            async () => (await events.ListAsync(workflowRunId))
                .FirstOrDefault(e => e.Envelope.Type == EventCatalog.ReverseDns.WorkflowRunCompleted),
            envelope => envelope is not null,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(50),
            $"workflow.run.completed event row for {workflowRunId}");

        await handler.HandleAsync(stored!.Envelope, CancellationToken.None);
    }

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

}
