using Mohist.Server.Tests.Support;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Mohist.Server.Tests.Specs.Epic.Api;

[Collection("MohistIntegration")]
public class EpicApiSpecs
{
    private readonly HttpClient _client;

    public EpicApiSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task CreateEpic_AssignsNextProjectScopedNumber()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-number-{Guid.NewGuid():N}" });

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });

        var first = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "First epic", description = "alpha", priority = "p2", projectId = project.Id });
        var second = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Second epic", description = "beta", priority = "p2", projectId = project.Id });
        var third = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Third epic", description = "gamma", priority = "p2", projectId = project.Id });

        Assert.Equal(1, first.Number);
        Assert.Equal(2, second.Number);
        Assert.Equal(3, third.Number);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task CreateEpic_NumberSequenceIsolatedByProject()
    {
        var firstProject = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-iso-a-{Guid.NewGuid():N}" });

        await _client.PostOkAsync($"/api/projects/{firstProject.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var secondProject = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-iso-b-{Guid.NewGuid():N}" });

        await _client.PostOkAsync($"/api/projects/{secondProject.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });

        var firstA = await _client.PostDataAsync<EpicDto>($"/api/projects/{firstProject.Id}/epics", new { title = "A1", projectId = firstProject.Id });
        var firstB = await _client.PostDataAsync<EpicDto>($"/api/projects/{secondProject.Id}/epics", new { title = "B1", projectId = secondProject.Id });
        var secondA = await _client.PostDataAsync<EpicDto>($"/api/projects/{firstProject.Id}/epics", new { title = "A2", projectId = firstProject.Id });

        Assert.Equal(1, firstA.Number);
        Assert.Equal(1, firstB.Number);
        Assert.Equal(2, secondA.Number);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task EpicList_ExposesAssignedNumber()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-list-{Guid.NewGuid():N}" });

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Listed", projectId = project.Id });

        var list = await _client.GetDataAsync<EpicWithProgressDto[]>($"/api/projects/{project.Id}/epics");

        var created = Assert.Single(list);
        Assert.Equal(1, created.Number);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task EpicDetail_PreservesIdLookupAndExposesNumber()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-detail-{Guid.NewGuid():N}" });

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Detailed", projectId = project.Id });

        var byId = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{created.Id}");

        Assert.Equal(created.Id, byId.Id);
        Assert.Equal(1, byId.Number);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task IssuePrimaryEpic_ProjectsAssignedNumber()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-issue-{Guid.NewGuid():N}" });

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Member issue", projectId = project.Id });
        var epic = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Container", projectId = project.Id });

        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{epic.Id}/issues", new { issueId = issue.Id });
        var issueDetail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");

        Assert.NotNull(issueDetail.PrimaryEpic);
        Assert.Equal(epic.Id, issueDetail.PrimaryEpic!.Id);
        Assert.Equal(epic.Number, issueDetail.PrimaryEpic!.Number);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task EpicLookup_ByNumberRoute_ReturnsDetailShape()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-bynum-{Guid.NewGuid():N}" });

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "First", description = "alpha", priority = "p2", projectId = project.Id });
        var second = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Second", description = "beta", priority = "p1", projectId = project.Id });

        var byNumber = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{second.Number}");
        var byId = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{second.Id}");

        Assert.Equal(second.Id, byNumber.Id);
        Assert.Equal(second.Number, byNumber.Number);
        Assert.Equal(byId.Id, byNumber.Id);
        Assert.Equal(byId.Number, byNumber.Number);
        Assert.Equal(byId.Title, byNumber.Title);
        Assert.Equal(byId.Description, byNumber.Description);
        Assert.Equal(byId.Status, byNumber.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task EpicDetailRoute_ResolvesNumericReference()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-numresolve-{Guid.NewGuid():N}" });

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Numbered", projectId = project.Id });

        var byNumeric = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{created.Number}");

        Assert.Equal(created.Id, byNumeric.Id);
        Assert.Equal(created.Number, byNumeric.Number);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task EpicDetailRoute_ContinuesToResolveIdReference()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-idresolve-{Guid.NewGuid():N}" });

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Ided", projectId = project.Id });

        var byId = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{created.Id}");

        Assert.Equal(created.Id, byId.Id);
        Assert.Equal(created.Number, byId.Number);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task EpicLookup_ByNumberRoute_UnknownNumberReturnsNotFoundEnvelope()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-bynum-missing-{Guid.NewGuid():N}" });

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task EpicPatch_UpdatesTitle()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-patch-title-{Guid.NewGuid():N}" });

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Original title", description = "Original body", priority = "p2", projectId = project.Id });

        var patched = await _client.PatchDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{created.Id}", new { title = "Renamed" });

        Assert.Equal(created.Id, patched.Id);
        Assert.Equal("Renamed", patched.Title);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task EpicPatch_UpdatesDescription()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-patch-desc-{Guid.NewGuid():N}" });

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Titled", description = "before", priority = "p2", projectId = project.Id });

        var patched = await _client.PatchDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{created.Id}", new { description = "after" });

        Assert.Equal(created.Id, patched.Id);
        Assert.Equal("after", patched.Description);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task EpicPatch_UpdatesPriority()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-patch-pri-{Guid.NewGuid():N}" });

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Titled", description = "body", priority = "p2", projectId = project.Id });

        var patched = await _client.PatchDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{created.Id}", new { priority = "p1" });

        Assert.Equal(created.Id, patched.Id);
        Assert.Equal("p1", patched.Priority);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task EpicPatch_AdvancesUpdatedAt()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-patch-updated-{Guid.NewGuid():N}" });

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Titled", description = "body", priority = "p2", projectId = project.Id });

        var before = DateTimeOffset.Parse(created.UpdatedAt);
        await Task.Delay(15);

        var patched = await _client.PatchDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{created.Id}", new { title = "Renamed" });

        var after = DateTimeOffset.Parse(patched.UpdatedAt);
        Assert.True(after > before, $"Expected UpdatedAt to advance. Before={before:O}, After={after:O}");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task EpicPatch_PreservesStatus()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-patch-status-{Guid.NewGuid():N}" });

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Titled", description = "body", priority = "p2", projectId = project.Id });
        Assert.Equal("active", created.Status);

        var patched = await _client.PatchDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{created.Id}", new { title = "Renamed", description = "new body", priority = "p0" });

        Assert.Equal("active", patched.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task EpicPatch_PreservesLinkedIssueMembership()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-patch-mem-{Guid.NewGuid():N}" });

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var epic = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Container", description = "body", priority = "p2", projectId = project.Id });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Member", projectId = project.Id });
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{epic.Id}/issues", new { issueId = issue.Id });

        var patched = await _client.PatchDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{epic.Id}", new { title = "Renamed", description = "after", priority = "p1" });

        Assert.Equal("Renamed", patched.Title);
        Assert.Equal("after", patched.Description);
        Assert.Equal("p1", patched.Priority);

        var detail = await _client.GetDataAsync<EpicDetailDto>($"/api/projects/{project.Id}/epics/{epic.Id}");
        Assert.Single(detail.LinkedIssues);
        Assert.Equal(issue.Id, detail.LinkedIssues[0].Id);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task EpicPatch_UnknownEpicReturnsNotFound()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-patch-404-{Guid.NewGuid():N}" });

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });

        using var response = await _client.PatchAsJsonAsync($"/api/projects/{project.Id}/epics/epic_{Guid.NewGuid():N}", new { title = "X" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task EpicPatch_PartialUpdate_LeavesUnspecifiedFieldsUnchanged()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-patch-partial-{Guid.NewGuid():N}" });

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Original", description = "Original body", priority = "p2", projectId = project.Id });

        var patched = await _client.PatchDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{created.Id}", new { title = "Renamed" });

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
