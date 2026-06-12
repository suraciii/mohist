using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Tests.Support;
using System.Net.Http.Json;
using Xunit;

namespace Mohist.Server.Tests.Specs.Api;

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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GivenIssueHasNotStarted_WhenUserOpensReviewViews_ThenMohistExplainsThatWorkHasNotStarted()
    {
        var project = await CreateProjectWithRepositoryAsync();
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Workspace issue", projectId = project.Id });

        var diff = await _client.GetDataAsync<UnavailableDto>($"/api/projects/{project.Id}/issues/{issue.Number}/diff");
        var commits = await _client.GetDataAsync<UnavailableDto>($"/api/projects/{project.Id}/issues/{issue.Number}/commits");
        var commitDiff = await _client.GetDataAsync<CommitDiffUnavailableDto>($"/api/projects/{project.Id}/issues/{issue.Number}/commits/deadbeef/diff");

        Assert.False(diff.Available);
        Assert.Equal("not_started", diff.Reason);
        Assert.False(commits.Available);
        Assert.Equal("not_started", commits.Reason);
        Assert.False(commitDiff.Available);
        Assert.Equal("not_started", commitDiff.Reason);
        Assert.Equal("deadbeef", commitDiff.Hash);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GivenIssueWorkspaceIsRemoved_WhenUserOpensReviewViews_ThenMohistReportsWorkspaceRemoved()
    {
        var project = await CreateProjectWithRepositoryAsync();
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Workspace issue", projectId = project.Id });
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start");

        var diff = await _client.GetDataAsync<UnavailableDto>($"/api/projects/{project.Id}/issues/{issue.Number}/diff");
        var commits = await _client.GetDataAsync<UnavailableDto>($"/api/projects/{project.Id}/issues/{issue.Number}/commits");
        var commitDiff = await _client.GetDataAsync<CommitDiffUnavailableDto>($"/api/projects/{project.Id}/issues/{issue.Number}/commits/deadbeef/diff");
        var status = await _client.GetDataAsync<StatusDto>($"/api/projects/{project.Id}/issues/{issue.Number}/workspace-status");
        var fileContent = await _client.GetDataAsync<FileContentDto>($"/api/projects/{project.Id}/issues/{issue.Number}/file-content?path=a.txt");

        Assert.Equal("workspace_removed", diff.Reason);
        Assert.Equal("workspace_removed", commits.Reason);
        Assert.Equal("workspace_removed", commitDiff.Reason);
        Assert.False(status.Exists);
        Assert.Equal("workspace_removed", status.Reason);
        Assert.Null(fileContent.Base);
        Assert.Null(fileContent.Head);
        Assert.Equal("workspace_removed", fileContent.Reason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GivenIssueWorkspaceExistsButBranchMissing_WhenUserRequestsDiff_ThenMohistReportsBranchMissing()
    {
        var project = await CreateProjectWithRepositoryAsync();
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Workspace issue", projectId = project.Id });
        await StartIssueAndCreateWorkspaceDirectoryAsync(project, issue.Number);
        _fixture.Git.BranchExists = false;

        var diff = await _client.GetDataAsync<UnavailableDto>($"/api/projects/{project.Id}/issues/{issue.Number}/diff");

        Assert.False(diff.Available);
        Assert.Equal("branch_missing", diff.Reason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GivenIssueWorkspaceExists_WhenGitFails_ThenMohistReportsGitError()
    {
        var project = await CreateProjectWithRepositoryAsync();
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Workspace issue", projectId = project.Id });
        await StartIssueAndCreateWorkspaceDirectoryAsync(project, issue.Number);
        _fixture.Git.BranchExists = true;
        _fixture.Git.Throw = new InvalidOperationException("git exploded");

        var diff = await _client.GetDataAsync<UnavailableDto>($"/api/projects/{project.Id}/issues/{issue.Number}/diff");

        Assert.False(diff.Available);
        Assert.Equal("git_error", diff.Reason);
        Assert.Contains("git exploded", diff.Message);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GivenIssueWorkspaceExists_WhenUserRequestsDiff_ThenReturnsMergeBaseComparisonData()
    {
        var project = await CreateProjectWithRepositoryAsync("main");
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Workspace issue", projectId = project.Id });
        var runId = await StartIssueAndCreateWorkspaceDirectoryAsync(project, issue.Number);
        _fixture.Git.BranchExists = true;
        _fixture.Git.MergeBase = "abc123";
        _fixture.Git.AheadBehind = (2, 1);
        _fixture.Git.Diff = new GitDiffResult
        {
            Files = [new DiffFile("a.txt", 3, 1, "@@ -1 +1 @@\n-x\n+y\n", false)],
            TotalAdditions = 3,
            TotalDeletions = 1,
        };
        _fixture.Git.Commits =
        [
            new GitCommit("def456", "def456", "Second", "Author", "2024-01-02T00:00:00Z", []),
            new GitCommit("abc123", "abc123", "First", "Author", "2024-01-01T00:00:00Z", []),
        ];

        var diff = await _client.GetDataAsync<DiffDto>($"/api/projects/{project.Id}/issues/{issue.Number}/diff");

        Assert.True(diff.Available);
        Assert.Null(diff.Reason);
        Assert.Equal("main", diff.Base);
        Assert.Equal($"mohist/run-{runId}", diff.Head);
        Assert.Equal("abc123", diff.MergeBase);
        Assert.Equal(2, diff.Ahead);
        Assert.Equal(1, diff.Behind);
        Assert.False(diff.CanFastForward);
        Assert.Equal("merge-base", diff.Comparison);
        Assert.Equal(1, diff.Summary.FilesChanged);
        Assert.Equal(2, diff.Summary.Commits);
        Assert.Equal(3, diff.Summary.Additions);
        Assert.Equal(1, diff.Summary.Deletions);
        Assert.Single(diff.Files);
        Assert.Equal("a.txt", diff.Files[0].File);
        Assert.Single(diff.Patches);
        Assert.Equal("a.txt", diff.Patches[0].Path);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GivenIssueBranchIsBehindBase_WhenUserRequestsDiff_ThenComparisonIsMergeBaseAndExcludesBaseOnlyChanges()
    {
        var project = await CreateProjectWithRepositoryAsync("main");
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Behind base issue", projectId = project.Id });
        await StartIssueAndCreateWorkspaceDirectoryAsync(project, issue.Number);
        _fixture.Git.BranchExists = true;
        _fixture.Git.AheadBehind = (0, 3);
        _fixture.Git.Diff = new GitDiffResult
        {
            Files = [new DiffFile("issue.txt", 1, 0, "patch", false)],
            TotalAdditions = 1,
            TotalDeletions = 0,
        };

        var diff = await _client.GetDataAsync<DiffDto>($"/api/projects/{project.Id}/issues/{issue.Number}/diff");

        Assert.True(diff.Available);
        Assert.Equal("merge-base", diff.Comparison);
        Assert.False(diff.CanFastForward);
        Assert.Single(diff.Files);
        Assert.Equal("issue.txt", diff.Files[0].File);
        Assert.Single(diff.Patches);
        Assert.Equal("issue.txt", diff.Patches[0].Path);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GivenIssueWorkspaceExists_WhenUserRequestsCommits_ThenReturnsComparisonMetadataAndCommitRange()
    {
        var project = await CreateProjectWithRepositoryAsync("main");
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Commits issue", projectId = project.Id });
        var runId = await StartIssueAndCreateWorkspaceDirectoryAsync(project, issue.Number);
        _fixture.Git.BranchExists = true;
        _fixture.Git.MergeBase = "base123";
        _fixture.Git.AheadBehind = (2, 0);
        _fixture.Git.Diff = new GitDiffResult
        {
            Files = [new DiffFile("a.txt", 4, 2, "patch", false)],
            TotalAdditions = 4,
            TotalDeletions = 2,
        };
        _fixture.Git.Commits =
        [
            new GitCommit("head123", "head123", "Top", "Author", "2024-01-02T00:00:00Z", []),
            new GitCommit("mid123", "mid123", "Middle", "Author", "2024-01-01T00:00:00Z", []),
        ];

        var commits = await _client.GetDataAsync<CommitsDto>($"/api/projects/{project.Id}/issues/{issue.Number}/commits");

        Assert.True(commits.Available);
        Assert.Equal("main", commits.Base);
        Assert.Equal($"mohist/run-{runId}", commits.Head);
        Assert.Equal("base123", commits.MergeBase);
        Assert.Equal(2, commits.Ahead);
        Assert.Equal(0, commits.Behind);
        Assert.True(commits.CanFastForward);
        Assert.Equal("merge-base", commits.Comparison);
        Assert.Equal(1, commits.Summary.FilesChanged);
        Assert.Equal(2, commits.Summary.Commits);
        Assert.Equal(4, commits.Summary.Additions);
        Assert.Equal(2, commits.Summary.Deletions);
        Assert.Equal(2, commits.Commits.Length);
        Assert.Equal("head123", commits.Commits[0].Hash);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GivenIssueWorkspaceExists_WhenUserRequestsSingleCommitDiff_ThenReturnsSingleCommitDiff()
    {
        var project = await CreateProjectWithRepositoryAsync("main");
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Commit diff issue", projectId = project.Id });
        await StartIssueAndCreateWorkspaceDirectoryAsync(project, issue.Number);
        _fixture.Git.BranchExists = true;
        _fixture.Git.CommitDiffs["deadbeef"] = "@@ -1 +1 @@\n-x\n+y\n";

        var commitDiff = await _client.GetDataAsync<CommitDiffDto>($"/api/projects/{project.Id}/issues/{issue.Number}/commits/deadbeef/diff");

        Assert.True(commitDiff.Available);
        Assert.Null(commitDiff.Reason);
        Assert.Equal("deadbeef", commitDiff.Hash);
        Assert.Contains("+y", commitDiff.Diff);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GivenIssueWorkspaceExists_WhenUserRequestsStatus_ThenStatusHeadMatchesPerRunBranch()
    {
        var project = await CreateProjectWithRepositoryAsync("main");
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Status issue", projectId = project.Id });
        var runId = await StartIssueAndCreateWorkspaceDirectoryAsync(project, issue.Number);
        _fixture.Git.BranchExists = true;
        _fixture.Git.WorkspaceStatus = new WorkspaceStatus
        {
            Exists = true,
            Branch = $"mohist/run-{runId}",
            BaseBranch = "main",
            Ahead = 5,
            Behind = 0,
            RebaseInProgress = false,
            ConflictingFiles = [],
        };

        var status = await _client.GetDataAsync<StatusDto>($"/api/projects/{project.Id}/issues/{issue.Number}/workspace-status");

        Assert.True(status.Exists);
        Assert.Equal($"mohist/run-{runId}", status.Branch);
        Assert.Equal("main", status.BaseBranch);
        Assert.Equal(5, status.Ahead);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GivenIssueWorkspaceExists_WhenUserRequestsFileContent_ThenReturnsBaseAndHeadContent()
    {
        var project = await CreateProjectWithRepositoryAsync("main");
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "File content issue", projectId = project.Id });
        var runId = await StartIssueAndCreateWorkspaceDirectoryAsync(project, issue.Number);
        _fixture.Git.BranchExists = true;
        _fixture.Git.FileContents[("main", "a.txt")] = "base content";
        _fixture.Git.FileContents[($"mohist/run-{runId}", "a.txt")] = "head content";

        var fileContent = await _client.GetDataAsync<FileContentDto>($"/api/projects/{project.Id}/issues/{issue.Number}/file-content?path=a.txt");

        Assert.Equal("base content", fileContent.Base);
        Assert.Equal("head content", fileContent.Head);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GivenIssueWorkspaceExists_WhenUserCleansUpWorkspace_ThenWorkspaceIsRemoved()
    {
        var project = await CreateProjectWithRepositoryAsync("main");
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Cleanup issue", projectId = project.Id });
        await StartIssueAndCreateWorkspaceDirectoryAsync(project, issue.Number);
        _fixture.Git.WorkspaceRemoval = new WorkspaceRemovalResultDto(true, "removed", null, null, "Workspace removed").ToDomain();
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/stop");

        var cleanup = await _client.PostDataAsync<CleanupDto>($"/api/projects/{project.Id}/issues/{issue.Number}/cleanup");

        Assert.True(cleanup.Removed);
        Assert.Equal("Workspace removed", cleanup.Message);
        Assert.Contains(cleanup.Resources, r => r.Type == "workspace" && r.Status == "removed");
        Assert.Single(_fixture.Git.RemoveWorkspaceCalls);
        Assert.Equal(_fixture.Git.RemoveWorkspaceCalls[0].WorkspacePath, _fixture.Git.RemoveWorkspaceCalls[0].WorkspacePath);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GivenIssueWorkspaceIsAlreadyClean_WhenUserRunsCleanupAgain_ThenCleanupSucceedsAsNoOp()
    {
        var project = await CreateProjectWithRepositoryAsync("main");
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Missing cleanup issue", projectId = project.Id });
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start");
        _fixture.Git.WorkspaceRemoval = new WorkspaceRemovalResultDto(false, "missing", "/fake/workspace", "workspace_missing", "Workspace already removed").ToDomain();
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/stop");

        var cleanup = await _client.PostDataAsync<CleanupDto>($"/api/projects/{project.Id}/issues/{issue.Number}/cleanup");

        Assert.False(cleanup.Removed);
        Assert.Equal("Workspace already removed", cleanup.Message);
        Assert.Contains(cleanup.Resources, r => r.Type == "workspace" && r.Status == "missing");
    }

    private async Task<ProjectDto> CreateProjectWithRepositoryAsync(string baseBranch = "main")
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"workspace-{Guid.NewGuid():N}" });
        await _client.PostDataAsync<RepositoryDto>($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = "git@example.com:repo.git", baseBranch });
        return project;
    }

    private async Task<string> StartIssueAndCreateWorkspaceDirectoryAsync(ProjectDto project, int issueNumber)
    {
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issueNumber}/start");
        var path = Mohist.Server.Infrastructure.Workspace.MohistWorkspaceLayout.IssueWorkspacePath(_fixture.RunnerRoot, project.Name, issueNumber);
        Directory.CreateDirectory(path);
        var issue = await _client.GetDataAsync<WorkflowStatusDto>($"/api/projects/{project.Id}/issues/{issueNumber}/workflow/status");
        return issue.WorkflowRunId ?? throw new InvalidOperationException("Issue started but no workflow run id was returned");
    }

    private sealed record WorkflowStatusDto(string? WorkflowRunId);

    private sealed record IssueDto(int Number);
    private sealed record ProjectDto(string Id, string Name);
    private sealed record RepositoryDto(string Name);
    private sealed record UnavailableDto(bool Available, string Reason, string Message);
    private sealed record CommitDiffUnavailableDto(bool Available, string Reason, string Message, string Hash, string Diff);
    private sealed record DiffDto(bool Available, string? Reason, string? Message, string Base, string Head, string MergeBase, int Ahead, int Behind, bool CanFastForward, string Comparison, SummaryDto Summary, DiffFileDto[] Files, PatchDto[] Patches);
    private sealed record SummaryDto(int FilesChanged, int Commits, int Additions, int Deletions);
    private sealed record DiffFileDto(string File, int Additions, int Deletions, string Diff, bool IsBinary);
    private sealed record PatchDto(string Path, string Diff);
    private sealed record CommitsDto(bool Available, string? Reason, string? Message, string Base, string Head, string MergeBase, int Ahead, int Behind, bool CanFastForward, string Comparison, SummaryDto Summary, GitCommitDto[] Commits);
    private sealed record GitCommitDto(string Hash, string ShortHash, string Message, string Author, string Date, string[] Files);
    private sealed record CommitDiffDto(bool Available, string? Reason, string? Message, string Hash, string Diff);
    private sealed record StatusDto(bool Exists, string? Reason, string? Branch, string? BaseBranch, int Ahead, int Behind, bool RebaseInProgress, string[] ConflictingFiles);
    private sealed record FileContentDto(string? Base, string? Head, string? Reason);
    private sealed record CleanupDto(bool Removed, string Message, CleanupResourceDto[] Resources);
    private sealed record CleanupResourceDto(string Type, string Status, string? Path, string? Reason);
    private sealed record WorkspaceRemovalResultDto(bool Removed, string Status, string? Path, string? Reason, string Message)
    {
        public Mohist.Server.Infrastructure.Workspace.WorkspaceRemovalResult ToDomain() => new(Removed, Status, Path, Reason, Message);
    }
}
