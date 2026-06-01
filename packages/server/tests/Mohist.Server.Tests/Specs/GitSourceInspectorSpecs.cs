using Mohist.Server.SystemInfo;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class GitSourceInspectorSpecs
{
    [Fact]
    public async Task Inspect_CleanRepo_ReturnsPathBranchHeadAndNotDirty()
    {
        var runner = new FakeGitRunner();
        var inspector = new GitSourceInspector(runner.Run);
        var repoDir = "/test/repo";

        runner.Init(repoDir, "main", "abc123def456");
        runner.SetDirty(repoDir, false);

        var state = await inspector.InspectAsync(repoDir);

        Assert.Equal(repoDir, state.Path);
        Assert.Equal("main", state.Branch);
        Assert.Equal("abc123def456", state.Head);
        Assert.False(state.Dirty);
    }

    [Fact]
    public async Task Inspect_DirtyRepo_ReturnsDirtyTrue()
    {
        var runner = new FakeGitRunner();
        var inspector = new GitSourceInspector(runner.Run);
        var repoDir = "/test/repo";

        runner.Init(repoDir, "main", "abc123def456");
        runner.SetDirty(repoDir, true);

        var state = await inspector.InspectAsync(repoDir);

        Assert.True(state.Dirty);
        Assert.Equal("abc123def456", state.Head);
    }

    [Fact]
    public async Task Inspect_AfterNewCommit_SourceHeadDiffersFromCapturedHash()
    {
        var runner = new FakeGitRunner();
        var inspector = new GitSourceInspector(runner.Run);
        var repoDir = "/test/repo";

        runner.Init(repoDir, "main", "hash1");
        var firstState = await inspector.InspectAsync(repoDir);
        var capturedRunningHash = firstState.Head;
        Assert.NotNull(capturedRunningHash);

        runner.SetHead(repoDir, "hash2");
        var secondState = await inspector.InspectAsync(repoDir);
        Assert.NotNull(secondState.Head);
        Assert.NotEqual(capturedRunningHash, secondState.Head);
    }

    [Fact]
    public async Task Inspect_NonGitDirectory_ReturnsNullBranchAndHead()
    {
        var runner = new FakeGitRunner();
        var inspector = new GitSourceInspector(runner.Run);
        var dir = Path.Combine(Path.GetTempPath(), $"mohist-nogit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var state = await inspector.InspectAsync(dir);

            Assert.Equal(dir, state.Path);
            Assert.Null(state.Branch);
            Assert.Null(state.Head);
            Assert.False(state.Dirty);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Inspect_MissingDirectory_ReturnsNullBranchAndHead()
    {
        var runner = new FakeGitRunner();
        var inspector = new GitSourceInspector(runner.Run);
        var dir = Path.Combine(Path.GetTempPath(), $"mohist-missing-{Guid.NewGuid():N}");

        var state = await inspector.InspectAsync(dir);

        Assert.Equal(dir, state.Path);
        Assert.Null(state.Branch);
        Assert.Null(state.Head);
        Assert.False(state.Dirty);
    }

    private sealed class FakeGitRunner
    {
        private readonly Dictionary<string, RepoState> _repos = new(StringComparer.Ordinal);

        public void Init(string repoDir, string branch, string head)
        {
            _repos[repoDir] = new RepoState(branch, head, false);
            Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
        }

        public void SetHead(string repoDir, string head)
        {
            if (_repos.TryGetValue(repoDir, out var state))
                _repos[repoDir] = state with { Head = head };
        }

        public void SetDirty(string repoDir, bool dirty)
        {
            if (_repos.TryGetValue(repoDir, out var state))
                _repos[repoDir] = state with { Dirty = dirty };
        }

        public Task<(string Output, int ExitCode)> Run(string workingDir, string command, string[] args)
        {
            if (!_repos.TryGetValue(workingDir, out var repo))
                return Task.FromResult(("", 128));

            var (output, exitCode) = (command, args) switch
            {
                ("rev-parse", [var flag, ..]) when flag == "--abbrev-ref" => (repo.Branch + "\n", 0),
                ("rev-parse", [var flag, ..]) => (repo.Head + "\n", 0),
                ("status", [var flag, ..]) when flag == "--porcelain" => (repo.Dirty ? " M file.txt" : "", 0),
                _ => ("", 128),
            };

            return Task.FromResult((output, exitCode));
        }

        private sealed record RepoState(string Branch, string Head, bool Dirty);
    }
}
