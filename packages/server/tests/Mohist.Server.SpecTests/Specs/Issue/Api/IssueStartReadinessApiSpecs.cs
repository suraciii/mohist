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
    public async Task GetIssue_OmitsStartEligibilityAndWaitingForDelivery_WhenReady()
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
    public async Task ListIssues_IncludesIsDraftCanStartBlocker_AndOmitsLegacyFields()
    {
        var project = await CreateProjectAsync();
        await CreateIssueAsync(project.Id, "Draft", isDraft: true);
        await CreateIssueAsync(project.Id, "Ready", isDraft: false);

        using var response = await _client.GetAsync($"/api/projects/{project.Id}/issues");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("startEligibility", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("waitingForDelivery", raw, StringComparison.OrdinalIgnoreCase);

        var list = await ReadDataAsync<List<IssueDto>>(response);
        Assert.Equal(2, list.Count);
        var draft = list.Single(i => i.Title == "Draft");
        Assert.True(draft.IsDraft);
        Assert.False(draft.CanStart);
        Assert.Equal("draft", draft.Blocker!.Kind);
        var ready = list.Single(i => i.Title == "Ready");
        Assert.False(ready.IsDraft);
        Assert.True(ready.CanStart);
        Assert.Null(ready.Blocker);
    }

    [Fact]
    public async Task StartIssue_OnDraftIssue_ReturnsDraftBlocker()
    {
        var project = await CreateProjectAsync();
        var issue = await CreateIssueAsync(project.Id, "Still draft", isDraft: true);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope>(JsonOptions);
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("draft", envelope.Code);
        Assert.Contains("still a draft", envelope.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(envelope.Details);
        var details = (JsonElement)envelope.Details!;
        Assert.False(details.GetProperty("canStart").GetBoolean());
        var blocker = details.GetProperty("blocker");
        Assert.Equal("draft", blocker.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task StartIssue_OnReadyIssueWithUndeliveredPrerequisite_ReturnsWaitingForBlocker()
    {
        var project = await CreateProjectAsync();
        var prereq = await CreateIssueAsync(project.Id, "Prereq", isDraft: false);
        var dependent = await CreateIssueAsync(project.Id, "Dependent", isDraft: false);

        using var prereqResponse = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{dependent.Number}/prerequisites",
            new { prerequisiteNumber = prereq.Number },
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, prereqResponse.StatusCode);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{dependent.Number}/start", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope>(JsonOptions);
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("waiting_for_prerequisite", envelope.Code);
        Assert.Contains($"waiting for prerequisite issue #{prereq.Number}", envelope.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(envelope.Details);
        var details = (JsonElement)envelope.Details!;
        Assert.False(details.GetProperty("canStart").GetBoolean());
        var blocker = details.GetProperty("blocker");
        Assert.Equal("waiting-for", blocker.GetProperty("kind").GetString());
        Assert.Equal(prereq.Number, blocker.GetProperty("issue").GetProperty("number").GetInt32());
    }

    [Fact]
    public async Task StartIssue_OnReadyUnblockedIssue_StartsAndEnqueuesPipeline()
    {
        var project = await CreateProjectAsync();
        var issue = await CreateIssueAsync(project.Id, "Ready to start", isDraft: false);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var detailResponse = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}");
        var detail = await ReadDataAsync<IssueDto>(detailResponse);
        Assert.Equal("in_progress", detail.Status);
        Assert.False(detail.IsDraft);
    }

    [Fact]
    public async Task UpdateIssue_IsDraftFalse_MarksReadyAndExposesBlocker()
    {
        var project = await CreateProjectAsync();
        var issue = await CreateIssueAsync(project.Id, "Draft to mark ready", isDraft: true);

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}",
            new { isDraft = false },
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var detail = await ReadDataAsync<IssueDto>(response);
        Assert.False(detail.IsDraft);
        Assert.True(detail.CanStart);
        Assert.Null(detail.Blocker);
    }

    [Fact]
    public async Task WaitingIssue_RemainsNormalBacklogWork_NotBlockedStatus()
    {
        var project = await CreateProjectAsync();
        var prereq = await CreateIssueAsync(project.Id, "Undelivered prereq", isDraft: false);
        var dependent = await CreateIssueAsync(project.Id, "Waiting dependent", isDraft: false);

        using var prereqResponse = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{dependent.Number}/prerequisites",
            new { prerequisiteNumber = prereq.Number },
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, prereqResponse.StatusCode);

        using var listResponse = await _client.GetAsync($"/api/projects/{project.Id}/issues");
        var list = await ReadDataAsync<List<IssueDto>>(listResponse);
        var waitingIssue = list.Single(i => i.Number == dependent.Number);

        Assert.Equal("backlog", waitingIssue.Status);
        Assert.Equal("active", waitingIssue.Health);
        Assert.False(waitingIssue.IsDraft);
        Assert.False(waitingIssue.CanStart);
        Assert.NotNull(waitingIssue.Blocker);
        Assert.Equal("waiting-for", waitingIssue.Blocker!.Kind);
        Assert.Equal(prereq.Number, waitingIssue.Blocker.Issue!.Number);
        Assert.Contains(waitingIssue.Prereq, p => p.Number == prereq.Number && !p.Completed);
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

    [Fact]
    public async Task IssueStartReadiness_GrainReportsBlocker_ForDraftAndWaitingFor()
    {
        var project = await CreateProjectAsync();
        var draftIssue = await CreateIssueAsync(project.Id, "Draft grain", isDraft: true);
        var draftGrain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(project.Id, draftIssue.Number)));

        var draftReadiness = await draftGrain.GetStartReadinessAsync();
        Assert.True(draftReadiness.IsDraft);
        Assert.False(draftReadiness.CanStart);
        Assert.NotNull(draftReadiness.Blocker);
        Assert.IsType<IssueStartBlockerDto.DraftBlocker>(draftReadiness.Blocker);

        var prereq = await CreateIssueAsync(project.Id, "Grain prereq", isDraft: false);
        var dependent = await CreateIssueAsync(project.Id, "Grain dependent", isDraft: false);
        var dependentGrain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(project.Id, dependent.Number)));
        await dependentGrain.AddPrerequisiteAsync(prereq.Number);

        var waitingReadiness = await dependentGrain.GetStartReadinessAsync();
        Assert.False(waitingReadiness.IsDraft);
        Assert.False(waitingReadiness.CanStart);
        var waiting = Assert.IsType<IssueStartBlockerDto.WaitingForBlocker>(waitingReadiness.Blocker);
        Assert.Equal(prereq.Number, waiting.Issue.Number);
    }

    [Fact]
    public async Task StartIssue_OnParentWithChildren_TriggersCompositeAdvancement()
    {
        // issue-419 T-002: starting a parent no longer returns the
        // "is_parent" blocker. Instead the start succeeds; the parent's
        // aggregated status is recomputed from its children.
        var project = await CreateProjectAsync();
        var parent = await CreateIssueAsync(project.Id, "Parent", isDraft: false);
        var child = await CreateIssueAsync(project.Id, "Child", isDraft: false);

        using var attachResponse = await _client.PatchAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{child.Number}",
            new { parentIssueNumber = parent.Number },
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, attachResponse.StatusCode);

        using var startResponse = await _client.PostAsync($"/api/projects/{project.Id}/issues/{parent.Number}/start", null);
        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);

        // Detaching the child reverts the parent to a normal issue;
        // starting it again starts its own workflow run.
        using var detachResponse = await _client.PatchAsync(
            $"/api/projects/{project.Id}/issues/{child.Number}",
            JsonContent.Create(
                new { parentIssueNumber = (int?)null },
                options: new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(HttpStatusCode.OK, detachResponse.StatusCode);

        using var startAgainResponse = await _client.PostAsync($"/api/projects/{project.Id}/issues/{parent.Number}/start", null);
        Assert.Equal(HttpStatusCode.OK, startAgainResponse.StatusCode);
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
    private sealed record ApiEnvelope(bool Success, string? Error = null, string? Code = null, object? Details = null);
    private sealed record ProjectResponse(string Id);
    private sealed record IssueResponse(int Number, string Id, string Title);
    private sealed record IssueDto(int Number, string Id, string Title, string Status, string Health, bool IsDraft, bool CanStart, BlockerDto? Blocker, PrerequisiteDto[] Prereq);
    private sealed record BlockerDto(string Kind, BlockerIssueDto? Issue);
    private sealed record BlockerIssueDto(int Number, string Title);
    private sealed record PrerequisiteDto(int Number, bool Completed);
}
