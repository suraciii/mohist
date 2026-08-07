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
public class EpicPauseResumeApiSpecs : EpicApiTestSupport
{
    public EpicPauseResumeApiSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Pause_FromRunning_ReturnsPausedStatusAndPersistsReason()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-pause-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "To pause", projectId = project.Id });
        await AddOpenIssueAsync(project.Id, created);
        await StartEpicAsync(project.Id, created);

        var paused = await _client.PostDataAsync<EpicFullDto>(
            $"/api/projects/{project.Id}/epics/{created.Number}/pause",
            new { reason = "Waiting for design review" });

        Assert.Equal("paused", paused.Status);
        Assert.Equal("Waiting for design review", paused.PauseReason);

        var detail = await _client.GetDataAsync<EpicDetailFullDto>($"/api/projects/{project.Id}/epics/{created.Number}");
        Assert.Equal("paused", detail.Status);
        Assert.Equal("Waiting for design review", detail.PauseReason);
    }

    [Fact]
    public async Task Pause_FromRunning_DoesNotUnbindLinkedIssues()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-pause-nounbind-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var epic = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Container", projectId = project.Id });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Member", projectId = project.Id });
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{epic.Number}/issues", new { issueNumber = issue.Number });
        await StartEpicAsync(project.Id, epic);

        await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{epic.Number}/pause", new { reason = "park" });

        var detail = await _client.GetDataAsync<EpicDetailFullDto>($"/api/projects/{project.Id}/epics/{epic.Number}");
        Assert.Equal("paused", detail.Status);
        Assert.Single(detail.LinkedIssues);
        Assert.Equal(issue.Number, detail.LinkedIssues[0].Number);
    }

    [Fact]
    public async Task Pause_WithoutReason_PersistsNullReason()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-pause-noreason-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "No reason", projectId = project.Id });
        await AddOpenIssueAsync(project.Id, created);
        await StartEpicAsync(project.Id, created);

        var paused = await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{created.Number}/pause");

        Assert.Equal("paused", paused.Status);
        Assert.Null(paused.PauseReason);
    }

    [Fact]
    public async Task Resume_FromPaused_ReturnsRunningStatusAndClearsReason()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-resume-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "To resume", projectId = project.Id });
        var openIssue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Open work", projectId = project.Id });
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{created.Number}/issues", new { issueNumber = openIssue.Number });
        await StartEpicAsync(project.Id, created);
        await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{created.Number}/pause", new { reason = "on hold" });

        var resumed = await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{created.Number}/resume");

        Assert.Equal("running", resumed.Status);
        Assert.Null(resumed.PauseReason);

        var detail = await _client.GetDataAsync<EpicDetailFullDto>($"/api/projects/{project.Id}/epics/{created.Number}");
        Assert.Equal("running", detail.Status);
        Assert.Null(detail.PauseReason);
    }

    [Fact]
    public async Task MarkDone_OnPausedEpic_Returns409WithEpicPausedCannotMarkDone()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-paused-done-reject-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Paused epic", projectId = project.Id });
        await AddOpenIssueAsync(project.Id, created);
        await StartEpicAsync(project.Id, created);
        await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{created.Number}/pause", new { reason = "hold" });

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/epics/{created.Number}/done", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ConflictEnvelope>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("EPIC_PAUSED_CANNOT_MARK_DONE", envelope.Code);
    }

    [Fact]
    public async Task Close_FromPaused_Succeeds()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-close-paused-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Paused then close", projectId = project.Id });
        await AddOpenIssueAsync(project.Id, created);
        await StartEpicAsync(project.Id, created);
        await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{created.Number}/pause", new { reason = "abandon" });

        var closed = await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{created.Number}/close");

        Assert.Equal("closed", closed.Status);
    }

    [Fact]
    public async Task EpicList_IncludesPauseReason()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-list-reason-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Idle one", projectId = project.Id });
        var paused = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Paused one", projectId = project.Id });
        await AddOpenIssueAsync(project.Id, paused);
        await StartEpicAsync(project.Id, paused);
        await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{paused.Number}/pause", new { reason = "hold" });

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
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-pause-num-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Number pause", projectId = project.Id });
        await AddOpenIssueAsync(project.Id, created);
        await StartEpicAsync(project.Id, created);

        var paused = await _client.PostDataAsync<EpicFullDto>(
            $"/api/projects/{project.Id}/epics/{created.Number}/pause",
            new { reason = "by number" });

        Assert.Equal("paused", paused.Status);
    }

    [Fact]
    public async Task ResumeRoute_AcceptsEpicNumber()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-resume-num-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Number resume", projectId = project.Id });
        var openIssue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Open work", projectId = project.Id });
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{created.Number}/issues", new { issueNumber = openIssue.Number });
        await StartEpicAsync(project.Id, created);
        await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{created.Number}/pause", new { reason = "hold" });

        var resumed = await _client.PostDataAsync<EpicFullDto>($"/api/projects/{project.Id}/epics/{created.Number}/resume");

        Assert.Equal("running", resumed.Status);
    }

}
