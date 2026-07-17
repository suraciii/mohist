using System.Diagnostics;
using Mohist.Cli;
using Mohist.Server.SpecTests.Support;
using Xunit;
using static Mohist.Server.SpecTests.Specs.SystemSpecs.WindowsInstallTestSupport;

namespace Mohist.Server.SpecTests.Specs.SystemSpecs;

public class WindowsServiceLifecycleSpecs
{
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
