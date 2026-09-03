using Mohist.Server.Epic.Grains;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.L1Tests.Support;
using Mohist.Server.TestSupport;
using Orleans;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Mohist.Server.L1Tests.Specs.Epic.Api;

/// <summary>
/// Integration specs for the batch link/unlink endpoints.
/// Exercises:
/// <list type="bullet">
/// <item>POST /{number}/issues:batch — per-issue outcomes, partial failure,
/// dedup, idempotency, and direct Issue affiliation moves,
/// and the issue-392 closed-epic whole-batch rejection (409
/// EPIC_CLOSED_CANNOT_LINK).</item>
/// <item>POST /{number}/issues:batch-unlink — per-issue unlink outcomes,
/// idempotent for non-members, leaves other memberships intact.</item>
/// <item>Single-issue link/unlink routes remain unchanged.</item>
/// </list>
/// </summary>
public partial class EpicBatchMembershipApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private readonly IGrainFactory _grains;

    public EpicBatchMembershipApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
        _grains = fixture.Grains;
    }

    [Fact]
    public async Task BatchLink_NewIssues_AllLinked()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "batch-new");
        var issueA = await CreateIssueAsync(project.Id, "Alpha");
        var issueB = await CreateIssueAsync(project.Id, "Beta");
        var issueC = await CreateIssueAsync(project.Id, "Gamma");

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/epics/{epic.Number}/issues:batch",
            new { issueNumbers = new[] { issueA.Number, issueB.Number, issueC.Number } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await ReadEnvelopeAsync(response);
        var results = Assert.IsType<JsonElement>(envelope.GetProperty("data")).GetProperty("results");
        Assert.Equal(3, results.GetArrayLength());
        var statuses = results.EnumerateArray().Select(r => r.GetProperty("status").GetString()).ToArray();
        Assert.Equal(new[] { "linked", "linked", "linked" }, statuses);
        Assert.All(results.EnumerateArray(), result =>
        {
            Assert.Equal(epic.Number, result.GetProperty("owningEpicNumber").GetInt32());
            Assert.Equal(epic.Title, result.GetProperty("owningEpicTitle").GetString());
        });

        var detail = await _client.GetDataAsync<EpicDetailDtoLike>($"/api/projects/{project.Id}/epics/{epic.Number}");
        Assert.Equal(3, detail.LinkedIssues.Length);
    }

    [Fact]
    public async Task BatchLink_IssueAlreadyInOtherEpic_ReturnsConflict_AndLinksOthers()
    {
        var project = await CreateProjectAsync();
        var firstEpic = await CreateEpicAsync(project.Id, "first", number: 1);
        var secondEpic = await CreateEpicAsync(project.Id, "second", number: 2);
        var moved = await CreateIssueAsync(project.Id, "moved");
        var clean = await CreateIssueAsync(project.Id, "clean");
        await LinkIssueAsync(project.Id, firstEpic, moved);

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/epics/{secondEpic.Number}/issues:batch",
            new { issueNumbers = new[] { moved.Number, clean.Number } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await ReadEnvelopeAsync(response);
        var results = Assert.IsType<JsonElement>(envelope.GetProperty("data")).GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());
        var arr = results.EnumerateArray().ToArray();
        var movedEntry = arr.Single(r => r.GetProperty("identifier").GetString() == moved.Number.ToString());
        Assert.Equal("conflict", movedEntry.GetProperty("status").GetString());
        Assert.Equal(firstEpic.Number, movedEntry.GetProperty("owningEpicNumber").GetInt32());
        Assert.Equal(firstEpic.Title, movedEntry.GetProperty("owningEpicTitle").GetString());

        var cleanEntry = arr.Single(r => r.GetProperty("identifier").GetString() == clean.Number.ToString());
        Assert.Equal("linked", cleanEntry.GetProperty("status").GetString());
        Assert.Equal(secondEpic.Number, cleanEntry.GetProperty("owningEpicNumber").GetInt32());
        Assert.Equal(secondEpic.Title, cleanEntry.GetProperty("owningEpicTitle").GetString());

        var detail = await _client.GetDataAsync<EpicDetailDtoLike>($"/api/projects/{project.Id}/epics/{secondEpic.Number}");
        Assert.Equal(new[] { clean.Number }, detail.LinkedIssues.Select(issue => issue.Number));

        var oldEpicDetail = await _client.GetDataAsync<EpicDetailDtoLike>($"/api/projects/{project.Id}/epics/{firstEpic.Number}");
        Assert.Equal(new[] { moved.Number }, oldEpicDetail.LinkedIssues.Select(issue => issue.Number));
    }

    [Fact]
    public async Task BatchUnlink_RemovesOnlyRequestedMembers_RemainingIntact()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "unlink-multi");
        var a = await CreateIssueAsync(project.Id, "a");
        var b = await CreateIssueAsync(project.Id, "b");
        var c = await CreateIssueAsync(project.Id, "c");
        await LinkIssueAsync(project.Id, epic, a);
        await LinkIssueAsync(project.Id, epic, b);
        await LinkIssueAsync(project.Id, epic, c);

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/epics/{epic.Number}/issues:batch-unlink",
            new { issueNumbers = new[] { a.Number, b.Number } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await ReadEnvelopeAsync(response);
        var results = Assert.IsType<JsonElement>(envelope.GetProperty("data")).GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());
        Assert.All(results.EnumerateArray(), r => Assert.Equal("unlinked", r.GetProperty("status").GetString()));

        var detail = await _client.GetDataAsync<EpicDetailDtoLike>($"/api/projects/{project.Id}/epics/{epic.Number}");
        Assert.Single(detail.LinkedIssues);
        Assert.Equal(c.Number, detail.LinkedIssues[0].Number);
    }

    [Fact]
    public async Task BatchLink_OnUnknownEpic_Returns404()
    {
        var project = await CreateProjectAsync();

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/epics/9999/issues:batch",
            new { issueNumbers = new[] { 1 } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BatchLink_OnClosedEpic_Returns409EpicClosedCannotLink_NoPerItemOutcomes()
    {
        // Spec: 'Batch link to a closed epic is rejected as a whole' —
        // the request is rejected with 409 EPIC_CLOSED_CANNOT_LINK, no
        // per-item linked/conflict outcomes are produced, and no link
        // rows are created.
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "closed-batch");
        var issueA = await CreateIssueAsync(project.Id, "a");
        var issueB = await CreateIssueAsync(project.Id, "b");

        // Drive the epic into `closed` through the public close route
        // (no terminal issues required — we just need to flip status).
        // First link a terminal issue so close doesn't release any
        // active memberships in flight; the close path itself accepts
        // any non-terminal epic per design D3.
        await LinkIssueAsync(project.Id, epic, issueA);
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{epic.Number}/close", null);

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/epics/{epic.Number}/issues:batch",
            new { issueNumbers = new[] { issueA.Number, issueB.Number } });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        var envelope = JsonSerializer.Deserialize<JsonElement>(raw);
        Assert.Equal("EPIC_CLOSED_CANNOT_LINK", envelope.GetProperty("code").GetString());

        // The epic stays closed and no link row was added for the
        // second issue (the first was already linked before close).
        var detail = await _client.GetDataAsync<EpicDetailDtoLike>($"/api/projects/{project.Id}/epics/{epic.Number}");
        Assert.Single(detail.LinkedIssues);
        Assert.Equal(issueA.Number, detail.LinkedIssues[0].Number);
    }

    private async Task<ProjectDto> CreateProjectAsync()
    {
        return await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>(
            "/api/projects",
            $"epic-batch-{Guid.NewGuid():N}",
            repoName: "main",
            gitUrl: $"file://{Guid.NewGuid():N}");
    }

    private async Task<EpicDto> CreateEpicAsync(string projectId, string title, int number = 1)
    {
        var dto = await _client.PostDataAsync<EpicDto>(
            $"/api/projects/{projectId}/epics",
            new { title, description = "batch", priority = "p2", projectId });
        return dto;
    }

    private async Task<IssueDto> CreateIssueAsync(string projectId, string title)
    {
        var dto = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{projectId}/issues",
            new { title, projectId });
        return dto;
    }

    private async Task LinkIssueAsync(string projectId, EpicDto epic, IssueDto issue)
    {
        await _client.PostOkAsync(
            $"/api/projects/{projectId}/epics/{epic.Number}/issues",
            new { issueNumber = issue.Number });
    }

    private static async Task<JsonElement> ReadEnvelopeAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(raw);
    }

    private static void AssertNoOwningEpic(JsonElement outcome)
    {
        Assert.True(!outcome.TryGetProperty("owningEpicNumber", out var number)
            || number.ValueKind == JsonValueKind.Null);
        Assert.True(!outcome.TryGetProperty("owningEpicTitle", out var title)
            || title.ValueKind == JsonValueKind.Null);
    }

    private sealed record ProjectDto(string Id);
    private sealed record EpicDto(int Number, string Title, string Description, string Priority, string Status, string CreatedAt, string UpdatedAt);
    private sealed record IssueDto(int Number);
    private sealed record EpicDetailDtoLike(int Number, string Title, string Status, LinkedIssueRefDto[] LinkedIssues);
    private sealed record LinkedIssueRefDto(int Number);
}
