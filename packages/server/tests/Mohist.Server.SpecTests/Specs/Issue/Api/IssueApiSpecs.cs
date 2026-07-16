using Mohist.Server.SpecTests.Support;
using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

[Collection("IntegrationIssue")]
public class IssueApiSpecs
{
    private readonly HttpClient _client;

    public IssueApiSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Comments_RoundTripThroughIssueDetailShape()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"web-compat-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Commented issue", projectId = project.Id });

        var comment = await _client.PostDataAsync<CommentDto>($"/api/projects/{project.Id}/issues/{issue.Number}/comments", new { body = "Looks good" });
        var detail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");

        Assert.Equal("Looks good", comment.Body);
        Assert.Contains(detail.Comments, c => c.Id == comment.Id && c.Body == "Looks good");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CreateIssue_OnLegacyCollectionRoute_ReturnsNotFound()
    {
        var projectA = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"web-multi-a-{Guid.NewGuid():N}");
        var projectB = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"web-multi-b-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{projectA.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        await _client.PostOkAsync($"/api/projects/{projectB.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });

        using var response = await _client.PostAsJsonAsync("/api/issues", new { title = "Ambiguous issue" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CreateIssue_OnProjectRoute_UsesRouteProjectContext()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"web-header-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });

        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Project scoped issue" });

        var detail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");

        Assert.Equal(issue.Id, detail.Id);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CreateEpic_OnProjectRoute_UsesRouteProjectContext()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-header-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });

        var epic = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Project scoped epic", description = "Runtime model", priority = "p2" });
        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Id}");

        Assert.NotNull(detail);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ListIssues_ReturnsOnlyIssuesInRouteProject()
    {
        var firstProject = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"web-list-all-a-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{firstProject.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var secondProject = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"web-list-all-b-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{secondProject.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var firstIssue = await _client.PostDataAsync<IssueDto>($"/api/projects/{firstProject.Id}/issues", new { title = "First listed issue" });
        var secondIssue = await _client.PostDataAsync<IssueDto>($"/api/projects/{secondProject.Id}/issues", new { title = "Second listed issue" });

        var issues = await _client.GetDataAsync<IssueDto[]>($"/api/projects/{firstProject.Id}/issues?all=true");

        Assert.Contains(issues, issue => issue.Id == firstIssue.Id);
        Assert.DoesNotContain(issues, issue => issue.Id == secondIssue.Id);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CreateIssue_WithWorkflowProfileId_RoundTripsProfileId()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"web-profile-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });

        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Profile issue", projectId = project.Id, workflowProfileId = "mohist/local" });
        var detail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");

        Assert.Equal("mohist/local", detail.WorkflowProfileId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task SystemWorkflowTemplates_ReturnDefaultTemplateMetadata()
    {
        var profiles = await _client.GetDataAsync<WorkflowProfileDto[]>("/api/workflow-templates/system");

        var defaultProfile = Assert.Single(profiles, p => p.Id == "mohist/local");
        Assert.Equal("Mohist Local", defaultProfile.Name);
        Assert.Contains("Mohist pipeline", defaultProfile.Description);

        var prProfile = Assert.Single(profiles, p => p.Id == "mohist/github-pr");
        Assert.Equal("Mohist GitHub PR", prProfile.Name);
        Assert.Contains("Mohist pipeline", prProfile.Description);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task WorkflowProfilesEndpoint_ReturnsIdDisplayNameDescriptionWithoutSuitableFor()
    {
        var profiles = await _client.GetDataAsync<WorkflowProfileDescriptionDto[]>("/api/workflow-profiles");

        Assert.Equal(2, profiles.Length);

        var defaultProfile = Assert.Single(profiles, p => p.Id == "mohist/local");
        Assert.Equal("Mohist Local", defaultProfile.DisplayName);
        Assert.Contains("Mohist pipeline", defaultProfile.Description);

        var prProfile = Assert.Single(profiles, p => p.Id == "mohist/github-pr");
        Assert.Equal("Mohist GitHub PR", prProfile.DisplayName);
        Assert.Contains("Mohist pipeline", prProfile.Description);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task WorkflowProfilesEndpoint_ResponsePayloadHasNoSuitableForField()
    {
        var raw = await _client.GetStringAsync("/api/workflow-profiles");

        using var document = JsonDocument.Parse(raw);
        Assert.True(document.RootElement.TryGetProperty("data", out var data));
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.All(data.EnumerateArray(), element =>
        {
            Assert.True(element.TryGetProperty("id", out _));
            Assert.True(element.TryGetProperty("displayName", out _));
            Assert.True(element.TryGetProperty("description", out _));
            Assert.False(element.TryGetProperty("suitableFor", out _),
                "workflow-profiles response must not serialize a suitableFor field");
        });
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Prerequisites_ProjectIntoBlocker()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"web-prereq-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var prereq = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Prereq", projectId = project.Id, isDraft = false });
        var dependent = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Dependent", projectId = project.Id, isDraft = false });

        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{dependent.Number}/prerequisites", new { prerequisiteNumber = prereq.Number });
        var detail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{dependent.Number}");

        Assert.False(detail.CanStart);
        Assert.NotNull(detail.Blocker);
        Assert.Equal("waiting-for", detail.Blocker!.Kind);
        Assert.Equal(prereq.Number, detail.Blocker.Issue!.Number);
        Assert.Contains(detail.Prerequisites, p => p.Number == prereq.Number && !p.Completed);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task StartIssue_WithIncompletePrerequisite_IsRejectedByWorkflowGate()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"web-prereq-gate-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var prereq = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Gate prereq", projectId = project.Id, isDraft = false });
        var dependent = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Gate dependent", projectId = project.Id, isDraft = false });
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{dependent.Number}/prerequisites", new { prerequisiteNumber = prereq.Number });

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{dependent.Number}/start", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task SystemUpdateStatus_WhenNoJobExists_ReturnsIdleEnvelope()
    {
        var status = await _client.GetDataAsync<SystemUpdateStatusEnvelopeDto>("/api/system/update/status");

        Assert.False(status.HasJob);
        Assert.Null(status.Job);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ProjectStatus_UsesIssueLifecycleStages()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"web-status-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Lifecycle status issue", projectId = project.Id, isDraft = false });

        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start", new { });
        try
        {
            var status = await _client.GetDataAsync<ProjectStatusDto>($"/api/projects/{project.Id}/status");

            Assert.Equal(1, status.Issues);
            Assert.Equal(1, status.IssuesByStatus["in_progress"]);
            Assert.Contains("cancelled", status.IssuesByStatus.Keys);
            Assert.DoesNotContain("plan", status.IssuesByStatus.Keys);
            Assert.DoesNotContain("build", status.IssuesByStatus.Keys);
            Assert.DoesNotContain("check", status.IssuesByStatus.Keys);
        }
        finally
        {
            using var _ = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/stop", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Epics_LinkIssueAndExposePrimaryEpic()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"web-epic-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Epic issue", projectId = project.Id });
        var epic = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Runtime model", description = "Ship runtime", priority = "p1", projectId = project.Id });

        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{epic.Id}/issues", new { issueId = issue.Id });
        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Id}");
        var issueDetail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");

        Assert.Contains(detail.LinkedIssues, i => i.Id == issue.Id);
        Assert.Equal(epic.Id, issueDetail.PrimaryEpic?.Id);
    }

    private sealed record IssueDto(int Number, string Id, CommentDto[] Comments, PrerequisiteDto[] Prerequisites, bool IsDraft, bool CanStart, BlockerDto? Blocker, PrimaryEpicDto? PrimaryEpic, string WorkflowProfileId);
    private sealed record WorkflowProfileDto(string Id, string Name, string Description);
    private sealed record WorkflowProfileDescriptionDto(string Id, string DisplayName, string Description);
    private sealed record ProjectDto(string Id);
    private sealed record CommentDto(string Id, string Body);
    private sealed record PrerequisiteDto(int Number, bool Completed);
    private sealed record BlockerDto(string Kind, BlockerIssueDto? Issue);
    private sealed record BlockerIssueDto(int Number, string Title);
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
