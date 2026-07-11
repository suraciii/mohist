using Mohist.Server.Epic.Grains;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.SpecTests.Support;
using Orleans;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Epic.Api;

[Collection("MohistIntegration")]
public class EpicLifecycleApiSpecs
{
    private readonly HttpClient _client;
    private readonly IGrainFactory _grains;

    public EpicLifecycleApiSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
        _grains = fixture.Grains;
    }

    private async Task StartEpicAsync(string projectId, EpicDto epic)
    {
        var grain = _grains.GetGrain<IEpicGrain>($"{projectId}:{epic.Id}");
        await grain.StartAsync();
    }

    private async Task CompleteIssueAsync(string projectId, IssueDto issueInfo)
    {
        var grain = _grains.GetGrain<IIssueGrain>(issueInfo.Id);
        var wrId = await grain.StartWorkAsync(new WorkflowProjectContext(projectId, "Epic API Test", RepositoryBaseBranch: "main"));
        await grain.CompleteWorkAsync(wrId);
    }

    [Fact]
    public async Task Pause_FromRunning_ReturnsPausedStatusAndPersistsReason()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-pause-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "To pause", projectId = project.Id });
        await StartEpicAsync(project.Id, created);

        var paused = await _client.PostDataAsync<EpicFullDto>(
            $"/api/projects/{project.Id}/epics/{created.Id}/pause",
            new { reason = "Waiting for design review" });

        Assert.Equal("paused", paused.Status);
        Assert.Equal("Waiting for design review", paused.PauseReason);

        var detail = await _client.GetDataAsync<EpicDetailFullDto>($"/api/projects/{project.Id}/epics/{created.Id}");
        Assert.Equal("paused", detail.Status);
        Assert.Equal("Waiting for design review", detail.PauseReason);
    }

    [Fact]
    public async Task Pause_FromRunning_DoesNotUnbindLinkedIssues()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-pause-nounbind-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var epic = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Container", projectId = project.Id });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Member", projectId = project.Id });
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{epic.Id}/issues", new { issueId = issue.Id });
        await StartEpicAsync(project.Id, epic);

        await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{epic.Id}/pause", new { reason = "park" });

        var detail = await _client.GetDataAsync<EpicDetailFullDto>($"/api/projects/{project.Id}/epics/{epic.Id}");
        Assert.Equal("paused", detail.Status);
        Assert.Single(detail.LinkedIssues);
        Assert.Equal(issue.Id, detail.LinkedIssues[0].Id);
    }

    [Fact]
    public async Task Pause_WithoutReason_PersistsNullReason()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-pause-noreason-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "No reason", projectId = project.Id });
        await StartEpicAsync(project.Id, created);

        var paused = await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{created.Id}/pause");

        Assert.Equal("paused", paused.Status);
        Assert.Null(paused.PauseReason);
    }

    [Fact]
    public async Task Resume_FromPaused_ReturnsRunningStatusAndClearsReason()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-resume-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "To resume", projectId = project.Id });
        var openIssue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Open work", projectId = project.Id });
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{created.Id}/issues", new { issueId = openIssue.Id });
        await StartEpicAsync(project.Id, created);
        await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{created.Id}/pause", new { reason = "on hold" });

        var resumed = await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{created.Id}/resume");

        Assert.Equal("running", resumed.Status);
        Assert.Null(resumed.PauseReason);

        var detail = await _client.GetDataAsync<EpicDetailFullDto>($"/api/projects/{project.Id}/epics/{created.Id}");
        Assert.Equal("running", detail.Status);
        Assert.Null(detail.PauseReason);
    }

    [Fact]
    public async Task MarkDone_OnPausedEpic_Returns409WithEpicPausedCannotMarkDone()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-paused-done-reject-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Paused epic", projectId = project.Id });
        await StartEpicAsync(project.Id, created);
        await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{created.Id}/pause", new { reason = "hold" });

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/{created.Id}/done", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ConflictEnvelope>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("EPIC_PAUSED_CANNOT_MARK_DONE", envelope.Code);
    }

    [Fact]
    public async Task Close_FromPaused_Succeeds()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-close-paused-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Paused then close", projectId = project.Id });
        await StartEpicAsync(project.Id, created);
        await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{created.Id}/pause", new { reason = "abandon" });

        var closed = await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{created.Id}/close");

        Assert.Equal("closed", closed.Status);
    }

    [Fact]
    public async Task EpicList_IncludesPauseReason()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-list-reason-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Idle one", projectId = project.Id });
        var paused = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Paused one", projectId = project.Id });
        await StartEpicAsync(project.Id, paused);
        await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{paused.Id}/pause", new { reason = "hold" });

        var list = await _client.GetDataAsync<EpicWithProgressFullDto[]>($"/api/projects/{project.Id}/epics");

        Assert.Equal(2, list.Length);
        var idleEpic = list.First(e => e.Status == "idle");
        Assert.Null(idleEpic.PauseReason);
        var pausedEpic = list.First(e => e.Status == "paused");
        Assert.Equal("hold", pausedEpic.PauseReason);
    }

    [Fact]
    public async Task PauseRoute_AcceptsEpicNumber()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-pause-num-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Number pause", projectId = project.Id });
        await StartEpicAsync(project.Id, created);

        var paused = await _client.PostDataAsync<EpicFullDto>(
            $"/api/projects/{project.Id}/epics/{created.Number}/pause",
            new { reason = "by number" });

        Assert.Equal("paused", paused.Status);
    }

    [Fact]
    public async Task ResumeRoute_AcceptsEpicNumber()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-resume-num-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Number resume", projectId = project.Id });
        var openIssue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Open work", projectId = project.Id });
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{created.Id}/issues", new { issueId = openIssue.Id });
        await StartEpicAsync(project.Id, created);
        await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{created.Number}/pause", new { reason = "hold" });

        var resumed = await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{created.Number}/resume");

        Assert.Equal("running", resumed.Status);
    }

    [Fact]
    public async Task Start_FromIdle_ReturnsRunningStatus()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-start-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "To start", projectId = project.Id });
        Assert.Equal("idle", created.Status);

        var started = await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{created.Id}/start", null);

        Assert.Equal("running", started.Status);
        Assert.Null(started.PauseReason);

        var detail = await _client.GetDataAsync<EpicDetailFullDto>($"/api/projects/{project.Id}/epics/{created.Id}");
        Assert.Equal("running", detail.Status);
    }

    [Fact]
    public async Task Start_OnRunningEpic_IsIdempotentNoOp200()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-start-running-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Running twice", projectId = project.Id });
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{created.Id}/start", null);

        var started = await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{created.Id}/start", null);

        Assert.Equal("running", started.Status);
    }

    [Fact]
    public async Task Start_OnPausedEpic_Returns409EpicStartRequiresIdle()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-start-paused-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Paused then start", projectId = project.Id });
        await StartEpicAsync(project.Id, created);
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{created.Id}/pause", new { reason = "hold" });

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/{created.Id}/start", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ConflictEnvelope>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("EPIC_START_REQUIRES_IDLE", envelope.Code);
        Assert.NotNull(envelope.Details);
        Assert.Equal("paused", envelope.Details!.CurrentStatus);

        var detail = await _client.GetDataAsync<EpicDetailFullDto>($"/api/projects/{project.Id}/epics/{created.Id}");
        Assert.Equal("paused", detail.Status);
    }

    [Fact]
    public async Task Start_OnDoneEpic_Returns409EpicAlreadyTerminal()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-start-done-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Member", projectId = project.Id, isDraft = false });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Done epic", projectId = project.Id });
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{created.Id}/issues", new { issueId = issue.Id });
        await CompleteIssueAsync(project.Id, issue);
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{created.Id}/done", null);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/{created.Id}/start", null);

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
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-start-closed-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Closed epic", projectId = project.Id });
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{created.Id}/close", null);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/{created.Id}/start", null);

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
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-start-num-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "By number", projectId = project.Id });

        var started = await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{created.Number}/start", null);

        Assert.Equal("running", started.Status);
    }

    [Fact]
    public async Task Pause_OnIdleEpic_Returns409EpicNotRunning()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-pause-idle-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Idle pause reject", projectId = project.Id });

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/{created.Id}/pause", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ConflictEnvelope>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("EPIC_NOT_RUNNING", envelope.Code);
        Assert.NotNull(envelope.Details);
        Assert.Equal("idle", envelope.Details!.CurrentStatus);

        var detail = await _client.GetDataAsync<EpicDetailFullDto>($"/api/projects/{project.Id}/epics/{created.Id}");
        Assert.Equal("idle", detail.Status);
    }

    [Fact]
    public async Task Pause_OnAlreadyPausedEpic_IsIdempotentNoOp200()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-pause-twice-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Pause twice", projectId = project.Id });
        await StartEpicAsync(project.Id, created);
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{created.Id}/pause", new { reason = "hold" });

        var pausedAgain = await _client.PostDataAsync<EpicFullDto>(
            $"/api/projects/{project.Id}/epics/{created.Id}/pause",
            new { reason = "hold-again" });

        Assert.Equal("paused", pausedAgain.Status);
        Assert.Equal("hold", pausedAgain.PauseReason);
    }

    [Fact]
    public async Task Pause_OnDoneEpic_Returns409EpicAlreadyTerminal()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-pause-done-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Member", projectId = project.Id, isDraft = false });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Done then pause", projectId = project.Id });
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{created.Id}/issues", new { issueId = issue.Id });
        await CompleteIssueAsync(project.Id, issue);
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{created.Id}/done", null);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/{created.Id}/pause", null);

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
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-resume-idle-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Idle resume reject", projectId = project.Id });

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/{created.Id}/resume", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ConflictEnvelope>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("EPIC_RESUME_REQUIRES_PAUSED", envelope.Code);
        Assert.NotNull(envelope.Details);
        Assert.Equal("idle", envelope.Details!.CurrentStatus);

        var detail = await _client.GetDataAsync<EpicDetailFullDto>($"/api/projects/{project.Id}/epics/{created.Id}");
        Assert.Equal("idle", detail.Status);
    }

    [Fact]
    public async Task Resume_OnAlreadyRunningEpic_IsIdempotentNoOp200()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-resume-twice-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Open work", projectId = project.Id, isDraft = false });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Resume twice", projectId = project.Id });
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{created.Id}/issues", new { issueId = issue.Id });
        await StartEpicAsync(project.Id, created);

        var resumed = await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{created.Id}/resume", null);

        Assert.Equal("running", resumed.Status);

        var detail = await _client.GetDataAsync<EpicDetailFullDto>($"/api/projects/{project.Id}/epics/{created.Id}");
        Assert.Equal("running", detail.Status);
    }

    [Fact]
    public async Task Resume_OnDoneEpic_Returns409EpicAlreadyTerminal()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-resume-done-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Member", projectId = project.Id, isDraft = false });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Done then resume", projectId = project.Id });
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{created.Id}/issues", new { issueId = issue.Id });
        await CompleteIssueAsync(project.Id, issue);
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{created.Id}/done", null);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/{created.Id}/resume", null);

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
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-start-404-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/epic_{Guid.NewGuid():N}/start", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
    private sealed record ProjectDto(string Id);
    private sealed record EpicDto(string Id, int? Number, string Title, string Description, string Priority, string Status, string CreatedAt, string UpdatedAt);
    private sealed record EpicFullDto(string Id, int? Number, string Title, string Description, string Priority, string Status, string CreatedAt, string UpdatedAt, string? PauseReason);
    private sealed record EpicWithProgressDto(string Id, int? Number, string Priority, string UpdatedAt);
    private sealed record EpicWithProgressFullDto(string Id, int? Number, string Status, string? PauseReason);
    private sealed record EpicDetailDto(string Id, int? Number, string Title, string Description, string Status, LinkedIssueDto[] LinkedIssues);
    private sealed record EpicDetailFullDto(string Id, int? Number, string Status, string? PauseReason, LinkedIssueDto[] LinkedIssues);
    private sealed record LinkedIssueDto(string Id);
    private sealed record IssueDto(int Number, string Id, PrimaryEpicDto? PrimaryEpic);
    private sealed record PrimaryEpicDto(string Id, int? Number, string Title);
    private sealed record NotFoundEnvelope(bool Success, string? Code = null, string? Error = null);
    private sealed record ConflictEnvelope(bool Success, string? Code = null, string? Error = null, ConflictDetails? Details = null);
    private sealed record ConflictDetails(string CurrentStatus, string? RequestedStatus = null);
}
