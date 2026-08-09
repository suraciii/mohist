using Mohist.Cli;
using EnvironmentAbstractions.TestHelpers;
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
            RunnerRoot: null,
            RuntimeRoot: "/stable/server");

        var exitCode = await f.Installer.InstallServerAsync(options);

        Assert.Equal(0, exitCode);
        var unitContent = f.Files.ReadAllText("/units/mohist.service");
        Assert.Contains("Description=Mohist Server", unitContent);
        Assert.Contains("ExecStart=", unitContent);
        Assert.Contains("WorkingDirectory=/stable/server", unitContent);
        Assert.Contains("dotnet /stable/server/current/Mohist.Server.dll", unitContent);
        Assert.DoesNotContain("/repo", unitContent);
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
            RunnerRoot: "/runner",
            RuntimeRoot: "/stable/runner");

        var exitCode = await f.Installer.InstallRunnerAsync(options);

        Assert.Equal(0, exitCode);
        var unitContent = f.Files.ReadAllText("/units/mohist-runner.service");
        Assert.Contains("Description=Mohist Runner", unitContent);
        Assert.Contains("ExecStart=", unitContent);
        Assert.Contains("WorkingDirectory=/stable/runner", unitContent);
        Assert.Contains("node /stable/runner/current/dist/cli.js", unitContent);
        Assert.DoesNotContain("/repo", unitContent);
        Assert.Contains("Environment=\"SERVER_URL=http://127.0.0.1:4567\"", unitContent);
        Assert.Contains("Environment=\"PATH=", unitContent);
        Assert.Contains("/.opencode/bin", unitContent);
        Assert.Contains("Environment=\"RUNNER_ROOT=/runner\"", unitContent);
    }

    [Fact]
    public async Task InstallSlack_CreatesSystemdUnitWithAdapterAndOperatorEnvironment()
    {
        var files = new FakeFileSystem();
        var environment = new MockEnvironmentVariableProvider();
        environment["MOHIST_OPERATOR_TOKEN"] = "operator-token-for-test";
        var installer = new SystemdServiceInstaller(
            TextWriter.Null, TextWriter.Null, files, new FakeCommandExecutor(), environment);

        var exitCode = await installer.InstallSlackAsync(new ServiceInstallOptions(
            DryRun: true,
            UnitDir: "/units",
            RepoRoot: "/repo",
            ListenUrl: null,
            ServerUrl: "http://127.0.0.1:4567",
            RunnerRoot: null));

        Assert.Equal(0, exitCode);
        var unitContent = files.Read("/units/mohist-slack.service");
        Assert.Contains("ExecStart=node packages/mohist-slack/dist/cli.js", unitContent);
        Assert.Contains("Restart=on-failure", unitContent);
        Assert.Contains("Environment=\"SERVER_URL=http://127.0.0.1:4567\"", unitContent);
        Assert.Contains("Environment=\"MOHIST_OPERATOR_TOKEN=operator-token-for-test\"", unitContent);
        Assert.DoesNotContain("LoadCredential=", unitContent);
        Assert.DoesNotContain("MOHIST_OPERATOR_TOKEN_PATH=", unitContent);
    }

    [Fact]
    public async Task InstallSlack_UsesUserCredentialFileWhenNoInstallationTokenIsSet()
    {
        var files = new FakeFileSystem();
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        var installer = new SystemdServiceInstaller(
            TextWriter.Null, TextWriter.Null, files, new FakeCommandExecutor(), environment);

        var exitCode = await installer.InstallSlackAsync(new ServiceInstallOptions(
            DryRun: true,
            UnitDir: "/units",
            RepoRoot: "/repo",
            ListenUrl: null,
            ServerUrl: null,
            RunnerRoot: null,
            RuntimeRoot: "/stable/runner"));

        Assert.Equal(0, exitCode);
        var unitContent = files.Read("/units/mohist-slack.service");
        Assert.Contains("LoadCredential=operator-token:%h/.mohist/operator-token", unitContent);
        Assert.Contains("Environment=\"MOHIST_OPERATOR_TOKEN_PATH=%d/operator-token\"", unitContent);
    }

    [Fact]
    public async Task InstallSlack_UsesExplicitCredentialPathWhenConfigured()
    {
        var files = new FakeFileSystem();
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        environment["MOHIST_OPERATOR_TOKEN_PATH"] = "/run/mohist/operator-token";
        var installer = new SystemdServiceInstaller(
            TextWriter.Null, TextWriter.Null, files, new FakeCommandExecutor(), environment);

        var exitCode = await installer.InstallSlackAsync(new ServiceInstallOptions(
            DryRun: true,
            UnitDir: "/units",
            RepoRoot: "/repo",
            ListenUrl: null,
            ServerUrl: null,
            RunnerRoot: null,
            RuntimeRoot: "/stable/runner"));

        Assert.Equal(0, exitCode);
        var unitContent = files.Read("/units/mohist-slack.service");
        Assert.Contains("LoadCredential=operator-token:/run/mohist/operator-token", unitContent);
        Assert.Contains("Environment=\"MOHIST_OPERATOR_TOKEN_PATH=%d/operator-token\"", unitContent);
    }

    [Fact]
    public async Task InstallRunner_IncludesUserLocalDotnetRoot()
    {
        var files = new FakeFileSystem();
        files.AddFile("/home/test/.dotnet/dotnet", "");
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        environment["HOME"] = "/home/test";
        var installer = new SystemdServiceInstaller(
            TextWriter.Null,
            TextWriter.Null,
            files,
            new FakeCommandExecutor(),
            environment);

        await installer.InstallRunnerAsync(new ServiceInstallOptions(
            DryRun: true,
            UnitDir: "/units",
            RepoRoot: "/repo",
            ListenUrl: null,
            ServerUrl: null,
            RunnerRoot: null,
            RuntimeRoot: "/stable/runner"));

        var unitContent = files.Read("/units/mohist-runner.service");
        Assert.Contains("Environment=\"DOTNET_ROOT=/home/test/.dotnet\"", unitContent);
        Assert.Contains("Environment=\"DOTNET_ROOT_X64=/home/test/.dotnet\"", unitContent);
    }

    [Fact]
    public async Task InstallServer_RejectsSourceBoundUnitWhenRuntimeTargetIsMissing()
    {
        var f = new UpdateTestFactory();

        var exitCode = await f.Installer.InstallServerAsync(new ServiceInstallOptions(
            DryRun: true,
            UnitDir: "/units",
            RepoRoot: "/repo",
            ListenUrl: null,
            ServerUrl: null,
            RunnerRoot: null));

        Assert.Equal(1, exitCode);
        Assert.False(f.Files.HasFile("/units/mohist.service"));
        Assert.Contains("source-bound unit", f.Stderr.ToString());
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
            RunnerRoot: null,
            RuntimeRoot: "/stable/server");

        await f.Installer.InstallServerAsync(options);

        var unitContent = f.Files.Read("/units/mohist.service");
        Assert.Contains("WorkingDirectory=/stable/server", unitContent);
        Assert.DoesNotContain("/custom/path", unitContent);
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
            RunnerRoot: null,
            RuntimeRoot: "/stable/server");

        await f.Installer.InstallServerAsync(options);

        var unitContent = f.Files.Read("/units/mohist.service");
        Assert.DoesNotContain("--urls", unitContent);
        Assert.DoesNotContain("http://", unitContent);
        Assert.Contains("dotnet /stable/server/current/Mohist.Server.dll", unitContent);
    }
}
