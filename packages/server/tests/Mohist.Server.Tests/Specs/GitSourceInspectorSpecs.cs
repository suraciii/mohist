using Mohist.Server.SystemInfo;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class GitSourceInspectorSpecs
{
    private readonly GitSourceInspector _inspector = new();

    [Fact]
    public async Task Inspect_CleanRepo_ReturnsPathBranchHeadAndNotDirty()
    {
        var repoDir = CreateTempRepo();
        try
        {
            await RunGitAsync(repoDir, "init");
            await RunGitAsync(repoDir, "config", "user.email", "test@test.com");
            await RunGitAsync(repoDir, "config", "user.name", "Test");
            await RunGitAsync(repoDir, "checkout", "-b", "main");
            File.WriteAllText(Path.Combine(repoDir, "file.txt"), "content");
            await RunGitAsync(repoDir, "add", ".");
            await RunGitAsync(repoDir, "commit", "-m", "initial");

            var state = await _inspector.InspectAsync(repoDir);

            Assert.Equal(repoDir, state.Path);
            Assert.Equal("main", state.Branch);
            Assert.NotNull(state.Head);
            Assert.NotEmpty(state.Head);
            Assert.False(state.Dirty);
        }
        finally
        {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public async Task Inspect_DirtyRepo_ReturnsDirtyTrue()
    {
        var repoDir = CreateTempRepo();
        try
        {
            await RunGitAsync(repoDir, "init");
            await RunGitAsync(repoDir, "config", "user.email", "test@test.com");
            await RunGitAsync(repoDir, "config", "user.name", "Test");
            await RunGitAsync(repoDir, "checkout", "-b", "main");
            File.WriteAllText(Path.Combine(repoDir, "file.txt"), "content");
            await RunGitAsync(repoDir, "add", ".");
            await RunGitAsync(repoDir, "commit", "-m", "initial");

            File.WriteAllText(Path.Combine(repoDir, "file.txt"), "modified");

            var state = await _inspector.InspectAsync(repoDir);

            Assert.True(state.Dirty);
            Assert.NotNull(state.Head);
        }
        finally
        {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public async Task Inspect_AfterNewCommit_SourceHeadDiffersFromCapturedHash()
    {
        var repoDir = CreateTempRepo();
        try
        {
            await RunGitAsync(repoDir, "init");
            await RunGitAsync(repoDir, "config", "user.email", "test@test.com");
            await RunGitAsync(repoDir, "config", "user.name", "Test");
            await RunGitAsync(repoDir, "checkout", "-b", "main");
            File.WriteAllText(Path.Combine(repoDir, "file1.txt"), "content1");
            await RunGitAsync(repoDir, "add", ".");
            await RunGitAsync(repoDir, "commit", "-m", "first");

            var firstState = await _inspector.InspectAsync(repoDir);
            var capturedRunningHash = firstState.Head;
            Assert.NotNull(capturedRunningHash);

            File.WriteAllText(Path.Combine(repoDir, "file2.txt"), "content2");
            await RunGitAsync(repoDir, "add", ".");
            await RunGitAsync(repoDir, "commit", "-m", "second");

            var secondState = await _inspector.InspectAsync(repoDir);
            Assert.NotNull(secondState.Head);
            Assert.NotEqual(capturedRunningHash, secondState.Head);
        }
        finally
        {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public async Task Inspect_NonGitDirectory_ReturnsNullBranchAndHead()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mohist-nogit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var state = await _inspector.InspectAsync(dir);

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
        var dir = Path.Combine(Path.GetTempPath(), $"mohist-missing-{Guid.NewGuid():N}");

        var state = await _inspector.InspectAsync(dir);

        Assert.Equal(dir, state.Path);
        Assert.Null(state.Branch);
        Assert.Null(state.Head);
        Assert.False(state.Dirty);
    }

    private static string CreateTempRepo()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mohist-git-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task RunGitAsync(string workingDir, string command, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", [command, ..args])
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = System.Diagnostics.Process.Start(psi);
        Assert.NotNull(process);
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }
}
