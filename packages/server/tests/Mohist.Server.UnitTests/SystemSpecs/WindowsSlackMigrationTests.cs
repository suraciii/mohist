using Mohist.Server.TestSupport;
using Xunit;
using static Mohist.Server.UnitTests.SystemSpecs.WindowsInstallTestSupport;

namespace Mohist.Server.UnitTests.SystemSpecs;

public class WindowsSlackMigrationTests
{
    [Fact]
    public async Task RefreshSlackService_MigratesNodeLauncherAndPreservesConfiguration()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(
            SlackLauncher,
            "@echo off\r\n" +
            "cd /d C:\\old-repo\r\n" +
            "set \"SERVER_URL=http://custom:3456\"\r\n" +
            "set \"MOHIST_OPERATOR_TOKEN=secret\"\r\n" +
            "node packages\\mohist-slack\\dist\\cli.js >> \"%USERPROFILE%\\.mohist\\slack\\out.log\" 2>&1\r\n");
        files.WriteAllText(
            SlackMetadata,
            "{\"backend\":\"startup-fallback\",\"serverUrl\":\"http://custom:3456\"}");
        files.WriteAllText(SlackStartup, $"@call \"{SlackLauncher}\"\r\n");
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) => IsSlackTaskProbe(fileName, args)
                ? (2, "", "")
                : (0, "", ""),
        };
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.RefreshSlackServiceAsync(@"C:\new repo");

        Assert.Equal(0, exitCode);
        var launcher = files.ReadAllText(SlackLauncher);
        Assert.Contains("cd /d \"C:\\new repo\"", launcher);
        Assert.Contains("set \"SERVER_URL=http://custom:3456\"", launcher);
        Assert.Contains("set \"MOHIST_OPERATOR_TOKEN=secret\"", launcher);
        Assert.Contains("packages\\go\\mohist-slack\\bin\\mohist-slack.exe", launcher);
        Assert.DoesNotContain("packages\\mohist-slack\\dist\\cli.js", launcher);
        var metadata = files.ReadAllText(SlackMetadata);
        Assert.Contains("\"backend\":\"startup-fallback\"", metadata);
        Assert.Contains("\"repoRoot\":\"C:\\\\new repo\"", metadata);
        Assert.Contains("\"serverUrl\":\"http://custom:3456\"", metadata);
    }

    [Fact]
    public async Task RefreshSlackService_RepairsMalformedMetadataFromRegisteredTask()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(
            SlackLauncher,
            "@echo off\r\n" +
            "cd /d C:\\old-repo\r\n" +
            "set \"SERVER_URL=http://custom:3456\"\r\n" +
            "node packages\\mohist-slack\\dist\\cli.js\r\n");
        files.WriteAllText(SlackMetadata, "{");
        var commands = new FakeCommandExecutor();
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.RefreshSlackServiceAsync(@"C:\new-repo");

        Assert.Equal(0, exitCode);
        var metadata = files.ReadAllText(SlackMetadata);
        Assert.Contains("\"backend\":\"scheduled-task\"", metadata);
        Assert.Contains("\"repoRoot\":\"C:\\\\new-repo\"", metadata);
        Assert.Contains("\"serverUrl\":\"http://custom:3456\"", metadata);
        Assert.Contains(commands.ExecutedCommands, command => IsSlackTaskProbe(command.FileName, command.Args));
    }

    [Fact]
    public async Task RefreshSlackService_WhenTaskProbeFails_DoesNotGuessBackend()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(
            SlackLauncher,
            "@echo off\r\n" +
            "cd /d C:\\old-repo\r\n" +
            "set \"SERVER_URL=http://custom:3456\"\r\n" +
            "node packages\\mohist-slack\\dist\\cli.js\r\n");
        files.WriteAllText(SlackMetadata, "{");
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) => IsSlackTaskProbe(fileName, args)
                ? (1, "", "task query unavailable")
                : (0, "", ""),
        };
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.RefreshSlackServiceAsync(@"C:\new-repo");

        Assert.Equal(1, exitCode);
        Assert.Equal("{", files.ReadAllText(SlackMetadata));
        Assert.Contains("packages\\mohist-slack\\dist\\cli.js", files.ReadAllText(SlackLauncher));
    }

    [Fact]
    public async Task StartSlack_WhenTaskProbeIsDenied_DoesNotStartFallbackProcess()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(SlackLauncher, "@echo off\r\n");
        files.WriteAllText(SlackStartup, $"@call \"{SlackLauncher}\"\r\n");
        files.WriteAllText(SlackMetadata, "{\"backend\":\"startup-fallback\"}");
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) => IsSlackTaskProbe(fileName, args)
                ? (1, "", "ERROR: Access is denied.")
                : (0, "", ""),
        };
        var processStarts = 0;
        var installer = CreateInstaller(
            files,
            commands,
            processLauncher: _ =>
            {
                processStarts++;
                return null;
            });

        var exitCode = await installer.StartSlackAsync(CommandOptions());

        Assert.Equal(1, exitCode);
        Assert.Equal(0, processStarts);
    }

    [Fact]
    public async Task StartSlack_WhenFallbackProcessIsAlreadyRunning_DoesNotStartDuplicate()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(SlackLauncher, "@echo off\r\ncd /d /repo\r\npackages\\go\\mohist-slack\\bin\\mohist-slack.exe\r\n");
        files.WriteAllText(SlackStartup, $"@call \"{SlackLauncher}\"\r\n");
        files.WriteAllText(SlackMetadata, "{\"backend\":\"startup-fallback\",\"repoRoot\":\"/repo\"}");
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) => IsSlackTaskProbe(fileName, args)
                ? (2, "", "")
                : IsSlackProcessProbe(fileName, args)
                    ? (0, "4321\r\n", "")
                    : (0, "", ""),
        };
        var processStarts = 0;
        var installer = CreateInstaller(
            files,
            commands,
            processLauncher: _ =>
            {
                processStarts++;
                return null;
            });

        var exitCode = await installer.StartSlackAsync(CommandOptions());

        Assert.Equal(0, exitCode);
        Assert.Equal(0, processStarts);
    }

    [Fact]
    public async Task StartSlack_WhenScheduledTaskProcessIsAlreadyRunning_DoesNotRunTaskAgain()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(SlackLauncher, "@echo off\r\ncd /d /repo\r\npackages\\go\\mohist-slack\\bin\\mohist-slack.exe\r\n");
        files.WriteAllText(SlackMetadata, "{\"backend\":\"scheduled-task\",\"repoRoot\":\"/repo\"}");
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) => IsSlackProcessProbe(fileName, args)
                ? (0, "4321\r\n", "")
                : (0, "", ""),
        };
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.StartSlackAsync(CommandOptions());

        Assert.Equal(0, exitCode);
        Assert.Contains(commands.ExecutedCommands, command => IsSlackProcessProbe(command.FileName, command.Args));
        Assert.DoesNotContain(commands.ExecutedCommands, command => command.FileName == "schtasks" && command.Args[0] == "/Run");
    }

    [Fact]
    public async Task RefreshSlackService_WithScheduledTask_RemovesStaleStartupTrigger()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(
            SlackLauncher,
            "@echo off\r\ncd /d C:\\old-repo\r\npackages\\go\\mohist-slack\\bin\\mohist-slack.exe\r\n");
        files.WriteAllText(SlackMetadata, "{\"backend\":\"scheduled-task\",\"repoRoot\":\"C:\\\\old-repo\"}");
        files.WriteAllText(SlackStartup, $"@call \"{SlackLauncher}\"\r\n");
        var commands = new FakeCommandExecutor();
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.RefreshSlackServiceAsync(@"C:\new-repo");

        Assert.Equal(0, exitCode);
        Assert.False(files.HasFile(SlackStartup));
        Assert.Contains("scheduled-task", files.ReadAllText(SlackMetadata));
    }

    [Fact]
    public async Task StopSlack_WhenTaskProbeIsDenied_ReturnsUnconfirmedFailure()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(SlackLauncher, "@echo off\r\ncd /d /repo\r\npackages\\go\\mohist-slack\\bin\\mohist-slack.exe\r\n");
        files.WriteAllText(SlackMetadata, "{\"backend\":\"startup-fallback\",\"repoRoot\":\"/repo\"}");
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) => IsSlackTaskProbe(fileName, args)
                ? (1, "", "ERROR: Access is denied.")
                : (0, "", ""),
        };
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.StopSlackAsync(CommandOptions());

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain(commands.ExecutedCommands, command => IsSlackProcessProbe(command.FileName, command.Args));
    }

    [Fact]
    public async Task StopSlack_WithNodeLauncher_KillsOnlyInstalledLauncherProcessTree()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(
            SlackLauncher,
            "@echo off\r\ncd /d C:\\repo\r\nnode packages\\mohist-slack\\dist\\cli.js\r\n");
        files.WriteAllText(SlackMetadata, "{\"backend\":\"launcher-only\"}");
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) => IsSlackTaskProbe(fileName, args)
                ? (2, "", "")
                : IsSlackProcessProbe(fileName, args)
                    ? (0, "4321\r\n", "")
                    : (0, "", "")
        };
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.StopSlackAsync(CommandOptions());

        Assert.Equal(0, exitCode);
        var query = Assert.Single(commands.ExecutedCommands, command => IsSlackProcessProbe(command.FileName, command.Args));
        Assert.Contains("Name = 'cmd.exe'", query.Args[^1]);
        Assert.Contains(SlackLauncher, query.Args[^1]);
        Assert.Contains(SlackStartup, query.Args[^1]);
        var kill = Assert.Single(commands.ExecutedCommands, command => command.FileName == "taskkill");
        Assert.Equal(["/F", "/T", "/PID", "4321"], kill.Args);
    }

    [Fact]
    public async Task StopSlack_WithScheduledNodeLauncher_CapturesAndKillsTreeBeforeEndingTask()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(
            SlackLauncher,
            "@echo off\r\ncd /d C:\\repo\r\nnode packages\\mohist-slack\\dist\\cli.js\r\n");
        files.WriteAllText(SlackMetadata, "{\"backend\":\"scheduled-task\"}");
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) => IsSlackTaskProbe(fileName, args)
                ? (0, "", "")
                : IsSlackProcessProbe(fileName, args)
                    ? (0, "7654\r\n", "")
                    : (0, "", "")
        };
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.StopSlackAsync(CommandOptions());

        Assert.Equal(0, exitCode);
        var query = Assert.Single(commands.ExecutedCommands, command => IsSlackProcessProbe(command.FileName, command.Args));
        var kill = Assert.Single(commands.ExecutedCommands, command => command.FileName == "taskkill");
        Assert.Equal(["/F", "/T", "/PID", "7654"], kill.Args);
        Assert.True(commands.ExecutedCommands.IndexOf(query) < commands.ExecutedCommands.IndexOf(kill));
        Assert.DoesNotContain(commands.ExecutedCommands, command => command.FileName == "schtasks" && command.Args[0] == "/End");
    }

    [Fact]
    public async Task StatusSlack_WithNodeLauncher_ReportsInstalledLauncherTree()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(
            SlackLauncher,
            "@echo off\r\ncd /d C:\\repo\r\nnode packages\\mohist-slack\\dist\\cli.js\r\n");
        files.WriteAllText(SlackMetadata, "{\"backend\":\"launcher-only\"}");
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) => IsSlackTaskProbe(fileName, args)
                ? (2, "", "")
                : IsSlackProcessProbe(fileName, args)
                    ? (0, "2468\r\n", "")
                    : (0, "", "")
        };
        var output = new StringWriter();
        var installer = CreateInstaller(files, commands, output: output);

        var exitCode = await installer.StatusSlackAsync(CommandOptions());

        Assert.Equal(0, exitCode);
        Assert.Contains("running: yes", output.ToString());
        var query = Assert.Single(commands.ExecutedCommands, command => IsSlackProcessProbe(command.FileName, command.Args));
        Assert.Contains("Name = 'cmd.exe'", query.Args[^1]);
    }

    [Fact]
    public async Task StopSlack_WithScheduledGoLauncher_KillsSurvivingExactExecutable()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(
            SlackLauncher,
            "@echo off\r\ncd /d /repo\r\n\"packages\\go\\mohist-slack\\bin\\mohist-slack.exe\"\r\n");
        files.WriteAllText(SlackMetadata, "{\"backend\":\"scheduled-task\",\"repoRoot\":\"/repo\"}");
        var commands = new FakeCommandExecutor
        {
            ResponseFactory = (fileName, args) => IsSlackTaskProbe(fileName, args)
                ? (0, "", "")
                : IsSlackProcessProbe(fileName, args)
                    ? (0, "9876\r\n", "")
                    : (0, "", "")
        };
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.StopSlackAsync(CommandOptions());

        Assert.Equal(0, exitCode);
        Assert.Contains(commands.ExecutedCommands, command => command.FileName == "schtasks" && command.Args[0] == "/End");
        var query = Assert.Single(commands.ExecutedCommands, command => IsSlackProcessProbe(command.FileName, command.Args));
        Assert.Contains("ExecutablePath -ieq $path", query.Args[^1]);
        Assert.Contains(SlackExecutable, query.Args[^1]);
        var kill = Assert.Single(commands.ExecutedCommands, command => command.FileName == "taskkill");
        Assert.Equal(["/F", "/PID", "9876"], kill.Args);
    }
}
