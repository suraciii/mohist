using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
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

    [Fact]
    public async Task Comments_RoundTripThroughIssueDetailShape()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"web-compat-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Commented issue", projectId = project.Id });

        var comment = await _client.PostDataAsync<CommentDto>($"/api/projects/{project.Id}/issues/{issue.Number}/comments", new { author = "  Ada Lovelace  ", body = "Looks good" });
        var detail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");

        Assert.Equal("Looks good", comment.Body);
        Assert.Equal("Ada Lovelace", comment.Author);
        Assert.Contains(detail.Comments, c => c.Id == comment.Id && c.Body == "Looks good" && c.Author == "Ada Lovelace");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public async Task AddComment_InvalidAuthor_ReturnsActionableValidationWithoutCreatingRow(string author)
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"comment-author-{Guid.NewGuid():N}");
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Comment validation" });

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/comments",
            new { author, body = "Not persisted" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(author.Length > 100 ? "100" : "required", error.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        var detail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.Empty(detail.Comments);
    }

    [Fact]
    public async Task AddComment_MissingAuthor_ReturnsActionableValidationWithoutCreatingRow()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"comment-author-missing-{Guid.NewGuid():N}");
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Missing author" });

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/comments",
            new { body = "Not persisted" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("author", error.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        var detail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.Empty(detail.Comments);
    }

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

    [Fact]
    public async Task CreateIssue_OnProjectRoute_UsesRouteProjectContext()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"web-header-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });

        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Project scoped issue" });

        var detail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");

        Assert.Equal(issue.Number, detail.Number);
    }

    [Fact]
    public async Task UpdateIssue_RiskOnly_PersistsAndReturnsRisk()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"risk-update-{Guid.NewGuid():N}");
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Risk update", projectId = project.Id });

        var updated = await _client.PatchDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues/{issue.Number}",
            new { risk = "high" });

        Assert.Equal("high", updated.Risk);
    }

    [Fact]
    public async Task CreateEpic_OnProjectRoute_UsesRouteProjectContext()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-header-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });

        var epic = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Project scoped epic", description = "Runtime model", priority = "p2" });
        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Number}");

        Assert.NotNull(detail);
    }

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

        var listed = Assert.Single(issues);
        Assert.Equal(firstIssue.Number, listed.Number);
    }

    [Fact]
    public async Task CreateIssue_WithWorkflowProfileId_RoundTripsProfileId()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"web-profile-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });

        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Profile issue", projectId = project.Id, workflowProfileId = "mohist/local" });
        var detail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");

        Assert.Equal("mohist/local", detail.WorkflowProfileId);
    }

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
        Assert.Contains(detail.Prereq, p => p.Number == prereq.Number && !p.Completed);
    }

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

    [Fact]
    public async Task Epics_LinkIssueAndExposePrimaryEpic()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"web-epic-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Epic issue", projectId = project.Id });
        var epic = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Runtime model", description = "Ship runtime", priority = "p1", projectId = project.Id });

        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{epic.Number}/issues", new { issueNumber = issue.Number });
        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Number}");
        var issueDetail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");

        Assert.Contains(detail.LinkedIssues, i => i.Number == issue.Number);
        Assert.Equal(epic.Number, issueDetail.Epic?.Number);
    }

    private sealed record IssueDto(int Number, CommentDto[] Comments, PrerequisiteDto[] Prereq, bool IsDraft, bool CanStart, BlockerDto? Blocker, IssueEpicDto? Epic, string WorkflowProfileId, string? Risk = null);
    private sealed record WorkflowProfileDto(string Id, string Name, string Description);
    private sealed record WorkflowProfileDescriptionDto(string Id, string DisplayName, string Description);
    private sealed record ProjectDto(string Id);
    private sealed record CommentDto(string Id, string Body, string? Author);
    private sealed record PrerequisiteDto(int Number, bool Completed);
    private sealed record BlockerDto(string Kind, BlockerIssueDto? Issue);
    private sealed record BlockerIssueDto(int Number, string Title);
    private sealed record IssueEpicDto(int Number, string Title);
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
    private sealed record EpicDto(int Number);
    private sealed record EpicDetailDto(LinkedIssueDto[] LinkedIssues);
    private sealed record LinkedIssueDto(int Number);
}
