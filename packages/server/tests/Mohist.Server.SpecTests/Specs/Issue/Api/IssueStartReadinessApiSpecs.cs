using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

[Collection("IssueLifecycle")]
public class IssueStartReadinessApiSpecs
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _client;
    private readonly IGrainFactory _grains;

    public IssueStartReadinessApiSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
        _grains = fixture.Grains;
    }

    [Fact]
    public async Task CreateIssue_DefaultsToDraft_WhenIsDraftOmitted()
    {
        var project = await CreateProjectAsync();

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues",
            new { title = "Draft by default" },
            JsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var payload = await ReadDataAsync<IssueDto>(response);
        Assert.True(payload.IsDraft);
        Assert.False(payload.CanStart);
        Assert.NotNull(payload.Blocker);
        Assert.Equal("draft", payload.Blocker!.Kind);
    }

    [Fact]
    public async Task CreateIssue_ExplicitReady_IsDraftFalse()
    {
        var project = await CreateProjectAsync();

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues",
            new { title = "Explicit ready", isDraft = false },
            JsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var payload = await ReadDataAsync<IssueDto>(response);
        Assert.False(payload.IsDraft);
        Assert.True(payload.CanStart);
        Assert.Null(payload.Blocker);
    }

    [Fact]
    public async Task GetIssue_OmitsLegacyFields_WhenReady()
    {
        var project = await CreateProjectAsync();
        var issue = await CreateIssueAsync(project.Id, "Ready", isDraft: false);

        using var response = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("startEligibility", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("waitingForDelivery", raw, StringComparison.OrdinalIgnoreCase);

        var payload = await ReadDataAsync<IssueDto>(response);
        Assert.False(payload.IsDraft);
        Assert.True(payload.CanStart);
        Assert.Null(payload.Blocker);
    }

    [Fact]
    public async Task CircularPrerequisiteDeclaration_StillRejects_AndReturnsReadinessFields()
    {
        var project = await CreateProjectAsync();
        var issue = await CreateIssueAsync(project.Id, "Self dep", isDraft: false);

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/prerequisites",
            new { prerequisiteNumber = issue.Number },
            JsonOptions);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var detailResponse = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}");
        var detail = await ReadDataAsync<IssueDto>(detailResponse);
        Assert.False(detail.IsDraft);
        Assert.True(detail.CanStart);
        Assert.Null(detail.Blocker);
        Assert.Empty(detail.Prereq);
    }

    private async Task<ProjectResponse> CreateProjectAsync()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/projects",
            new
            {
                name = $"readiness-{Guid.NewGuid():N}",
                repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
            },
            JsonOptions);
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<ProjectResponse>>(JsonOptions);
        var project = envelope!.Data!;
        return project;
    }

    private async Task<IssueResponse> CreateIssueAsync(string projectId, string title, bool isDraft)
    {
        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);
        _ = await projectGrain.GetAsync();

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues",
            new { title, isDraft },
            JsonOptions);
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<IssueResponse>>(JsonOptions);
        return envelope!.Data!;
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(JsonOptions);
        if (envelope is null || !envelope.Success)
            throw new InvalidOperationException($"API request failed: {envelope?.Error}");
        return envelope.Data!;
    }

    private sealed record ApiEnvelope<T>(bool Success, T? Data, string? Error = null, string? Code = null, object? Details = null);
    private sealed record ProjectResponse(string Id);
    private sealed record IssueResponse(int Number, string Id, string Title);
    private sealed record IssueDto(int Number, string Id, string Title, string Status, string Health, bool IsDraft, bool CanStart, BlockerDto? Blocker, PrerequisiteDto[] Prereq);
    private sealed record BlockerDto(string Kind, BlockerIssueDto? Issue);
    private sealed record BlockerIssueDto(int Number, string Title);
    private sealed record PrerequisiteDto(int Number, bool Completed);
}