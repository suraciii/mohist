using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class OverriddenPromptDispatchSpecs : IAsyncLifetime
{
    private const string OverrideProposalBody = "# Overridden proposal body for project A\nUse ${{ openspecChangeDir }}/proposal.md";

    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private readonly List<TrackedIssue> _tracked = new();

    public OverriddenPromptDispatchSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var entry in _tracked)
        {
            using var _ = await _client.PostAsync($"/api/issues/{entry.IssueNumber}/stop?projectId={entry.ProjectId}", null);
        }
    }

    [Fact]
    public async Task ProposalDispatch_DeliversOverriddenBodyForOneProject_AndSystemBodyForAnother()
    {
        var systemProposalBody = await GetSystemProposalBodyAsync();
        Assert.False(string.IsNullOrWhiteSpace(systemProposalBody));
        Assert.NotEqual(OverrideProposalBody, systemProposalBody);

        var (projectA, issueA) = await CreateIssueAsync("override");
        var (projectB, issueB) = await CreateIssueAsync("system");

        await UpsertProposalOverrideAsync(projectA, OverrideProposalBody);

        var runnerA = await StartIssueWithRunnerAsync(projectA, issueA, "override");
        var runnerB = await StartIssueWithRunnerAsync(projectB, issueB, "system");

        var workA = await PollProposalWorkAsync(runnerA, projectA, issueA);
        var workB = await PollProposalWorkAsync(runnerB, projectB, issueB);

        Assert.Equal(OverrideProposalBody, ReadPromptsProposal(workA.Variables));
        Assert.Equal(systemProposalBody, ReadPromptsProposal(workB.Variables));
    }

    private async Task<(string ProjectId, int IssueNumber)> CreateIssueAsync(string label)
    {
        var project = await _client.PostDataAsync<ProjectDto>(
            "/api/projects",
            new { name = $"prompt-dispatch-{label}-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>(
            "/api/issues",
            new { title = $"Prompt dispatch {label}", body = "body", labels = Array.Empty<string>(), priority = "p1", projectId = project.Id });
        _tracked.Add(new TrackedIssue(project.Id, issue.Number));
        return (project.Id, issue.Number);
    }

    private async Task<string> StartIssueWithRunnerAsync(string projectId, int issueNumber, string label)
    {
        await _client.PostOkAsync($"/api/issues/{issueNumber}/start?projectId={projectId}");
        var runnerId = $"prompt-dispatch-runner-{label}-{Guid.NewGuid():N}";
        await _client.PostOkAsync(
            $"/api/runner/{runnerId}/register",
            new { capabilities = Array.Empty<string>(), hostname = "test-host", projectId });
        return runnerId;
    }

    private async Task UpsertProposalOverrideAsync(string projectId, string body)
    {
        using var response = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/templates/proposal/override",
            new
            {
                displayName = "Override Proposal",
                description = "Integration spec override",
                tags = new[] { "spec" },
                stage = "plan",
                body,
            });
        response.EnsureSuccessStatusCode();
    }

    private async Task<string> GetSystemProposalBodyAsync()
    {
        var response = await _client.GetFromJsonAsync<JsonElement>("/api/templates/system");
        Assert.True(response.GetProperty("success").GetBoolean());
        var entry = response.GetProperty("data").EnumerateArray()
            .Single(item => item.GetProperty("key").GetString() == "proposal");
        return entry.GetProperty("body").GetString()!;
    }

    private async Task<WorkDispatchDto> PollProposalWorkAsync(string runnerId, string projectId, int issueNumber)
    {
        var observed = new List<string>();
        for (var attempt = 0; attempt < 200; attempt++)
        {
            using var response = await _client.PostAsync($"/api/runner/{runnerId}/poll", null);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                await Task.Delay(20);
                continue;
            }

            response.EnsureSuccessStatusCode();
            var work = await response.Content.ReadFromJsonAsync<WorkDispatchDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException("Empty work dispatch");

            if (work.ProjectId == projectId
                && work.IssueNumber == issueNumber
                && work.WorkType == "task"
                && IsProposalWork(work.WorkId))
            {
                return work;
            }

            observed.Add($"{work.WorkType}/{work.Stage}/{work.WorkId}");
            await _client.PostOkAsync(
                $"/api/runner/{runnerId}/report",
                new
                {
                    workflowRunId = work.WorkflowRunId,
                    workId = work.WorkId,
                    status = work.WorkType == "checks" ? "pass" : "completed",
                });
        }

        Assert.Fail($"Runner '{runnerId}' never received the proposal task for project '{projectId}' issue '{issueNumber}'. Observed: {string.Join("; ", observed)}");
        return default!;
    }

    private static bool IsProposalWork(string workId)
    {
        return workId == "proposal" || workId.StartsWith("proposal.", StringComparison.Ordinal);
    }

    private static string ReadPromptsProposal(string? variablesJson)
    {
        Assert.False(string.IsNullOrWhiteSpace(variablesJson));
        using var document = JsonDocument.Parse(variablesJson!);
        var prompts = document.RootElement.GetProperty("prompts");
        return prompts.GetProperty("proposal").GetString()!;
    }

    private sealed record TrackedIssue(string ProjectId, int IssueNumber);
    private sealed record ProjectDto(string Id);
    private sealed record IssueDto(int Number);

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
