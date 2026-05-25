using System.Text.Json;
using System.Diagnostics;
using Xunit;

namespace Mohist.Runner.Tests.Specs;

public class WorkspaceManagerSpecs
{
    [Fact]
    public async Task UsesExistingWorkspaceVariable()
    {
        using var temp = new TempDir();
        var workspacePath = Path.Combine(temp.Path, "existing-workspace");
        Directory.CreateDirectory(workspacePath);

        var ws = new WorkspaceManager(SpecHelpers.Logger<WorkspaceManager>(), temp.Path);
        var variables = new Dictionary<string, JsonElement?>
        {
            ["workspace"] = JsonSerializer.SerializeToElement(new { path = workspacePath })
        };

        var info = await ws.EnsureAsync(variables, CancellationToken.None);

        Assert.Equal(workspacePath, info.Path);
    }

    [Fact]
    public async Task CreatesFallbackWorkspace()
    {
        using var temp = new TempDir();
        var ws = new WorkspaceManager(SpecHelpers.Logger<WorkspaceManager>(), temp.Path);
        var variables = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>("""
        {"project":{"id":"test"},"issue":{"number":42}}
        """)!;

        var info = await ws.EnsureAsync(variables, CancellationToken.None);

        Assert.Contains("issue-42", info.Path);
        Assert.True(Directory.Exists(info.Path));
    }

    [Fact]
    public async Task CreatesChangeDirInWorkspace()
    {
        using var temp = new TempDir();
        var ws = new WorkspaceManager(SpecHelpers.Logger<WorkspaceManager>(), temp.Path);
        var variables = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>("""
        {"project":{"id":"test"},"issue":{"number":42},"openspecChangeDir":"openspec/changes/issue-42"}
        """)!;

        var info = await ws.EnsureAsync(variables, CancellationToken.None);

        Assert.NotNull(info.ChangeDir);
        Assert.EndsWith(Path.Combine("openspec", "changes", "issue-42"), info.ChangeDir);
        Assert.True(Directory.Exists(info.ChangeDir));
        Assert.True(Directory.Exists(Path.Combine(info.ChangeDir, "specs")));
    }

    [Fact]
    public async Task ProjectPathAndIssueNumber_CreatesGitWorktree()
    {
        using var project = new TempDir();
        using var runnerRoot = new TempDir();
        await InitRepositoryAsync(project.Path);

        var ws = new WorkspaceManager(SpecHelpers.Logger<WorkspaceManager>(), runnerRoot.Path);
        var variables = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>($$"""
        {
          "project": { "id": "proj-1", "name": "My Project", "path": {{JsonSerializer.Serialize(project.Path)}}, "baseBranch": "main" },
          "issue": { "number": 42 },
          "openspecChangeDir": "openspec/changes/issue-42"
        }
        """)!;

        var info = await ws.EnsureAsync(variables, CancellationToken.None);

        Assert.Equal(MohistWorkspaceLayout.IssueWorktreePath(runnerRoot.Path, "My Project", 42), info.Path);
        Assert.Equal("mo/issue-42", info.Branch);
        Assert.True(Directory.Exists(info.Path));
        Assert.True(Directory.Exists(Path.Combine(info.Path, ".git")) || File.Exists(Path.Combine(info.Path, ".git")));
        Assert.True(Directory.Exists(Path.Combine(info.Path, "openspec", "changes", "issue-42", "specs")));
    }

    [Fact]
    public async Task ExistingGitWorktree_IsReused()
    {
        using var project = new TempDir();
        using var runnerRoot = new TempDir();
        await InitRepositoryAsync(project.Path);

        var ws = new WorkspaceManager(SpecHelpers.Logger<WorkspaceManager>(), runnerRoot.Path);
        var variables = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>($$"""
        {
          "project": { "id": "proj-1", "name": "My Project", "path": {{JsonSerializer.Serialize(project.Path)}}, "baseBranch": "main" },
          "issue": { "number": 42 }
        }
        """)!;

        var first = await ws.EnsureAsync(variables, CancellationToken.None);
        var second = await ws.EnsureAsync(variables, CancellationToken.None);

        Assert.Equal(first.Path, second.Path);
        Assert.Equal("mo/issue-42", second.Branch);
    }

    private static async Task InitRepositoryAsync(string path)
    {
        await RunGitAsync(path, "init", "-b", "main");
        await RunGitAsync(path, "config", "user.email", "mohist@example.test");
        await RunGitAsync(path, "config", "user.name", "Mohist Test");
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "hello");
        await RunGitAsync(path, "add", ".");
        await RunGitAsync(path, "commit", "-m", "initial");
    }

    private static async Task RunGitAsync(string workDir, params string[] args)
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
