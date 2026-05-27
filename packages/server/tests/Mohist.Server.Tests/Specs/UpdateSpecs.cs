using Xunit;
using Mohist.Cli;

namespace Mohist.Server.Tests.Specs;

public class UpdateSpecs
{
    [Fact]
    public async Task UpdateServer_PullsLatestCode_BuildsAndRestarts()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        var updater = new SourceCodeUpdater(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands);

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(3, commands.ExecutedCommands.Count);
        Assert.Equal("git", commands.ExecutedCommands[0].FileName);
        Assert.Equal(new[] { "pull" }, commands.ExecutedCommands[0].Args);
        Assert.Equal("/repo", commands.ExecutedCommands[0].WorkingDirectory);
        Assert.Equal("dotnet", commands.ExecutedCommands[1].FileName);
        Assert.Equal(new[] { "build", "Mohist.sln" }, commands.ExecutedCommands[1].Args);
        Assert.Equal("systemctl", commands.ExecutedCommands[2].FileName);
        Assert.Equal(new[] { "--user", "restart", "mohist.service" }, commands.ExecutedCommands[2].Args);
    }

    [Fact]
    public async Task UpdateRunner_PullsLatestCode_BuildsAndRestarts()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        var updater = new SourceCodeUpdater(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands);

        var exitCode = await updater.UpdateRunnerAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(3, commands.ExecutedCommands.Count);
        Assert.Equal("git", commands.ExecutedCommands[0].FileName);
        Assert.Equal(new[] { "pull" }, commands.ExecutedCommands[0].Args);
        Assert.Equal("npm", commands.ExecutedCommands[1].FileName);
        Assert.Equal(new[] { "run", "build", "-w", "packages/runner" }, commands.ExecutedCommands[1].Args);
        Assert.Equal("systemctl", commands.ExecutedCommands[2].FileName);
        Assert.Equal(new[] { "--user", "restart", "mohist-runner.service" }, commands.ExecutedCommands[2].Args);
    }

    [Fact]
    public async Task UpdateServer_WhenGitPullFails_AbortsWithError()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        commands.SetNextExitCode(1); // git pull fails
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            stderr,
            files,
            commands);
        var updater = new SourceCodeUpdater(
            new StringWriter(),
            stderr,
            installer,
            commands);

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Single(commands.ExecutedCommands);
        Assert.Contains("Git pull failed", stderr.ToString());
    }

    [Fact]
    public async Task UpdateServer_WhenBuildFails_AbortsWithError()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        commands.SetNextExitCode(0);  // git pull succeeds
        commands.SetNextExitCode(1);  // build fails
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            stderr,
            files,
            commands);
        var updater = new SourceCodeUpdater(
            new StringWriter(),
            stderr,
            installer,
            commands);

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Equal(2, commands.ExecutedCommands.Count);
        Assert.Contains("Build failed", stderr.ToString());
    }

    [Fact]
    public async Task UpdateServer_InDryRunMode_PreviewsCommands()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var stdout = new StringWriter();
        var installer = new SystemdServiceInstaller(
            stdout,
            new StringWriter(),
            files,
            commands);
        var updater = new SourceCodeUpdater(
            stdout,
            new StringWriter(),
            installer,
            commands);

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: true);

        Assert.Equal(0, exitCode);
        Assert.Empty(commands.ExecutedCommands);
        var output = stdout.ToString();
        Assert.Contains("Dry run: would execute:", output);
        Assert.Contains("git pull", output);
        Assert.Contains("dotnet build Mohist.sln", output);
    }

    private sealed class FakeFileSystem : IFileSystem
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

        public Task WriteAllTextAsync(string path, string contents)
        {
            _files[Path.GetFullPath(path)] = contents;
            return Task.CompletedTask;
        }

        public bool Exists(string path) => _files.ContainsKey(Path.GetFullPath(path));

        public void Delete(string path) => _files.Remove(Path.GetFullPath(path));

        public string Read(string path) => _files[Path.GetFullPath(path)];
    }

    private sealed class FakeCommandExecutor : ICommandExecutor
    {
        public readonly List<(string FileName, string[] Args, string? WorkingDirectory)> ExecutedCommands = new();
        private readonly Queue<int> _exitCodes = new();

        public void SetNextExitCode(int code) => _exitCodes.Enqueue(code);

        public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
            string fileName, string[] args, string? workingDirectory = null)
        {
            ExecutedCommands.Add((fileName, args, workingDirectory));
            var code = _exitCodes.Count > 0 ? _exitCodes.Dequeue() : 0;
            return Task.FromResult((code, "", ""));
        }
    }
}
