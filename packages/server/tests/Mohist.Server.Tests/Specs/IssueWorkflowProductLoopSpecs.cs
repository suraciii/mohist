using System.Text.Json;
using System.Net.Http.Json;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class IssueWorkflowProductLoopSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private string? _runnerId;

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

        await _client.PostOkAsync($"/api/issues/{issue.Number}/start?projectId={project.Id}");
        _runnerId = "product-loop-runner";
        await _client.PostOkAsync($"/api/runner/{_runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host" });

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
        Assert.Equal("awaiting", planStage.Approval?.Status);

        var planLogs = await _client.GetDataAsync<WorkflowLogDto[]>($"/api/issues/{issue.Number}/logs?projectId={project.Id}");
        Assert.Contains(planLogs, e => e.EventType == "workflow_task_completed");
        Assert.Contains(planLogs, e => e.EventType == "workflow_check_passed");

        var listedAtApproval = await _client.GetDataAsync<IssueDto>($"/api/issues/{issue.Number}?projectId={project.Id}");
        Assert.Equal("in_progress", listedAtApproval.Stage);
        Assert.Equal("attention", listedAtApproval.Status);
        Assert.Equal("review_required", listedAtApproval.Attention?.Reason);
        Assert.Equal("awaiting", listedAtApproval.ApprovalState?.Status);

        await _client.PostOkAsync($"/api/issues/{issue.Number}/approve?projectId={project.Id}");

        await DrainUntilApprovalAsync(project.Id, issue.Number, "check");
        await _client.PostOkAsync($"/api/issues/{issue.Number}/approve?projectId={project.Id}");

        await DrainUntilDoneAsync(project.Id, issue.Number);

        var completed = await _client.GetDataAsync<IssueDto>($"/api/issues/{issue.Number}?projectId={project.Id}");
        Assert.Equal("done", completed.Stage);
        Assert.Equal("completed", completed.Status);

        await _client.PostOkAsync($"/api/issues/{issue.Number}/archive?projectId={project.Id}");
        var events = await _client.GetDataAsync<EventDto[]>($"/api/issues/{issue.Number}/events?projectId={project.Id}");
        Assert.Contains(events, e => e.Type == "issue_completed");
        Assert.Contains(events, e => e.Type == "issue_archived");
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
                await ReportAsync(work.WorkId, "completed");
                break;
            case "load":
                await ReportAsync(work.WorkId, "loaded", output: JsonSerializer.Serialize(new
                {
                    tasks = new[] { new { id = "build-1", title = "Build task", uses = "mohist/coder-agent" } }
                }));
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
            if (work.Stage == "plan" && work.Uses == "mohist/coder-agent")
            {
                Assert.NotNull(work.Variables);
                using var doc = JsonDocument.Parse(work.Variables);
                Assert.Equal("anthropic/claude", doc.RootElement.GetProperty("model").GetProperty("stage").GetProperty("plan").GetString());
            }
            return work;
        }

        Assert.Fail($"Runner '{_runnerId}' has no work");
        return default!;
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
    private sealed record IssueDto(int Number, string Title, string Stage, string Status, ApprovalStateDto? ApprovalState, AttentionDto? Attention);
    private sealed record ApprovalStateDto(string Stage, string Status);
    private sealed record AttentionDto(string Reason);
    private sealed record IssueWorkflowStatusDto(WorkflowStatusDto? Workflow);
    private sealed record WorkflowStatusDto(string Status, string? CurrentStage);
    private sealed record EventDto(string Id, string Type, string Category, string? Status, string CreatedAt);
    private sealed record WorkflowLogDto(string Id, string EventType, string CreatedAt);
    private sealed record WorkflowTimelineDto(string WorkflowRunId, string Status, string? CurrentStage, WorkflowStageDto[] Stages);
    private sealed record WorkflowStageDto(string Stage, string Status, WorkflowTaskDto[] Tasks, ApprovalDto? Approval);
    private sealed record WorkflowTaskDto(string Id, string Title, string? Uses, string Status);
    private sealed record ApprovalDto(string Status);
    private sealed record WorkDispatchDto(
        string WorkflowRunId,
        string WorkId,
        string? Uses,
        string? With,
        string? Variables,
        string WorkType,
        string? Stage,
        string? Title);
}
