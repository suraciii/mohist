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
    public async Task TryResolveAsync_DirectoryOutsideGitWorkTree_ReportsNotGitRepo()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(WorkTreeRoot);
        var executor = new FakeCommandExecutor();
        QueueGit(executor, WorkTreeRoot, ["rev-parse", "--show-toplevel"], 128, "", "fatal: not a git repository");

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync(WorkTreeRoot, fs, executor);

        var failure = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Failure>(outcome);
        Assert.Contains("Git", failure.Message, StringComparison.OrdinalIgnoreCase);
        executor.AssertExpectedCommandsExecuted();
    }

    [Fact]
    public async Task TryResolveAsync_RevParseTopLevelFails_ReportsNotGitRepo()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(WorkTreeRoot);
        fs.CreateDirectory(Path.Combine(WorkTreeRoot, ".git"));
        var executor = new FakeCommandExecutor();
        QueueGit(executor, WorkTreeRoot, ["rev-parse", "--show-toplevel"], 128, "", "fatal: not a git repository");

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync(WorkTreeRoot, fs, executor);

        var failure = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Failure>(outcome);
        Assert.Contains("Git", failure.Message, StringComparison.OrdinalIgnoreCase);
        executor.AssertExpectedCommandsExecuted();
    }

    [Fact]
    public async Task TryResolveAsync_NoCommitYet_ReportsHeadUnreachable()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(WorkTreeRoot);
        fs.CreateDirectory(Path.Combine(WorkTreeRoot, ".git"));
        var executor = new FakeCommandExecutor();
        QueueGit(executor, WorkTreeRoot, ["rev-parse", "--show-toplevel"], 0, WorkTreeRoot + "\n");
        QueueGit(executor, WorkTreeRoot, ["rev-parse", "HEAD"], 128, "", "fatal: ambiguous argument 'HEAD'");

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync(WorkTreeRoot, fs, executor);

        var failure = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Failure>(outcome);
        Assert.Contains("HEAD", failure.Message, StringComparison.OrdinalIgnoreCase);
        executor.AssertExpectedCommandsExecuted();
    }

    [Fact]
    public async Task TryResolveAsync_MissingOriginRemote_ReportsOriginMissing()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(WorkTreeRoot);
        fs.CreateDirectory(Path.Combine(WorkTreeRoot, ".git"));
        var executor = new FakeCommandExecutor();
        QueueGit(executor, WorkTreeRoot, ["rev-parse", "--show-toplevel"], 0, WorkTreeRoot + "\n");
        QueueGit(executor, WorkTreeRoot, ["rev-parse", "HEAD"], 0, "abc123\n");
        QueueGit(executor, WorkTreeRoot, ["remote", "get-url", "origin"], 128, "", "fatal: No such remote 'origin'");

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync(WorkTreeRoot, fs, executor);

        var failure = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Failure>(outcome);
        Assert.Contains("origin", failure.Message, StringComparison.OrdinalIgnoreCase);
        executor.AssertExpectedCommandsExecuted();
    }

    [Fact]
    public async Task TryResolveAsync_BlankOriginUrl_ReportsOriginMissing()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(WorkTreeRoot);
        fs.CreateDirectory(Path.Combine(WorkTreeRoot, ".git"));
        var executor = new FakeCommandExecutor();
        QueueGit(executor, WorkTreeRoot, ["rev-parse", "--show-toplevel"], 0, WorkTreeRoot + "\n");
        QueueGit(executor, WorkTreeRoot, ["rev-parse", "HEAD"], 0, "abc123\n");
        QueueGit(executor, WorkTreeRoot, ["remote", "get-url", "origin"], 0, "   \n");

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync(WorkTreeRoot, fs, executor);

        var failure = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Failure>(outcome);
        Assert.Contains("origin", failure.Message, StringComparison.OrdinalIgnoreCase);
        executor.AssertExpectedCommandsExecuted();
    }

    [Fact]
    public async Task TryResolveAsync_RelativeOriginUrl_ReportsRunnerInaccessibleOrigin()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(WorkTreeRoot);
        var executor = new FakeCommandExecutor();
        QueueGit(executor, WorkTreeRoot, ["rev-parse", "--show-toplevel"], 0, WorkTreeRoot + "\n");
        QueueGit(executor, WorkTreeRoot, ["rev-parse", "HEAD"], 0, "abc123\n");
        QueueGit(executor, WorkTreeRoot, ["remote", "get-url", "origin"], 0, "../remote.git\n");

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync(WorkTreeRoot, fs, executor);

        var failure = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Failure>(outcome);
        Assert.Contains("Runner", failure.Message, StringComparison.OrdinalIgnoreCase);
        executor.AssertExpectedCommandsExecuted();
    }

    [Theory]
    [InlineData("ssh://localhost/remote.git")]
    [InlineData("https://127.0.0.1/remote.git")]
    [InlineData("git@localhost:remote.git")]
    public async Task TryResolveAsync_LoopbackOriginUrl_ReportsRunnerInaccessibleOrigin(string origin)
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(WorkTreeRoot);
        var executor = new FakeCommandExecutor();
        QueueGit(executor, WorkTreeRoot, ["rev-parse", "--show-toplevel"], 0, WorkTreeRoot + "\n");
        QueueGit(executor, WorkTreeRoot, ["rev-parse", "HEAD"], 0, "abc123\n");
        QueueGit(executor, WorkTreeRoot, ["remote", "get-url", "origin"], 0, origin + "\n");

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync(WorkTreeRoot, fs, executor);

        var failure = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Failure>(outcome);
        Assert.Contains("Runner", failure.Message, StringComparison.OrdinalIgnoreCase);
        executor.AssertExpectedCommandsExecuted();
    }

    [Fact]
    public async Task TryResolveAsync_NoResolvableBranch_ReportsBranchMissing()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(WorkTreeRoot);
        fs.CreateDirectory(Path.Combine(WorkTreeRoot, ".git"));
        var executor = new FakeCommandExecutor();
        QueueGit(executor, WorkTreeRoot, ["rev-parse", "--show-toplevel"], 0, WorkTreeRoot + "\n");
        QueueGit(executor, WorkTreeRoot, ["rev-parse", "HEAD"], 0, "abc123\n");
        QueueGit(executor, WorkTreeRoot, ["remote", "get-url", "origin"], 0, "git@example.com:team/product-a.git\n");
        QueueGit(executor, WorkTreeRoot, ["symbolic-ref", "--short", "refs/remotes/origin/HEAD"], 128, "", "fatal: no such ref");
        QueueGit(executor, WorkTreeRoot, ["symbolic-ref", "--short", "HEAD"], 128, "", "fatal: not on a branch");

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync(WorkTreeRoot, fs, executor);

        var failure = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Failure>(outcome);
        Assert.Contains("branch", failure.Message, StringComparison.OrdinalIgnoreCase);
        executor.AssertExpectedCommandsExecuted();
    }

    [Fact]
    public async Task TryResolveAsync_OriginHeadPointsToBranch_ReturnsBranchWithoutPrefix()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(WorkTreeRoot);
        fs.CreateDirectory(Path.Combine(WorkTreeRoot, ".git"));
        var executor = new FakeCommandExecutor();
        QueueGit(executor, WorkTreeRoot, ["rev-parse", "--show-toplevel"], 0, WorkTreeRoot + "\n");
        QueueGit(executor, WorkTreeRoot, ["rev-parse", "HEAD"], 0, "abc123\n");
        QueueGit(executor, WorkTreeRoot, ["remote", "get-url", "origin"], 0, "git@example.com:team/product-a.git\n");
        QueueGit(executor, WorkTreeRoot, ["symbolic-ref", "--short", "refs/remotes/origin/HEAD"], 0, "origin/main\n");

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync(WorkTreeRoot, fs, executor);

        var success = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Success>(outcome);
        Assert.Equal("main", success.Result.BaseBranch);
        Assert.Equal("product-a", success.Result.RepositoryName);
        Assert.Equal("git@example.com:team/product-a.git", success.Result.GitUrl);
        Assert.Equal(WorkTreeRoot, success.Result.WorkTreeRoot);
        executor.AssertExpectedCommandsExecuted();
    }

    [Fact]
    public async Task TryResolveAsync_OriginHeadMissing_FallsBackToCheckedOutBranch()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(WorkTreeRoot);
        fs.CreateDirectory(Path.Combine(WorkTreeRoot, ".git"));
        var executor = new FakeCommandExecutor();
        QueueGit(executor, WorkTreeRoot, ["rev-parse", "--show-toplevel"], 0, WorkTreeRoot + "\n");
        QueueGit(executor, WorkTreeRoot, ["rev-parse", "HEAD"], 0, "abc123\n");
        QueueGit(executor, WorkTreeRoot, ["remote", "get-url", "origin"], 0, "git@example.com:team/product-a.git\n");
        QueueGit(executor, WorkTreeRoot, ["symbolic-ref", "--short", "refs/remotes/origin/HEAD"], 128, "", "fatal: no origin/HEAD");
        QueueGit(executor, WorkTreeRoot, ["symbolic-ref", "--short", "HEAD"], 0, "develop\n");

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync(WorkTreeRoot, fs, executor);

        var success = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Success>(outcome);
        Assert.Equal("develop", success.Result.BaseBranch);
        executor.AssertExpectedCommandsExecuted();
    }

    [Fact]
    public async Task TryResolveAsync_OriginHeadBlank_FallsBackToCheckedOutBranch()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(WorkTreeRoot);
        fs.CreateDirectory(Path.Combine(WorkTreeRoot, ".git"));
        var executor = new FakeCommandExecutor();
        QueueGit(executor, WorkTreeRoot, ["rev-parse", "--show-toplevel"], 0, WorkTreeRoot + "\n");
        QueueGit(executor, WorkTreeRoot, ["rev-parse", "HEAD"], 0, "abc123\n");
        QueueGit(executor, WorkTreeRoot, ["remote", "get-url", "origin"], 0, "git@example.com:team/product-a.git\n");
        QueueGit(executor, WorkTreeRoot, ["symbolic-ref", "--short", "refs/remotes/origin/HEAD"], 0, "  \n");
        QueueGit(executor, WorkTreeRoot, ["symbolic-ref", "--short", "HEAD"], 0, "release\n");

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync(WorkTreeRoot, fs, executor);

        var success = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Success>(outcome);
        Assert.Equal("release", success.Result.BaseBranch);
        executor.AssertExpectedCommandsExecuted();
    }

    [Fact]
    public async Task TryResolveAsync_DoesNotLeakPathOrRemoteFieldsThroughInvocations()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory(WorkTreeRoot);
        fs.CreateDirectory(Path.Combine(WorkTreeRoot, ".git"));
        var executor = new FakeCommandExecutor();
        QueueGit(executor, WorkTreeRoot, ["rev-parse", "--show-toplevel"], 0, WorkTreeRoot + "\n");
        QueueGit(executor, WorkTreeRoot, ["rev-parse", "HEAD"], 0, "abc123\n");
        QueueGit(executor, WorkTreeRoot, ["remote", "get-url", "origin"], 0, "git@example.com:team/product-a.git\n");
        QueueGit(executor, WorkTreeRoot, ["symbolic-ref", "--short", "refs/remotes/origin/HEAD"], 0, "origin/main\n");

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync(WorkTreeRoot, fs, executor);

        var success = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Success>(outcome);
        var fieldNames = string.Join(',',
            success.Result.GetType().GetProperties().Select(p => p.Name));
        Assert.DoesNotContain("Path", fieldNames, StringComparison.Ordinal);
        Assert.DoesNotContain("Remote", fieldNames, StringComparison.Ordinal);
        Assert.DoesNotContain("Origin", fieldNames, StringComparison.Ordinal);
        executor.AssertExpectedCommandsExecuted();
    }

    [Fact]
    public async Task TryResolveAsync_NestedWorkTreeDirectory_UsesResolvedRoot()
    {
        var nestedPath = Path.Combine(WorkTreeRoot, "src");
        var fs = new FakeFileSystem();
        fs.CreateDirectory(WorkTreeRoot);
        fs.CreateDirectory(nestedPath);
        var executor = new FakeCommandExecutor();
        QueueGit(executor, nestedPath, ["rev-parse", "--show-toplevel"], 0, WorkTreeRoot + "\n");
        QueueGit(executor, WorkTreeRoot, ["rev-parse", "HEAD"], 0, "abc123\n");
        QueueGit(executor, WorkTreeRoot, ["remote", "get-url", "origin"], 0, "git@example.com:team/product-a.git\n");
        QueueGit(executor, WorkTreeRoot, ["symbolic-ref", "--short", "refs/remotes/origin/HEAD"], 0, "origin/main\n");

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync(nestedPath, fs, executor);

        var success = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Success>(outcome);
        Assert.Equal(WorkTreeRoot, success.Result.WorkTreeRoot);
        Assert.Equal("product-a", success.Result.RepositoryName);
        executor.AssertExpectedCommandsExecuted();
    }

    [Fact]
    public async Task TryResolveAsync_PosixBackslashInPath_PreservesDirectoryAndRepositoryName()
    {
        const string backslashRoot = "/work/product\\a";
        var fs = new FakeFileSystem();
        fs.CreateDirectory(backslashRoot);
        var executor = new FakeCommandExecutor();
        QueueGit(executor, backslashRoot, ["rev-parse", "--show-toplevel"], 0, backslashRoot + "\n");
        QueueGit(executor, backslashRoot, ["rev-parse", "HEAD"], 0, "abc123\n");
        QueueGit(executor, backslashRoot, ["remote", "get-url", "origin"], 0, "git@example.com:team/product-a.git\n");
        QueueGit(executor, backslashRoot, ["symbolic-ref", "--short", "refs/remotes/origin/HEAD"], 0, "origin/main\n");

        var outcome = await ProjectRepositoryBootstrap.TryResolveAsync(backslashRoot, fs, executor);

        var success = Assert.IsType<ProjectRepositoryBootstrap.Outcome.Success>(outcome);
        Assert.Equal(backslashRoot, success.Result.WorkTreeRoot);
        Assert.Equal("product\\a", success.Result.RepositoryName);
        executor.AssertExpectedCommandsExecuted();
    }

    private static void QueueGit(
        FakeCommandExecutor executor,
        string workingDirectory,
        string[] gitArgs,
        int exitCode,
        string stdout = "",
        string stderr = "")
    {
        var args = new List<string> { "-C", workingDirectory };
        args.AddRange(gitArgs);
        executor.QueueExpected("git", args.ToArray(), workingDirectory, exitCode, stdout, stderr);
    }
}
