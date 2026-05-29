using System.Text.Json;
using System.Net.Http.Json;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class IssueWorkflowProductLoopSpecs
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

    [Fact]
    public async Task IssueStart_RunnerCompletesWorkflow_IssueBecomesDone()
    {
        var projectName = $"project-{Guid.NewGuid():N}";
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = "/tmp/mohist-product-loop", baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Ship product loop", body = "body", labels = Array.Empty<string>(), priority = "p1", model = "openai/gpt-4o", stageModels = new Dictionary<string, string> { ["plan"] = "anthropic/claude" }, projectId = project.Id });
        _projectId = project.Id;
        _issueNumber = issue.Number;

        await _client.PostOkAsync($"/api/issues/{issue.Number}/start?projectId={project.Id}");
        _runnerId = "product-loop-runner";
        await _client.PostOkAsync($"/api/runner/{_runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host" });
        var startedIssue = await _client.GetDataAsync<IssueDto>($"/api/issues/{issue.Number}?projectId={project.Id}");
        Assert.False(string.IsNullOrWhiteSpace(startedIssue.WorkflowRunId));
        await _fixture.Grains.GetGrain<IRunnerGrain>(_runnerId).AssignWorkflowAsync(startedIssue.WorkflowRunId!);

        var startEvents = await _client.GetDataAsync<EventDto[]>($"/api/issues/{issue.Number}/events?projectId={project.Id}");
        Assert.Contains(startEvents, e => e.Type == "issue_created");
        Assert.Contains(startEvents, e => e.Type == "issue_started");
        Assert.Contains(startEvents, e => e.Type == "workflow_started");

        var initialTimeline = await _client.GetDataAsync<WorkflowTimelineDto>($"/api/issues/{issue.Number}/workflow/timeline?projectId={project.Id}");
        Assert.Contains(initialTimeline.Stages, s => s.Stage == "plan" && s.Tasks.Any(t => t.Id == "proposal"));

        await DrainUntilApprovalAsync(project.Id, issue.Number, "plan");

        var planTimeline = await _client.GetDataAsync<WorkflowTimelineDto>($"/api/issues/{issue.Number}/workflow/timeline?projectId={project.Id}");
        var planStage = Assert.Single(planTimeline.Stages, s => s.Stage == "plan");
        Assert.Contains(planStage.Tasks, t => t.Id.StartsWith("proposal", StringComparison.Ordinal) && t.Status == "completed");
        Assert.Null(planStage.ApprovalStatus?.Result);

        var planLogs = await _client.GetDataAsync<WorkflowLogDto[]>($"/api/issues/{issue.Number}/logs?projectId={project.Id}");
        Assert.Contains(planLogs, e => e.EventType == "workflow_task_completed");
        Assert.Contains(planLogs, e => e.EventType == "workflow_check_passed");

        var listedAtApproval = await _client.GetDataAsync<IssueDto>($"/api/issues/{issue.Number}?projectId={project.Id}");
        Assert.Equal("in_progress", listedAtApproval.Stage);
        Assert.Equal("plan", listedAtApproval.WorkflowStage);
        Assert.Equal("AwaitingApproval", listedAtApproval.WorkflowStatus);
        Assert.Equal("attention", listedAtApproval.Status);
        Assert.Equal("review_required", listedAtApproval.Attention?.Reason);
        Assert.Equal("awaiting", listedAtApproval.ApprovalState?.Status);

        await _client.PostOkAsync($"/api/issues/{issue.Number}/approve?projectId={project.Id}");

        await DrainUntilApprovalAsync(project.Id, issue.Number, "check");
        await _client.PostOkAsync($"/api/issues/{issue.Number}/approve?projectId={project.Id}");

        await DrainUntilDoneAsync(project.Id, issue.Number);

        var completed = await _client.GetDataAsync<IssueDto>($"/api/issues/{issue.Number}?projectId={project.Id}");
        Assert.Equal("done", completed.Stage);
        Assert.Equal("done", completed.Status);

        await _client.PostOkAsync($"/api/issues/{issue.Number}/archive?projectId={project.Id}");
        var events = await _client.GetDataAsync<EventDto[]>($"/api/issues/{issue.Number}/events?projectId={project.Id}");
        Assert.Contains(events, e => e.Type == "issue_completed");
        Assert.Contains(events, e => e.Type == "issue_archived");
    }

    [Fact]
    public async Task IssueWorkflowVariablesPatch_AppliesToFutureDispatches()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"variables-{Guid.NewGuid():N}", path = "/tmp/mohist-variable-patch", baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Patch workflow variables", body = "body", labels = Array.Empty<string>(), priority = "p1", model = "openai/gpt-4o", projectId = project.Id });
        _projectId = project.Id;
        _issueNumber = issue.Number;

        await _client.PostOkAsync($"/api/issues/{issue.Number}/start?projectId={project.Id}");
        _runnerId = "variable-patch-runner";
        await _client.PostOkAsync($"/api/runner/{_runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host" });
        var startedIssue = await _client.GetDataAsync<IssueDto>($"/api/issues/{issue.Number}?projectId={project.Id}");
        await _fixture.Grains.GetGrain<IRunnerGrain>(_runnerId).AssignWorkflowAsync(startedIssue.WorkflowRunId!);

        var patched = await _client.PatchDataAsync<WorkflowVariablesDto>(
            $"/api/issues/{issue.Number}/workflow/vars/agent?projectId={project.Id}",
            new { type = "opencode", model = "kimi/k2", timeout = 1200 });
        Assert.Equal("future-dispatches", patched.Affected);

        var firstWork = await PollWorkAnyAsync();

        Assert.NotNull(firstWork.Variables);
        using var doc = JsonDocument.Parse(firstWork.Variables!);
        var agent = doc.RootElement.GetProperty("vars").GetProperty("agent");
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("kimi/k2", agent.GetProperty("model").GetString());
        Assert.Equal(1200, agent.GetProperty("timeout").GetInt32());
    }

    [Fact]
    public async Task IssueWorkflowYaml_ReturnsActiveWorkflowDefinition()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"yaml-{Guid.NewGuid():N}", path = "/tmp/mohist-yaml", baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Show workflow yaml", body = "body", labels = Array.Empty<string>(), priority = "p1", projectId = project.Id });
        _projectId = project.Id;
        _issueNumber = issue.Number;

        await _client.PostOkAsync($"/api/issues/{issue.Number}/start?projectId={project.Id}");

        var response = await _client.GetDataAsync<WorkflowYamlDto>($"/api/issues/{issue.Number}/workflow/yaml?projectId={project.Id}");

        Assert.Equal(issue.Number, response.IssueNumber);
        Assert.False(string.IsNullOrWhiteSpace(response.WorkflowRunId));
        Assert.Contains("stages:", response.Yaml);
        Assert.Contains("agent: ${{ vars.agent }}", response.Yaml);
        Assert.Contains("prompt: ${{ prompts.proposal }}", response.Yaml);
    }

    private async Task DrainUntilApprovalAsync(string projectId, int issueNumber, string stage)
    {
        for (var i = 0; i < 100; i++)
        {
            var status = await _client.GetDataAsync<IssueWorkflowStatusDto>($"/api/issues/{issueNumber}/workflow/status?projectId={projectId}");
            if (status.Workflow?.Status == "AwaitingApproval" && status.Workflow.CurrentStage == stage)
                return;
            await CompleteNextWorkAsync();
        }

        Assert.Fail($"Workflow did not reach approval at stage {stage}");
    }

    private async Task DrainUntilDoneAsync(string projectId, int issueNumber)
    {
        for (var i = 0; i < 100; i++)
        {
            var status = await _client.GetDataAsync<IssueWorkflowStatusDto>($"/api/issues/{issueNumber}/workflow/status?projectId={projectId}");
            if (status.Workflow?.Status == "Completed")
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

    private sealed record ProjectDto(string Id, string Name, string Path, string BaseBranch);
    private sealed record IssueDto(int Number, string Title, string Stage, string Status, ApprovalStateDto? ApprovalState, AttentionDto? Attention, string? WorkflowRunId, string? WorkflowStage, string? WorkflowStatus);
    private sealed record ApprovalStateDto(string Stage, string Status);
    private sealed record AttentionDto(string Reason);
    private sealed record IssueWorkflowStatusDto(WorkflowStatusDto? Workflow);
    private sealed record WorkflowStatusDto(string Status, string? CurrentStage);
    private sealed record EventDto(string Id, string Type, string Category, string? Status, string CreatedAt);
    private sealed record WorkflowLogDto(string Id, string EventType, string CreatedAt);
    private sealed record WorkflowTimelineDto(string WorkflowRunId, string Status, string? CurrentStage, WorkflowStageDto[] Stages);
    private sealed record WorkflowVariablesDto(int IssueNumber, string WorkflowRunId, string Affected);
    private sealed record WorkflowYamlDto(int IssueNumber, string WorkflowRunId, string Yaml);
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
