using System.Net;
using System.Net.Http.Json;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Tests.Support;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Epic.Api;

[Trait("level", "L1")]
public class EpicStateTransitionApiSpecs : EpicApiTestSupport, IClassFixture<DefaultMohistIntegrationFixture>
{
    public EpicStateTransitionApiSpecs(DefaultMohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Start_FromIdle_ReturnsRunningStatus()
    {
        var project = await CreateProjectAsync("start");
        var created = await CreateEpicAsync(project.Id, "To start");
        Assert.Equal("idle", created.Status);
        await AddOpenIssueAsync(project.Id, created);

        var started = await _client.PostDataAsync<EpicFullDto>(
            $"/api/projects/{project.Id}/epics/{created.Number}/start",
            null);

        Assert.Equal("running", started.Status);
        Assert.Null(started.PauseReason);

        var detail = await _client.GetDataAsync<EpicDetailFullDto>(
            $"/api/projects/{project.Id}/epics/{created.Number}");
        Assert.Equal("running", detail.Status);
    }

    [Fact]
    public async Task Start_OnPausedEpic_Returns409EpicStartRequiresIdle()
    {
        var project = await CreateProjectAsync("start-paused");
        var created = await CreateEpicAsync(project.Id, "Paused then start");
        await AddOpenIssueAsync(project.Id, created);
        await StartEpicAsync(project.Id, created);
        await _client.PostOkAsync(
            $"/api/projects/{project.Id}/epics/{created.Number}/pause",
            new { reason = "hold" });

        using var response = await _client.PostAsync(
            $"/api/projects/{project.Id}/epics/{created.Number}/start",
            null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ConflictEnvelope>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("EPIC_START_REQUIRES_IDLE", envelope.Code);
        Assert.NotNull(envelope.Details);
        Assert.Equal("paused", envelope.Details!.CurrentStatus);

        var detail = await _client.GetDataAsync<EpicDetailFullDto>(
            $"/api/projects/{project.Id}/epics/{created.Number}");
        Assert.Equal("paused", detail.Status);
    }

    [Fact]
    public async Task Start_OnDoneEpic_Returns409EpicAlreadyTerminal()
    {
        var project = await CreateProjectAsync("start-done");
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Member", projectId = project.Id, isDraft = false });
        var created = await CreateEpicAsync(project.Id, "Done epic");
        await _client.PostOkAsync(
            $"/api/projects/{project.Id}/epics/{created.Number}/issues",
            new { issueNumber = issue.Number });
        await CompleteIssueAsync(project.Id, issue);

        using var response = await _client.PostAsync(
            $"/api/projects/{project.Id}/epics/{created.Number}/start",
            null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ConflictEnvelope>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("EPIC_ALREADY_TERMINAL", envelope.Code);
        Assert.NotNull(envelope.Details);
        Assert.Equal("done", envelope.Details!.CurrentStatus);
        Assert.Equal("running", envelope.Details.RequestedStatus);
    }

    [Fact]
    public async Task Pause_OnIdleEpic_Returns409EpicNotRunning()
    {
        var project = await CreateProjectAsync("pause-idle");
        var created = await CreateEpicAsync(project.Id, "Idle pause reject");

        using var response = await _client.PostAsync(
            $"/api/projects/{project.Id}/epics/{created.Number}/pause",
            null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ConflictEnvelope>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("EPIC_NOT_RUNNING", envelope.Code);
        Assert.NotNull(envelope.Details);
        Assert.Equal("idle", envelope.Details!.CurrentStatus);
    }

    [Fact]
    public async Task Resume_OnIdleEpic_Returns409EpicResumeRequiresPaused()
    {
        var project = await CreateProjectAsync("resume-idle");
        var created = await CreateEpicAsync(project.Id, "Idle resume reject");

        using var response = await _client.PostAsync(
            $"/api/projects/{project.Id}/epics/{created.Number}/resume",
            null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ConflictEnvelope>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("EPIC_RESUME_REQUIRES_PAUSED", envelope.Code);
        Assert.NotNull(envelope.Details);
        Assert.Equal("idle", envelope.Details!.CurrentStatus);
    }

    [Fact]
    public async Task StartRoute_UnknownEpicReturnsNotFound()
    {
        var project = await CreateProjectAsync("start-404");

        using var response = await _client.PostAsync(
            $"/api/projects/{project.Id}/epics/9999/start",
            null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<ProjectDto> CreateProjectAsync(string scenario)
    {
        var projectId = $"project-{Guid.NewGuid():N}";
        await _grains.GetGrain<IProjectGrain>(projectId).CreateAsync(
            $"epic-{scenario}-{Guid.NewGuid():N}",
            new RepositoryInfo
            {
                Name = "main",
                GitUrl = $"file://{Guid.NewGuid():N}",
                BaseBranch = "main",
                IsDefault = true,
            },
            "true");
        return new ProjectDto(projectId);
    }
}
