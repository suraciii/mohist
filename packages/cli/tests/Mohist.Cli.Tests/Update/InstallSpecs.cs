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
        Assert.Contains("ExecStart=/repo/packages/go/mohist-slack/bin/mohist-slack", unitContent);
        Assert.Contains("Restart=on-failure", unitContent);
        Assert.Contains("Environment=\"SERVER_URL=http://127.0.0.1:4567\"", unitContent);
        Assert.Contains("Environment=\"MOHIST_OPERATOR_TOKEN=operator-token-for-test\"", unitContent);
        Assert.DoesNotContain("LoadCredential=", unitContent);
        Assert.DoesNotContain("MOHIST_OPERATOR_TOKEN_PATH=", unitContent);
    }

    [Fact]
    public async Task InstallSlack_PromotesRootBuildArtifactBeforeStartingService()
    {
        var files = new FakeFileSystem();
        var source = Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", "build", "mohist-slack");
        var destination = Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", "mohist-slack");
        files.AddFile(source, "static binary");
        var installer = new SystemdServiceInstaller(
            TextWriter.Null,
            TextWriter.Null,
            files,
            new FakeCommandExecutor(),
            new MockEnvironmentVariableProvider());

        var exitCode = await installer.InstallSlackAsync(new ServiceInstallOptions(
            DryRun: false,
            UnitDir: "/units",
            RepoRoot: "/repo",
            ListenUrl: null,
            ServerUrl: null,
            RunnerRoot: null));

        Assert.Equal(0, exitCode);
        Assert.Equal("static binary", files.Read(destination));
    }

    [Fact]
    public async Task InstallSlack_WhenUpdateHoldsUserLock_DoesNotPromoteOrCreateUnit()
    {
        const string home = "/home/test";
        var files = new FakeFileSystem();
        var source = Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", "build", "mohist-slack");
        var destination = Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", "mohist-slack");
        files.AddFile(source, "static binary");
        using var held = files.TryAcquireFileLock(Path.Combine(
            home, ".mohist", "update", "slack", "transaction.lock"));
        Assert.NotNull(held);
        var commands = new FakeCommandExecutor();
        var error = new StringWriter();
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false)
        {
            ["HOME"] = home,
        };
        var installer = new SystemdServiceInstaller(
            TextWriter.Null,
            error,
            files,
            commands,
            environment);

        var exitCode = await installer.InstallSlackAsync(new ServiceInstallOptions(
            DryRun: false,
            UnitDir: "/units",
            RepoRoot: "/repo",
            ListenUrl: null,
            ServerUrl: null,
            RunnerRoot: null));

        Assert.Equal(1, exitCode);
        Assert.False(files.Exists(destination));
        Assert.False(files.Exists("/units/mohist-slack.service"));
        Assert.Empty(commands.ExecutedCommands);
        Assert.Contains("already running", error.ToString());
    }

    [Fact]
    public async Task InstallSlack_WhenInterruptedUpdateMarkerExists_DoesNotPromoteOrCreateUnit()
    {
        const string home = "/home/test";
        var files = new FakeFileSystem();
        var source = Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", "build", "mohist-slack");
        var destination = Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", "mohist-slack");
        files.AddFile(source, "static binary");
        files.WriteAllTextUserOnly(
            Path.Combine(home, ".mohist", "update", "slack", "recovery-required"),
            "{}");
        var commands = new FakeCommandExecutor();
        var error = new StringWriter();
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false)
        {
            ["HOME"] = home,
        };
        var installer = new SystemdServiceInstaller(
            TextWriter.Null,
            error,
            files,
            commands,
            environment);

        var exitCode = await installer.InstallSlackAsync(new ServiceInstallOptions(
            DryRun: false,
            UnitDir: "/units",
            RepoRoot: "/repo",
            ListenUrl: null,
            ServerUrl: null,
            RunnerRoot: null));

        Assert.Equal(1, exitCode);
        Assert.False(files.Exists(destination));
        Assert.False(files.Exists("/units/mohist-slack.service"));
        Assert.Empty(commands.ExecutedCommands);
        Assert.Contains("unresolved Slack update", error.ToString());
    }

    [Fact]
    public async Task InstallSlack_WithInvalidUnitConfiguration_DoesNotReplaceInstalledBinary()
    {
        var files = new FakeFileSystem();
        var source = Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", "build", "mohist-slack");
        var destination = Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", "mohist-slack");
        files.AddFile(source, "new binary");
        files.AddFile(destination, "old binary");
        var commands = new FakeCommandExecutor();
        var installer = new SystemdServiceInstaller(
            TextWriter.Null,
            TextWriter.Null,
            files,
            commands,
            new MockEnvironmentVariableProvider());

        await Assert.ThrowsAsync<ArgumentException>(() => installer.InstallSlackAsync(new ServiceInstallOptions(
            DryRun: false,
            UnitDir: "/units",
            RepoRoot: "/repo",
            ListenUrl: null,
            ServerUrl: "http://example.com\ninvalid",
            RunnerRoot: null)));

        Assert.Equal("old binary", files.Read(destination));
        Assert.False(files.Exists("/units/mohist-slack.service"));
        Assert.Empty(commands.ExecutedCommands);
    }

    [Fact]
    public async Task InstallSlack_WhenBuildArtifactIsMissing_DoesNotCreateService()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var error = new StringWriter();
        var installer = new SystemdServiceInstaller(
            TextWriter.Null,
            error,
            files,
            commands,
            new MockEnvironmentVariableProvider());

        var exitCode = await installer.InstallSlackAsync(new ServiceInstallOptions(
            DryRun: false,
            UnitDir: "/units",
            RepoRoot: "/repo",
            ListenUrl: null,
            ServerUrl: null,
            RunnerRoot: null));

        Assert.Equal(1, exitCode);
        Assert.False(files.Exists("/units/mohist-slack.service"));
        Assert.Empty(commands.ExecutedCommands);
        Assert.Contains("build artifact not found", error.ToString());
    }

    [Fact]
    public async Task InstallSlack_UsesSystemdEscapingForExecutablePath()
    {
        var files = new FakeFileSystem();
        var installer = new SystemdServiceInstaller(
            TextWriter.Null, TextWriter.Null, files, new FakeCommandExecutor(), new MockEnvironmentVariableProvider());

        var exitCode = await installer.InstallSlackAsync(new ServiceInstallOptions(
            DryRun: true,
            UnitDir: "/units",
            RepoRoot: "/repo with 'single' \"double\" $cash %rate",
            ListenUrl: null,
            ServerUrl: null,
            RunnerRoot: null));

        Assert.Equal(0, exitCode);
        Assert.Contains(
            "WorkingDirectory=\"/repo with 'single' \\\"double\\\" $cash %%rate\"",
            files.Read("/units/mohist-slack.service"));
        Assert.Contains(
            "ExecStart=\"/repo with 'single' \\\"double\\\" $$cash %%rate/packages/go/mohist-slack/bin/mohist-slack\"",
            files.Read("/units/mohist-slack.service"));
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
            RunnerRoot: null));

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
            RunnerRoot: null));

        Assert.Equal(0, exitCode);
        var unitContent = files.Read("/units/mohist-slack.service");
        Assert.Contains("LoadCredential=operator-token:/run/mohist/operator-token", unitContent);
        Assert.Contains("Environment=\"MOHIST_OPERATOR_TOKEN_PATH=%d/operator-token\"", unitContent);
    }

    [Fact]
    public async Task SlackLifecycle_ForwardsCancellationToSystemctl()
    {
        var f = new UpdateTestFactory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            f.Installer.StopSlackAsync(
                new ServiceCommandOptions(false, "/units", 100, false),
                cancellation.Token));

        Assert.Empty(f.Commands.ExecutedCommands);
    }

    [Fact]
    public async Task RestartSlack_WhenCancelledDuringSystemctl_AttemptsNonCancellableStart()
    {
        var f = new UpdateTestFactory();
        using var cancellation = new CancellationTokenSource();
        f.Commands.OnExecute = (fileName, args) =>
        {
            if (fileName == "systemctl" && args.Contains("restart")) cancellation.Cancel();
        };
        f.Commands.SetStdoutFor(
            "systemctl",
            args => args.SequenceEqual(["--user", "is-active", "mohist-slack.service"]),
            "active\n");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            f.Installer.RestartSlackAsync(
                new ServiceCommandOptions(false, "/units", 100, false),
                cancellation.Token));

        Assert.Collection(
            f.Commands.ExecutedCommands,
            command => Assert.Equal(["--user", "is-active", "mohist-slack.service"], command.Args),
            command => Assert.Equal(["--user", "restart", "mohist-slack.service"], command.Args),
            command => Assert.Equal(["--user", "start", "mohist-slack.service"], command.Args));
    }

    [Fact]
    public async Task RestartSlack_WhenStoppedAndCancelled_RestoresStoppedState()
    {
        var f = new UpdateTestFactory();
        using var cancellation = new CancellationTokenSource();
        f.Commands.OnExecute = (fileName, args) =>
        {
            if (fileName == "systemctl" && args.Contains("restart")) cancellation.Cancel();
        };
        f.Commands.SetResultFor(
            "systemctl",
            args => args.SequenceEqual(["--user", "is-active", "mohist-slack.service"]),
            3,
            "inactive\n",
            "");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            f.Installer.RestartSlackAsync(
                new ServiceCommandOptions(false, "/units", 100, false),
                cancellation.Token));

        Assert.Collection(
            f.Commands.ExecutedCommands,
            command => Assert.Equal(["--user", "is-active", "mohist-slack.service"], command.Args),
            command => Assert.Equal(["--user", "restart", "mohist-slack.service"], command.Args),
            command => Assert.Equal(["--user", "stop", "mohist-slack.service"], command.Args));
    }

    [Fact]
    public async Task RestartSlack_WhenInitialStateIsUnknown_DoesNotRestart()
    {
        var f = new UpdateTestFactory();
        f.Commands.SetResultFor(
            "systemctl",
            args => args.SequenceEqual(["--user", "is-active", "mohist-slack.service"]),
            1,
            "",
            "connection failed");

        var exitCode = await f.Installer.RestartSlackAsync(
            new ServiceCommandOptions(false, "/units", 100, false));

        Assert.Equal(1, exitCode);
        Assert.Single(f.Commands.ExecutedCommands);
        Assert.Contains("state could not be verified", f.Stderr.ToString());
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
            RunnerRoot: null));

        var unitContent = files.Read("/units/mohist-runner.service");
        Assert.Contains("Environment=\"DOTNET_ROOT=/home/test/.dotnet\"", unitContent);
        Assert.Contains("Environment=\"DOTNET_ROOT_X64=/home/test/.dotnet\"", unitContent);
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
