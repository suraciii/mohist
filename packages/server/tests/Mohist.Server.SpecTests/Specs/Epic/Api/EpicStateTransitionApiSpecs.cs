using Mohist.Server.Epic.Grains;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Orleans;
using System.Net;
using System.Net.Http.Json;
using Xunit;
namespace Mohist.Server.SpecTests.Specs.Epic.Api;

[Collection("MohistIntegration")]
public class EpicStateTransitionApiSpecs : EpicApiTestSupport
{
    public EpicStateTransitionApiSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Start_FromIdle_ReturnsRunningStatus()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-start-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "To start", projectId = project.Id });
        Assert.Equal("idle", created.Status);
        await AddOpenIssueAsync(project.Id, created);

        var started = await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{created.Number}/start", null);

        Assert.Equal("running", started.Status);
        Assert.Null(started.PauseReason);

        var detail = await _client.GetDataAsync<EpicDetailFullDto>($"/api/projects/{project.Id}/epics/{created.Number}");
        Assert.Equal("running", detail.Status);
    }

    [Fact]
    public async Task Start_OnRunningEpic_IsIdempotentNoOp200()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-start-running-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Running twice", projectId = project.Id });
        await AddOpenIssueAsync(project.Id, created);
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{created.Number}/start", null);

        var started = await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{created.Number}/start", null);

        Assert.Equal("running", started.Status);
    }

    [Fact]
    public async Task Start_OnPausedEpic_Returns409EpicStartRequiresIdle()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-start-paused-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Paused then start", projectId = project.Id });
        await AddOpenIssueAsync(project.Id, created);
        await StartEpicAsync(project.Id, created);
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{created.Number}/pause", new { reason = "hold" });

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/{created.Number}/start", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ConflictEnvelope>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("EPIC_START_REQUIRES_IDLE", envelope.Code);
        Assert.NotNull(envelope.Details);
        Assert.Equal("paused", envelope.Details!.CurrentStatus);

        var detail = await _client.GetDataAsync<EpicDetailFullDto>($"/api/projects/{project.Id}/epics/{created.Number}");
        Assert.Equal("paused", detail.Status);
    }

    [Fact]
    public async Task Start_OnDoneEpic_Returns409EpicAlreadyTerminal()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-start-done-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Member", projectId = project.Id, isDraft = false });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Done epic", projectId = project.Id });
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{created.Number}/issues", new { issueNumber = issue.Number });
        await CompleteIssueAsync(project.Id, issue);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/{created.Number}/start", null);

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
    public async Task Start_OnClosedEpic_Returns409EpicAlreadyTerminal()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-start-closed-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Closed epic", projectId = project.Id });
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{created.Number}/close", null);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/{created.Number}/start", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ConflictEnvelope>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("EPIC_ALREADY_TERMINAL", envelope.Code);
        Assert.NotNull(envelope.Details);
        Assert.Equal("closed", envelope.Details!.CurrentStatus);
        Assert.Equal("running", envelope.Details.RequestedStatus);
    }

    [Fact]
    public async Task Start_AcceptsEpicNumber()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-start-num-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "By number", projectId = project.Id });
        await AddOpenIssueAsync(project.Id, created);

        var started = await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{created.Number}/start", null);

        Assert.Equal("running", started.Status);
    }

    [Fact]
    public async Task Pause_OnIdleEpic_Returns409EpicNotRunning()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-pause-idle-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Idle pause reject", projectId = project.Id });

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/{created.Number}/pause", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ConflictEnvelope>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("EPIC_NOT_RUNNING", envelope.Code);
        Assert.NotNull(envelope.Details);
        Assert.Equal("idle", envelope.Details!.CurrentStatus);

        var detail = await _client.GetDataAsync<EpicDetailFullDto>($"/api/projects/{project.Id}/epics/{created.Number}");
        Assert.Equal("idle", detail.Status);
    }

    [Fact]
    public async Task Pause_OnAlreadyPausedEpic_IsIdempotentNoOp200()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-pause-twice-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Pause twice", projectId = project.Id });
        await AddOpenIssueAsync(project.Id, created);
        await StartEpicAsync(project.Id, created);
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{created.Number}/pause", new { reason = "hold" });

        var pausedAgain = await _client.PostDataAsync<EpicFullDto>(
            $"/api/projects/{project.Id}/epics/{created.Number}/pause",
            new { reason = "hold-again" });

        Assert.Equal("paused", pausedAgain.Status);
        Assert.Equal("hold", pausedAgain.PauseReason);
    }

    [Fact]
    public async Task Pause_OnDoneEpic_Returns409EpicAlreadyTerminal()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-pause-done-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Member", projectId = project.Id, isDraft = false });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Done then pause", projectId = project.Id });
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{created.Number}/issues", new { issueNumber = issue.Number });
        await CompleteIssueAsync(project.Id, issue);
        await DispatchPendingEventsAsync();

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/{created.Number}/pause", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ConflictEnvelope>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("EPIC_ALREADY_TERMINAL", envelope.Code);
        Assert.NotNull(envelope.Details);
        Assert.Equal("done", envelope.Details!.CurrentStatus);
        Assert.Equal("paused", envelope.Details.RequestedStatus);
    }

    [Fact]
    public async Task Resume_OnIdleEpic_Returns409EpicResumeRequiresPaused()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-resume-idle-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Idle resume reject", projectId = project.Id });

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/{created.Number}/resume", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ConflictEnvelope>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("EPIC_RESUME_REQUIRES_PAUSED", envelope.Code);
        Assert.NotNull(envelope.Details);
        Assert.Equal("idle", envelope.Details!.CurrentStatus);

        var detail = await _client.GetDataAsync<EpicDetailFullDto>($"/api/projects/{project.Id}/epics/{created.Number}");
        Assert.Equal("idle", detail.Status);
    }

    [Fact]
    public async Task Resume_OnAlreadyRunningEpic_IsIdempotentNoOp200()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-resume-twice-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Open work", projectId = project.Id, isDraft = false });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Resume twice", projectId = project.Id });
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{created.Number}/issues", new { issueNumber = issue.Number });
        await StartEpicAsync(project.Id, created);

        var resumed = await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{created.Number}/resume", null);

        Assert.Equal("running", resumed.Status);

        var detail = await _client.GetDataAsync<EpicDetailFullDto>($"/api/projects/{project.Id}/epics/{created.Number}");
        Assert.Equal("running", detail.Status);
    }

    [Fact]
    public async Task Resume_OnDoneEpic_Returns409EpicAlreadyTerminal()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-resume-done-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Member", projectId = project.Id, isDraft = false });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Done then resume", projectId = project.Id });
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{created.Number}/issues", new { issueNumber = issue.Number });
        await CompleteIssueAsync(project.Id, issue);
        await DispatchPendingEventsAsync();

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/{created.Number}/resume", null);

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
    public async Task StartRoute_UnknownEpicReturnsNotFound()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-start-404-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/9999/start", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

}
