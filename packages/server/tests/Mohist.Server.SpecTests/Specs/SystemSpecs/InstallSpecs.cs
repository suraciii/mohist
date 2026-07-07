using Xunit;
using Mohist.Cli;
using Mohist.Server.SpecTests.Support;

namespace Mohist.Server.SpecTests.Specs.SystemSpecs;

public class InstallSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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
        var unitContent = files.ReadAllText("/units/mohist.service");
        Assert.Contains("Description=Mohist Server", unitContent);
        Assert.Contains("ExecStart=", unitContent);
        Assert.Contains("dotnet run --project", unitContent);
        Assert.Contains("Mohist.Server.csproj", unitContent);
        Assert.Contains("Environment=\"PATH=", unitContent);
        Assert.Contains("http://127.0.0.1:4567", unitContent);
        Assert.Contains("SuccessExitStatus=0 143", unitContent);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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
        var unitContent = files.ReadAllText("/units/mohist-runner.service");
        Assert.Contains("Description=Mohist Runner", unitContent);
        Assert.Contains("ExecStart=", unitContent);
        Assert.Contains("node packages/runner/dist/cli.js", unitContent);
        Assert.Contains("Environment=\"SERVER_URL=http://127.0.0.1:4567\"", unitContent);
        Assert.Contains("Environment=\"PATH=", unitContent);
        Assert.Contains("/.opencode/bin", unitContent);
        Assert.Contains("Environment=\"RUNNER_ROOT=/runner\"", unitContent);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task InstallServer_WithoutCustomUrl_OmitsListenUrl()
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
        Assert.DoesNotContain("--urls", unitContent);
        Assert.DoesNotContain("http://", unitContent);
        Assert.Contains("dotnet run --project", unitContent);
    }

    private sealed class FakeFileSystem : Mohist.Server.SpecTests.Support.FakeFileSystem
    {
    }

    private sealed class FakeCommandExecutor : ICommandExecutor
    {
        public readonly List<(string FileName, string[] Args, string? WorkingDirectory)> ExecutedCommands = new();

        public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
            string fileName, string[] args, string? workingDirectory = null, CancellationToken cancellationToken = default)
        {
            ExecutedCommands.Add((fileName, args, workingDirectory));
            return Task.FromResult((0, "", ""));
        }
    }
}
