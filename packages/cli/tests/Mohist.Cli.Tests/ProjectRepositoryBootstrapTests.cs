using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class ProjectRepositoryBootstrapTests
{
    private const string WorkTreeRoot = "/work/product-a";

    [Fact]
    public async Task TryResolveAsync_MissingPath_ReportsPathRequired()
    {
        var fs = new FakeFileSystem();
        var executor = new FakeCommandExecutor();

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync(string.Empty, fs, executor);

        var failure = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Failure>(outcome);
        Assert.Contains("--path", failure.Message, StringComparison.Ordinal);
        Assert.Empty(executor.Invocations);
    }

    [Fact]
    public async Task TryResolveAsync_WhitespacePath_ReportsPathRequired()
    {
        var fs = new FakeFileSystem();
        var executor = new FakeCommandExecutor();

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync("   ", fs, executor);

        var failure = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Failure>(outcome);
        Assert.Contains("--path", failure.Message, StringComparison.Ordinal);
        Assert.Empty(executor.Invocations);
    }

    [Fact]
    public async Task TryResolveAsync_NonexistentDirectory_ReportsDirectoryMissing()
    {
        var fs = new FakeFileSystem();
        var executor = new FakeCommandExecutor();

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync(WorkTreeRoot, fs, executor);

        var failure = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Failure>(outcome);
        Assert.Contains(WorkTreeRoot, failure.Message, StringComparison.Ordinal);
        Assert.Empty(executor.Invocations);
    }

    [Fact]
    public async Task TryResolveAsync_DirectoryWithoutGitMarker_ReportsNotGitRepo()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(WorkTreeRoot);
        var executor = new FakeCommandExecutor();

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync(WorkTreeRoot, fs, executor);

        var failure = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Failure>(outcome);
        Assert.Contains("Git", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(executor.Invocations);
    }

    [Fact]
    public async Task TryResolveAsync_RevParseTopLevelFails_ReportsNotGitRepo()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(WorkTreeRoot);
        fs.CreateDirectory(Path.Combine(WorkTreeRoot, ".git"));
        var executor = new FakeCommandExecutor();
        executor.QueueForFile("git", 128, "", "fatal: not a git repository");

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync(WorkTreeRoot, fs, executor);

        var failure = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Failure>(outcome);
        Assert.Contains("Git", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryResolveAsync_NoCommitYet_ReportsHeadUnreachable()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(WorkTreeRoot);
        fs.CreateDirectory(Path.Combine(WorkTreeRoot, ".git"));
        var executor = new FakeCommandExecutor();
        executor.QueueForFile("git", 0, WorkTreeRoot + "\n");
        executor.QueueForFile("git", 128, "", "fatal: ambiguous argument 'HEAD'");

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync(WorkTreeRoot, fs, executor);

        var failure = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Failure>(outcome);
        Assert.Contains("HEAD", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryResolveAsync_MissingOriginRemote_ReportsOriginMissing()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(WorkTreeRoot);
        fs.CreateDirectory(Path.Combine(WorkTreeRoot, ".git"));
        var executor = new FakeCommandExecutor();
        executor.QueueForFile("git", 0, WorkTreeRoot + "\n");
        executor.QueueForFile("git", 0, "abc123\n");
        executor.QueueForFile("git", 128, "", "fatal: No such remote 'origin'");

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync(WorkTreeRoot, fs, executor);

        var failure = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Failure>(outcome);
        Assert.Contains("origin", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryResolveAsync_BlankOriginUrl_ReportsOriginMissing()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(WorkTreeRoot);
        fs.CreateDirectory(Path.Combine(WorkTreeRoot, ".git"));
        var executor = new FakeCommandExecutor();
        executor.QueueForFile("git", 0, WorkTreeRoot + "\n");
        executor.QueueForFile("git", 0, "abc123\n");
        executor.QueueForFile("git", 0, "   \n");

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync(WorkTreeRoot, fs, executor);

        var failure = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Failure>(outcome);
        Assert.Contains("origin", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryResolveAsync_NoResolvableBranch_ReportsBranchMissing()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(WorkTreeRoot);
        fs.CreateDirectory(Path.Combine(WorkTreeRoot, ".git"));
        var executor = new FakeCommandExecutor();
        executor.QueueForFile("git", 0, WorkTreeRoot + "\n");
        executor.QueueForFile("git", 0, "abc123\n");
        executor.QueueForFile("git", 0, "git@example.com:team/product-a.git\n");
        executor.QueueForFile("git", 128, "", "fatal: no such ref");
        executor.QueueForFile("git", 128, "", "fatal: not on a branch");

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync(WorkTreeRoot, fs, executor);

        var failure = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Failure>(outcome);
        Assert.Contains("branch", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryResolveAsync_OriginHeadPointsToBranch_ReturnsBranchWithoutPrefix()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(WorkTreeRoot);
        fs.CreateDirectory(Path.Combine(WorkTreeRoot, ".git"));
        var executor = new FakeCommandExecutor();
        executor.QueueForFile("git", 0, WorkTreeRoot + "\n");
        executor.QueueForFile("git", 0, "abc123\n");
        executor.QueueForFile("git", 0, "git@example.com:team/product-a.git\n");
        executor.QueueForFile("git", 0, "origin/main\n");

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync(WorkTreeRoot, fs, executor);

        var success = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Success>(outcome);
        Assert.Equal("main", success.Result.BaseBranch);
        Assert.Equal("product-a", success.Result.RepositoryName);
        Assert.Equal("git@example.com:team/product-a.git", success.Result.GitUrl);
        Assert.Equal(WorkTreeRoot, success.Result.WorkTreeRoot);
    }

    [Fact]
    public async Task TryResolveAsync_OriginHeadMissing_FallsBackToCheckedOutBranch()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(WorkTreeRoot);
        fs.CreateDirectory(Path.Combine(WorkTreeRoot, ".git"));
        var executor = new FakeCommandExecutor();
        executor.QueueForFile("git", 0, WorkTreeRoot + "\n");
        executor.QueueForFile("git", 0, "abc123\n");
        executor.QueueForFile("git", 0, "git@example.com:team/product-a.git\n");
        executor.QueueForFile("git", 128, "", "fatal: no origin/HEAD");
        executor.QueueForFile("git", 0, "develop\n");

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync(WorkTreeRoot, fs, executor);

        var success = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Success>(outcome);
        Assert.Equal("develop", success.Result.BaseBranch);
    }

    [Fact]
    public async Task TryResolveAsync_OriginHeadBlank_FallsBackToCheckedOutBranch()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(WorkTreeRoot);
        fs.CreateDirectory(Path.Combine(WorkTreeRoot, ".git"));
        var executor = new FakeCommandExecutor();
        executor.QueueForFile("git", 0, WorkTreeRoot + "\n");
        executor.QueueForFile("git", 0, "abc123\n");
        executor.QueueForFile("git", 0, "git@example.com:team/product-a.git\n");
        executor.QueueForFile("git", 0, "  \n");
        executor.QueueForFile("git", 0, "release\n");

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync(WorkTreeRoot, fs, executor);

        var success = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Success>(outcome);
        Assert.Equal("release", success.Result.BaseBranch);
    }

    [Fact]
    public async Task TryResolveAsync_DoesNotLeakPathOrRemoteFieldsThroughInvocations()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(WorkTreeRoot);
        fs.CreateDirectory(Path.Combine(WorkTreeRoot, ".git"));
        var executor = new FakeCommandExecutor();
        executor.QueueForFile("git", 0, WorkTreeRoot + "\n");
        executor.QueueForFile("git", 0, "abc123\n");
        executor.QueueForFile("git", 0, "git@example.com:team/product-a.git\n");
        executor.QueueForFile("git", 0, "origin/main\n");

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync(WorkTreeRoot, fs, executor);

        var success = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Success>(outcome);
        var fieldNames = string.Join(',',
            success.Result.GetType().GetProperties().Select(p => p.Name));
        Assert.DoesNotContain("Path", fieldNames, StringComparison.Ordinal);
        Assert.DoesNotContain("Remote", fieldNames, StringComparison.Ordinal);
        Assert.DoesNotContain("Origin", fieldNames, StringComparison.Ordinal);
    }
}