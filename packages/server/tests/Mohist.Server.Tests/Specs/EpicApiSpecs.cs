using Mohist.Server.Tests.Support;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class EpicApiSpecs
{
    private readonly HttpClient _client;

    public EpicApiSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task CreateEpic_AssignsNextProjectScopedNumber()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-number-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });

        var first = await _client.PostDataAsync<EpicDto>("/api/epics", new { title = "First epic", description = "alpha", priority = "p2", projectId = project.Id });
        var second = await _client.PostDataAsync<EpicDto>("/api/epics", new { title = "Second epic", description = "beta", priority = "p2", projectId = project.Id });
        var third = await _client.PostDataAsync<EpicDto>("/api/epics", new { title = "Third epic", description = "gamma", priority = "p2", projectId = project.Id });

        Assert.Equal(1, first.Number);
        Assert.Equal(2, second.Number);
        Assert.Equal(3, third.Number);
    }

    [Fact]
    public async Task CreateEpic_NumberSequenceIsolatedByProject()
    {
        var firstProject = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-iso-a-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var secondProject = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-iso-b-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });

        var firstA = await _client.PostDataAsync<EpicDto>("/api/epics", new { title = "A1", projectId = firstProject.Id });
        var firstB = await _client.PostDataAsync<EpicDto>("/api/epics", new { title = "B1", projectId = secondProject.Id });
        var secondA = await _client.PostDataAsync<EpicDto>("/api/epics", new { title = "A2", projectId = firstProject.Id });

        Assert.Equal(1, firstA.Number);
        Assert.Equal(1, firstB.Number);
        Assert.Equal(2, secondA.Number);
    }

    [Fact]
    public async Task EpicList_ExposesAssignedNumber()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-list-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        await _client.PostDataAsync<EpicDto>("/api/epics", new { title = "Listed", projectId = project.Id });

        var list = await _client.GetDataAsync<EpicWithProgressDto[]>($"/api/epics?projectId={project.Id}");

        var created = Assert.Single(list);
        Assert.Equal(1, created.Number);
    }

    [Fact]
    public async Task EpicDetail_PreservesIdLookupAndExposesNumber()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-detail-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var created = await _client.PostDataAsync<EpicDto>("/api/epics", new { title = "Detailed", projectId = project.Id });

        var byId = await _client.GetDataAsync<EpicDetailDto>($"/api/epics/{created.Id}?projectId={project.Id}");

        Assert.Equal(created.Id, byId.Id);
        Assert.Equal(1, byId.Number);
    }

    [Fact]
    public async Task IssuePrimaryEpic_ProjectsAssignedNumber()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-issue-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Member issue", projectId = project.Id });
        var epic = await _client.PostDataAsync<EpicDto>("/api/epics", new { title = "Container", projectId = project.Id });

        await _client.PostOkAsync($"/api/epics/{epic.Id}/issues?projectId={project.Id}", new { issueId = issue.Id });
        var issueDetail = await _client.GetDataAsync<IssueDto>($"/api/issues/{issue.Number}?projectId={project.Id}");

        Assert.NotNull(issueDetail.PrimaryEpic);
        Assert.Equal(epic.Id, issueDetail.PrimaryEpic!.Id);
        Assert.Equal(epic.Number, issueDetail.PrimaryEpic!.Number);
    }

    [Fact]
    public async Task EpicLookup_ByNumberRoute_ReturnsDetailShape()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-bynum-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        await _client.PostDataAsync<EpicDto>("/api/epics", new { title = "First", description = "alpha", priority = "p2", projectId = project.Id });
        var second = await _client.PostDataAsync<EpicDto>("/api/epics", new { title = "Second", description = "beta", priority = "p1", projectId = project.Id });

        var byNumber = await _client.GetDataAsync<EpicDetailDto>($"/api/epics/by-number/{second.Number}?projectId={project.Id}");
        var byId = await _client.GetDataAsync<EpicDetailDto>($"/api/epics/{second.Id}?projectId={project.Id}");

        Assert.Equal(second.Id, byNumber.Id);
        Assert.Equal(second.Number, byNumber.Number);
        Assert.Equal(byId.Id, byNumber.Id);
        Assert.Equal(byId.Number, byNumber.Number);
        Assert.Equal(byId.Title, byNumber.Title);
        Assert.Equal(byId.Description, byNumber.Description);
        Assert.Equal(byId.Status, byNumber.Status);
    }

    [Fact]
    public async Task EpicDetailRoute_ResolvesNumericReference()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-numresolve-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var created = await _client.PostDataAsync<EpicDto>("/api/epics", new { title = "Numbered", projectId = project.Id });

        var byNumeric = await _client.GetDataAsync<EpicDetailDto>($"/api/epics/{created.Number}?projectId={project.Id}");

        Assert.Equal(created.Id, byNumeric.Id);
        Assert.Equal(created.Number, byNumeric.Number);
    }

    [Fact]
    public async Task EpicDetailRoute_ContinuesToResolveIdReference()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-idresolve-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var created = await _client.PostDataAsync<EpicDto>("/api/epics", new { title = "Ided", projectId = project.Id });

        var byId = await _client.GetDataAsync<EpicDetailDto>($"/api/epics/{created.Id}?projectId={project.Id}");

        Assert.Equal(created.Id, byId.Id);
        Assert.Equal(created.Number, byId.Number);
    }

    [Fact]
    public async Task EpicLookup_ByNumberRoute_UnknownNumberReturnsNotFoundEnvelope()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-bynum-missing-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        await _client.PostDataAsync<EpicDto>("/api/epics", new { title = "Only", projectId = project.Id });

        using var byNumber = await _client.GetAsync($"/api/epics/by-number/9999?projectId={project.Id}");
        using var byDetail = await _client.GetAsync($"/api/epics/9999?projectId={project.Id}");

        Assert.Equal(HttpStatusCode.NotFound, byNumber.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byDetail.StatusCode);

        var byNumberEnvelope = await byNumber.Content.ReadFromJsonAsync<NotFoundEnvelope>();
        var byDetailEnvelope = await byDetail.Content.ReadFromJsonAsync<NotFoundEnvelope>();
        Assert.NotNull(byNumberEnvelope);
        Assert.False(byNumberEnvelope!.Success);
        Assert.Equal("not_found", byNumberEnvelope.Code);
        Assert.NotNull(byDetailEnvelope);
        Assert.False(byDetailEnvelope!.Success);
        Assert.Equal("not_found", byDetailEnvelope.Code);
    }

    [Fact]
    public async Task EpicPatch_UpdatesTitle()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-patch-title-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var created = await _client.PostDataAsync<EpicDto>("/api/epics", new { title = "Original title", description = "Original body", priority = "p2", projectId = project.Id });

        var patched = await _client.PatchDataAsync<EpicDto>($"/api/epics/{created.Id}?projectId={project.Id}", new { title = "Renamed" });

        Assert.Equal(created.Id, patched.Id);
        Assert.Equal("Renamed", patched.Title);
    }

    [Fact]
    public async Task EpicPatch_UpdatesDescription()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-patch-desc-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var created = await _client.PostDataAsync<EpicDto>("/api/epics", new { title = "Titled", description = "before", priority = "p2", projectId = project.Id });

        var patched = await _client.PatchDataAsync<EpicDto>($"/api/epics/{created.Id}?projectId={project.Id}", new { description = "after" });

        Assert.Equal(created.Id, patched.Id);
        Assert.Equal("after", patched.Description);
    }

    [Fact]
    public async Task EpicPatch_UpdatesPriority()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-patch-pri-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var created = await _client.PostDataAsync<EpicDto>("/api/epics", new { title = "Titled", description = "body", priority = "p2", projectId = project.Id });

        var patched = await _client.PatchDataAsync<EpicDto>($"/api/epics/{created.Id}?projectId={project.Id}", new { priority = "p1" });

        Assert.Equal(created.Id, patched.Id);
        Assert.Equal("p1", patched.Priority);
    }

    [Fact]
    public async Task EpicPatch_AdvancesUpdatedAt()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-patch-updated-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var created = await _client.PostDataAsync<EpicDto>("/api/epics", new { title = "Titled", description = "body", priority = "p2", projectId = project.Id });

        var before = DateTimeOffset.Parse(created.UpdatedAt);
        await Task.Delay(15);

        var patched = await _client.PatchDataAsync<EpicDto>($"/api/epics/{created.Id}?projectId={project.Id}", new { title = "Renamed" });

        var after = DateTimeOffset.Parse(patched.UpdatedAt);
        Assert.True(after > before, $"Expected UpdatedAt to advance. Before={before:O}, After={after:O}");
    }

    [Fact]
    public async Task EpicPatch_PreservesStatus()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-patch-status-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var created = await _client.PostDataAsync<EpicDto>("/api/epics", new { title = "Titled", description = "body", priority = "p2", projectId = project.Id });
        Assert.Equal("active", created.Status);

        var patched = await _client.PatchDataAsync<EpicDto>($"/api/epics/{created.Id}?projectId={project.Id}", new { title = "Renamed", description = "new body", priority = "p0" });

        Assert.Equal("active", patched.Status);
    }

    [Fact]
    public async Task EpicPatch_PreservesLinkedIssueMembership()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-patch-mem-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var epic = await _client.PostDataAsync<EpicDto>("/api/epics", new { title = "Container", description = "body", priority = "p2", projectId = project.Id });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Member", projectId = project.Id });
        await _client.PostOkAsync($"/api/epics/{epic.Id}/issues?projectId={project.Id}", new { issueId = issue.Id });

        var patched = await _client.PatchDataAsync<EpicDto>($"/api/epics/{epic.Id}?projectId={project.Id}", new { title = "Renamed", description = "after", priority = "p1" });

        Assert.Equal("Renamed", patched.Title);
        Assert.Equal("after", patched.Description);
        Assert.Equal("p1", patched.Priority);

        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/epics/{epic.Id}?projectId={project.Id}");
        Assert.Single(detail.LinkedIssues);
        Assert.Equal(issue.Id, detail.LinkedIssues[0].Id);
    }

    [Fact]
    public async Task EpicPatch_UnknownEpicReturnsNotFound()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-patch-404-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });

        using var response = await _client.PatchAsJsonAsync($"/api/epics/epic_{Guid.NewGuid():N}?projectId={project.Id}", new { title = "X" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EpicPatch_PartialUpdate_LeavesUnspecifiedFieldsUnchanged()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-patch-partial-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var created = await _client.PostDataAsync<EpicDto>("/api/epics", new { title = "Original", description = "Original body", priority = "p2", projectId = project.Id });

        var patched = await _client.PatchDataAsync<EpicDto>($"/api/epics/{created.Id}?projectId={project.Id}", new { title = "Renamed" });

        Assert.Equal("Renamed", patched.Title);
        Assert.Equal("Original body", patched.Description);
        Assert.Equal("p2", patched.Priority);
    }

    private sealed record ProjectDto(string Id);
    private sealed record EpicDto(string Id, int? Number, string Title, string Description, string Priority, string Status, string CreatedAt, string UpdatedAt);
    private sealed record EpicWithProgressDto(string Id, int? Number);
    private sealed record EpicDetailDto(string Id, int? Number, string Title, string Description, string Status, LinkedIssueDto[] LinkedIssues);
    private sealed record LinkedIssueDto(string Id);
    private sealed record IssueDto(int Number, string Id, PrimaryEpicDto? PrimaryEpic);
    private sealed record PrimaryEpicDto(string Id, int? Number, string Title);
    private sealed record NotFoundEnvelope(bool Success, string? Code = null, string? Error = null);
}
