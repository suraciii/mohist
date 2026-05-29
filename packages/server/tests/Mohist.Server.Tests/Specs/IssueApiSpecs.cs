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

    [Fact(Skip = "API not yet implemented: /api/log-level, /api/agent-runtime, /api/opencode/runtime, /api/system/info")]
    public async Task GivenUserUpdatesRuntimePreferences_WhenDashboardLoadsSystemSettings_ThenCurrentValuesAreReturned()
    {
        await _client.PutAsJsonOkAsync("/api/log-level", new { level = "DEBUG" });
        await _client.PutAsJsonOkAsync("/api/agent-runtime", new { timeout = 900, maxConcurrent = 5 });

        var logLevel = await _client.GetDataAsync<LogLevelDto>("/api/log-level");
        var runtime = await _client.GetDataAsync<AgentRuntimeDto>("/api/agent-runtime");
        var opencodeRuntime = await _client.GetDataAsync<OpencodeRuntimeDto>("/api/opencode/runtime");
        var system = await _client.GetDataAsync<SystemInfoDto>("/api/system/info");

        Assert.Equal("DEBUG", logLevel.Level);
        Assert.Equal(900, runtime.Timeout);
        Assert.Equal(5, runtime.MaxConcurrent);
        Assert.Equal("local-opencode", opencodeRuntime.Mode);
        Assert.Equal("running", system.Server.Status);
    }

    [Fact]
    public async Task ProjectStatus_UsesIssueLifecycleStages()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"web-status-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Lifecycle status issue", projectId = project.Id });

        await _client.PostOkAsync($"/api/issues/{issue.Number}/start?projectId={project.Id}", new { });
        var status = await _client.GetDataAsync<ProjectStatusDto>($"/api/status?projectId={project.Id}");

        Assert.Equal(1, status.Issues);
        Assert.Equal(1, status.IssuesByStage["in_progress"]);
        Assert.Contains("ready", status.IssuesByStage.Keys);
        Assert.Contains("cancelled", status.IssuesByStage.Keys);
        Assert.DoesNotContain("plan", status.IssuesByStage.Keys);
        Assert.DoesNotContain("build", status.IssuesByStage.Keys);
        Assert.DoesNotContain("check", status.IssuesByStage.Keys);
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
    private sealed record LogLevelDto(string Level);
    private sealed record AgentRuntimeDto(int Timeout, int MaxConcurrent);
    private sealed record ProjectStatusDto(int Issues, Dictionary<string, int> IssuesByStage);
    private sealed record OpencodeRuntimeDto(string Mode, string Command, string? Model);
    private sealed record SystemInfoDto(ServerInfoDto Server);
    private sealed record ServerInfoDto(string Status);
    private sealed record EpicDto(string Id);
    private sealed record EpicDetailDto(LinkedIssueDto[] LinkedIssues);
    private sealed record LinkedIssueDto(string Id);
}
