using Mohist.Server.Epic.Grains;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.SpecTests.Support;
using Orleans;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Epic.Api;

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
[Collection("IntegrationWorkflow")]
public class EpicBatchMembershipApiSpecs
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
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

        var detail = await _client.GetDataAsync<EpicDetailDtoLike>($"/api/projects/{project.Id}/epics/{epic.Number}");
        Assert.Equal(3, detail.LinkedIssues.Length);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task BatchLink_Numbers_AllLinked()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "batch-mixed");
        var issueA = await CreateIssueAsync(project.Id, "By number");
        var issueB = await CreateIssueAsync(project.Id, "Second number");

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/epics/{epic.Number}/issues:batch",
            new { issueNumbers = new[] { issueA.Number, issueB.Number } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await ReadEnvelopeAsync(response);
        var results = Assert.IsType<JsonElement>(envelope.GetProperty("data")).GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());
        Assert.All(results.EnumerateArray(), r => Assert.Equal("linked", r.GetProperty("status").GetString()));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task BatchLink_IssueAlreadyInOtherEpic_MovesIssue_AndLinksOthers()
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
        Assert.Equal("linked", movedEntry.GetProperty("status").GetString());

        var cleanEntry = arr.Single(r => r.GetProperty("identifier").GetString() == clean.Number.ToString());
        Assert.Equal("linked", cleanEntry.GetProperty("status").GetString());

        var detail = await _client.GetDataAsync<EpicDetailDtoLike>($"/api/projects/{project.Id}/epics/{secondEpic.Number}");
        Assert.Equal(
            new[] { moved.Number, clean.Number },
            detail.LinkedIssues.Select(issue => issue.Number).Order());

        var oldEpicDetail = await _client.GetDataAsync<EpicDetailDtoLike>($"/api/projects/{project.Id}/epics/{firstEpic.Number}");
        Assert.Empty(oldEpicDetail.LinkedIssues);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task BatchLink_IssueAlreadyMember_ReportedAsAlreadyLinked()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "dup");
        var issue = await CreateIssueAsync(project.Id, "re-add");
        await LinkIssueAsync(project.Id, epic, issue);

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/epics/{epic.Number}/issues:batch",
            new { issueNumbers = new[] { issue.Number } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await ReadEnvelopeAsync(response);
        var results = Assert.IsType<JsonElement>(envelope.GetProperty("data")).GetProperty("results");
        Assert.Equal("already-linked", results[0].GetProperty("status").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task BatchLink_UnknownIdentifier_ReportedAsNotFound()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "nope");
        var issue = await CreateIssueAsync(project.Id, "real");

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/epics/{epic.Number}/issues:batch",
            new { issueNumbers = new[] { 99999, issue.Number } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await ReadEnvelopeAsync(response);
        var results = Assert.IsType<JsonElement>(envelope.GetProperty("data")).GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());
        var arr = results.EnumerateArray().ToArray();
        Assert.Equal("not-found", arr[0].GetProperty("status").GetString());
        Assert.Equal("linked", arr[1].GetProperty("status").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task BatchLink_DuplicateIdentifierInOneRequest_LinkedAtMostOnce()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "dup-req");
        var issue = await CreateIssueAsync(project.Id, "only-once");

        // Same identifier (the issue number) twice in one request: the
        // issue is linked at most once, but the response still contains
        // one non-error outcome for each requested token.
        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/epics/{epic.Number}/issues:batch",
            new { issueNumbers = new[] { issue.Number, issue.Number } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await ReadEnvelopeAsync(response);
        var results = Assert.IsType<JsonElement>(envelope.GetProperty("data")).GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());
        var arr = results.EnumerateArray().ToArray();
        Assert.Equal(new[] { issue.Number.ToString(), issue.Number.ToString() }, arr.Select(r => r.GetProperty("identifier").GetString()).ToArray());
        Assert.Equal("linked", arr[0].GetProperty("status").GetString());
        Assert.Equal("already-linked", arr[1].GetProperty("status").GetString());

        var detail = await _client.GetDataAsync<EpicDetailDtoLike>($"/api/projects/{project.Id}/epics/{epic.Number}");
        Assert.Single(detail.LinkedIssues);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task BatchLink_DuplicateNumber_LinkedAtMostOnce()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "dup-mixed");
        var issue = await CreateIssueAsync(project.Id, "mixed");

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/epics/{epic.Number}/issues:batch",
            new { issueNumbers = new[] { issue.Number, issue.Number } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await ReadEnvelopeAsync(response);
        var results = Assert.IsType<JsonElement>(envelope.GetProperty("data")).GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());
        var arr = results.EnumerateArray().ToArray();
        Assert.Equal(new[] { issue.Number.ToString(), issue.Number.ToString() }, arr.Select(r => r.GetProperty("identifier").GetString()).ToArray());
        var byStatus = arr.Select(r => r.GetProperty("status").GetString()).ToArray();
        // At least one linked and the second is either linked or
        // already-linked; the duplicate is not an error.
        Assert.Contains("linked", byStatus);

        var detail = await _client.GetDataAsync<EpicDetailDtoLike>($"/api/projects/{project.Id}/epics/{epic.Number}");
        Assert.Single(detail.LinkedIssues);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task BatchUnlink_NotMember_ReportedAsWasNotAMember_AndOthersUnlinked()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "unlink-mixed");
        var member = await CreateIssueAsync(project.Id, "member");
        var nonMember = await CreateIssueAsync(project.Id, "non");
        await LinkIssueAsync(project.Id, epic, member);

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/epics/{epic.Number}/issues:batch-unlink",
            new { issueNumbers = new[] { member.Number, nonMember.Number } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await ReadEnvelopeAsync(response);
        var results = Assert.IsType<JsonElement>(envelope.GetProperty("data")).GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());
        var arr = results.EnumerateArray().ToArray();
        Assert.Equal("unlinked", arr[0].GetProperty("status").GetString());
        Assert.Equal("was-not-a-member", arr[1].GetProperty("status").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task BatchUnlink_UnknownIdentifier_ReportedAsWasNotAMember()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "unlink-unknown");
        var member = await CreateIssueAsync(project.Id, "member");
        await LinkIssueAsync(project.Id, epic, member);

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/epics/{epic.Number}/issues:batch-unlink",
            new { issueNumbers = new[] { 99999, member.Number } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await ReadEnvelopeAsync(response);
        var results = Assert.IsType<JsonElement>(envelope.GetProperty("data")).GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());
        var arr = results.EnumerateArray().ToArray();
        Assert.Equal("99999", arr[0].GetProperty("identifier").GetString());
        Assert.Equal("was-not-a-member", arr[0].GetProperty("status").GetString());
        Assert.Equal("unlinked", arr[1].GetProperty("status").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task BatchUnlink_DuplicateIdentifier_ReturnsOutcomePerRequestedIdentifier()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "unlink-dup");
        var issue = await CreateIssueAsync(project.Id, "member");
        await LinkIssueAsync(project.Id, epic, issue);

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/epics/{epic.Number}/issues:batch-unlink",
            new { issueNumbers = new[] { issue.Number, issue.Number } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await ReadEnvelopeAsync(response);
        var results = Assert.IsType<JsonElement>(envelope.GetProperty("data")).GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());
        var arr = results.EnumerateArray().ToArray();
        Assert.Equal(new[] { issue.Number.ToString(), issue.Number.ToString() }, arr.Select(r => r.GetProperty("identifier").GetString()).ToArray());
        Assert.Equal("unlinked", arr[0].GetProperty("status").GetString());
        Assert.Equal("was-not-a-member", arr[1].GetProperty("status").GetString());

        var detail = await _client.GetDataAsync<EpicDetailDtoLike>($"/api/projects/{project.Id}/epics/{epic.Number}");
        Assert.Empty(detail.LinkedIssues);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task BatchLink_EmptyArray_ReturnsOkWithEmptyResults()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "empty-batch");

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/epics/{epic.Number}/issues:batch",
            new { issueNumbers = Array.Empty<int>() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await ReadEnvelopeAsync(response);
        var results = Assert.IsType<JsonElement>(envelope.GetProperty("data")).GetProperty("results");
        Assert.Equal(0, results.GetArrayLength());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task BatchLink_OnUnknownEpic_Returns404()
    {
        var project = await CreateProjectAsync();

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/epics/9999/issues:batch",
            new { issueNumbers = new[] { 1 } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task SingleLinkEndpoint_RemainsUnchanged_AfterBatchEndpointAdded()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "single-link");
        var issue = await CreateIssueAsync(project.Id, "single");

        await _client.PostOkAsync(
            $"/api/projects/{project.Id}/epics/{epic.Number}/issues",
            new { issueNumber = issue.Number });

        var detail = await _client.GetDataAsync<EpicDetailDtoLike>($"/api/projects/{project.Id}/epics/{epic.Number}");
        Assert.Single(detail.LinkedIssues);
        Assert.Equal(issue.Number, detail.LinkedIssues[0].Number);
    }

    private async Task<ProjectDto> CreateProjectAsync()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-batch-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        return project;
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

    private sealed record ProjectDto(string Id);
    private sealed record EpicDto(int Number, string Title, string Description, string Priority, string Status, string CreatedAt, string UpdatedAt);
    private sealed record IssueDto(int Number);
    private sealed record EpicDetailDtoLike(int Number, string Title, string Status, LinkedIssueRefDto[] LinkedIssues);
    private sealed record LinkedIssueRefDto(int Number);
}
