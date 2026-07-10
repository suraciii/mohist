using Mohist.Server.IntegrationSpecs.Support;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Mohist.Server.IntegrationSpecs.Specs.Epic.Api;

[Collection("MohistIntegration")]
public class EpicPatchApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public EpicPatchApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

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

    [Fact]
    public async Task EpicPatch_AdvancesUpdatedAt()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-patch-updated-{Guid.NewGuid():N}" });

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Titled", description = "body", priority = "p2", projectId = project.Id });

        var before = DateTimeOffset.Parse(created.UpdatedAt);
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));

        var patched = await _client.PatchDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{created.Id}", new { title = "Renamed" });

        var after = DateTimeOffset.Parse(patched.UpdatedAt);
        Assert.True(after > before, $"Expected UpdatedAt to advance. Before={before:O}, After={after:O}");
    }

    [Fact]
    public async Task EpicPatch_PreservesStatus()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-patch-status-{Guid.NewGuid():N}" });

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var created = await _client.PostDataAsync<EpicDto>($"/api/projects/{project.Id}/epics", new { title = "Titled", description = "body", priority = "p2", projectId = project.Id });
        Assert.Equal("idle", created.Status);

        var patched = await _client.PatchDataAsync<EpicDto>($"/api/projects/{project.Id}/epics/{created.Id}", new { title = "Renamed", description = "new body", priority = "p0" });

        Assert.Equal("idle", patched.Status);
    }

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

    [Fact]
    public async Task EpicPatch_UnknownEpicReturnsNotFound()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"epic-patch-404-{Guid.NewGuid():N}" });

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });

        using var response = await _client.PatchAsJsonAsync($"/api/projects/{project.Id}/epics/epic_{Guid.NewGuid():N}", new { title = "X" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

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
