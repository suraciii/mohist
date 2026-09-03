using Mohist.Server.Epic.Grains;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Orleans;
using System.Net;
using System.Net.Http.Json;
using Xunit;
namespace Mohist.Server.Tests.Epic.Api;

[Trait("level", "L1")]
public class EpicPauseResumeApiSpecs : EpicApiTestSupport, IClassFixture<DefaultMohistIntegrationFixture>
{
    public EpicPauseResumeApiSpecs(DefaultMohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Pause_FromRunning_ReturnsPausedStatusAndPersistsReason()
    {
        var project = await CreateProjectAsync($"epic-pause-{Guid.NewGuid():N}");
        var created = await CreateEpicAsync(project.Id, "To pause");
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
        var project = await CreateProjectAsync($"epic-pause-nounbind-{Guid.NewGuid():N}");
        var epic = await CreateEpicAsync(project.Id, "Container");
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
    public async Task Resume_FromPaused_ReturnsRunningStatusAndClearsReason()
    {
        var project = await CreateProjectAsync($"epic-resume-{Guid.NewGuid():N}");
        var created = await CreateEpicAsync(project.Id, "To resume");
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
        var project = await CreateProjectAsync($"epic-paused-done-reject-{Guid.NewGuid():N}");
        var created = await CreateEpicAsync(project.Id, "Paused epic");
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
    public async Task EpicList_IncludesPauseReason()
    {
        var project = await CreateProjectAsync($"epic-list-reason-{Guid.NewGuid():N}");
        await CreateEpicAsync(project.Id, "Idle one");
        var paused = await CreateEpicAsync(project.Id, "Paused one");
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

    private async Task<ProjectDto> CreateProjectAsync(string name)
    {
        var projectId = $"project-{Guid.NewGuid():N}";
        var projectName = name.Length > 63 ? name[..63] : name;
        await _fixture.Grains.GetGrain<IProjectGrain>(projectId).CreateAsync(
            projectName,
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
