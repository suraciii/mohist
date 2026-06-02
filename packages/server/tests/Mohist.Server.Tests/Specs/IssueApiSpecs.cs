using Mohist.Server.Tests.Support;
using Xunit;
using System.Net;
using System.Net.Http.Json;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class IssueApiSpecs
{
    private readonly HttpClient _client;

    public IssueApiSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task Comments_RoundTripThroughIssueDetailShape()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"web-compat-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Commented issue", projectId = project.Id });

        var comment = await _client.PostDataAsync<CommentDto>($"/api/issues/{issue.Number}/comments?projectId={project.Id}", new { body = "Looks good" });
        var detail = await _client.GetDataAsync<IssueDto>($"/api/issues/{issue.Number}?projectId={project.Id}");

        Assert.Equal("Looks good", comment.Body);
        Assert.Contains(detail.Comments, c => c.Id == comment.Id && c.Body == "Looks good");
    }

    [Fact]
    public async Task CreateIssue_WithMultipleProjectsAndNoProjectId_ReturnsBadRequest()
    {
        await _client.PostOkAsync("/api/projects", new { name = $"web-multi-a-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        await _client.PostOkAsync("/api/projects", new { name = $"web-multi-b-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });

        using var response = await _client.PostAsJsonAsync("/api/issues", new { title = "Ambiguous issue" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateIssue_WhenProjectHeaderProvided_UsesHeaderProjectContext()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"web-header-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/issues")
        {
            Content = JsonContent.Create(new { title = "Header scoped issue" }),
        };
        request.Headers.Add("X-Mohist-Project-Id", project.Id);

        using var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var issue = await response.ReadDataAsync<IssueDto>();

        var detail = await _client.GetDataAsync<IssueDto>($"/api/issues/{issue.Number}?projectId={project.Id}");

        Assert.Equal(issue.Id, detail.Id);
    }

    [Fact]
    public async Task CreateEpic_WhenProjectHeaderProvided_UsesHeaderProjectContext()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-header-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/epics")
        {
            Content = JsonContent.Create(new { title = "Header scoped epic", description = "Runtime model", priority = "p2" }),
        };
        request.Headers.Add("X-Mohist-Project-Id", project.Id);

        using var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var epic = await response.ReadDataAsync<EpicDto>();
        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/epics/{epic.Id}?projectId={project.Id}");

        Assert.NotNull(detail);
    }

    [Fact]
    public async Task ListIssues_WithAllAcrossMultipleProjects_ReturnsIssues()
    {
        var firstProject = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"web-list-all-a-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var secondProject = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"web-list-all-b-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var firstIssue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "First listed issue", projectId = firstProject.Id });
        var secondIssue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Second listed issue", projectId = secondProject.Id });

        var issues = await _client.GetDataAsync<IssueDto[]>("/api/issues?all=true");

        Assert.Contains(issues, issue => issue.Id == firstIssue.Id);
        Assert.Contains(issues, issue => issue.Id == secondIssue.Id);
    }

    [Fact]
    public async Task CreateIssue_WithWorkflowProfileId_RoundTripsProfileId()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"web-profile-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });

        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Profile issue", projectId = project.Id, workflowProfileId = "mohist/default" });
        var detail = await _client.GetDataAsync<IssueDto>($"/api/issues/{issue.Number}?projectId={project.Id}");

        Assert.Equal("mohist/default", detail.WorkflowProfileId);
    }

    [Fact]
    public async Task WorkflowProfiles_ReturnDefaultProfileMetadata()
    {
        var profiles = await _client.GetDataAsync<WorkflowProfileDto[]>("/api/workflow-profiles");

        var profile = Assert.Single(profiles);
        Assert.Equal("mohist/default", profile.Id);
        Assert.Equal("Mohist Default", profile.DisplayName);
        Assert.True(profile.IsDefault);
        Assert.Contains("OpenSpec", profile.Description);
    }

    [Fact]
    public async Task Prerequisites_ProjectIntoStartEligibility()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"web-prereq-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var prereq = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Prereq", projectId = project.Id });
        var dependent = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Dependent", projectId = project.Id });

        await _client.PostOkAsync($"/api/issues/{dependent.Number}/prerequisites?projectId={project.Id}", new { prerequisiteNumber = prereq.Number });
        var detail = await _client.GetDataAsync<IssueDto>($"/api/issues/{dependent.Number}?projectId={project.Id}");

        Assert.False(detail.StartEligibility.Startable);
        Assert.Contains(detail.Prerequisites, p => p.Number == prereq.Number && !p.Completed);
    }

    [Fact]
    public async Task StartIssue_WithIncompletePrerequisite_IsRejectedByWorkflowGate()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"web-prereq-gate-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var prereq = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Gate prereq", projectId = project.Id });
        var dependent = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Gate dependent", projectId = project.Id });
        await _client.PostOkAsync($"/api/issues/{dependent.Number}/prerequisites?projectId={project.Id}", new { prerequisiteNumber = prereq.Number });

        using var response = await _client.PostAsync($"/api/issues/{dependent.Number}/start?projectId={project.Id}", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task SystemInfo_ReturnsTypedRuntimePayload()
    {
        var system = await _client.GetDataAsync<SystemInfoDto>("/api/system/info");

        Assert.NotNull(system.Running);
        Assert.NotNull(system.Source);
        Assert.NotNull(system.Install);
        Assert.NotNull(system.Update);
        Assert.NotNull(system.Services);
        Assert.NotNull(system.Paths);
        Assert.False(string.IsNullOrWhiteSpace(system.Running.StartedAt));
        Assert.False(string.IsNullOrWhiteSpace(system.Install.Mode));
    }

    [Fact]
    public async Task SystemUpdateStatus_WhenNoJobExists_ReturnsIdleEnvelope()
    {
        var status = await _client.GetDataAsync<SystemUpdateStatusEnvelopeDto>("/api/system/update/status");

        Assert.False(status.HasJob);
        Assert.Null(status.Job);
    }

    [Fact]
    public async Task ProjectStatus_UsesIssueLifecycleStages()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"web-status-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Lifecycle status issue", projectId = project.Id });

        await _client.PostOkAsync($"/api/issues/{issue.Number}/start?projectId={project.Id}", new { });
        try
        {
            var status = await _client.GetDataAsync<ProjectStatusDto>($"/api/status?projectId={project.Id}");

            Assert.Equal(1, status.Issues);
            Assert.Equal(1, status.IssuesByStatus["in_progress"]);
            Assert.Contains("ready", status.IssuesByStatus.Keys);
            Assert.Contains("cancelled", status.IssuesByStatus.Keys);
            Assert.DoesNotContain("plan", status.IssuesByStatus.Keys);
            Assert.DoesNotContain("build", status.IssuesByStatus.Keys);
            Assert.DoesNotContain("check", status.IssuesByStatus.Keys);
        }
        finally
        {
            using var _ = await _client.PostAsync($"/api/issues/{issue.Number}/stop?projectId={project.Id}", null);
        }
    }

    [Fact]
    public async Task Epics_LinkIssueAndExposePrimaryEpic()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"web-epic-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Epic issue", projectId = project.Id });
        var epic = await _client.PostDataAsync<EpicDto>("/api/epics", new { title = "Runtime model", description = "Ship runtime", priority = "p1", projectId = project.Id });

        await _client.PostOkAsync($"/api/epics/{epic.Id}/issues?projectId={project.Id}", new { issueId = issue.Id });
        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/epics/{epic.Id}?projectId={project.Id}");
        var issueDetail = await _client.GetDataAsync<IssueDto>($"/api/issues/{issue.Number}?projectId={project.Id}");

        Assert.Contains(detail.LinkedIssues, i => i.Id == issue.Id);
        Assert.Equal(epic.Id, issueDetail.PrimaryEpic?.Id);
    }

    private sealed record IssueDto(int Number, string Id, CommentDto[] Comments, PrerequisiteDto[] Prerequisites, StartEligibilityDto StartEligibility, PrimaryEpicDto? PrimaryEpic, string WorkflowProfileId);
    private sealed record WorkflowProfileDto(string Id, string DisplayName, string Description, bool IsDefault);
    private sealed record ProjectDto(string Id);
    private sealed record CommentDto(string Id, string Body);
    private sealed record PrerequisiteDto(int Number, bool Completed);
    private sealed record StartEligibilityDto(bool Startable);
    private sealed record PrimaryEpicDto(string Id, string Title);
    private sealed record ProjectStatusDto(int Issues, Dictionary<string, int> IssuesByStatus);
    private sealed record SystemInfoDto(
        RunningInfoDto Running,
        SourceInfoDto Source,
        InstallInfoDto Install,
        UpdateInfoDto Update,
        ServicesInfoDto Services,
        PathsInfoDto Paths);
    private sealed record RunningInfoDto(string? Version, string? GitHash, string StartedAt);
    private sealed record SourceInfoDto(string? Path, string? Branch, string? Head, bool Dirty);
    private sealed record InstallInfoDto(string Mode, string? ServiceManager, string? ServerUnit, string? RunnerUnit, string? Reason);
    private sealed record UpdateInfoDto(string Status, bool Available, string? Reason);
    private sealed record ServicesInfoDto(string? Server, string? Runner);
    private sealed record PathsInfoDto(string? Db, string? Config, string? Logs, string? Opencode);
    private sealed record SystemUpdateStatusEnvelopeDto(bool HasJob, SystemUpdateStatusDto? Job);
    private sealed record SystemUpdateStatusDto(string JobId, string Status, string Stage);
    private sealed record EpicDto(string Id);
    private sealed record EpicDetailDto(LinkedIssueDto[] LinkedIssues);
    private sealed record LinkedIssueDto(string Id);
}
