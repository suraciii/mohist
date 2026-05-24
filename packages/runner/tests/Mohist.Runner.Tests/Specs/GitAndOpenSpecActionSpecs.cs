using System.Diagnostics;
using System.Text.Json;
using Mohist.Runner.Actions;
using Xunit;

namespace Mohist.Runner.Tests.Specs;

public class GitAndOpenSpecActionSpecs
{
    [Fact]
    public async Task MergeReady_InCleanRepository_Passes()
    {
        using var temp = new TempDir();
        await InitRepositoryAsync(temp.Path);

        var result = await new MergeReadyAction().ExecuteAsync(
            SpecHelpers.Context(temp.Path, "check", "mohist/merge-ready", new { baseBranch = "main" }));

        Assert.Equal("success", result.Status);
        using var document = JsonDocument.Parse(result.Output!);
        Assert.True(document.RootElement.GetProperty("canMerge").GetBoolean());
    }

    [Fact]
    public async Task OpenSpecSync_CopiesSpecFiles()
    {
        using var temp = new TempDir();
        var changeDir = Path.Combine(temp.Path, "openspec", "changes", "1-test");
        var specDir = Path.Combine(changeDir, "specs", "search");
        Directory.CreateDirectory(specDir);
        await File.WriteAllTextAsync(Path.Combine(specDir, "spec.md"), "Requirement");

        var result = await new OpenSpecSyncAction().ExecuteAsync(
            SpecHelpers.Context(temp.Path, "task", "mohist/openspec-sync", new { changeDir = "openspec/changes/1-test" }));

        Assert.Equal("success", result.Status);
        Assert.True(File.Exists(Path.Combine(temp.Path, "specs", "search", "spec.md")));
    }

    [Fact]
    public async Task OpenSpecSync_MissingSpecsDirectory_Fails()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "openspec", "changes", "1-test"));

        var result = await new OpenSpecSyncAction().ExecuteAsync(
            SpecHelpers.Context(temp.Path, "task", "mohist/openspec-sync", new { changeDir = "openspec/changes/1-test" }));

        Assert.Equal("failure", result.Status);
    }

    [Fact]
    public async Task ArchiveChange_MovesChangeDirectoryToArchive()
    {
        using var temp = new TempDir();
        var changeDir = Path.Combine(temp.Path, "openspec", "changes", "1-test");
        Directory.CreateDirectory(changeDir);
        await File.WriteAllTextAsync(Path.Combine(changeDir, "proposal.md"), "proposal");

        var result = await new ArchiveChangeAction().ExecuteAsync(
            SpecHelpers.Context(temp.Path, "task", "mohist/archive-change", new { changeDir = "openspec/changes/1-test" }));

        Assert.Equal("success", result.Status);
        Assert.False(Directory.Exists(changeDir));
        var archiveDir = Path.Combine(temp.Path, "openspec", "changes", "archive");
        Assert.Contains(Directory.EnumerateDirectories(archiveDir), path => path.EndsWith("1-test"));
    }

    [Fact]
    public async Task ArchiveChange_MissingChangeDir_Fails()
    {
        using var temp = new TempDir();

        var result = await new ArchiveChangeAction().ExecuteAsync(
            SpecHelpers.Context(temp.Path, "task", "mohist/archive-change", new { changeDir = "openspec/changes/missing" }));

        Assert.Equal("failure", result.Status);
    }

    private static async Task InitRepositoryAsync(string path)
    {
        await RunAsync(path, "init", "-b", "main");
        await RunAsync(path, "config", "user.email", "mohist@example.test");
        await RunAsync(path, "config", "user.name", "Mohist Test");
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "hello");
        await RunAsync(path, "add", ".");
        await RunAsync(path, "commit", "-m", "initial");
    }

    private static async Task RunAsync(string workDir, params string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = string.Join(" ", args.Select(Quote)),
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        process.Start();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed");
    }

    private static string Quote(string value) => value.Any(char.IsWhiteSpace)
        ? $"\"{value.Replace("\"", "\\\"")}\""
        : value;
}
