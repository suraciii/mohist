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
        await _client.PostOkAsync($"/api/projects/{projectName}/use");
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Ship product loop", body = "body", labels = Array.Empty<string>(), priority = "p1" });

        await _client.PostOkAsync($"/api/issues/{issue.Number}/start");
        _runnerId = "product-loop-runner";
        await _client.PostOkAsync($"/api/runner/{_runnerId}/register", new { capabilities = Array.Empty<string>(), hostname = "test-host" });

        await DrainUntilApprovalAsync(issue.Number, "plan");

        var listedAtApproval = await _client.GetDataAsync<IssueDto>($"/api/issues/{issue.Number}");
        Assert.Equal("plan", listedAtApproval.Stage);
        Assert.Equal("awaiting", listedAtApproval.ApprovalState?.Status);

        await _client.PostOkAsync($"/api/issues/{issue.Number}/approve");

        await DrainUntilApprovalAsync(issue.Number, "check");
        await _client.PostOkAsync($"/api/issues/{issue.Number}/approve");

        await DrainUntilDoneAsync(issue.Number);

        var completed = await _client.GetDataAsync<IssueDto>($"/api/issues/{issue.Number}");
        Assert.Equal("done", completed.Stage);
        Assert.Equal("completed", completed.Status);
    }

    private async Task DrainUntilApprovalAsync(int issueNumber, string stage)
    {
        for (var i = 0; i < 100; i++)
        {
            var status = await _client.GetDataAsync<IssueWorkflowStatusDto>($"/api/issues/{issueNumber}/workflow/status");
            if (status.Workflow?.Status == "AwaitingApproval" && status.Workflow.CurrentStage == stage)
                return;
            await CompleteNextWorkAsync();
        }

        Assert.Fail($"Workflow did not reach approval at stage {stage}");
    }

    private async Task DrainUntilDoneAsync(int issueNumber)
    {
        for (var i = 0; i < 100; i++)
        {
            var status = await _client.GetDataAsync<IssueWorkflowStatusDto>($"/api/issues/{issueNumber}/workflow/status");
            if (status.Workflow?.Status == "Passed")
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
                    tasks = new[] { new { id = "build-1", title = "Build task", uses = "mohist/agent" } }
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
            return await response.Content.ReadFromJsonAsync<WorkDispatchDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException("Empty work dispatch");
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
    private sealed record IssueDto(int Number, string Title, string Stage, string Status, ApprovalStateDto? ApprovalState);
    private sealed record ApprovalStateDto(string Stage, string Status);
    private sealed record IssueWorkflowStatusDto(WorkflowStatusDto? Workflow);
    private sealed record WorkflowStatusDto(string Status, string? CurrentStage);
    private sealed record WorkDispatchDto(
        string WorkflowRunId,
        string WorkId,
        string? Uses,
        string? With,
        string WorkType,
        string? Stage,
        string? Title);
}
