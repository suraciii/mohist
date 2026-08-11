using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Epic.Api;

public partial class EpicBatchMembershipApiSpecs
{
    [Fact]
    public async Task SingleLinkEndpoint_ReturnsCanonicalBatchMembershipOutcome()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "single-link");
        var issue = await CreateIssueAsync(project.Id, "single");

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/epics/{epic.Number}/issues",
            new { issueNumber = issue.Number });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await ReadEnvelopeAsync(response);
        var results = Assert.IsType<JsonElement>(envelope.GetProperty("data")).GetProperty("results");
        var outcome = Assert.Single(results.EnumerateArray());
        Assert.Equal(issue.Number.ToString(), outcome.GetProperty("identifier").GetString());
        Assert.Equal("linked", outcome.GetProperty("status").GetString());
        Assert.Equal(issue.Number, outcome.GetProperty("issueNumber").GetInt32());
        Assert.Equal(epic.Number, outcome.GetProperty("owningEpicNumber").GetInt32());
        Assert.Equal(epic.Title, outcome.GetProperty("owningEpicTitle").GetString());

        using var retryResponse = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/epics/{epic.Number}/issues",
            new { issueNumber = issue.Number });

        Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
        var retryEnvelope = await ReadEnvelopeAsync(retryResponse);
        var retryResults = Assert.IsType<JsonElement>(retryEnvelope.GetProperty("data")).GetProperty("results");
        var retryOutcome = Assert.Single(retryResults.EnumerateArray());
        Assert.Equal("already-linked", retryOutcome.GetProperty("status").GetString());
        Assert.Equal(epic.Number, retryOutcome.GetProperty("owningEpicNumber").GetInt32());
        Assert.Equal(epic.Title, retryOutcome.GetProperty("owningEpicTitle").GetString());

        var detail = await _client.GetDataAsync<EpicDetailDtoLike>($"/api/projects/{project.Id}/epics/{epic.Number}");
        Assert.Single(detail.LinkedIssues);
        Assert.Equal(issue.Number, detail.LinkedIssues[0].Number);
    }

    [Fact]
    public async Task SingleLinkEndpoint_WhenIssueBelongsToAnotherEpic_ReturnsActualOwnerConflict()
    {
        var project = await CreateProjectAsync();
        var existingEpic = await CreateEpicAsync(project.Id, "existing-owner");
        var targetEpic = await CreateEpicAsync(project.Id, "target-epic");
        var issue = await CreateIssueAsync(project.Id, "already-owned");
        await LinkIssueAsync(project.Id, existingEpic, issue);

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/epics/{targetEpic.Number}/issues",
            new { issueNumber = issue.Number });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await ReadEnvelopeAsync(response);
        var results = Assert.IsType<JsonElement>(envelope.GetProperty("data")).GetProperty("results");
        var outcome = Assert.Single(results.EnumerateArray());
        Assert.Equal("conflict", outcome.GetProperty("status").GetString());
        Assert.Equal(existingEpic.Number, outcome.GetProperty("owningEpicNumber").GetInt32());
        Assert.Equal(existingEpic.Title, outcome.GetProperty("owningEpicTitle").GetString());

        var existingDetail = await _client.GetDataAsync<EpicDetailDtoLike>(
            $"/api/projects/{project.Id}/epics/{existingEpic.Number}");
        var targetDetail = await _client.GetDataAsync<EpicDetailDtoLike>(
            $"/api/projects/{project.Id}/epics/{targetEpic.Number}");
        Assert.Single(existingDetail.LinkedIssues);
        Assert.Empty(targetDetail.LinkedIssues);
    }

    [Fact]
    public async Task SingleUnlinkEndpoint_ReturnsCanonicalBatchMembershipOutcome()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "single-unlink");
        var issue = await CreateIssueAsync(project.Id, "single");
        await LinkIssueAsync(project.Id, epic, issue);

        using var response = await _client.DeleteAsync(
            $"/api/projects/{project.Id}/epics/{epic.Number}/issues/{issue.Number}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await ReadEnvelopeAsync(response);
        var results = Assert.IsType<JsonElement>(envelope.GetProperty("data")).GetProperty("results");
        var outcome = Assert.Single(results.EnumerateArray());
        Assert.Equal(issue.Number.ToString(), outcome.GetProperty("identifier").GetString());
        Assert.Equal("unlinked", outcome.GetProperty("status").GetString());
        Assert.Equal(issue.Number, outcome.GetProperty("issueNumber").GetInt32());
        AssertNoOwningEpic(outcome);

        var detail = await _client.GetDataAsync<EpicDetailDtoLike>($"/api/projects/{project.Id}/epics/{epic.Number}");
        Assert.Empty(detail.LinkedIssues);
    }
}
