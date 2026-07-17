using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Update;

public class InstallSpecs
{
    [Fact]
    public async Task InstallServer_CreatesSystemdUnitWithCorrectConfiguration()
    {
        var f = new UpdateTestFactory();

        var options = new ServiceInstallOptions(
            DryRun: true,
            UnitDir: "/units",
            RepoRoot: "/repo",
            ListenUrl: "http://127.0.0.1:4567",
            ServerUrl: null,
            RunnerRoot: null);

        var exitCode = await f.Installer.InstallServerAsync(options);

        Assert.Equal(0, exitCode);
        var unitContent = f.Files.ReadAllText("/units/mohist.service");
        Assert.Contains("Description=Mohist Server", unitContent);
        Assert.Contains("ExecStart=", unitContent);
        Assert.Contains("dotnet run --project", unitContent);
        Assert.Contains("Mohist.Server.csproj", unitContent);
        Assert.Contains("Environment=\"PATH=", unitContent);
        Assert.Contains("http://127.0.0.1:4567", unitContent);
        Assert.Contains("SuccessExitStatus=0 143", unitContent);
    }

    [Fact]
    public async Task InstallRunner_CreatesSystemdUnitWithCorrectConfiguration()
    {
        var f = new UpdateTestFactory();

        var options = new ServiceInstallOptions(
            DryRun: true,
            UnitDir: "/units",
            RepoRoot: "/repo",
            ListenUrl: null,
            ServerUrl: "http://127.0.0.1:4567",
            RunnerRoot: "/runner");

        var exitCode = await f.Installer.InstallRunnerAsync(options);

        Assert.Equal(0, exitCode);
        var unitContent = f.Files.ReadAllText("/units/mohist-runner.service");
        Assert.Contains("Description=Mohist Runner", unitContent);
        Assert.Contains("ExecStart=", unitContent);
        Assert.Contains("node packages/runner/dist/cli.js", unitContent);
        Assert.Contains("Environment=\"SERVER_URL=http://127.0.0.1:4567\"", unitContent);
        Assert.Contains("Environment=\"PATH=", unitContent);
        Assert.Contains("/.opencode/bin", unitContent);
        Assert.Contains("Environment=\"RUNNER_ROOT=/runner\"", unitContent);
    }

    [Fact]
    public async Task InstallServer_WithCustomRepoRoot_ResolvesRepoPath()
    {
        var f = new UpdateTestFactory();

        var options = new ServiceInstallOptions(
            DryRun: true,
            UnitDir: "/units",
            RepoRoot: "/custom/path",
            ListenUrl: null,
            ServerUrl: null,
            RunnerRoot: null);

        await f.Installer.InstallServerAsync(options);

        var unitContent = f.Files.Read("/units/mohist.service");
        Assert.Contains("WorkingDirectory=/custom/path", unitContent);
    }

    [Fact]
    public async Task InstallServer_WithoutCustomUrl_OmitsListenUrl()
    {
        var f = new UpdateTestFactory();

        var options = new ServiceInstallOptions(
            DryRun: true,
            UnitDir: "/units",
            RepoRoot: "/repo",
            ListenUrl: null,
            ServerUrl: null,
            RunnerRoot: null);

        await f.Installer.InstallServerAsync(options);

        var unitContent = f.Files.Read("/units/mohist.service");
        Assert.DoesNotContain("--urls", unitContent);
        Assert.DoesNotContain("http://", unitContent);
        Assert.Contains("dotnet run --project", unitContent);
    }
}
