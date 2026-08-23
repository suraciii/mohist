using System.Diagnostics;
using Mohist.Cli;
using Mohist.Server.TestSupport;
using Xunit;
using static Mohist.Server.UnitTests.SystemSpecs.WindowsInstallTestSupport;

namespace Mohist.Server.UnitTests.SystemSpecs;

public class WindowsServiceLifecycleTests
{
    [Fact]
    public async Task StopSlack_WithLauncherOnlyBackend_KillsOnlyInstalledExecutable()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) => IsSlackTaskProbe(fileName, args)
                ? (2, "", "")
                : IsSlackProcessProbe(fileName, args)
                    ? (0, "1234\r\n", "")
                    : (0, "", "")
        };
        files.WriteAllText(SlackLauncher, "@echo off\r\ncd /d /repo\r\n");
        files.WriteAllText(SlackMetadata, "{\"backend\":\"launcher-only\"}");
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.StopSlackAsync(CommandOptions());

        Assert.Equal(0, exitCode);
        var processQuery = Assert.Single(commands.ExecutedCommands, command => IsSlackProcessProbe(command.FileName, command.Args));
        Assert.Contains("$ErrorActionPreference = 'Stop'", processQuery.Args[^1]);
        Assert.Contains("-ErrorAction Stop", processQuery.Args[^1]);
        Assert.Contains("ExecutablePath -ieq $path", processQuery.Args[^1]);
        Assert.Contains(SlackExecutable, processQuery.Args[^1]);
        Assert.DoesNotContain(commands.ExecutedCommands, command => command.FileName == "tasklist");
        var taskkill = Assert.Single(commands.ExecutedCommands, command => command.FileName == "taskkill");
        Assert.Equal(["/F", "/PID", "1234"], taskkill.Args);
    }

    [Fact]
    public async Task StatusSlack_ReportsGoProcessAsRunning()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) => IsSlackTaskProbe(fileName, args)
                ? (2, "", "")
                : IsSlackProcessProbe(fileName, args)
                    ? (0, "1234\r\n", "")
                    : (0, "", "")
        };
        files.WriteAllText(SlackLauncher, "@echo off\r\ncd /d /repo\r\n");
        files.WriteAllText(SlackMetadata, "{\"backend\":\"launcher-only\"}");
        var output = new StringWriter();
        var installer = CreateInstaller(files, commands, output: output);

        var exitCode = await installer.StatusSlackAsync(CommandOptions());

        Assert.Equal(0, exitCode);
        Assert.Contains("running: yes", output.ToString());
        var processQuery = Assert.Single(commands.ExecutedCommands, command => IsSlackProcessProbe(command.FileName, command.Args));
        Assert.Contains(SlackExecutable, processQuery.Args[^1]);
        Assert.DoesNotContain(commands.ExecutedCommands, command => command.FileName == "tasklist");
    }

    [Fact]
    public async Task StopSlack_WithoutInstalledRepoPath_RefusesBroadImageKill()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) => IsSlackTaskProbe(fileName, args)
                ? (0, "", "")
                : (0, "", ""),
        };
        files.WriteAllText(SlackLauncher, "@echo off");
        var error = new StringWriter();
        var installer = CreateInstaller(files, commands, error: error);

        var exitCode = await installer.StopSlackAsync(CommandOptions());

        Assert.Equal(1, exitCode);
        Assert.Contains("Cannot safely stop Slack", error.ToString());
        Assert.DoesNotContain(commands.ExecutedCommands, command => command.FileName is "taskkill" or "tasklist");
    }

    [Fact]
    public async Task StopSlack_WhenExactProcessQueryFails_ReturnsFailure()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) => IsSlackTaskProbe(fileName, args)
                ? (0, "", "")
                : IsSlackProcessProbe(fileName, args)
                    ? (7, "", "CIM unavailable")
                    : (0, "", "")
        };
        files.WriteAllText(SlackLauncher, "@echo off\r\ncd /d /repo\r\n");
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.StopSlackAsync(CommandOptions());

        Assert.Equal(7, exitCode);
        Assert.DoesNotContain(commands.ExecutedCommands, command => command.FileName == "taskkill");
    }

    [Fact]
    public async Task RestartSlack_WhenStopFails_DoesNotStartAnotherProcess()
    {
        var files = new FakeFileSystem();
        var launched = new List<ProcessStartInfo>();
        files.WriteAllText(SlackLauncher, "@echo off");
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) => IsSlackTaskProbe(fileName, args)
                ? (0, "", "")
                : (0, "", ""),
        };
        var installer = CreateInstaller(files, commands, processLauncher: info =>
        {
            launched.Add(info);
            return null;
        });

        var exitCode = await installer.RestartSlackAsync(CommandOptions());

        Assert.Equal(1, exitCode);
        Assert.Empty(launched);
    }

    [Fact]
    public async Task RestartSlack_WhenCancelledAfterEndingTask_AttemptsNonCancellableStart()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(SlackLauncher, "@echo off\r\ncd /d /repo\r\npackages\\go\\mohist-slack\\bin\\mohist-slack.exe\r\n");
        files.WriteAllText(SlackMetadata, "{\"backend\":\"scheduled-task\",\"repoRoot\":\"/repo\"}");
        using var cancellation = new CancellationTokenSource();
        var processProbeCount = 0;
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) => IsSlackProcessProbe(fileName, args)
                ? (++processProbeCount == 1 ? (0, "4321\r\n", "") : (0, "", ""))
                : (0, "", ""),
            OnExecute = (fileName, args) =>
            {
                if (fileName == "schtasks" && args[0] == "/End") cancellation.Cancel();
            },
        };
        var installer = CreateInstaller(files, commands);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            installer.RestartSlackAsync(CommandOptions(), cancellation.Token));

        Assert.Contains(commands.ExecutedCommands, command => command.FileName == "schtasks" && command.Args[0] == "/Run");
    }

    [Fact]
    public async Task RestartSlack_WhenAlreadyCancelled_DoesNotStartStoppedService()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(SlackLauncher, "@echo off\r\ncd /d /repo\r\npackages\\go\\mohist-slack\\bin\\mohist-slack.exe\r\n");
        files.WriteAllText(SlackMetadata, "{\"backend\":\"scheduled-task\",\"repoRoot\":\"/repo\"}");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var commands = new FakeCommandExecutor();
        var installer = CreateInstaller(files, commands);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            installer.RestartSlackAsync(CommandOptions(), cancellation.Token));

        Assert.Empty(commands.ExecutedCommands);
    }

    [Fact]
    public async Task RestartSlack_WhenStoppedAndCancelledAfterRun_RestoresStoppedState()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(SlackLauncher, "@echo off\r\ncd /d /repo\r\npackages\\go\\mohist-slack\\bin\\mohist-slack.exe\r\n");
        files.WriteAllText(SlackMetadata, "{\"backend\":\"scheduled-task\",\"repoRoot\":\"/repo\"}");
        using var cancellation = new CancellationTokenSource();
        var commands = new FakeCommandExecutor
        {
            OnExecute = (fileName, args) =>
            {
                if (fileName == "schtasks" && args[0] == "/Run") cancellation.Cancel();
            },
        };
        var installer = CreateInstaller(files, commands);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            installer.RestartSlackAsync(CommandOptions(), cancellation.Token));

        var run = Assert.Single(commands.ExecutedCommands, command => command.FileName == "schtasks" && command.Args[0] == "/Run");
        var ends = commands.ExecutedCommands.Where(command => command.FileName == "schtasks" && command.Args[0] == "/End").ToArray();
        Assert.Equal(2, ends.Length);
        Assert.True(commands.ExecutedCommands.IndexOf(run) < commands.ExecutedCommands.IndexOf(ends[^1]));
    }

    [Theory]
    [InlineData(WindowsServiceTarget.Server)]
    [InlineData(WindowsServiceTarget.Runner)]
    public async Task Start_WithScheduledTaskBackend_RunsSchtasksRun(WindowsServiceTarget target)
    {
        var files = new FakeFileSystem();
        var commands = ScheduledTaskCommands();
        files.WriteAllText(Metadata(target), "{\"backend\":\"scheduled-task\"}");
        var installer = CreateInstaller(files, commands);

        var exitCode = await StartAsync(installer, target, CommandOptions());

        Assert.Equal(0, exitCode);
        var runCommand = commands.ExecutedCommands.FirstOrDefault(c => c.Args[0] == "/Run");
        Assert.NotEqual(default, runCommand);
        Assert.Equal(TaskName(target), runCommand.Args[2]);
    }

    [Theory]
    [InlineData(WindowsServiceTarget.Server)]
    [InlineData(WindowsServiceTarget.Runner)]
    public async Task Start_WithLauncherOnlyBackend_StartsDetachedProcess(WindowsServiceTarget target)
    {
        var files = new FakeFileSystem();
        var launched = new List<ProcessStartInfo>();
        Process? Launch(ProcessStartInfo startInfo)
        {
            launched.Add(startInfo);
            return null;
        }

        files.WriteAllText(Launcher(target), "@echo off");
        var installer = CreateInstaller(files, new FakeCommandExecutor(), processLauncher: Launch);

        var exitCode = await StartAsync(installer, target, CommandOptions());

        Assert.Equal(0, exitCode);
        var startInfo = Assert.Single(launched);
        Assert.Equal(Launcher(target), startInfo.FileName);
        Assert.True(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
    }

    [Theory]
    [InlineData(WindowsServiceTarget.Server)]
    [InlineData(WindowsServiceTarget.Runner)]
    public async Task Stop_WithScheduledTaskBackend_RunsSchtasksEnd(WindowsServiceTarget target)
    {
        var files = new FakeFileSystem();
        var commands = ScheduledTaskCommands();
        files.WriteAllText(Metadata(target), "{\"backend\":\"scheduled-task\"}");
        var installer = CreateInstaller(files, commands);

        var exitCode = await StopAsync(installer, target, CommandOptions());

        Assert.Equal(0, exitCode);
        var endCommand = commands.ExecutedCommands.FirstOrDefault(c => c.Args[0] == "/End");
        Assert.NotEqual(default, endCommand);
        Assert.Equal(TaskName(target), endCommand.Args[2]);
        Assert.Equal(default, commands.ExecutedCommands.FirstOrDefault(c => c.Args[0] == "/Delete"));
    }

    [Theory]
    [InlineData(WindowsServiceTarget.Server)]
    [InlineData(WindowsServiceTarget.Runner)]
    public async Task Stop_WithLauncherOnlyBackend_ScopesKillToMatchingLauncher(WindowsServiceTarget target)
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        files.WriteAllText(Launcher(target), "@echo off");
        var installer = CreateInstaller(files, commands);

        var exitCode = await StopAsync(installer, target, CommandOptions());

        Assert.Equal(0, exitCode);
        var tasklist = commands.ExecutedCommands.FirstOrDefault(c => c.FileName == "tasklist");
        Assert.NotEqual(default, tasklist);
        Assert.Contains($"IMAGENAME eq {ProcessName(target)}", tasklist.Args);
    }

    [Theory]
    [InlineData(WindowsServiceTarget.Server)]
    [InlineData(WindowsServiceTarget.Runner)]
    public async Task Restart_CallsStopThenStart(WindowsServiceTarget target)
    {
        var files = new FakeFileSystem();
        var commands = ScheduledTaskCommands();
        files.WriteAllText(Metadata(target), "{\"backend\":\"scheduled-task\"}");
        var installer = CreateInstaller(files, commands);

        var exitCode = await RestartAsync(installer, target, CommandOptions());

        Assert.Equal(0, exitCode);
        var endIndex = commands.ExecutedCommands.FindIndex(c => c.Args[0] == "/End");
        var runIndex = commands.ExecutedCommands.FindIndex(c => c.Args[0] == "/Run");
        Assert.True(endIndex >= 0);
        Assert.True(runIndex > endIndex);
    }

    [Theory]
    [InlineData(WindowsServiceTarget.Server)]
    [InlineData(WindowsServiceTarget.Runner)]
    public async Task Status_WithScheduledTaskBackend_ReportsCorrectState(WindowsServiceTarget target)
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) =>
            {
                if (fileName == "schtasks" && args[0] == "/Query")
                    return (0, "Task exists", "");
                if (fileName == "tasklist")
                    return (0, $"\"Image Name\",\"PID\"\r\n{ProcessName(target)},1234", "");
                return (0, "", "");
            }
        };
        files.WriteAllText(Launcher(target), "@echo off");
        files.WriteAllText(Metadata(target), "{\"backend\":\"scheduled-task\",\"listenUrl\":\"http://127.0.0.1:3456\"}");
        var output = new StringWriter();
        var installer = CreateInstaller(files, commands, output: output, healthProbe: _ => Task.FromResult(true));

        var exitCode = await StatusAsync(installer, target, CommandOptions());

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("scheduled-task: yes", text);
        Assert.Contains("launcher file: present", text);
        Assert.Contains("running: yes", text);
        if (target == WindowsServiceTarget.Server)
            Assert.Contains("health: reachable", text);
    }

    [Theory]
    [InlineData(WindowsServiceTarget.Server)]
    [InlineData(WindowsServiceTarget.Runner)]
    public async Task Logs_TailsLastNLines(WindowsServiceTarget target)
    {
        var files = new FakeFileSystem();
        files.WriteAllText(Log(target), string.Join("\n", Enumerable.Range(1, 100).Select(i => $"Line {i}")) + "\n");
        var output = new StringWriter();
        var installer = CreateInstaller(files, new FakeCommandExecutor(), output: output);

        var exitCode = await LogsAsync(installer, target, CommandOptions(lines: 10));

        Assert.Equal(0, exitCode);
        Assert.Contains("Line 91", output.ToString());
        Assert.Contains("Line 100", output.ToString());
        Assert.DoesNotContain("Line 90", output.ToString());
    }

    [Theory]
    [InlineData(WindowsServiceTarget.Server)]
    [InlineData(WindowsServiceTarget.Runner)]
    public async Task Logs_Follow_StreamsNewLinesUntilCancelled(WindowsServiceTarget target)
    {
        var files = new FakeFileSystem();
        files.WriteAllText(Log(target), "initial\n");
        var output = new StringWriter();
        var watcher = new FakeFileSystemWatcher();
        var cancellation = new CancellationTokenSource();
        var followStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var installer = CreateInstaller(
            files,
            new FakeCommandExecutor(),
            output: output,
            logChangeObserverFactory: _ => watcher);
        installer.TestFollowToken = cancellation.Token;
        installer.TestFollowStarted = () => followStarted.TrySetResult();

        var task = LogsAsync(installer, target, CommandOptions(follow: true));
        await TestWait.ForAsync(
            () => followStarted.Task.IsCompleted,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(20),
            $"{target} log follow watcher to start");

        files.WriteAllText(Log(target), "initial\nnew line 1\nnew line 2\n");
        await watcher.RaiseChangedAsync();
        await TestWait.ForAsync(
            () => output.ToString().Contains("new line 1", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(20),
            $"{target} log follow output to include new line");

        cancellation.Cancel();
        await task;

        Assert.Contains("initial", output.ToString());
        Assert.Contains("new line 1", output.ToString());
    }

    [Theory]
    [InlineData(WindowsServiceTarget.Server)]
    [InlineData(WindowsServiceTarget.Runner)]
    public async Task Uninstall_RemovesArtifactsButPreservesUserData(WindowsServiceTarget target)
    {
        var files = new FakeFileSystem();
        files.WriteAllText(Launcher(target), "@echo off");
        files.WriteAllText(Startup(target), "call x");
        files.WriteAllText(Metadata(target), "{\"backend\":\"scheduled-task\"}");
        var database = Path.Combine(UserProfile, ".mohist", "mohist.db");
        files.WriteAllText(database, "data");
        files.WriteAllText(Log(target), "log content");
        var installer = CreateInstaller(files, new FakeCommandExecutor());

        var exitCode = await UninstallAsync(installer, target, CommandOptions());

        Assert.Equal(0, exitCode);
        Assert.False(files.HasFile(Launcher(target)));
        Assert.False(files.HasFile(Startup(target)));
        Assert.False(files.HasFile(Metadata(target)));
        Assert.True(files.HasFile(database));
        Assert.True(files.HasFile(Log(target)));
    }

    [Theory]
    [InlineData(WindowsServiceTarget.Server)]
    [InlineData(WindowsServiceTarget.Runner)]
    public async Task Start_DryRun_DoesNotExecute(WindowsServiceTarget target)
    {
        var (files, commands, installer) = DryRunFixture(target);

        var exitCode = await StartAsync(installer, target, CommandOptions(dryRun: true));

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(commands.ExecutedCommands, c => c.Args[0] == "/Run");
        Assert.Single(files.Files);
    }

    [Theory]
    [InlineData(WindowsServiceTarget.Server)]
    [InlineData(WindowsServiceTarget.Runner)]
    public async Task Stop_DryRun_DoesNotExecute(WindowsServiceTarget target)
    {
        var (files, commands, installer) = DryRunFixture(target);

        var exitCode = await StopAsync(installer, target, CommandOptions(dryRun: true));

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(commands.ExecutedCommands, c => c.Args[0] == "/End");
        Assert.Single(files.Files);
    }

    [Theory]
    [InlineData(WindowsServiceTarget.Server)]
    [InlineData(WindowsServiceTarget.Runner)]
    public async Task Restart_DryRun_DoesNotExecute(WindowsServiceTarget target)
    {
        var (files, commands, installer) = DryRunFixture(target);

        var exitCode = await RestartAsync(installer, target, CommandOptions(dryRun: true));

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(commands.ExecutedCommands, c => c.Args[0] is "/End" or "/Run");
        Assert.Single(files.Files);
    }

    [Theory]
    [InlineData(WindowsServiceTarget.Server)]
    [InlineData(WindowsServiceTarget.Runner)]
    public async Task Status_DryRun_DoesNotExecute(WindowsServiceTarget target)
    {
        var (files, commands, installer) = DryRunFixture(target);

        var exitCode = await StatusAsync(installer, target, CommandOptions(dryRun: true));

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(commands.ExecutedCommands, c => c.FileName == "tasklist");
        Assert.Single(files.Files);
    }

    [Theory]
    [InlineData(WindowsServiceTarget.Server)]
    [InlineData(WindowsServiceTarget.Runner)]
    public async Task Logs_DryRun_DoesNotRead(WindowsServiceTarget target)
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        files.WriteAllText(Log(target), "log content");
        var installer = CreateInstaller(files, commands);

        var exitCode = await LogsAsync(installer, target, CommandOptions(dryRun: true, follow: true));

        Assert.Equal(0, exitCode);
        Assert.Empty(commands.ExecutedCommands);
        Assert.Single(files.Files);
    }

    [Theory]
    [InlineData(WindowsServiceTarget.Server)]
    [InlineData(WindowsServiceTarget.Runner)]
    public async Task Uninstall_DryRun_DoesNotExecuteOrDelete(WindowsServiceTarget target)
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        files.WriteAllText(Launcher(target), "@echo off");
        files.WriteAllText(Startup(target), "call x");
        files.WriteAllText(Metadata(target), "{\"backend\":\"scheduled-task\"}");
        var snapshot = new Dictionary<string, string>(files.Files);
        var installer = CreateInstaller(files, commands);

        var exitCode = await UninstallAsync(installer, target, CommandOptions(dryRun: true));

        Assert.Equal(0, exitCode);
        Assert.Empty(commands.ExecutedCommands);
        Assert.Equal(snapshot, files.Files);
    }

    private static FakeCommandExecutor ScheduledTaskCommands() => new()
    {
        ResponseFactory = (fileName, args) =>
            fileName == "schtasks" && args[0] == "/Query"
                ? (0, "Task exists", "")
                : (0, "", "")
    };

    private static (FakeFileSystem Files, FakeCommandExecutor Commands, WindowsScheduledTaskInstaller Installer) DryRunFixture(WindowsServiceTarget target)
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        files.WriteAllText(Metadata(target), "{\"backend\":\"scheduled-task\"}");
        return (files, commands, CreateInstaller(files, commands));
    }
}
