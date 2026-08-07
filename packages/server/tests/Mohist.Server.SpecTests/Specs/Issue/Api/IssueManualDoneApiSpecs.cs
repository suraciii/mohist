using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

[Collection("IssueLifecycle")]
public class IssueManualDoneApiSpecs
{
    private readonly HttpClient _client;

    public IssueManualDoneApiSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task Done_StoppedLeafIssue_CompletesOnceAndPreservesWorkflowHistory()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>(
            "/api/projects",
            $"manual-done-{Guid.NewGuid():N}");
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Delivered outside workflow", isDraft = false });
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start");

        using var active = await _client.PostAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/done",
            null);
        Assert.Equal(HttpStatusCode.Conflict, active.StatusCode);

        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/stop");
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/done");
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/done");

        var completed = await _client.GetDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.Equal("done", completed.Status);
        Assert.Equal("stopped", completed.WorkflowStatus);
        Assert.False(string.IsNullOrWhiteSpace(completed.WorkflowRunId));

        var events = await _client.GetDataAsync<EventDto[]>(
            $"/api/projects/{project.Id}/issues/{issue.Number}/events");
        var completion = Assert.Single(events, e => e.Type == EventCatalog.ReverseDns.IssueCompleted);
        Assert.Equal("manual", completion.Data.GetProperty("completionKind").GetString());
        Assert.Equal(completed.WorkflowRunId, completion.Data.GetProperty("workflowRunId").GetString());
    }

    [Fact]
    public async Task Done_ParentIssue_Rejects()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>(
            "/api/projects",
            $"manual-done-parent-{Guid.NewGuid():N}");
        var parent = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Parent", isDraft = false });
        var child = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Child", isDraft = false });
        using var attach = await _client.PatchAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{child.Number}",
            new { parentIssueNumber = parent.Number });
        Assert.Equal(HttpStatusCode.OK, attach.StatusCode);

        using var response = await _client.PostAsync(
            $"/api/projects/{project.Id}/issues/{parent.Number}/done",
            null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private sealed record ProjectDto(string Id);
    private sealed record IssueDto(int Number, string Status, string? WorkflowRunId, string? WorkflowStatus);
    private sealed record EventDto(string Type, JsonElement Data);
}
