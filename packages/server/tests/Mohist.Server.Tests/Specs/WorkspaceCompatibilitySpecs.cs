using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class WorkspaceCompatibilitySpecs
{
    private readonly HttpClient _client;

    public WorkspaceCompatibilitySpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task GitEvidence_WhenBranchMissing_ReturnsCompatibleUnavailableResponses()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"workspace-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Workspace issue", projectId = project.Id });

        var diff = await _client.GetDataAsync<UnavailableDto>($"/api/issues/{issue.Number}/diff?projectId={project.Id}");
        var commits = await _client.GetDataAsync<UnavailableDto>($"/api/issues/{issue.Number}/commits?projectId={project.Id}");
        var commitDiff = await _client.GetDataAsync<CommitDiffUnavailableDto>($"/api/issues/{issue.Number}/commits/deadbeef/diff?projectId={project.Id}");

        Assert.False(diff.Available);
        Assert.Equal("branch_missing", diff.Reason);
        Assert.False(commits.Available);
        Assert.Equal("branch_missing", commits.Reason);
        Assert.False(commitDiff.Available);
        Assert.Equal("deadbeef", commitDiff.Hash);
    }

    private sealed record IssueDto(int Number);
    private sealed record ProjectDto(string Id);
    private sealed record UnavailableDto(bool Available, string Reason, string Message);
    private sealed record CommitDiffUnavailableDto(bool Available, string Reason, string Message, string Hash, string Diff);
}
