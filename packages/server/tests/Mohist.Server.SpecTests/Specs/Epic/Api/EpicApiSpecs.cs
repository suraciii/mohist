using Mohist.Server.Epic.Grains;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.SpecTests.Support;
using Orleans;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Epic.Api;

[Collection("MohistIntegration")]
public class EpicApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private readonly IGrainFactory _grains;

    public EpicApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
        _grains = fixture.Grains;
    }

    private async Task StartEpicAsync(string projectId, EpicDto epic)
    {
        var grain = _grains.GetGrain<IEpicGrain>(GrainKey.Epic(new EpicKey(projectId, epic.Number)));
        await grain.StartAsync();
    }

    private async Task AddOpenIssueAsync(string projectId, EpicDto epic)
    {
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{projectId}/issues",
            new { title = "Open work", projectId, isDraft = false });
        await _client.PostOkAsync(
            $"/api/projects/{projectId}/epics/{epic.Number}/issues",
            new { issueNumber = issue.Number });
    }

    private async Task CompleteIssueAsync(string projectId, IssueDto issueInfo)
    {
        var grain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueInfo.Number)));
        var wrId = await grain.StartWorkAsync(new WorkflowProjectContext(projectId, "Epic API Test", RepositoryBaseBranch: "main"));
        await grain.CompleteWorkAsync(wrId);
    }

    [Fact]
    public async Task CreateEpic_AssignsNextProjectScopedNumber()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-number-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });

        var first = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "First epic", description = "alpha", priority = "p2", projectId = project.Id });
        var second = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Second epic", description = "beta", priority = "p2", projectId = project.Id });
        var third = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Third epic", description = "gamma", priority = "p2", projectId = project.Id });

        Assert.Equal(1, first.Number);
        Assert.Equal(2, second.Number);
        Assert.Equal(3, third.Number);
    }

    [Fact]
    public async Task CreateEpic_NumberSequenceIsolatedByProject()
    {
        var firstProject = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-iso-a-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{firstProject.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var secondProject = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-iso-b-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{secondProject.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });

        var firstA = await _client.PostDataAsync<EpicDto>($"/api/projects/{firstProject.Id}/epics", new { title = "A1", projectId = firstProject.Id });
        var firstB = await _client.PostDataAsync<EpicDto>($"/api/projects/{secondProject.Id}/epics", new { title = "B1", projectId = secondProject.Id });
        var secondA = await _client.PostDataAsync<EpicDto>($"/api/projects/{firstProject.Id}/epics", new { title = "A2", projectId = firstProject.Id });

        Assert.Equal(1, firstA.Number);
        Assert.Equal(1, firstB.Number);
        Assert.Equal(2, secondA.Number);
    }

    [Fact]
    public async Task EpicList_ExposesAssignedNumber()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-list-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Listed", projectId = project.Id });

        var list = await _client.GetDataAsync<EpicWithProgressDto[]>($"/api/projects/{project.Id}/epics");

        var created = Assert.Single(list);
        Assert.Equal(1, created.Number);
    }

    [Fact]
    public async Task EpicList_OrdersByPriorityWithinStatusGroup()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-order-pri-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var higherPriority = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Should be first (p0)", description = "alpha", priority = "p2", projectId = project.Id });
        var lowerPriority = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Should be second (p2)", description = "beta", priority = "p2", projectId = project.Id });

        await _client.PatchDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{higherPriority.Number}", new { priority = "p0" });

        var list = await _client.GetDataAsync<EpicWithProgressDto[]>($"/api/projects/{project.Id}/epics");

        Assert.Equal(2, list.Length);
        Assert.Equal(higherPriority.Number, list[0].Number);
        Assert.Equal("p0", list[0].Priority);
        Assert.Equal(lowerPriority.Number, list[1].Number);
        Assert.Equal("p2", list[1].Priority);
    }

    [Fact]
    public async Task EpicList_OrdersByRecentUpdatedAtWhenPrioritiesMatch()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-order-upd-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var older = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Older (p2)", description = "alpha", priority = "p2", projectId = project.Id });
        var newer = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Newer (p2)", description = "beta", priority = "p2", projectId = project.Id });

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        var updatedNewer = await _client.PatchDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{newer.Number}", new { title = "Newer (renamed)" });

        var list = await _client.GetDataAsync<EpicWithProgressDto[]>($"/api/projects/{project.Id}/epics");

        Assert.Equal(2, list.Length);
        Assert.Equal(newer.Number, list[0].Number);
        Assert.Equal(older.Number, list[1].Number);
        Assert.True(
            DateTimeOffset.Parse(list[0].UpdatedAt) > DateTimeOffset.Parse(list[1].UpdatedAt),
            $"Expected newer epic UpdatedAt to be more recent. List[0]={list[0].UpdatedAt}, List[1]={list[1].UpdatedAt}");
        Assert.True(DateTimeOffset.Parse(updatedNewer.UpdatedAt) > DateTimeOffset.Parse(older.UpdatedAt));
    }

    [Fact]
    public async Task EpicList_ReturnsOrderedArraySoConsumerCanRenderInServerSuppliedOrder()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-order-arr-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var p2CreatedFirst = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Created first but p2", description = "alpha", priority = "p2", projectId = project.Id });
        var p0CreatedLater = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Created later but p0", description = "beta", priority = "p2", projectId = project.Id });

        await _client.PatchDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{p0CreatedLater.Number}", new { priority = "p0" });

        var list = await _client.GetDataAsync<EpicWithProgressDto[]>($"/api/projects/{project.Id}/epics");

        Assert.Equal(2, list.Length);
        Assert.Equal(p0CreatedLater.Number, list[0].Number);
        Assert.Equal(p2CreatedFirst.Number, list[1].Number);
    }

    [Fact]
    public async Task EpicDetail_UsesNumberLookup()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-detail-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Detailed", projectId = project.Id });

        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{created.Number}");

        Assert.Equal(created.Number, detail.Number);
    }

    [Fact]
    public async Task IssuePrimaryEpic_ProjectsAssignedNumber()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-issue-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Member issue", projectId = project.Id });
        var epic = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Container", projectId = project.Id });

        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{epic.Number}/issues", new { issueNumber = issue.Number });
        var issueDetail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");

        Assert.NotNull(issueDetail.PrimaryEpic);
        Assert.Equal(epic.Number, issueDetail.PrimaryEpic!.Number);
    }

    [Fact]
    public async Task EpicLookup_ByNumberRoute_ReturnsDetailShape()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-bynum-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "First", description = "alpha", priority = "p2", projectId = project.Id });
        var second = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Second", description = "beta", priority = "p1", projectId = project.Id });

        var byNumber = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{second.Number}");

        Assert.Equal(second.Number, byNumber.Number);
        Assert.Equal(second.Title, byNumber.Title);
        Assert.Equal(second.Description, byNumber.Description);
        Assert.Equal(second.Status, byNumber.Status);
    }

    [Fact]
    public async Task EpicDetailRoute_ResolvesNumericReference()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-numresolve-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Numbered", projectId = project.Id });

        var byNumeric = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{created.Number}");

        Assert.Equal(created.Number, byNumeric.Number);
    }

    [Fact]
    public async Task EpicLookup_ByNumberRoute_UnknownNumberReturnsNotFoundEnvelope()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-bynum-missing-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Only", projectId = project.Id });

        using var byNumber = await _client.GetAsync($"/api/projects/{project.Id}/epics/9999");
        using var byDetail = await _client.GetAsync($"/api/projects/{project.Id}/epics/9999");

        Assert.Equal(HttpStatusCode.NotFound, byNumber.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byDetail.StatusCode);

        var byDetailEnvelope = await byDetail.Content.ReadFromJsonAsync<NotFoundEnvelope>();
        Assert.NotNull(byDetailEnvelope);
        Assert.False(byDetailEnvelope!.Success);
        Assert.Equal("not_found", byDetailEnvelope.Code);
    }

    [Fact]
    public async Task EpicPatch_UpdatesTitle()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-patch-title-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Original title", description = "Original body", priority = "p2", projectId = project.Id });

        var patched = await _client.PatchDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{created.Number}", new { title = "Renamed" });

        Assert.Equal(created.Number, patched.Number);
        Assert.Equal("Renamed", patched.Title);
    }

    [Fact]
    public async Task EpicPatch_UpdatesDescription()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-patch-desc-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Titled", description = "before", priority = "p2", projectId = project.Id });

        var patched = await _client.PatchDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{created.Number}", new { description = "after" });

        Assert.Equal(created.Number, patched.Number);
        Assert.Equal("after", patched.Description);
    }

    [Fact]
    public async Task EpicPatch_UpdatesPriority()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-patch-pri-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Titled", description = "body", priority = "p2", projectId = project.Id });

        var patched = await _client.PatchDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{created.Number}", new { priority = "p1" });

        Assert.Equal(created.Number, patched.Number);
        Assert.Equal("p1", patched.Priority);
    }

    [Fact]
    public async Task EpicPatch_AdvancesUpdatedAt()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-patch-updated-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Titled", description = "body", priority = "p2", projectId = project.Id });

        var before = DateTimeOffset.Parse(created.UpdatedAt);
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));

        var patched = await _client.PatchDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{created.Number}", new { title = "Renamed" });

        var after = DateTimeOffset.Parse(patched.UpdatedAt);
        Assert.True(after > before, $"Expected UpdatedAt to advance. Before={before:O}, After={after:O}");
    }

    [Fact]
    public async Task EpicPatch_PreservesStatus()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-patch-status-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Titled", description = "body", priority = "p2", projectId = project.Id });
        Assert.Equal("idle", created.Status);

        var patched = await _client.PatchDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{created.Number}", new { title = "Renamed", description = "new body", priority = "p0" });

        Assert.Equal("idle", patched.Status);
    }

    [Fact]
    public async Task EpicPatch_PreservesLinkedIssueMembership()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-patch-mem-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var epic = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Container", description = "body", priority = "p2", projectId = project.Id });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Member", projectId = project.Id });
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{epic.Number}/issues", new { issueNumber = issue.Number });

        var patched = await _client.PatchDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{epic.Number}", new { title = "Renamed", description = "after", priority = "p1" });

        Assert.Equal("Renamed", patched.Title);
        Assert.Equal("after", patched.Description);
        Assert.Equal("p1", patched.Priority);

        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Number}");
        Assert.Single(detail.LinkedIssues);
        Assert.Equal(issue.Number, detail.LinkedIssues[0].Number);
    }

    [Fact]
    public async Task EpicPatch_UnknownEpicReturnsNotFound()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-patch-404-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });

        using var response = await _client.PatchAsJsonAsync($"/api/projects/{project.Id}/epics/9999", new { title = "X" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EpicPatch_PartialUpdate_LeavesUnspecifiedFieldsUnchanged()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-patch-partial-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Original", description = "Original body", priority = "p2", projectId = project.Id });

        var patched = await _client.PatchDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{created.Number}", new { title = "Renamed" });

        Assert.Equal("Renamed", patched.Title);
        Assert.Equal("Original body", patched.Description);
        Assert.Equal("p2", patched.Priority);
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
        // The dispatcher's auto-mark-done handler recomputes the epic when
        // every linked issue is terminal; wait for that eventual transition
        // instead of racing it with an explicit /done call.
        await _client.WaitForStatusAsync<EpicDetailFullDto>(
            $"/api/projects/{project.Id}/epics/{created.Number}",
            dto => dto.Status,
            "done");

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
        await _client.WaitForStatusAsync<EpicDetailFullDto>(
            $"/api/projects/{project.Id}/epics/{created.Number}",
            dto => dto.Status,
            "done");

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
        await _client.WaitForStatusAsync<EpicDetailFullDto>(
            $"/api/projects/{project.Id}/epics/{created.Number}",
            dto => dto.Status,
            "done");

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

    private sealed record ProjectDto(string Id);
    private sealed record EpicDto(int Number, string Title, string Description, string Priority, string Status, string CreatedAt, string UpdatedAt);
    private sealed record EpicFullDto(int Number, string Title, string Description, string Priority, string Status, string CreatedAt, string UpdatedAt, string? PauseReason);
    private sealed record EpicWithProgressDto(int Number, string Priority, string UpdatedAt);
    private sealed record EpicWithProgressFullDto(int Number, string Status, string? PauseReason);
    private sealed record EpicDetailDto(int Number, string Title, string Description, string Status, LinkedIssueDto[] LinkedIssues);
    private sealed record EpicDetailFullDto(int Number, string Status, string? PauseReason, LinkedIssueDto[] LinkedIssues);
    private sealed record LinkedIssueDto(int Number);
    private sealed record IssueDto(int Number, PrimaryEpicDto? PrimaryEpic);
    private sealed record PrimaryEpicDto(int Number, string Title);
    private sealed record NotFoundEnvelope(bool Success, string? Code = null, string? Error = null);
    private sealed record ConflictEnvelope(bool Success, string? Code = null, string? Error = null, ConflictDetails? Details = null);
    private sealed record ConflictDetails(string CurrentStatus, string? RequestedStatus = null);
}
