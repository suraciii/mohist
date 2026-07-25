using Mohist.Server.SystemInfo;
using Xunit;

namespace Mohist.Server.UnitTests;

public class GitSourceInspectorTests
{
    [Fact]
    public async Task Inspect_CleanRepo_ReturnsPathBranchHeadAndNotDirty()
    {
        var fileSystem = new FakeFileSystem();
        var repoDir = $"/fake-repo/{Guid.NewGuid():N}";

        fileSystem.AddDirectory(repoDir);
        fileSystem.AddDirectory(Path.Combine(repoDir, ".git"));

        var runner = new FakeGitRunner();
        var inspector = new GitSourceInspector(fileSystem, runner.Run);
        runner.SetRepo(repoDir, "main", "abc123def456", dirty: false);

        var state = await inspector.InspectAsync(repoDir);

        Assert.Equal(repoDir, state.Path);
        Assert.Equal("main", state.Branch);
        Assert.Equal("abc123def456", state.Head);
        Assert.False(state.Dirty);
    }

    [Fact]
    public async Task Inspect_DirtyRepo_ReturnsDirtyTrue()
    {
        var fileSystem = new FakeFileSystem();
        var repoDir = $"/fake-repo/{Guid.NewGuid():N}";

        fileSystem.AddDirectory(repoDir);
        fileSystem.AddDirectory(Path.Combine(repoDir, ".git"));

        var runner = new FakeGitRunner();
        var inspector = new GitSourceInspector(fileSystem, runner.Run);
        runner.SetRepo(repoDir, "main", "abc123def456", dirty: true);

        var state = await inspector.InspectAsync(repoDir);

        Assert.True(state.Dirty);
        Assert.Equal("abc123def456", state.Head);
    }

    [Fact]
    public async Task Inspect_AfterNewCommit_SourceHeadDiffersFromCapturedHash()
    {
        var fileSystem = new FakeFileSystem();
        var repoDir = $"/fake-repo/{Guid.NewGuid():N}";

        fileSystem.AddDirectory(repoDir);
        fileSystem.AddDirectory(Path.Combine(repoDir, ".git"));

        var runner = new FakeGitRunner();
        var inspector = new GitSourceInspector(fileSystem, runner.Run);
        runner.SetRepo(repoDir, "main", "hash1", dirty: false);

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
        var fileSystem = new FakeFileSystem();
        var dir = $"/fake-nogit/{Guid.NewGuid():N}";
        fileSystem.AddDirectory(dir);
        // No .git subdirectory

        var runner = new FakeGitRunner();
        var inspector = new GitSourceInspector(fileSystem, runner.Run);

        var state = await inspector.InspectAsync(dir);

        Assert.Equal(dir, state.Path);
        Assert.Null(state.Branch);
        Assert.Null(state.Head);
        Assert.False(state.Dirty);
    }

    [Fact]
    public async Task Inspect_MissingDirectory_ReturnsNullBranchAndHead()
    {
        var fileSystem = new FakeFileSystem();
        var runner = new FakeGitRunner();
        var inspector = new GitSourceInspector(fileSystem, runner.Run);
        var dir = $"/fake-missing/{Guid.NewGuid():N}";

        var state = await inspector.InspectAsync(dir);

        Assert.Equal(dir, state.Path);
        Assert.Null(state.Branch);
        Assert.Null(state.Head);
        Assert.False(state.Dirty);
    }

    private sealed class FakeFileSystem : IFileSystem
    {
        private readonly HashSet<string> _paths = new(StringComparer.Ordinal);

        public void AddDirectory(string path) => _paths.Add(path);

        public bool Exists(string path) => _paths.Contains(path);

        public string ReadAllText(string path)
            => throw new NotSupportedException("FakeFileSystem is in-memory; only Exists is exercised by these tests");

        public void CreateDirectory(string path) { }

        public long? GetFileLength(string path) => null;
    }

    private sealed class FakeGitRunner
    {
        private readonly Dictionary<string, RepoState> _repos = new(StringComparer.Ordinal);

        public void SetRepo(string repoDir, string branch, string head, bool dirty)
        {
            _repos[repoDir] = new RepoState(branch, head, dirty);
        }

        public void SetHead(string repoDir, string head)
        {
            if (_repos.TryGetValue(repoDir, out var state))
                _repos[repoDir] = state with { Head = head };
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
