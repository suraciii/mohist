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

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/done", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<IssueDto>>();
        Assert.NotNull(envelope);
        Assert.True(envelope!.Success);
        Assert.NotNull(envelope.Data);
        Assert.Equal(issue.Number, envelope.Data!.Number);
        Assert.Equal("Delivered outside workflow", envelope.Data.Title);
        Assert.Equal("done", envelope.Data.Status);

        var completed = await _client.GetDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.Equal("done", completed.Status);
        Assert.Null(completed.WorkflowRunId);
        Assert.Equal(completed.Number, envelope.Data.Number);
        Assert.Equal(completed.Title, envelope.Data.Title);
        Assert.Equal(completed.Status, envelope.Data.Status);
    }

    [Fact]
    public async Task Close_ReturnsCanonicalIssueResource_AndRepeatedCloseDoesToo()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>(
            "/api/projects",
            $"manual-close-resource-{Guid.NewGuid():N}");
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Close resource", isDraft = false });

        var first = await PostLifecycleResourceAsync(project.Id, issue.Number, "close");
        var second = await PostLifecycleResourceAsync(project.Id, issue.Number, "close");

        Assert.Equal(issue.Number, first.Number);
        Assert.Equal("Close resource", first.Title);
        Assert.Equal("cancelled", first.Status);
        Assert.Equal(first.Number, second.Number);
        Assert.Equal(first.Title, second.Title);
        Assert.Equal(first.Status, second.Status);
    }

    [Fact]
    public async Task Reopen_ReturnsCanonicalIssueResource()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>(
            "/api/projects",
            $"manual-reopen-resource-{Guid.NewGuid():N}");
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Reopen resource", isDraft = false });

        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/close");
        var reopened = await PostLifecycleResourceAsync(project.Id, issue.Number, "reopen");
        var current = await _client.GetDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues/{issue.Number}");

        Assert.Equal(issue.Number, reopened.Number);
        Assert.Equal("Reopen resource", reopened.Title);
        Assert.Equal("backlog", reopened.Status);
        Assert.Equal(current.Number, reopened.Number);
        Assert.Equal(current.Title, reopened.Title);
        Assert.Equal(current.Status, reopened.Status);

        using var repeated = await _client.PostAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/reopen",
            null);
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        var repeatedEnvelope = await repeated.Content.ReadFromJsonAsync<ApiEnvelope<IssueDto>>();
        Assert.NotNull(repeatedEnvelope);
        Assert.False(repeatedEnvelope!.Success);
        Assert.Null(repeatedEnvelope.Data);
        Assert.Equal("conflict", repeatedEnvelope.Code);
    }

    [Fact]
    public async Task LifecycleErrors_ReturnFailureEnvelopeWithoutResource()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>(
            "/api/projects",
            $"manual-lifecycle-errors-{Guid.NewGuid():N}");
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Lifecycle error", isDraft = false });

        using var unknown = await _client.PostAsync(
            $"/api/projects/{project.Id}/issues/999999/done",
            null);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        using var invalidReopen = await _client.PostAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/reopen",
            null);
        Assert.Equal(HttpStatusCode.Conflict, invalidReopen.StatusCode);
        var reopened = await invalidReopen.Content.ReadFromJsonAsync<ApiEnvelope<IssueDto>>();
        Assert.NotNull(reopened);
        Assert.False(reopened!.Success);
        Assert.Null(reopened.Data);
        Assert.Equal("conflict", reopened.Code);
    }

    private async Task<IssueDto> PostLifecycleResourceAsync(string projectId, int number, string action)
    {
        using var response = await _client.PostAsync(
            $"/api/projects/{projectId}/issues/{number}/{action}",
            null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<IssueDto>>();
        Assert.NotNull(envelope);
        Assert.True(envelope!.Success);
        Assert.NotNull(envelope.Data);
        return envelope.Data!;
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
    private sealed record IssueDto(int Number, string Status, string? WorkflowRunId, string? Title = null);
    private sealed record StartDto(int Number, string? WorkflowRunId);
    private sealed record ApiEnvelope<T>(bool Success, T? Data, string? Error = null, string? Code = null);
}
