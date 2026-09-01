using EnvironmentAbstractions.TestHelpers;
using Microsoft.Extensions.Time.Testing;
using Mohist.Cli;
using Mohist.Server.TestSupport;
using Xunit;
using static Mohist.Server.L0Tests.SystemSpecs.WindowsInstallTestSupport;

namespace Mohist.Server.L0Tests.SystemSpecs;

public class WindowsInstallRegistrationTests
{
    [Fact]
    public async Task InstallSlack_WithSucceedingSchtasks_WritesGoLauncherAndMetadata()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(
            Path.Combine(@"C:\repo", "packages", "go", "mohist-slack", "bin", "build", "mohist-slack.exe"),
            "binary");
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) => IsSlackTaskProbe(fileName, args)
                ? (2, "", "")
                : (0, "", ""),
        };
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.InstallSlackAsync(InstallOptions(
            repoRoot: @"C:\repo",
            serverUrl: "http://example.com:9999"));

        Assert.Equal(0, exitCode);
        Assert.True(files.HasFile(SlackLauncher));
        Assert.True(files.HasFile(SlackMetadata));
        Assert.True(files.HasFile(Path.Combine(@"C:\repo", "packages", "go", "mohist-slack", "bin", "mohist-slack.exe")));
        Assert.Contains("packages\\go\\mohist-slack\\bin\\mohist-slack.exe", files.ReadAllText(SlackLauncher));
        Assert.Contains("scheduled-task", files.ReadAllText(SlackMetadata));
        Assert.Contains("repoRoot", files.ReadAllText(SlackMetadata));
        Assert.Contains("Mohist_Slack", commands.ExecutedCommands.Single(c => c.Args[0] == "/Create").Args);
    }

    [Fact]
    public async Task InstallSlack_WithInvalidConfiguration_DoesNotReplaceInstalledBinary()
    {
        var files = new FakeFileSystem();
        var artifact = Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", "build", "mohist-slack.exe");
        files.WriteAllText(artifact, "new binary");
        files.WriteAllText(SlackExecutable, "old binary");
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false)
        {
            ["MOHIST_OPERATOR_TOKEN"] = "invalid\nvalue",
        };
        var commands = new FakeCommandExecutor();
        var installer = CreateInstaller(files, commands, environment: environment);

        await Assert.ThrowsAsync<ArgumentException>(
            () => installer.InstallSlackAsync(InstallOptions()));

        Assert.Equal("old binary", files.ReadAllText(SlackExecutable));
        Assert.Empty(commands.ExecutedCommands);
    }

    [Fact]
    public async Task InstallSlack_WhenBuildArtifactIsMissing_DoesNotProbeOrRegisterTask()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.InstallSlackAsync(InstallOptions());

        Assert.Equal(1, exitCode);
        Assert.Empty(commands.ExecutedCommands);
        Assert.False(files.HasFile(SlackLauncher));
        Assert.False(files.HasFile(SlackMetadata));
    }

    [Fact]
    public async Task InstallSlack_WithUnresolvedBinaryBackup_FailsBeforeTaskOrFileMutation()
    {
        var files = new FakeFileSystem();
        var artifact = Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", "build", "mohist-slack.exe");
        var backup = $"{SlackExecutable}.install.previous";
        files.WriteAllText(artifact, "next binary");
        files.WriteAllText(SlackExecutable, "interrupted binary");
        files.WriteAllText(backup, "known good binary");
        var commands = new FakeCommandExecutor();
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.InstallSlackAsync(InstallOptions());

        Assert.Equal(1, exitCode);
        Assert.Equal("interrupted binary", files.ReadAllText(SlackExecutable));
        Assert.Equal("known good binary", files.ReadAllText(backup));
        Assert.Empty(commands.ExecutedCommands);
        Assert.False(files.HasFile(SlackLauncher));
    }

    [Fact]
    public async Task InstallSlack_WhenExistingStateCannotBeRead_DoesNotMutateTaskOrFiles()
    {
        var files = new FakeFileSystem();
        var artifact = Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", "build", "mohist-slack.exe");
        files.WriteAllText(artifact, "next binary");
        files.WriteAllText(SlackLauncher, "old launcher");
        files.FailNextRead = path => string.Equals(path, SlackLauncher, StringComparison.OrdinalIgnoreCase);
        var commands = new FakeCommandExecutor();
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.InstallSlackAsync(InstallOptions());

        Assert.Equal(1, exitCode);
        Assert.Equal("old launcher", files.ReadAllText(SlackLauncher));
        Assert.Empty(commands.ExecutedCommands);
        Assert.False(files.HasFile(SlackExecutable));
    }

    [Fact]
    public async Task InstallSlack_WhenAnotherInstallOrUpdateHoldsUserLock_DoesNotProbeOrMutate()
    {
        var files = new FakeFileSystem();
        var artifact = Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", "build", "mohist-slack.exe");
        files.WriteAllText(artifact, "next binary");
        using var held = files.TryAcquireFileLock(Path.Combine(
            UserProfile, ".mohist", "update", "slack", "transaction.lock"));
        Assert.NotNull(held);
        var commands = new FakeCommandExecutor();
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.InstallSlackAsync(InstallOptions());

        Assert.Equal(1, exitCode);
        Assert.Empty(commands.ExecutedCommands);
        Assert.False(files.HasFile(SlackExecutable));
    }

    [Fact]
    public async Task InstallSlack_WhenInterruptedUpdateMarkerExists_DoesNotProbeOrMutate()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(
            Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", "build", "mohist-slack.exe"),
            "next binary");
        files.WriteAllText(
            Path.Combine(UserProfile, ".mohist", "update", "slack", "recovery-required"),
            "{}");
        var commands = new FakeCommandExecutor();
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.InstallSlackAsync(InstallOptions());

        Assert.Equal(1, exitCode);
        Assert.Empty(commands.ExecutedCommands);
        Assert.False(files.HasFile(SlackExecutable));
    }

    [Fact]
    public async Task InstallSlack_WhenNamedTaskIsNotOwned_DoesNotMutateInstall()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(
            Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", "build", "mohist-slack.exe"),
            "binary");
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) => IsSlackTaskProbe(fileName, args)
                ? (3, "", "")
                : (0, "", ""),
        };
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.InstallSlackAsync(InstallOptions());

        Assert.Equal(1, exitCode);
        Assert.False(files.HasFile(SlackExecutable));
        Assert.False(files.HasFile(SlackLauncher));
        Assert.DoesNotContain(commands.ExecutedCommands, command => command.FileName == "schtasks");
    }

    [Fact]
    public async Task InstallSlack_ValidatesExistingTaskOwnershipAndDefinition()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(
            Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", "build", "mohist-slack.exe"),
            "binary");
        var commands = new FakeCommandExecutor();
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.InstallSlackAsync(InstallOptions());

        Assert.Equal(0, exitCode);
        var probe = Assert.Single(commands.ExecutedCommands, command => IsSlackTaskProbe(command.FileName, command.Args));
        var script = probe.Args[^1];
        Assert.Contains("$task.Enabled", script);
        Assert.Contains("Principal.UserId", script);
        Assert.Contains("$triggers[0].Type -ne 9", script);
        Assert.Contains("-not $triggers[0].Enabled", script);
        Assert.Contains("$actions[0].Path", script);
        Assert.Contains("[IO.Path]::GetFullPath", script);
        Assert.Contains(SlackLauncher, script);
    }

    [Fact]
    public async Task InstallSlack_WhenPromotionFails_PreservesExistingStartupFallback()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(
            Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", "build", "mohist-slack.exe"),
            "new binary");
        files.WriteAllText(SlackStartup, "call stale-launcher");
        files.FailNextWrite = path => path.EndsWith("mohist-slack.exe.install.tmp", StringComparison.OrdinalIgnoreCase);
        var commands = new FakeCommandExecutor();
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.InstallSlackAsync(InstallOptions());

        Assert.Equal(1, exitCode);
        Assert.Equal("call stale-launcher", files.ReadAllText(SlackStartup));
    }

    [Fact]
    public async Task InstallSlack_TransfersRunningNodeLauncherAfterGoFilesAreReady()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(
            Path.Combine(@"C:\repo", "packages", "go", "mohist-slack", "bin", "build", "mohist-slack.exe"),
            "new binary");
        files.WriteAllText(
            SlackLauncher,
            "@echo off\r\ncd /d C:\\repo\r\nnode packages\\mohist-slack\\dist\\cli.js\r\n");
        files.WriteAllText(SlackStartup, $"@call \"{SlackLauncher}\"\r\n");
        files.WriteAllText(SlackMetadata, "{\"backend\":\"startup-fallback\"}");
        var goProbeCount = 0;
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) => IsSlackTaskProbe(fileName, args)
                ? (2, "", "")
                : IsSlackProcessProbe(fileName, args)
                    ? (args[^1].Contains("Name = 'cmd.exe'", StringComparison.Ordinal)
                        ? (0, "4321\r\n", "")
                        : ++goProbeCount == 1
                            ? (0, "", "")
                            : (0, "9876\r\n", ""))
                    : (0, "", ""),
        };
        var processStarts = 0;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        var installer = CreateInstaller(
            files,
            commands,
            processLauncher: _ =>
            {
                processStarts++;
                return null;
            },
            timeProvider: time,
            pollWait: (delay, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                time.Advance(delay);
                return Task.CompletedTask;
            });

        var exitCode = await installer.InstallSlackAsync(InstallOptions(repoRoot: @"C:\repo"));

        Assert.Equal(0, exitCode);
        Assert.Equal("new binary", files.ReadAllText(Path.Combine(
            @"C:\repo", "packages", "go", "mohist-slack", "bin", "mohist-slack.exe")));
        Assert.Contains("packages\\go\\mohist-slack\\bin\\mohist-slack.exe", files.ReadAllText(SlackLauncher));
        var kill = Assert.Single(commands.ExecutedCommands, command => command.FileName == "taskkill");
        Assert.Equal(["/F", "/T", "/PID", "4321"], kill.Args);
        Assert.Equal(1, processStarts);
    }

    [Fact]
    public async Task InstallSlack_WhenTransferredGoProcessExitsImmediately_PreservesGoInstall()
    {
        const string root = @"C:\repo";
        var files = new FakeFileSystem();
        files.WriteAllText(
            Path.Combine(root, "packages", "go", "mohist-slack", "bin", "build", "mohist-slack.exe"),
            "new binary");
        files.WriteAllText(
            SlackLauncher,
            "@echo off\r\ncd /d C:\\repo\r\nnode packages\\mohist-slack\\dist\\cli.js\r\n");
        files.WriteAllText(SlackStartup, $"@call \"{SlackLauncher}\"\r\n");
        files.WriteAllText(SlackMetadata, "{\"backend\":\"startup-fallback\"}");
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) => IsSlackTaskProbe(fileName, args)
                ? (2, "", "")
                : IsSlackProcessProbe(fileName, args)
                    ? (args[^1].Contains("Name = 'cmd.exe'", StringComparison.Ordinal)
                        ? (0, "4321\r\n", "")
                        : (0, "", ""))
                    : (0, "", ""),
        };
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        var installer = CreateInstaller(
            files,
            commands,
            processLauncher: _ => null,
            timeProvider: time,
            pollWait: (delay, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                time.Advance(delay);
                return Task.CompletedTask;
            });

        var exitCode = await installer.InstallSlackAsync(InstallOptions(repoRoot: root));

        Assert.Equal(1, exitCode);
        Assert.Equal("new binary", files.ReadAllText(Path.Combine(
            root, "packages", "go", "mohist-slack", "bin", "mohist-slack.exe")));
        Assert.Contains("packages\\go\\mohist-slack\\bin\\mohist-slack.exe", files.ReadAllText(SlackLauncher));
    }

    [Fact]
    public async Task InstallSlack_WhenTransferredFallbackStartThrows_PreservesGoInstall()
    {
        const string root = @"C:\repo";
        var files = new FakeFileSystem();
        files.WriteAllText(
            Path.Combine(root, "packages", "go", "mohist-slack", "bin", "build", "mohist-slack.exe"),
            "new binary");
        files.WriteAllText(
            SlackLauncher,
            "@echo off\r\ncd /d C:\\repo\r\nnode packages\\mohist-slack\\dist\\cli.js\r\n");
        files.WriteAllText(SlackStartup, $"@call \"{SlackLauncher}\"\r\n");
        files.WriteAllText(SlackMetadata, "{\"backend\":\"startup-fallback\"}");
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) => IsSlackTaskProbe(fileName, args)
                ? (2, "", "")
                : IsSlackProcessProbe(fileName, args)
                    ? (args[^1].Contains("Name = 'cmd.exe'", StringComparison.Ordinal)
                        ? (0, "4321\r\n", "")
                        : (0, "", ""))
                    : (0, "", ""),
        };
        var installer = CreateInstaller(
            files,
            commands,
            processLauncher: _ => throw new InvalidOperationException("start failed"));

        var exitCode = await installer.InstallSlackAsync(InstallOptions(repoRoot: root));

        Assert.Equal(1, exitCode);
        Assert.Equal("new binary", files.ReadAllText(Path.Combine(
            root, "packages", "go", "mohist-slack", "bin", "mohist-slack.exe")));
        Assert.Contains("packages\\go\\mohist-slack\\bin\\mohist-slack.exe", files.ReadAllText(SlackLauncher));
    }

    [Fact]
    public async Task InstallSlack_WhenTransferredGoServiceDoesNotStart_PreservesGoInstall()
    {
        const string root = @"C:\repo";
        var files = new FakeFileSystem();
        files.WriteAllText(
            Path.Combine(root, "packages", "go", "mohist-slack", "bin", "build", "mohist-slack.exe"),
            "new binary");
        files.WriteAllText(
            SlackLauncher,
            "@echo off\r\ncd /d C:\\repo\r\nnode packages\\mohist-slack\\dist\\cli.js\r\n");
        files.WriteAllText(SlackMetadata, "{\"backend\":\"scheduled-task\"}");
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) => IsSlackTaskProbe(fileName, args)
                ? (0, "", "")
                : IsSlackProcessProbe(fileName, args)
                    ? (args[^1].Contains("Name = 'cmd.exe'", StringComparison.Ordinal)
                        ? (0, "4321\r\n", "")
                        : (0, "", ""))
                    : fileName == "schtasks" && args[0] == "/Run"
                        ? (7, "", "start failed")
                        : (0, "", ""),
        };
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.InstallSlackAsync(InstallOptions(repoRoot: root));

        Assert.Equal(7, exitCode);
        Assert.Equal("new binary", files.ReadAllText(Path.Combine(
            root, "packages", "go", "mohist-slack", "bin", "mohist-slack.exe")));
        Assert.Contains("packages\\go\\mohist-slack\\bin\\mohist-slack.exe", files.ReadAllText(SlackLauncher));
        Assert.DoesNotContain("packages\\mohist-slack\\dist\\cli.js", files.ReadAllText(SlackLauncher));
    }

    [Fact]
    public async Task InstallSlack_WhenFailedCreateLeavesTaskAndLauncherWriteFails_DeletesTaskOnRollback()
    {
        var files = new FakeFileSystem();
        var artifact = Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", "build", "mohist-slack.exe");
        files.WriteAllText(artifact, "next binary");
        files.FailNextWrite = path => string.Equals(path, SlackLauncher, StringComparison.OrdinalIgnoreCase);
        var probeCount = 0;
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) => IsSlackTaskProbe(fileName, args)
                ? (++probeCount == 1 ? (2, "", "") : (0, "", ""))
                : fileName == "schtasks" && args[0] == "/Create"
                    ? (1, "", "creation returned an error")
                    : (0, "", ""),
        };
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.InstallSlackAsync(InstallOptions());

        Assert.Equal(1, exitCode);
        Assert.False(files.HasFile(SlackExecutable));
        Assert.Contains(commands.ExecutedCommands, command => command.FileName == "schtasks" && command.Args[0] == "/Delete");
    }

    [Fact]
    public async Task InstallSlack_WhenTaskCreationFailsAndTaskIsAbsent_UsesStartupFallback()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(
            Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", "build", "mohist-slack.exe"),
            "binary");
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) => IsSlackTaskProbe(fileName, args)
                ? (2, "", "")
                : fileName == "schtasks" && args[0] == "/Create"
                    ? (1, "", "Access denied")
                    : (0, "", ""),
        };
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.InstallSlackAsync(InstallOptions());

        Assert.Equal(0, exitCode);
        Assert.True(files.HasFile(SlackStartup));
        Assert.Contains("startup-fallback", files.ReadAllText(SlackMetadata));
    }

    [Fact]
    public async Task InstallSlack_WhenTaskProbeIsDenied_DoesNotMutateInstall()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(
            Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", "build", "mohist-slack.exe"),
            "binary");
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) => IsSlackTaskProbe(fileName, args)
                ? (1, "", "ERROR: Access is denied.")
                : (0, "", ""),
        };
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.InstallSlackAsync(InstallOptions());

        Assert.Equal(1, exitCode);
        Assert.False(files.HasFile(SlackStartup));
        Assert.False(files.HasFile(SlackMetadata));
        Assert.False(files.HasFile(SlackExecutable));
    }

    [Fact]
    public async Task InstallSlack_WithExistingFallbackAndAbsentTask_PreservesSingleFallbackBackend()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(
            Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", "build", "mohist-slack.exe"),
            "new binary");
        files.WriteAllText(SlackStartup, "call old-launcher");
        files.WriteAllText(SlackMetadata, "{");
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) => IsSlackTaskProbe(fileName, args)
                ? (2, "", "")
                : (0, "", ""),
        };
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.InstallSlackAsync(InstallOptions());

        Assert.Equal(0, exitCode);
        Assert.True(files.HasFile(SlackStartup));
        Assert.Contains("startup-fallback", files.ReadAllText(SlackMetadata));
        Assert.DoesNotContain(commands.ExecutedCommands, command => command.FileName == "schtasks" && command.Args[0] == "/Create");
    }

    [Fact]
    public async Task InstallSlack_WithTaskAndStaleFallback_RemovesFallbackBeforeMutation()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(
            Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", "build", "mohist-slack.exe"),
            "new binary");
        files.WriteAllText(SlackStartup, "call stale-launcher");
        files.WriteAllText(SlackMetadata, "{");
        var commands = new FakeCommandExecutor();
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.InstallSlackAsync(InstallOptions());

        Assert.Equal(0, exitCode);
        Assert.False(files.HasFile(SlackStartup));
        Assert.Contains("scheduled-task", files.ReadAllText(SlackMetadata));
        Assert.DoesNotContain(commands.ExecutedCommands, command => command.FileName == "schtasks" && command.Args[0] == "/Create");
    }

    [Theory]
    [InlineData(WindowsServiceTarget.Server)]
    [InlineData(WindowsServiceTarget.Runner)]
    public async Task Install_WithFailingSchtasks_CreatesStartupFallbackAndRecordsMetadata(WindowsServiceTarget target)
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) =>
                fileName == "schtasks" && args[0] == "/Create"
                    ? (1, "", "Access denied")
                    : (0, "", "")
        };
        var installer = CreateInstaller(files, commands);

        var exitCode = await InstallAsync(installer, target, TargetInstallOptions(target));

        Assert.Equal(0, exitCode);
        Assert.True(files.HasFile(Startup(target)));
        Assert.True(files.HasFile(Metadata(target)));
        Assert.Contains("startup-fallback", files.ReadAllText(Metadata(target)));
        Assert.Contains("call", files.ReadAllText(Startup(target)));
        Assert.Contains(Path.GetFileName(Launcher(target)), files.ReadAllText(Startup(target)));
    }

    [Theory]
    [InlineData(WindowsServiceTarget.Server)]
    [InlineData(WindowsServiceTarget.Runner)]
    public async Task Install_WithSucceedingSchtasks_WritesLauncherAndMetadata(WindowsServiceTarget target)
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var output = new StringWriter();
        var installer = CreateInstaller(files, commands, output: output);

        var exitCode = await InstallAsync(installer, target, TargetInstallOptions(target));

        Assert.Equal(0, exitCode);
        Assert.True(files.HasFile(Launcher(target)));
        Assert.True(files.HasFile(Metadata(target)));
        Assert.Contains("scheduled-task", files.ReadAllText(Metadata(target)));
        Assert.Contains($"Registered Scheduled Task {TaskName(target)}", output.ToString());

        var createCommand = commands.ExecutedCommands.FirstOrDefault(c => c.Args[0] == "/Create");
        Assert.NotEqual(default, createCommand);
        Assert.Contains(TaskName(target), createCommand.Args);

        var body = files.ReadAllText(Launcher(target));
        if (target == WindowsServiceTarget.Server)
        {
            Assert.Contains("dotnet run --project", body);
            Assert.Contains("ASPNETCORE_URLS=http://127.0.0.1:3456", body);
            Assert.Contains(@"%USERPROFILE%\.mohist\server\out.log", body);
        }
        else
        {
            Assert.Contains("set \"SERVER_URL=http://example.com:9999\"", body);
            Assert.Contains("set \"RUNNER_ROOT=C:\\custom-runner\"", body);
            Assert.Contains("node packages\\runner\\dist\\cli.js", body);
            Assert.Contains("http://example.com:9999", files.ReadAllText(Metadata(target)));
        }
    }

    [Theory]
    [InlineData(WindowsServiceTarget.Server)]
    [InlineData(WindowsServiceTarget.Runner)]
    public async Task Install_ReinstallFromStartupFallbackToScheduledTask_RemovesStaleStartupFile(WindowsServiceTarget target)
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        files.WriteAllText(Metadata(target), "{\"backend\":\"startup-fallback\"}");
        files.WriteAllText(Startup(target), "call \"x\"");
        var installer = CreateInstaller(files, commands);

        var exitCode = await InstallAsync(installer, target, TargetInstallOptions(target));

        Assert.Equal(0, exitCode);
        Assert.False(files.HasFile(Startup(target)));
        Assert.Contains("scheduled-task", files.ReadAllText(Metadata(target)));
    }
}
