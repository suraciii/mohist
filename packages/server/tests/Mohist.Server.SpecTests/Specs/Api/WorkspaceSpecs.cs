using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Api;

[Collection("IntegrationApi")]
public class WorkspaceSpecs
{
    private readonly HttpClient _client;
    private readonly MohistIntegrationFixture _fixture;

    public WorkspaceSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
        _fixture.RunnerWorkspace.Reset();
    }

    [Fact]
    public async Task GivenIssueHasNotStarted_WhenUserOpensReviewViews_ThenMohistExplainsThatWorkHasNotStarted()
    {
        var project = await CreateProjectWithRepositoryAsync();
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Workspace issue", projectId = project.Id, isDraft = false });

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

    [Fact]
    public async Task GivenIssueWorkspaceIsRemoved_WhenUserOpensReviewViews_ThenMohistReportsWorkspaceRemoved()
    {
        var project = await CreateProjectWithRepositoryAsync();
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Workspace issue", projectId = project.Id, isDraft = false });
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start");
        await DispatchEventsAsync();

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

    [Fact]
    public async Task GivenRunnerIsUnavailable_WhenUserOpensReviewViews_ThenMohistReportsRunnerUnavailable()
    {
        var project = await CreateProjectWithRepositoryAsync();
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Runner unavailable issue", projectId = project.Id });
        await StartIssueAndCreateWorkspaceDirectoryAsync(project, issue.Number);
        _fixture.RunnerWorkspace.WorkspaceStatus = new WorkspaceStatus { Exists = false, Reason = "runner_unavailable" };

        var diff = await _client.GetDataAsync<UnavailableDto>($"/api/projects/{project.Id}/issues/{issue.Number}/diff");
        var commits = await _client.GetDataAsync<UnavailableDto>($"/api/projects/{project.Id}/issues/{issue.Number}/commits");
        var status = await _client.GetDataAsync<StatusDto>($"/api/projects/{project.Id}/issues/{issue.Number}/workspace-status");

        Assert.Equal("runner_unavailable", diff.Reason);
        Assert.Equal("runner_unavailable", commits.Reason);
        Assert.False(status.Exists);
        Assert.Equal("runner_unavailable", status.Reason);
    }

    [Fact]
    public async Task GivenIssueWorkspaceExistsButBranchMissing_WhenUserRequestsDiff_ThenMohistReportsBranchMissing()
    {
        var project = await CreateProjectWithRepositoryAsync();
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Workspace issue", projectId = project.Id });
        await StartIssueAndCreateWorkspaceDirectoryAsync(project, issue.Number);
        _fixture.RunnerWorkspace.WorkspaceStatus = new WorkspaceStatus { Exists = false, Reason = "branch_missing" };

        var diff = await _client.GetDataAsync<UnavailableDto>($"/api/projects/{project.Id}/issues/{issue.Number}/diff");

        Assert.False(diff.Available);
        Assert.Equal("branch_missing", diff.Reason);
    }

    [Fact]
    public async Task GivenIssueWorkspaceExists_WhenGitFails_ThenMohistReportsGitError()
    {
        var project = await CreateProjectWithRepositoryAsync();
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Workspace issue", projectId = project.Id });
        await StartIssueAndCreateWorkspaceDirectoryAsync(project, issue.Number);
        _fixture.RunnerWorkspace.Throw = new InvalidOperationException("git exploded");

        var diff = await _client.GetDataAsync<UnavailableDto>($"/api/projects/{project.Id}/issues/{issue.Number}/diff");

        Assert.False(diff.Available);
        Assert.Equal("git_error", diff.Reason);
        Assert.Contains("git exploded", diff.Message);
    }

    [Fact]
    public async Task GivenIssueWorkspaceExists_WhenUserRequestsDiff_ThenReturnsMergeBaseComparisonData()
    {
        var project = await CreateProjectWithRepositoryAsync("main");
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Workspace issue", projectId = project.Id });
        var runId = await StartIssueAndCreateWorkspaceDirectoryAsync(project, issue.Number);
        _fixture.RunnerWorkspace.WorkspaceStatus = AvailableStatus(runId, "main", ahead: 2, behind: 1);
        _fixture.RunnerWorkspace.Diff = new RunnerWorkspaceDiffResult(
            "main",
            $"mohist/run-{runId}",
            "abc123",
            2,
            1,
            2,
            3,
            1,
            [new DiffFile("a.txt", 3, 1, "@@ -1 +1 @@\n-x\n+y\n", false)]);

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

    [Fact]
    public async Task GivenIssueBranchIsBehindBase_WhenUserRequestsDiff_ThenComparisonIsMergeBaseAndExcludesBaseOnlyChanges()
    {
        var project = await CreateProjectWithRepositoryAsync("main");
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Behind base issue", projectId = project.Id });
        var runId = await StartIssueAndCreateWorkspaceDirectoryAsync(project, issue.Number);
        _fixture.RunnerWorkspace.WorkspaceStatus = AvailableStatus(runId, "main", ahead: 0, behind: 3);
        _fixture.RunnerWorkspace.Diff = new RunnerWorkspaceDiffResult(
            "main",
            $"mohist/run-{runId}",
            "merge-base",
            0,
            3,
            0,
            1,
            0,
            [new DiffFile("issue.txt", 1, 0, "patch", false)]);

        var diff = await _client.GetDataAsync<DiffDto>($"/api/projects/{project.Id}/issues/{issue.Number}/diff");

        Assert.True(diff.Available);
        Assert.Equal("merge-base", diff.Comparison);
        Assert.False(diff.CanFastForward);
        Assert.Single(diff.Files);
        Assert.Equal("issue.txt", diff.Files[0].File);
        Assert.Single(diff.Patches);
        Assert.Equal("issue.txt", diff.Patches[0].Path);
    }

    [Fact]
    public async Task GivenIssueWorkspaceExists_WhenUserRequestsCommits_ThenReturnsComparisonMetadataAndCommitRange()
    {
        var project = await CreateProjectWithRepositoryAsync("main");
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Commits issue", projectId = project.Id });
        var runId = await StartIssueAndCreateWorkspaceDirectoryAsync(project, issue.Number);
        _fixture.RunnerWorkspace.WorkspaceStatus = AvailableStatus(runId, "main", ahead: 2, behind: 0);
        _fixture.RunnerWorkspace.Commits = new RunnerWorkspaceCommitsResult(
            "main",
            $"mohist/run-{runId}",
            "base123",
            2,
            0,
            1,
            4,
            2,
            [
                new GitCommit("head123", "head123", "Top", "Author", "2024-01-02T00:00:00Z", []),
                new GitCommit("mid123", "mid123", "Middle", "Author", "2024-01-01T00:00:00Z", []),
            ]);

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

    [Fact]
    public async Task GivenIssueWorkspaceExists_WhenUserRequestsSingleCommitDiff_ThenReturnsSingleCommitDiff()
    {
        var project = await CreateProjectWithRepositoryAsync("main");
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Commit diff issue", projectId = project.Id });
        var runId = await StartIssueAndCreateWorkspaceDirectoryAsync(project, issue.Number);
        _fixture.RunnerWorkspace.WorkspaceStatus = AvailableStatus(runId, "main");
        _fixture.RunnerWorkspace.CommitDiffs["deadbeef"] = new RunnerWorkspaceCommitDiffResult("@@ -1 +1 @@\n-x\n+y\n");

        var commitDiff = await _client.GetDataAsync<CommitDiffDto>($"/api/projects/{project.Id}/issues/{issue.Number}/commits/deadbeef/diff");

        Assert.True(commitDiff.Available);
        Assert.Null(commitDiff.Reason);
        Assert.Equal("deadbeef", commitDiff.Hash);
        Assert.Contains("+y", commitDiff.Diff);
    }

    [Fact]
    public async Task GivenIssueWorkspaceExists_WhenUserRequestsStatus_ThenStatusHeadMatchesPerRunBranch()
    {
        var project = await CreateProjectWithRepositoryAsync("main");
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Status issue", projectId = project.Id });
        var runId = await StartIssueAndCreateWorkspaceDirectoryAsync(project, issue.Number);
        _fixture.RunnerWorkspace.WorkspaceStatus = new WorkspaceStatus
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

    [Fact]
    public async Task GivenIssueWorkspaceExists_WhenUserRequestsFileContent_ThenReturnsBaseAndHeadContent()
    {
        var project = await CreateProjectWithRepositoryAsync("main");
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "File content issue", projectId = project.Id });
        var runId = await StartIssueAndCreateWorkspaceDirectoryAsync(project, issue.Number);
        _fixture.RunnerWorkspace.WorkspaceStatus = AvailableStatus(runId, "main");
        _fixture.RunnerWorkspace.FileContent = new RunnerWorkspaceFileContentResult("base content", "head content");

        var fileContent = await _client.GetDataAsync<FileContentDto>($"/api/projects/{project.Id}/issues/{issue.Number}/file-content?path=a.txt");

        Assert.Equal("base content", fileContent.Base);
        Assert.Equal("head content", fileContent.Head);
    }

    [Fact]
    public async Task GivenIssueWorkspaceExists_WhenUserCleansUpWorkspace_ThenWorkspaceIsRemoved()
    {
        var project = await CreateProjectWithRepositoryAsync("main");
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Cleanup issue", projectId = project.Id });
        await StartIssueAndCreateWorkspaceDirectoryAsync(project, issue.Number);
        var expectedPath = MohistWorkspaceLayout.IssueWorkspacePath(_fixture.RunnerRoot, project.Name, issue.Number);
        _fixture.RunnerWorkspace.WorkspaceRemoval = new WorkspaceRemovalResultDto(true, "removed", expectedPath, null, "Workspace removed").ToDomain();
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/stop");

        var cleanup = await _client.PostDataAsync<CleanupDto>($"/api/projects/{project.Id}/issues/{issue.Number}/cleanup");

        Assert.True(cleanup.Removed);
        Assert.Equal("Workspace removed", cleanup.Message);
        Assert.Contains(cleanup.Resources, r => r.Type == "workspace" && r.Status == "removed");
        Assert.Single(_fixture.RunnerWorkspace.RemoveWorkspaceCalls);
        Assert.Equal(expectedPath, _fixture.RunnerWorkspace.RemoveWorkspaceCalls[0].WorkspacePath);
    }

    [Fact]
    public async Task GivenIssueWorkspaceIsAlreadyClean_WhenUserRunsCleanupAgain_ThenCleanupSucceedsAsNoOp()
    {
        var project = await CreateProjectWithRepositoryAsync("main");
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Missing cleanup issue", projectId = project.Id, isDraft = false });
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start");
        await DispatchEventsAsync();
        _fixture.RunnerWorkspace.WorkspaceRemoval = new WorkspaceRemovalResultDto(false, "missing", "/fake/workspace", "workspace_missing", "Workspace already removed").ToDomain();
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/stop");

        var cleanup = await _client.PostDataAsync<CleanupDto>($"/api/projects/{project.Id}/issues/{issue.Number}/cleanup");

        Assert.False(cleanup.Removed);
        Assert.Equal("Workspace already removed", cleanup.Message);
        Assert.Contains(cleanup.Resources, r => r.Type == "workspace" && r.Status == "missing");
    }

    private async Task<ProjectDto> CreateProjectWithRepositoryAsync(string baseBranch = "main")
    {
        return await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>(
            "/api/projects",
            $"workspace-{Guid.NewGuid():N}",
            repoName: "main",
            gitUrl: "git@example.com:repo.git",
            baseBranch: baseBranch);
    }

    private async Task<string> StartIssueAndCreateWorkspaceDirectoryAsync(ProjectDto project, int issueNumber)
    {
        await _client.PatchAsJsonAsync($"/api/projects/{project.Id}/issues/{issueNumber}", new { isDraft = false });
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issueNumber}/start");
        await DispatchEventsAsync();
        var issue = await _client.GetDataAsync<WorkflowStatusDto>($"/api/projects/{project.Id}/issues/{issueNumber}/workflow/status");
        var workflowRunId = issue.WorkflowRunId ?? throw new InvalidOperationException("Issue started but no workflow run id was returned");
        var expectedRepository = Assert.Single(project.Repositories);
        Assert.True(expectedRepository.IsDefault);

        using var scope = _fixture.Services.CreateScope();
        var workflowQuerier = scope.ServiceProvider.GetRequiredService<WorkflowQuerier>();
        var variables = await workflowQuerier.GetEffectiveVariablesAsync(workflowRunId);
        var repository = variables.GetProperty("repository");
        Assert.Equal(expectedRepository.Name, repository.GetProperty("name").GetString());
        Assert.Equal(expectedRepository.GitUrl, repository.GetProperty("gitUrl").GetString());
        Assert.Equal(expectedRepository.BaseBranch, repository.GetProperty("baseBranch").GetString());
        return workflowRunId;
    }

    private Task DispatchEventsAsync() =>
        _fixture.Grains.GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global).DispatchNowAsync();

    private static WorkspaceStatus AvailableStatus(string runId, string baseBranch, int ahead = 0, int behind = 0) => new()
    {
        Exists = true,
        Branch = $"mohist/run-{runId}",
        BaseBranch = baseBranch,
        Ahead = ahead,
        Behind = behind,
        RebaseInProgress = false,
        ConflictingFiles = [],
    };

    private sealed record WorkflowStatusDto(string? WorkflowRunId);

    private sealed record IssueDto(int Number);
    private sealed record ProjectDto(string Id, string Name, RepositoryDto[] Repositories);
    private sealed record RepositoryDto(string Name, string GitUrl, string BaseBranch, bool IsDefault);
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
