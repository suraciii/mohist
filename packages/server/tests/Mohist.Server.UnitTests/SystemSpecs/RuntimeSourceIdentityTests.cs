using Mohist.Server.SystemInfo;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs;

public class RuntimeSourceIdentityTests
{
    [Fact]
    public void ResolveGitHead_WhenInstalledArtifactHasManifest_ReturnsInstalledSourceHashWithoutGitMetadata()
    {
        var files = new FakeFileSystem()
            .Add("/runtime/server/current/mohist-build.json", "{\"gitHash\":\"0123456789abcdef0123456789abcdef01234567\"}");

        var head = RuntimeSourceIdentity.ResolveGitHead(files, "/runtime/server/current");

        Assert.Equal("0123456789abcdef0123456789abcdef01234567", head);
    }

    [Fact]
    public void ResolveGitHead_WhenHeadReferencesBranch_ReturnsCommit()
    {
        var files = new FakeFileSystem()
            .Add("/repo/.git", string.Empty)
            .Add("/repo/.git/HEAD", "ref: refs/heads/main\n")
            .Add("/repo/.git/refs/heads/main", "abc123\n");

        var head = RuntimeSourceIdentity.ResolveGitHead(files, "/repo/bin/Debug/net11.0");

        Assert.Equal("abc123", head);
    }

    [Fact]
    public void ResolveGitHead_WhenWorktreeUsesGitFile_ReturnsCommit()
    {
        var files = new FakeFileSystem()
            .Add("/repo/worktree/.git", "gitdir: /repo/.git/worktrees/test\n")
            .Add("/repo/.git/worktrees/test/HEAD", "def456\n");

        var head = RuntimeSourceIdentity.ResolveGitHead(files, "/repo/worktree/bin");

        Assert.Equal("def456", head);
    }

    private sealed class FakeFileSystem : IFileSystem
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

        public FakeFileSystem Add(string path, string content)
        {
            _files[path] = content;
            return this;
        }

        public bool Exists(string path) => _files.ContainsKey(path);

        public string ReadAllText(string path) => _files[path];

        public void CreateDirectory(string path) { }

        public long? GetFileLength(string path) =>
            _files.TryGetValue(path, out var content) ? (long?)System.Text.Encoding.UTF8.GetByteCount(content) : null;

        public void WriteAllText(string path, string contents) => _files[path] = contents;

        public void Delete(string path) => _files.Remove(path);
    }
}
