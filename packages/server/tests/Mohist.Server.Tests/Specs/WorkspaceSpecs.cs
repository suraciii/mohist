using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class WorkspaceSpecs
{
    private readonly HttpClient _client;
    private readonly MohistIntegrationFixture _fixture;

    public WorkspaceSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
        _fixture.Git.Reset();
    }

    [Fact]
    public async Task GivenIssueHasNoWorkspaceBranch_WhenUserOpensReviewViews_ThenMohistExplainsThatNoChangesAreAvailable()
    {
        // Given a project and an issue that has not produced a workspace branch yet.
        _fixture.Git.BranchExists = false;
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"workspace-{Guid.NewGuid():N}", path = "/fake/project", baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Workspace issue", projectId = project.Id });

        // When the user opens the diff, commit list, and commit diff review views.
        var diff = await _client.GetDataAsync<UnavailableDto>($"/api/issues/{issue.Number}/diff?projectId={project.Id}");
        var commits = await _client.GetDataAsync<UnavailableDto>($"/api/issues/{issue.Number}/commits?projectId={project.Id}");
        var commitDiff = await _client.GetDataAsync<CommitDiffUnavailableDto>($"/api/issues/{issue.Number}/commits/deadbeef/diff?projectId={project.Id}");

        // Then Mohist keeps the review UI usable and explains that no change branch exists yet.
        Assert.False(diff.Available);
        Assert.Equal("branch_missing", diff.Reason);
        Assert.Equal("Branch not found", diff.Message);
        Assert.False(commits.Available);
        Assert.Equal("branch_missing", commits.Reason);
        Assert.Equal("Branch not found", commits.Message);
        Assert.False(commitDiff.Available);
        Assert.Equal("branch_missing", commitDiff.Reason);
        Assert.Equal("Branch not found", commitDiff.Message);
        Assert.Equal("deadbeef", commitDiff.Hash);
    }

    [Fact]
    public async Task GivenIssueWorktreeExists_WhenUserCleansUpWorkspace_ThenLocalWorktreeIsRemoved()
    {
        // Given an issue has an active local worktree.
        var projectName = $"workspace-{Guid.NewGuid():N}";
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = "/fake/project", baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Cleanup issue", projectId = project.Id });
        _fixture.Git.WorktreeRemoval = new WorktreeRemovalResultDto(true, "removed", "/fake/worktree", null, "Worktree removed").ToDomain();

        // When the user asks Mohist to clean up the issue workspace.
        var cleanup = await _client.PostDataAsync<CleanupDto>($"/api/issues/{issue.Number}/cleanup?projectId={project.Id}");

        // Then Mohist removes the worktree and reports the cleanup as completed.
        Assert.True(cleanup.Removed);
        Assert.Equal("Worktree removed", cleanup.Message);
        Assert.Contains(cleanup.Resources, r => r.Type == "worktree" && r.Status == "removed" && r.Path == "/fake/worktree");
        Assert.Contains(_fixture.Git.RemoveWorktreeCalls, c => c.ProjectPath == "/fake/project" && c.ProjectName == projectName && c.IssueNumber == issue.Number);
    }

    [Fact]
    public async Task GivenIssueWorkspaceIsAlreadyClean_WhenUserRunsCleanupAgain_ThenCleanupSucceedsAsNoOp()
    {
        // Given an issue whose local workspace has already been removed.
        var projectName = $"workspace-{Guid.NewGuid():N}";
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = "/fake/project", baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Missing cleanup issue", projectId = project.Id });
        _fixture.Git.WorktreeRemoval = new WorktreeRemovalResultDto(false, "missing", "/fake/worktree", "worktree_missing", "Worktree already removed").ToDomain();

        // When the user runs cleanup.
        var cleanup = await _client.PostDataAsync<CleanupDto>($"/api/issues/{issue.Number}/cleanup?projectId={project.Id}");

        // Then Mohist treats cleanup as idempotent and tells the user nothing needed removal.
        Assert.False(cleanup.Removed);
        Assert.Equal("Worktree already removed", cleanup.Message);
        Assert.Contains(cleanup.Resources, r => r.Type == "worktree" && r.Status == "missing");
    }

    private sealed record IssueDto(int Number);
    private sealed record ProjectDto(string Id);
    private sealed record UnavailableDto(bool Available, string Reason, string Message);
    private sealed record CommitDiffUnavailableDto(bool Available, string Reason, string Message, string Hash, string Diff);
    private sealed record CleanupDto(bool Removed, string Message, CleanupResourceDto[] Resources);
    private sealed record CleanupResourceDto(string Type, string Status, string? Path, string? Reason);
    private sealed record WorktreeRemovalResultDto(bool Removed, string Status, string? Path, string? Reason, string Message)
    {
        public Mohist.Server.Infrastructure.Workspace.WorktreeRemovalResult ToDomain() => new(Removed, Status, Path, Reason, Message);
    }
}
