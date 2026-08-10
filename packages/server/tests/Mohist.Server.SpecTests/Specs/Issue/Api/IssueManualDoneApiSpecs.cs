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
    public async Task Done_OnUnknownProject_Returns404()
    {
        using var response = await _client.PostAsync(
            $"/api/projects/proj-does-not-exist-{Guid.NewGuid():N}/issues/1/done",
            null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Done_OnUnknownIssue_Returns404()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>(
            "/api/projects",
            $"manual-done-unknown-{Guid.NewGuid():N}");

        using var response = await _client.PostAsync(
            $"/api/projects/{project.Id}/issues/999999/done",
            null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Done_OnBacklogIssueWithoutWorkflow_TransitionsToDone()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>(
            "/api/projects",
            $"manual-done-delivered-{Guid.NewGuid():N}");
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Delivered outside workflow", isDraft = false });

        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/done");

        var completed = await _client.GetDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.Equal("done", completed.Status);
        Assert.Null(completed.WorkflowRunId);
    }

    [Fact]
    public async Task Done_OnRunningWorkflow_ReturnsConflictAndLeavesIssueInProgress()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>(
            "/api/projects",
            $"manual-done-running-{Guid.NewGuid():N}");
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Running workflow", isDraft = false });
        var started = await _client.PostDataAsync<StartDto>(
            $"/api/projects/{project.Id}/issues/{issue.Number}/start");

        using var response = await _client.PostAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/done",
            null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var current = await _client.GetDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.Equal("in_progress", current.Status);
        Assert.Equal(started.WorkflowRunId, current.WorkflowRunId);
    }

    private sealed record ProjectDto(string Id);
    private sealed record IssueDto(int Number, string Status, string? WorkflowRunId);
    private sealed record StartDto(int Number, string? WorkflowRunId);
}
