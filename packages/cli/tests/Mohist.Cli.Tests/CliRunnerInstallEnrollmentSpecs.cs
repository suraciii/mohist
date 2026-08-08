using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

/// <summary>
/// <c>mo install runner</c> injects the one-time enrollment token into
/// the runner service environment so the fresh runner can register on
/// first start (docs/auth.md "Runner：安装即注册"). The token is a
/// 15-minute secret: only installs that received one carry the env line.
/// </summary>
public sealed class CliRunnerInstallEnrollmentSpecs
{
    [Fact]
    public async Task SystemdInstallRunner_WritesTheEnrollmentTokenIntoTheServiceEnvironment()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(stdout, stderr, files, commands);

        var exitCode = await installer.InstallRunnerAsync(new ServiceInstallOptions(
            DryRun: true,
            UnitDir: "/units",
            RepoRoot: "/repo",
            ListenUrl: null,
            ServerUrl: "http://127.0.0.1:3456",
            RunnerRoot: "/var/lib/runner",
            EnrollmentToken: "moh_enroll_unit-test"));

        Assert.Equal(0, exitCode);
        var unit = files.ReadAllText("/units/mohist-runner.service");
        Assert.Contains("MOHIST_ENROLLMENT_TOKEN=moh_enroll_unit-test", unit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SystemdInstallRunner_WithoutAToken_OmitsTheEnvironmentLine()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var installer = new SystemdServiceInstaller(new StringWriter(), new StringWriter(), files, commands);

        var exitCode = await installer.InstallRunnerAsync(new ServiceInstallOptions(
            DryRun: true,
            UnitDir: "/units",
            RepoRoot: "/repo",
            ListenUrl: null,
            ServerUrl: "http://127.0.0.1:3456",
            RunnerRoot: null,
            EnrollmentToken: null));

        Assert.Equal(0, exitCode);
        var unit = files.ReadAllText("/units/mohist-runner.service");
        Assert.DoesNotContain("MOHIST_ENROLLMENT_TOKEN", unit, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsRunnerLauncher_WritesTheEnrollmentTokenIntoTheEnvironment()
    {
        var installer = new WindowsScheduledTaskInstaller(
            new StringWriter(), new StringWriter(), new FakeFileSystem(), new FakeCommandExecutor());

        var launcher = installer.RenderRunnerLauncher(new WindowsScheduledTaskInstaller.RunnerLauncherSpec(
            RepoRoot: "C:\\repo",
            ServerUrl: "http://127.0.0.1:3456",
            RunnerRoot: null,
            EnrollmentToken: "moh_enroll_win-test"));

        Assert.Contains("set \"MOHIST_ENROLLMENT_TOKEN=moh_enroll_win-test\"", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsRunnerLauncher_WithoutAToken_OmitsTheEnvironmentLine()
    {
        var installer = new WindowsScheduledTaskInstaller(
            new StringWriter(), new StringWriter(), new FakeFileSystem(), new FakeCommandExecutor());

        var launcher = installer.RenderRunnerLauncher(new WindowsScheduledTaskInstaller.RunnerLauncherSpec(
            RepoRoot: "C:\\repo",
            ServerUrl: "http://127.0.0.1:3456",
            RunnerRoot: null,
            EnrollmentToken: null));

        Assert.DoesNotContain("MOHIST_ENROLLMENT_TOKEN", launcher, StringComparison.Ordinal);
    }
}
