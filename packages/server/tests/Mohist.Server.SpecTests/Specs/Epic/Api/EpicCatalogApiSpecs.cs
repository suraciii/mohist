using Mohist.Server.SpecTests.Support;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Epic.Api;

[Collection("MohistIntegration")]
public class EpicCatalogApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public EpicCatalogApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

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

    [Fact]
    public async Task EpicList_OrdersByPriorityWithinStatusGroup()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-order-pri-{Guid.NewGuid():N}" });

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var higherPriority = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Should be first (p0)", description = "alpha", priority = "p2", projectId = project.Id });
        var lowerPriority = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Should be second (p2)", description = "beta", priority = "p2", projectId = project.Id });

        await _client.PatchDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{higherPriority.Id}", new { priority = "p0" });

        var list = await _client.GetDataAsync<EpicWithProgressDto[]>($"/api/projects/{project.Id}/epics");

        Assert.Equal(2, list.Length);
        Assert.Equal(higherPriority.Id, list[0].Id);
        Assert.Equal("p0", list[0].Priority);
        Assert.Equal(lowerPriority.Id, list[1].Id);
        Assert.Equal("p2", list[1].Priority);
    }

    [Fact]
    public async Task EpicList_OrdersByRecentUpdatedAtWhenPrioritiesMatch()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-order-upd-{Guid.NewGuid():N}" });

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var older = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Older (p2)", description = "alpha", priority = "p2", projectId = project.Id });
        var newer = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Newer (p2)", description = "beta", priority = "p2", projectId = project.Id });

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        var updatedNewer = await _client.PatchDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{newer.Id}", new { title = "Newer (renamed)" });

        var list = await _client.GetDataAsync<EpicWithProgressDto[]>($"/api/projects/{project.Id}/epics");

        Assert.Equal(2, list.Length);
        Assert.Equal(newer.Id, list[0].Id);
        Assert.Equal(older.Id, list[1].Id);
        Assert.True(
            DateTimeOffset.Parse(list[0].UpdatedAt) > DateTimeOffset.Parse(list[1].UpdatedAt),
            $"Expected newer epic UpdatedAt to be more recent. List[0]={list[0].UpdatedAt}, List[1]={list[1].UpdatedAt}");
        Assert.True(DateTimeOffset.Parse(updatedNewer.UpdatedAt) > DateTimeOffset.Parse(older.UpdatedAt));
    }

    [Fact]
    public async Task EpicList_ReturnsOrderedArraySoConsumerCanRenderInServerSuppliedOrder()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-order-arr-{Guid.NewGuid():N}" });

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var p2CreatedFirst = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Created first but p2", description = "alpha", priority = "p2", projectId = project.Id });
        var p0CreatedLater = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Created later but p0", description = "beta", priority = "p2", projectId = project.Id });

        await _client.PatchDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{p0CreatedLater.Id}", new { priority = "p0" });

        var list = await _client.GetDataAsync<EpicWithProgressDto[]>($"/api/projects/{project.Id}/epics");

        Assert.Equal(2, list.Length);
        Assert.Equal(p0CreatedLater.Id, list[0].Id);
        Assert.Equal(p2CreatedFirst.Id, list[1].Id);
    }

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
