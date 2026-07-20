using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Infrastructure.Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

/// <summary>
/// Issue-420 T-001 API coverage. Verifies the additive composite child
/// projection on the Issue list and detail endpoints:
/// <list type="bullet">
/// <item>Every parent detail returns an additive <c>children</c> array
///   with number, title, status, health, and persisted repository name.</item>
/// <item><c>childIssuesSummary</c> carries the new <c>blockedCount</c>
///   field without breaking the existing per-status fields.</item>
/// <item>List reads surface <c>children</c> for every parent in the
///   result set; an ordinary issue carries an empty <c>children</c>
///   array and no <c>childIssuesSummary</c>.</item>
/// </list>
/// Spec:
/// <c>openspec/changes/issue-420/specs/composite-issue-detail/spec.md</c>.
/// </summary>
[Collection("IssueLifecycle")]
public class IssueCompositeChildProjectionApiSpecs
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public IssueCompositeChildProjectionApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task ListAndDetail_ExposeAdditiveChildrenArrayAndBlockedCount()
    {
        var projectId = await CreateProjectAsync();
        var parent = await CreateIssueAsync(projectId, "Parent");
        var firstChild = await CreateIssueAsync(projectId, "First child");
        var secondChild = await CreateIssueAsync(projectId, "Second child");
        await AttachChildAsync(projectId, firstChild.Number, parent.Number);
        await AttachChildAsync(projectId, secondChild.Number, parent.Number);
        var standalone = await CreateIssueAsync(projectId, "Standalone");
        const string firstChildTitle = "First child";

        var listRaw = await _client.GetStringAsync($"/api/projects/{projectId}/issues?all=true");
        using (var listDocument = JsonDocument.Parse(listRaw))
        {
            var data = listDocument.RootElement.GetProperty("data");
            var listByNumber = data.EnumerateArray()
                .ToDictionary(e => e.GetProperty("number").GetInt32());

            var parentList = listByNumber[parent.Number];
            AssertChildrenArray(parentList, parent.Number, firstChild.Number, secondChild.Number);
            AssertBlockedCount(parentList, expected: 0);
            AssertChildIssuesSummaryShape(parentList, expectedCount: 2);

            var standaloneList = listByNumber[standalone.Number];
            Assert.Empty(standaloneList.GetProperty("children").EnumerateArray());
            Assert.True(!standaloneList.TryGetProperty("childIssuesSummary", out _)
                || standaloneList.GetProperty("childIssuesSummary").ValueKind == JsonValueKind.Null,
                "ordinary issue list entry must not carry a childIssuesSummary");
        }

        var detailRaw = await _client.GetStringAsync($"/api/projects/{projectId}/issues/{parent.Number}");
        using (var detailDocument = JsonDocument.Parse(detailRaw))
        {
            var data = detailDocument.RootElement.GetProperty("data");
            AssertChildrenArray(data, parent.Number, firstChild.Number, secondChild.Number);
            AssertBlockedCount(data, expected: 0);
            AssertChildIssuesSummaryShape(data, expectedCount: 2);

            var firstChildElement = data.GetProperty("children").EnumerateArray()
                .Single(c => c.GetProperty("number").GetInt32() == firstChild.Number);
            Assert.Equal(firstChildTitle, firstChildElement.GetProperty("title").GetString());
            Assert.Equal("backlog", firstChildElement.GetProperty("status").GetString());
            Assert.Equal("active", firstChildElement.GetProperty("health").GetString());
            Assert.True(firstChildElement.TryGetProperty("repositoryName", out _),
                "child row must expose the persisted repositoryName field");
        }

        var standaloneDetailRaw = await _client.GetStringAsync($"/api/projects/{projectId}/issues/{standalone.Number}");
        using (var standaloneDocument = JsonDocument.Parse(standaloneDetailRaw))
        {
            var data = standaloneDocument.RootElement.GetProperty("data");
            Assert.Empty(data.GetProperty("children").EnumerateArray());
            Assert.True(!data.TryGetProperty("childIssuesSummary", out _)
                || data.GetProperty("childIssuesSummary").ValueKind == JsonValueKind.Null,
                "ordinary issue detail must not carry a childIssuesSummary");
        }
    }

    private static void AssertChildrenArray(JsonElement parentElement, int parentNumber, params int[] expectedChildNumbers)
    {
        Assert.True(parentElement.TryGetProperty("children", out var childrenElement),
            $"parent #{parentNumber} must expose an additive 'children' array");
        Assert.Equal(JsonValueKind.Array, childrenElement.ValueKind);
        var actualNumbers = childrenElement.EnumerateArray()
            .Select(c => c.GetProperty("number").GetInt32())
            .ToArray();
        Assert.Equal(expectedChildNumbers, actualNumbers);
        foreach (var childElement in childrenElement.EnumerateArray())
        {
            Assert.True(childElement.TryGetProperty("title", out _));
            Assert.True(childElement.TryGetProperty("status", out _));
            Assert.True(childElement.TryGetProperty("health", out _));
            Assert.True(childElement.TryGetProperty("repositoryName", out _));
        }
    }

    private static void AssertBlockedCount(JsonElement parentElement, int expected)
    {
        var summary = parentElement.GetProperty("childIssuesSummary");
        Assert.True(summary.TryGetProperty("blockedCount", out var blocked));
        Assert.Equal(expected, blocked.GetInt32());
    }

    private static void AssertChildIssuesSummaryShape(JsonElement parentElement, int expectedCount)
    {
        var summary = parentElement.GetProperty("childIssuesSummary");
        Assert.True(summary.GetProperty("hasChildren").GetBoolean());
        Assert.Equal(expectedCount, summary.GetProperty("count").GetInt32());
        Assert.True(summary.TryGetProperty("backlogCount", out _));
        Assert.True(summary.TryGetProperty("inProgressCount", out _));
        Assert.True(summary.TryGetProperty("doneCount", out _));
        Assert.True(summary.TryGetProperty("cancelledCount", out _));
        Assert.True(summary.TryGetProperty("blockedCount", out _));
    }

    private async Task<string> CreateProjectAsync()
    {
        var id = $"proj_composite_child_api_{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IProjectGrain>(id);
        await grain.CreateAsync(
            $"composite-child-{Guid.NewGuid():N}",
            new Mohist.Server.Project.Domain.RepositoryInfo
            {
                Name = "main",
                GitUrl = "git@example.com:repo.git",
                BaseBranch = "main",
                IsDefault = true,
            });
        return id;
    }

    private async Task<(int Number, string IssueKey)> CreateIssueAsync(string projectId, string title)
    {
        var number = await _fixture.Grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueKey = GrainKey.Issue(new IssueKey(projectId, number));
        var grain = _fixture.Grains.GetGrain<IIssueGrain>(issueKey);
        await grain.CreateAsync(projectId, number, title, null, null, null, isDraft: false);
        return (number, issueKey);
    }

    private async Task AttachChildAsync(string projectId, int childNumber, int parentNumber)
    {
        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/issues/{childNumber}",
            new { parentIssueNumber = parentNumber },
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}