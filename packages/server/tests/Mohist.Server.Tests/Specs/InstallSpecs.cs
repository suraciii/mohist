using Xunit;
using Mohist.Cli;

namespace Mohist.Server.Tests.Specs;

public class InstallSpecs
{
    [Fact]
    public async Task InstallServer_CreatesSystemdUnitWithCorrectConfiguration()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);

        var options = new ServiceInstallOptions(
            DryRun: true,
            UnitDir: "/units",
            RepoRoot: "/repo",
            ListenUrl: "http://127.0.0.1:4567",
            ServerUrl: null,
            RunnerRoot: null);

        var exitCode = await installer.InstallServerAsync(options);

        Assert.Equal(0, exitCode);
        var unitContent = files.Read("/units/mohist.service");
        Assert.Contains("Description=Mohist Server", unitContent);
        Assert.Contains("ExecStart=dotnet run --project", unitContent);
        Assert.Contains("Mohist.Server.csproj", unitContent);
        Assert.Contains("http://127.0.0.1:4567", unitContent);
        Assert.Contains("SuccessExitStatus=0 143", unitContent);
    }

    [Fact]
    public async Task InstallRunner_CreatesSystemdUnitWithCorrectConfiguration()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);

        var options = new ServiceInstallOptions(
            DryRun: true,
            UnitDir: "/units",
            RepoRoot: "/repo",
            ListenUrl: null,
            ServerUrl: "http://127.0.0.1:4567",
            RunnerRoot: "/runner");

        var exitCode = await installer.InstallRunnerAsync(options);

        Assert.Equal(0, exitCode);
        var unitContent = files.Read("/units/mohist-runner.service");
        Assert.Contains("Description=Mohist Runner", unitContent);
        Assert.Contains("ExecStart=node packages/runner/dist/cli.js", unitContent);
        Assert.Contains("Environment=\"SERVER_URL=http://127.0.0.1:4567\"", unitContent);
        Assert.Contains("Environment=\"PATH=", unitContent);
        Assert.Contains("/.opencode/bin", unitContent);
        Assert.Contains("Environment=\"RUNNER_ROOT=/runner\"", unitContent);
    }

    [Fact]
    public async Task InstallServer_WithCustomRepoRoot_ResolvesRepoPath()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);

        var options = new ServiceInstallOptions(
            DryRun: true,
            UnitDir: "/units",
            RepoRoot: "/custom/path",
            ListenUrl: null,
            ServerUrl: null,
            RunnerRoot: null);

        await installer.InstallServerAsync(options);

        var unitContent = files.Read("/units/mohist.service");
        Assert.Contains("WorkingDirectory=/custom/path", unitContent);
    }

    [Fact]
    public async Task InstallServer_WithoutCustomUrl_UsesDefaultListenUrl()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);

        var options = new ServiceInstallOptions(
            DryRun: true,
            UnitDir: "/units",
            RepoRoot: "/repo",
            ListenUrl: null,
            ServerUrl: null,
            RunnerRoot: null);

        await installer.InstallServerAsync(options);

        var unitContent = files.Read("/units/mohist.service");
        Assert.Contains("http://127.0.0.1:3456", unitContent);
    }

    private sealed class FakeFileSystem : IFileSystem
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

        public Task WriteAllTextAsync(string path, string contents)
        {
            _files[Path.GetFullPath(path)] = contents;
            return Task.CompletedTask;
        }

        public Task<string> ReadAllTextAsync(string path) => Task.FromResult(Read(path));

        public bool Exists(string path) => _files.ContainsKey(Path.GetFullPath(path));

        public void Delete(string path) => _files.Remove(Path.GetFullPath(path));

        public string Read(string path) => _files[Path.GetFullPath(path)];
    }

    private sealed class FakeCommandExecutor : ICommandExecutor
    {
        public readonly List<(string FileName, string[] Args, string? WorkingDirectory)> ExecutedCommands = new();

        public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
            string fileName, string[] args, string? workingDirectory = null)
        {
            ExecutedCommands.Add((fileName, args, workingDirectory));
            return Task.FromResult((0, "", ""));
        }
    }
}
