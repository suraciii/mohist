using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Xunit;
using Mohist.Cli;
using Mohist.Server.SpecTests.Support;

namespace Mohist.Server.SpecTests.Specs.SystemSpecs;

public class WindowsInstallSpecs
{
    private const string UserProfile = "/mohist-tests/user";
    private static string ServiceDir => Path.Combine(UserProfile, ".mohist", "service");
    private static string ServerLauncher => Path.Combine(ServiceDir, "mohist-server.cmd");
    private static string RunnerLauncher => Path.Combine(ServiceDir, "mohist-runner.cmd");
    private static string ServerStartup => Path.Combine(UserProfile, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", "Startup", "Mohist_Server.cmd");
    private static string RunnerStartup => Path.Combine(UserProfile, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", "Startup", "Mohist_Runner.cmd");
    private static string ServerMetadata => Path.Combine(ServiceDir, "mohist-server.install.json");
    private static string RunnerMetadata => Path.Combine(ServiceDir, "mohist-runner.install.json");
    private static string ServerLog => Path.Combine(UserProfile, ".mohist", "server", "out.log");
    private static string RunnerLog => Path.Combine(UserProfile, ".mohist", "runner", "out.log");

    private static WindowsScheduledTaskInstaller CreateInstaller(
        FakeFileSystem files,
        FakeCommandExecutor commands,
        StringWriter? output = null,
        StringWriter? error = null,
        Func<ProcessStartInfo, Process?>? processLauncher = null,
        Func<string, ILogChangeObserver>? logChangeObserverFactory = null,
        Func<string, Task<bool>>? healthProbe = null)
    {
        return new WindowsScheduledTaskInstaller(
            output ?? new StringWriter(),
            error ?? new StringWriter(),
            files,
            commands,
            processLauncher,
            logChangeObserverFactory,
            healthProbe,
            UserProfile);
    }

    private static ServiceInstallOptions InstallOptions(bool dryRun = false, string? repoRoot = "/repo", string? listenUrl = null, string? serverUrl = null, string? runnerRoot = null)
    {
        return new ServiceInstallOptions(
            DryRun: dryRun,
            UnitDir: "/units",
            RepoRoot: repoRoot,
            ListenUrl: listenUrl,
            ServerUrl: serverUrl,
            RunnerRoot: runnerRoot);
    }

    private static ServiceCommandOptions CommandOptions(bool dryRun = false, int lines = 50, bool follow = false)
    {
        return new ServiceCommandOptions(
            DryRun: dryRun,
            UnitDir: "/units",
            Lines: lines,
            Follow: follow);
    }

    [Fact]
    public void BuildCreateTaskArgs_ContainsDiscreteElements()
    {
        var args = WindowsScheduledTaskInstaller.BuildCreateTaskArgs(
            new WindowsScheduledTaskInstaller.TaskCreateSpec("Mohist_Server", @"C:\path\launcher.cmd"));

        Assert.Equal("/Create", args[0]);
        Assert.Equal("/SC", args[1]);
        Assert.Equal("ONLOGON", args[2]);
        Assert.Equal("/RL", args[3]);
        Assert.Equal("LIMITED", args[4]);
        Assert.Equal("/TN", args[5]);
        Assert.Equal("Mohist_Server", args[6]);
        Assert.Equal("/TR", args[7]);
        Assert.Equal(@"C:\path\launcher.cmd", args[8]);
        Assert.Equal("/F", args[9]);
    }

    [Fact]
    public void BuildRunArgs_ContainsDiscreteVerbAndTaskName()
    {
        var args = WindowsScheduledTaskInstaller.BuildRunArgs("Mohist_Server");

        Assert.Equal("/Run", args[0]);
        Assert.Equal("/TN", args[1]);
        Assert.Equal("Mohist_Server", args[2]);
    }

    [Fact]
    public void BuildEndArgs_ContainsDiscreteVerbAndTaskName()
    {
        var args = WindowsScheduledTaskInstaller.BuildEndArgs("Mohist_Runner");

        Assert.Equal("/End", args[0]);
        Assert.Equal("/TN", args[1]);
        Assert.Equal("Mohist_Runner", args[2]);
    }

    [Fact]
    public void BuildDeleteArgs_ContainsDiscreteVerbAndTaskNameAndForceFlag()
    {
        var args = WindowsScheduledTaskInstaller.BuildDeleteArgs("Mohist_Server");

        Assert.Equal("/Delete", args[0]);
        Assert.Equal("/TN", args[1]);
        Assert.Equal("Mohist_Server", args[2]);
        Assert.Equal("/F", args[3]);
    }

    [Fact]
    public void BuildQueryArgs_ContainsDiscreteVerbAndTaskName()
    {
        var args = WindowsScheduledTaskInstaller.BuildQueryArgs("Mohist_Runner");

        Assert.Equal("/Query", args[0]);
        Assert.Equal("/TN", args[1]);
        Assert.Equal("Mohist_Runner", args[2]);
    }

    [Fact]
    public void RenderServerLauncher_WithSpacePath_ContainsQuotedCd()
    {
        var installer = CreateInstaller(new FakeFileSystem(), new FakeCommandExecutor());
        var pathWithSpaces = @"C:\Users\Mohist User\repos\space repo";
        var body = installer.RenderServerLauncher(
            new WindowsScheduledTaskInstaller.ServerLauncherSpec(pathWithSpaces, "http://127.0.0.1:3456"));

        Assert.Contains("cd /d", body);
        Assert.Contains('"', body);
        Assert.Contains(pathWithSpaces, body);
    }

    [Fact]
    public void RenderRunnerLauncher_ContainsExpectedElements()
    {
        var installer = CreateInstaller(new FakeFileSystem(), new FakeCommandExecutor());
        var body = installer.RenderRunnerLauncher(
            new WindowsScheduledTaskInstaller.RunnerLauncherSpec(@"C:\repo", "http://127.0.0.1:3456", @"C:\runner"));

        Assert.Contains("cd /d", body);
        Assert.Contains("set \"SERVER_URL=http://127.0.0.1:3456\"", body);
        Assert.Contains("set \"RUNNER_ROOT=C:\\runner\"", body);
        Assert.Contains("node packages\\runner\\dist\\cli.js", body);
        Assert.Contains(@"%USERPROFILE%\.mohist\runner\out.log", body);
    }

    [Fact]
    public void RenderRunnerLauncher_WithNonDefaultServerUrl_PassesItThrough()
    {
        var installer = CreateInstaller(new FakeFileSystem(), new FakeCommandExecutor());
        var body = installer.RenderRunnerLauncher(
            new WindowsScheduledTaskInstaller.RunnerLauncherSpec(@"C:\repo", "http://example.com:9999", null));

        Assert.Contains("set \"SERVER_URL=http://example.com:9999\"", body);
        Assert.DoesNotContain("http://127.0.0.1:3456", body);
    }

    [Fact]
    public void RenderServerLauncher_WithNonDefaultListenUrl_PassesItThrough()
    {
        var installer = CreateInstaller(new FakeFileSystem(), new FakeCommandExecutor());
        var body = installer.RenderServerLauncher(
            new WindowsScheduledTaskInstaller.ServerLauncherSpec(@"C:\repo", "http://example.com:9999"));

        Assert.Contains("ASPNETCORE_URLS=http://example.com:9999", body);
        Assert.DoesNotContain("http://127.0.0.1:3456", body);
    }

    [Fact]
    public void RenderServerLauncher_WithoutListenUrl_OmitsAspnetcoreUrls()
    {
        var installer = CreateInstaller(new FakeFileSystem(), new FakeCommandExecutor());
        var body = installer.RenderServerLauncher(
            new WindowsScheduledTaskInstaller.ServerLauncherSpec(@"C:\repo", null));

        Assert.DoesNotContain("ASPNETCORE_URLS", body);
        Assert.Contains("dotnet run --project", body);
    }

    [Fact]
    public async Task InstallServer_WithoutListenUrl_OmitsAspnetcoreUrlsInLauncher()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.InstallServerAsync(InstallOptions(repoRoot: @"C:\repo"));

        Assert.Equal(0, exitCode);
        var body = files.ReadAllText(ServerLauncher);
        Assert.DoesNotContain("ASPNETCORE_URLS", body);
        Assert.Contains("dotnet run --project", body);
    }

    [Fact]
    public void QuoteForCmdBody_And_QuoteForSchtasksTr_ProduceDifferentOutputs_ForSamePath()
    {
        // The two helpers target different runtimes (cmd body vs. schtasks /TR
        // payload) and therefore apply different escape rules. A path that
        // contains a cmd metacharacter such as `&` exercises the difference:
        // QuoteForCmdBody caret-escapes the `&` for the .cmd body, while
        // QuoteForSchtasksTr leaves the `&` literal (cmd's quoting rules for
        // the /TR field are different from .cmd body rules).
        var path = @"C:\repo\bin&tools\launcher.cmd";
        var cmdBody = WindowsScheduledTaskInstaller.QuoteForCmdBody(path);
        var schtasksTr = WindowsScheduledTaskInstaller.QuoteForSchtasksTr(path);

        Assert.NotEqual(cmdBody, schtasksTr);
    }

    [Fact]
    public void QuoteForSchtasksTr_WithSpacePath_WrapsInDoubleQuotes()
    {
        var path = @"C:\Users\Mohist User\repos\space repo\launcher.cmd";
        var tr = WindowsScheduledTaskInstaller.QuoteForSchtasksTr(path);

        Assert.StartsWith("\"", tr);
        Assert.EndsWith("\"", tr);
        Assert.Contains("Mohist User", tr);
    }

    [Fact]
    public void QuoteForSchtasksTr_WithoutSpace_DoesNotWrapInDoubleQuotes()
    {
        var path = @"C:\repo\launcher.cmd";
        var tr = WindowsScheduledTaskInstaller.QuoteForSchtasksTr(path);

        Assert.Equal(path, tr);
    }

    [Fact]
    public void BuildCreateTaskArgs_WithSpaceLauncherPath_WrapsTrPayloadInDoubleQuotes()
    {
        var path = @"C:\Users\Mohist User\repos\space repo\launcher.cmd";
        var tr = WindowsScheduledTaskInstaller.QuoteForSchtasksTr(path);
        var args = WindowsScheduledTaskInstaller.BuildCreateTaskArgs(
            new WindowsScheduledTaskInstaller.TaskCreateSpec("Mohist_Server", tr));

        var trIndex = Array.IndexOf(args, "/TR");
        Assert.True(trIndex >= 0);
        var trPayload = args[trIndex + 1];
        Assert.StartsWith("\"", trPayload);
        Assert.EndsWith("\"", trPayload);
        Assert.Contains("Mohist User", trPayload);
    }

    [Theory]
    [InlineData("value with \r")]
    [InlineData("value with \n")]
    [InlineData("value with \0")]
    [InlineData("value with \" quote")]
    public void SanitizeForCmdAssignment_RejectsInjectionPayloads(string value)
    {
        Assert.Throws<ArgumentException>(() => WindowsScheduledTaskInstaller.SanitizeForCmdAssignment(value));
    }

    [Fact]
    public void SanitizeForCmdAssignment_AllowsSafeValues()
    {
        Assert.Equal("http://127.0.0.1:3456", WindowsScheduledTaskInstaller.SanitizeForCmdAssignment("http://127.0.0.1:3456"));
        Assert.Equal(@"C:\repo\runner", WindowsScheduledTaskInstaller.SanitizeForCmdAssignment(@"C:\repo\runner"));
    }

    [Fact]
    public async Task InstallServer_WithInjectionInListenUrl_AbortsBeforeWrite()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var installer = CreateInstaller(files, commands);

        var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await installer.InstallServerAsync(InstallOptions(
                repoRoot: @"C:\repo",
                listenUrl: "http://x\" & calc & \"")));

        Assert.Contains("unsafe", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(commands.ExecutedCommands);
        Assert.False(files.HasFile(ServerLauncher));
    }

    [Fact]
    public async Task InstallRunner_WithInjectionInRunnerRoot_AbortsBeforeWrite()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var installer = CreateInstaller(files, commands);

        var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await installer.InstallRunnerAsync(InstallOptions(
                repoRoot: @"C:\repo",
                serverUrl: "http://127.0.0.1:3456",
                runnerRoot: "C:\\runner\" & calc & \"")));

        Assert.Contains("unsafe", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(commands.ExecutedCommands);
        Assert.False(files.HasFile(RunnerLauncher));
    }

    [Fact]
    public async Task InstallServer_WithFailingSchtasks_CreatesStartupFallbackAndRecordsMetadata()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        commands.ResponseFactory = (fileName, args) =>
        {
            if (fileName == "schtasks" && args[0] == "/Create")
                return (1, "", "Access denied");
            return (0, "", "");
        };
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.InstallServerAsync(InstallOptions(repoRoot: @"C:\repo"));

        Assert.Equal(0, exitCode);
        Assert.True(files.HasFile(ServerStartup), "Startup fallback should be written");
        Assert.True(files.HasFile(ServerMetadata), "Metadata should be written");

        var metadata = files.ReadAllText(ServerMetadata);
        Assert.Contains("startup-fallback", metadata);

        var startupBody = files.ReadAllText(ServerStartup);
        Assert.Contains("call", startupBody);
        Assert.Contains("mohist-server.cmd", startupBody);
    }

    [Fact]
    public async Task InstallRunner_WithFailingSchtasks_CreatesStartupFallbackAndRecordsMetadata()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        commands.ResponseFactory = (fileName, args) =>
        {
            if (fileName == "schtasks" && args[0] == "/Create")
                return (1, "", "Access denied");
            return (0, "", "");
        };
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.InstallRunnerAsync(InstallOptions(repoRoot: @"C:\repo", serverUrl: "http://127.0.0.1:3456", runnerRoot: @"C:\runner"));

        Assert.Equal(0, exitCode);
        Assert.True(files.HasFile(RunnerStartup), "Startup fallback should be written");
        Assert.True(files.HasFile(RunnerMetadata), "Metadata should be written");

        var metadata = files.ReadAllText(RunnerMetadata);
        Assert.Contains("startup-fallback", metadata);
    }

    [Fact]
    public async Task InstallServer_WithSucceedingSchtasks_WritesLauncherAndMetadata()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var output = new StringWriter();
        var installer = CreateInstaller(files, commands, output: output);

        var exitCode = await installer.InstallServerAsync(InstallOptions(
            repoRoot: @"C:\repo",
            listenUrl: "http://127.0.0.1:3456"));

        Assert.Equal(0, exitCode);
        Assert.True(files.HasFile(ServerLauncher), "Server launcher should be written");
        var body = files.ReadAllText(ServerLauncher);
        Assert.Contains("dotnet run --project", body);
        Assert.Contains("ASPNETCORE_URLS=http://127.0.0.1:3456", body);
        Assert.Contains(@"%USERPROFILE%\.mohist\server\out.log", body);

        Assert.True(files.HasFile(ServerMetadata), "Metadata should be written");
        var metadata = files.ReadAllText(ServerMetadata);
        Assert.Contains("scheduled-task", metadata);

        Assert.Contains("Registered Scheduled Task Mohist_Server", output.ToString());

        var createCommand = commands.ExecutedCommands.FirstOrDefault(c => c.Args[0] == "/Create");
        Assert.NotEqual(default, createCommand);
        Assert.Contains("Mohist_Server", createCommand.Args);
    }

    [Fact]
    public async Task InstallRunner_WithSucceedingSchtasks_WritesLauncherAndMetadata()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var output = new StringWriter();
        var installer = CreateInstaller(files, commands, output: output);

        var exitCode = await installer.InstallRunnerAsync(InstallOptions(
            repoRoot: @"C:\repo",
            serverUrl: "http://example.com:9999",
            runnerRoot: @"C:\custom-runner"));

        Assert.Equal(0, exitCode);
        Assert.True(files.HasFile(RunnerLauncher), "Runner launcher should be written");
        var body = files.ReadAllText(RunnerLauncher);
        Assert.Contains("set \"SERVER_URL=http://example.com:9999\"", body);
        Assert.Contains("set \"RUNNER_ROOT=C:\\custom-runner\"", body);
        Assert.Contains("node packages\\runner\\dist\\cli.js", body);

        Assert.True(files.HasFile(RunnerMetadata), "Metadata should be written");
        var metadata = files.ReadAllText(RunnerMetadata);
        Assert.Contains("scheduled-task", metadata);
        Assert.Contains("http://example.com:9999", metadata);

        Assert.Contains("Registered Scheduled Task Mohist_Runner", output.ToString());

        var createCommand = commands.ExecutedCommands.FirstOrDefault(c => c.Args[0] == "/Create");
        Assert.NotEqual(default, createCommand);
        Assert.Contains("Mohist_Runner", createCommand.Args);
    }

    [Fact]
    public async Task InstallServer_ReinstallFromStartupFallbackToScheduledTask_RemovesStaleStartupFile()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        // Previous install was blocked from creating a Scheduled Task and used the
        // Startup-fallback. The metadata records the old backend and the
        // Startup-folder shortcut is still on disk.
        files.WriteAllText(ServerMetadata, "{\"backend\":\"startup-fallback\",\"listenUrl\":\"http://127.0.0.1:3456\"}");
        files.WriteAllText(ServerStartup, "call \"x\"");
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.InstallServerAsync(InstallOptions(
            repoRoot: @"C:\repo",
            listenUrl: "http://127.0.0.1:3456"));

        Assert.Equal(0, exitCode);
        Assert.False(files.HasFile(ServerStartup), "Stale Startup-folder shortcut should be removed when switching to scheduled-task backend");
        Assert.True(files.HasFile(ServerMetadata), "Metadata should be rewritten with the new backend");
        Assert.Contains("scheduled-task", files.ReadAllText(ServerMetadata));
    }

    [Fact]
    public async Task InstallRunner_ReinstallFromStartupFallbackToScheduledTask_RemovesStaleStartupFile()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        files.WriteAllText(RunnerMetadata, "{\"backend\":\"startup-fallback\",\"serverUrl\":\"http://127.0.0.1:3456\"}");
        files.WriteAllText(RunnerStartup, "call \"x\"");
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.InstallRunnerAsync(InstallOptions(
            repoRoot: @"C:\repo",
            serverUrl: "http://127.0.0.1:3456",
            runnerRoot: @"C:\runner"));

        Assert.Equal(0, exitCode);
        Assert.False(files.HasFile(RunnerStartup), "Stale Startup-folder shortcut should be removed");
        Assert.Contains("scheduled-task", files.ReadAllText(RunnerMetadata));
    }

    [Fact]
    public async Task StartServer_WithScheduledTask_Backend_RunsSchtasksRun()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        commands.ResponseFactory = (fileName, args) =>
        {
            if (fileName == "schtasks" && args[0] == "/Query")
                return (0, "Task exists", "");
            return (0, "", "");
        };
        files.WriteAllText(ServerMetadata, "{\"backend\":\"scheduled-task\"}");
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.StartServerAsync(CommandOptions());

        Assert.Equal(0, exitCode);
        var runCommand = commands.ExecutedCommands.FirstOrDefault(c => c.Args[0] == "/Run");
        Assert.NotEqual(default, runCommand);
        Assert.Equal("Mohist_Server", runCommand.Args[2]);
    }

    [Fact]
    public async Task StartServer_WithStartupFallback_Backend_StartsDetachedProcess()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var launched = new List<ProcessStartInfo>();
        Process? fakeLauncher(ProcessStartInfo psi) { launched.Add(psi); return null; }
        files.WriteAllText(ServerStartup, "call \"x\"");
        files.WriteAllText(ServerLauncher, "@echo off");
        var installer = CreateInstaller(files, commands, processLauncher: fakeLauncher);

        var exitCode = await installer.StartServerAsync(CommandOptions());

        Assert.Equal(0, exitCode);
        Assert.Single(launched);
        Assert.Equal(ServerLauncher, launched[0].FileName);
        Assert.True(launched[0].UseShellExecute);
        Assert.True(launched[0].CreateNoWindow);
    }

    [Fact]
    public async Task StartServer_WithLauncherOnly_Backend_StartsDetachedProcess()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var launched = new List<ProcessStartInfo>();
        Process? fakeLauncher(ProcessStartInfo psi) { launched.Add(psi); return null; }
        files.WriteAllText(ServerLauncher, "@echo off");
        var installer = CreateInstaller(files, commands, processLauncher: fakeLauncher);

        var exitCode = await installer.StartServerAsync(CommandOptions());

        Assert.Equal(0, exitCode);
        Assert.Single(launched);
        Assert.Equal(ServerLauncher, launched[0].FileName);
    }

    [Fact]
    public async Task StartServer_WithLauncherOnly_Backend_DetachesFromParentProcessGroup()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var launched = new List<ProcessStartInfo>();
        Process? fakeLauncher(ProcessStartInfo psi) { launched.Add(psi); return null; }
        files.WriteAllText(ServerLauncher, "@echo off");
        var installer = CreateInstaller(files, commands, processLauncher: fakeLauncher);

        await installer.StartServerAsync(CommandOptions());

        // Pinned: the detached process must opt out of the parent's job object on
        // Windows so it can outlive the terminal that started it. UseShellExecute
        // + CreateNoWindow + CreateNewProcessGroup is the Hermes pattern.
        var psi = Assert.Single(launched);
        Assert.True(psi.UseShellExecute);
        Assert.True(psi.CreateNoWindow);
        if (OperatingSystem.IsWindows())
            Assert.True(psi.CreateNewProcessGroup);
        Assert.False(psi.ErrorDialog);
    }

    [Fact]
    public async Task StopServer_WithScheduledTask_Backend_RunsSchtasksEnd()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        commands.ResponseFactory = (fileName, args) =>
        {
            if (fileName == "schtasks" && args[0] == "/Query")
                return (0, "Task exists", "");
            return (0, "", "");
        };
        files.WriteAllText(ServerMetadata, "{\"backend\":\"scheduled-task\"}");
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.StopServerAsync(CommandOptions());

        Assert.Equal(0, exitCode);
        var endCommand = commands.ExecutedCommands.FirstOrDefault(c => c.Args[0] == "/End");
        Assert.NotEqual(default, endCommand);
        Assert.Equal("Mohist_Server", endCommand.Args[2]);

        var deleteCommand = commands.ExecutedCommands.FirstOrDefault(c => c.Args[0] == "/Delete");
        Assert.Equal(default, deleteCommand);
    }

    [Fact]
    public async Task StopServer_WithLauncherOnly_Backend_ScopesKillToMatchingLauncher()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        files.WriteAllText(ServerLauncher, "@echo off");
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.StopServerAsync(CommandOptions());

        Assert.Equal(0, exitCode);
        // The scoped stop uses tasklist first to find PIDs whose command line
        // references the launcher, then taskkill /F /PID for each. It
        // deliberately does NOT use `taskkill /F /IM dotnet.exe` because that
        // would kill unrelated dotnet processes on the user's box.
        var tasklist = commands.ExecutedCommands.FirstOrDefault(c => c.FileName == "tasklist");
        Assert.NotEqual(default, tasklist);
        Assert.Contains("IMAGENAME eq dotnet.exe", tasklist.Args);
    }

    [Fact]
    public async Task StopServer_WithLauncherOnly_Backend_TaskkillsPidsFoundInTaskList()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        // The launcher marker is matched in the row text; the test embeds
        // the marker in the window-title column to make the test realistic.
        var launcherName = Path.GetFileName(ServerLauncher);
        commands.ResponseFactory = (fileName, args) =>
        {
            if (fileName == "tasklist")
                return (0, $"\"dotnet.exe\",\"1234\",\"Console\",\"1\",\"{launcherName}\"\r\n\"dotnet.exe\",\"5678\",\"Console\",\"1\",\"{launcherName}\"", "");
            return (0, "", "");
        };
        files.WriteAllText(ServerLauncher, "@echo off");
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.StopServerAsync(CommandOptions());

        Assert.Equal(0, exitCode);
        var pidKills = commands.ExecutedCommands.Where(c => c.FileName == "taskkill").ToList();
        Assert.Equal(2, pidKills.Count);
        Assert.All(pidKills, k => Assert.Contains("/F", k.Args));
        Assert.All(pidKills, k => Assert.Contains("/PID", k.Args));
        Assert.Contains(pidKills, k => k.Args.Contains("1234"));
        Assert.Contains(pidKills, k => k.Args.Contains("5678"));
    }

    [Fact]
    public async Task RestartServer_CallsStopThenStart()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        commands.ResponseFactory = (fileName, args) =>
        {
            if (fileName == "schtasks" && args[0] == "/Query")
                return (0, "Task exists", "");
            return (0, "", "");
        };
        files.WriteAllText(ServerMetadata, "{\"backend\":\"scheduled-task\"}");
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.RestartServerAsync(CommandOptions());

        Assert.Equal(0, exitCode);
        var endIndex = commands.ExecutedCommands.FindIndex(c => c.Args[0] == "/End");
        var runIndex = commands.ExecutedCommands.FindIndex(c => c.Args[0] == "/Run");
        Assert.True(endIndex >= 0, "Stop (End) should be called");
        Assert.True(runIndex >= 0, "Start (Run) should be called");
        Assert.True(endIndex < runIndex, "Stop should come before Start");
    }

    [Fact]
    public async Task StatusServer_WithScheduledTask_Backend_ReportsCorrectState()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        commands.ResponseFactory = (fileName, args) =>
        {
            if (fileName == "schtasks" && args[0] == "/Query")
                return (0, "Task exists", "");
            if (fileName == "tasklist")
                return (0, "\"Image Name\",\"PID\"\r\ndotnet.exe,1234", "");
            return (0, "", "");
        };
        files.WriteAllText(ServerLauncher, "@echo off");
        files.WriteAllText(ServerMetadata, "{\"backend\":\"scheduled-task\",\"listenUrl\":\"http://127.0.0.1:3456\"}");
        var output = new StringWriter();
        var installer = CreateInstaller(files, commands, output: output,
            healthProbe: _ => Task.FromResult(true));

        var exitCode = await installer.StatusServerAsync(CommandOptions());

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("scheduled-task: yes", text);
        Assert.Contains("launcher file: present", text);
        Assert.Contains("running: yes", text);
        Assert.Contains("health: reachable", text);
    }

    [Fact]
    public async Task LogsServer_TailsLastNLines()
    {
        var files = new FakeFileSystem();
        var lines = Enumerable.Range(1, 100).Select(i => $"Line {i}");
        files.WriteAllText(ServerLog, string.Join("\n", lines) + "\n");
        var output = new StringWriter();
        var installer = CreateInstaller(files, new FakeCommandExecutor(), output: output);

        var exitCode = await installer.LogsServerAsync(CommandOptions(lines: 10));

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("Line 91", text);
        Assert.Contains("Line 100", text);
        Assert.DoesNotContain("Line 90", text);
    }

    [Fact]
    public async Task LogsServer_WithLargeFile_BoundedTailStillReturnsLastNLines()
    {
        // Build a fake log file whose total size exceeds the 1 MiB cap.
        // The bounded tail should still surface the last 50 lines.
        var files = new FakeFileSystem();
        const int lineCount = 200_000;
        var sb = new System.Text.StringBuilder();
        for (var i = 1; i <= lineCount; i++)
            sb.AppendLine($"Line {i}");
        files.WriteAllText(ServerLog, sb.ToString());
        Assert.True(files.ReadAllText(ServerLog).Length > 1_000_000, "test fixture should exceed the cap");

        var output = new StringWriter();
        var installer = CreateInstaller(files, new FakeCommandExecutor(), output: output);

        var exitCode = await installer.LogsServerAsync(CommandOptions(lines: 50));

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains($"Line {lineCount}", text);
        Assert.Contains($"Line {lineCount - 49}", text);
    }

    [Fact]
    public async Task LogsServer_Follow_StreamsNewLinesUntilCancelled()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(ServerLog, "initial\n");
        var output = new StringWriter();
        var watcher = new FakeFileSystemWatcher();
        var cts = new CancellationTokenSource();
        var followStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var installer = CreateInstaller(
            files,
            new FakeCommandExecutor(),
            output: output,
            logChangeObserverFactory: _ => watcher);
        installer.TestFollowToken = cts.Token;
        installer.TestFollowStarted = () => followStarted.TrySetResult();

        var task = installer.LogsServerAsync(CommandOptions(follow: true));
        await TestWait.ForAsync(
            () => followStarted.Task.IsCompleted,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(20),
            "server log follow watcher to start");

        files.WriteAllText(ServerLog, "initial\nnew line 1\nnew line 2\n");
        await watcher.RaiseChangedAsync();
        await TestWait.ForAsync(
            () => output.ToString().Contains("new line 1", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(20),
            "server log follow output to include new line");

        cts.Cancel();
        await task;

        var text = output.ToString();
        Assert.Contains("initial", text);
        Assert.Contains("new line 1", text);
    }

    [Fact]
    public async Task UninstallServer_RemovesArtifactsButPreservesUserData()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        files.WriteAllText(ServerLauncher, "@echo off");
        files.WriteAllText(ServerStartup, "call x");
        files.WriteAllText(ServerMetadata, "{\"backend\":\"scheduled-task\"}");
        files.WriteAllText(Path.Combine(UserProfile, ".mohist", "mohist.db"), "data");
        files.WriteAllText(ServerLog, "log content");

        var installer = CreateInstaller(files, commands);
        var exitCode = await installer.UninstallServerAsync(CommandOptions());

        Assert.Equal(0, exitCode);
        Assert.False(files.HasFile(ServerLauncher));
        Assert.False(files.HasFile(ServerStartup));
        Assert.False(files.HasFile(ServerMetadata));
        Assert.True(files.HasFile(Path.Combine(UserProfile, ".mohist", "mohist.db")), "mohist.db should be preserved");
        Assert.True(files.HasFile(ServerLog), "out.log should be preserved");
    }

    [Fact]
    public async Task UninstallServer_WithFailingSchtasksDelete_StillCleansUpFiles()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        commands.ResponseFactory = (fileName, args) =>
        {
            if (fileName == "schtasks" && args[0] == "/Delete")
                return (1, "", "ERROR: The system cannot find the file specified.");
            return (0, "", "");
        };
        files.WriteAllText(ServerLauncher, "@echo off");
        files.WriteAllText(ServerStartup, "call x");
        files.WriteAllText(ServerMetadata, "{\"backend\":\"scheduled-task\"}");
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.UninstallServerAsync(CommandOptions());

        // Pinned behavior: even when schtasks /Delete returns non-zero (e.g. the task
        // was never registered), the installer still removes the launcher, the
        // Startup-fallback, and the metadata, and reports success.
        Assert.Equal(0, exitCode);
        Assert.False(files.HasFile(ServerLauncher));
        Assert.False(files.HasFile(ServerStartup));
        Assert.False(files.HasFile(ServerMetadata));
    }

    [Fact]
    public async Task InstallServer_DryRun_DoesNotWriteOrExecute()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var snapshot = new Dictionary<string, string>(files.Files);
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.InstallServerAsync(InstallOptions(dryRun: true));

        Assert.Equal(0, exitCode);
        Assert.Empty(commands.ExecutedCommands);
        Assert.Equal(snapshot.Count, files.Files.Count);
        foreach (var kvp in snapshot)
            Assert.Equal(kvp.Value, files.Files[kvp.Key]);
    }

    [Fact]
    public async Task StartServer_DryRun_DoesNotExecute()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        files.WriteAllText(ServerMetadata, "{\"backend\":\"scheduled-task\"}");
        var snapshot = new Dictionary<string, string>(files.Files);
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.StartServerAsync(CommandOptions(dryRun: true));

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(commands.ExecutedCommands, c => c.Args[0] == "/Run");
        Assert.Equal(snapshot.Count, files.Files.Count);
    }

    [Fact]
    public async Task StopServer_DryRun_DoesNotExecute()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        files.WriteAllText(ServerMetadata, "{\"backend\":\"scheduled-task\"}");
        var snapshot = new Dictionary<string, string>(files.Files);
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.StopServerAsync(CommandOptions(dryRun: true));

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(commands.ExecutedCommands, c => c.Args[0] == "/End");
        Assert.Equal(snapshot.Count, files.Files.Count);
    }

    [Fact]
    public async Task RestartServer_DryRun_DoesNotExecute()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        files.WriteAllText(ServerMetadata, "{\"backend\":\"scheduled-task\"}");
        var snapshot = new Dictionary<string, string>(files.Files);
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.RestartServerAsync(CommandOptions(dryRun: true));

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(commands.ExecutedCommands, c => c.Args[0] == "/End");
        Assert.DoesNotContain(commands.ExecutedCommands, c => c.Args[0] == "/Run");
        Assert.Equal(snapshot.Count, files.Files.Count);
    }

    [Fact]
    public async Task StatusServer_DryRun_DoesNotExecute()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        files.WriteAllText(ServerMetadata, "{\"backend\":\"scheduled-task\"}");
        var snapshot = new Dictionary<string, string>(files.Files);
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.StatusServerAsync(CommandOptions(dryRun: true));

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(commands.ExecutedCommands, c => c.FileName == "tasklist");
        Assert.Equal(snapshot.Count, files.Files.Count);
    }

    [Fact]
    public async Task LogsServer_DryRun_DoesNotRead()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        files.WriteAllText(ServerLog, "log content");
        var snapshot = new Dictionary<string, string>(files.Files);
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.LogsServerAsync(CommandOptions(dryRun: true, follow: true));

        Assert.Equal(0, exitCode);
        Assert.Empty(commands.ExecutedCommands);
        Assert.Equal(snapshot.Count, files.Files.Count);
    }

    [Fact]
    public async Task UninstallServer_DryRun_DoesNotExecuteOrDelete()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        files.WriteAllText(ServerLauncher, "@echo off");
        files.WriteAllText(ServerStartup, "call x");
        files.WriteAllText(ServerMetadata, "{\"backend\":\"scheduled-task\"}");
        var snapshot = new Dictionary<string, string>(files.Files);
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.UninstallServerAsync(CommandOptions(dryRun: true));

        Assert.Equal(0, exitCode);
        Assert.Empty(commands.ExecutedCommands);
        Assert.Equal(snapshot.Count, files.Files.Count);
        foreach (var kvp in snapshot)
            Assert.Equal(kvp.Value, files.Files[kvp.Key]);
    }

    [Fact]
    public async Task StartRunner_WithScheduledTask_Backend_RunsSchtasksRun()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        commands.ResponseFactory = (fileName, args) =>
        {
            if (fileName == "schtasks" && args[0] == "/Query")
                return (0, "Task exists", "");
            return (0, "", "");
        };
        files.WriteAllText(RunnerMetadata, "{\"backend\":\"scheduled-task\"}");
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.StartRunnerAsync(CommandOptions());

        Assert.Equal(0, exitCode);
        var runCommand = commands.ExecutedCommands.FirstOrDefault(c => c.Args[0] == "/Run");
        Assert.NotEqual(default, runCommand);
        Assert.Equal("Mohist_Runner", runCommand.Args[2]);
    }

    [Fact]
    public async Task StartRunner_WithLauncherOnly_Backend_StartsDetachedProcess()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var launched = new List<ProcessStartInfo>();
        Process? fakeLauncher(ProcessStartInfo psi) { launched.Add(psi); return null; }
        files.WriteAllText(RunnerLauncher, "@echo off");
        var installer = CreateInstaller(files, commands, processLauncher: fakeLauncher);

        var exitCode = await installer.StartRunnerAsync(CommandOptions());

        Assert.Equal(0, exitCode);
        Assert.Single(launched);
        Assert.Equal(RunnerLauncher, launched[0].FileName);
    }

    [Fact]
    public async Task StopRunner_WithScheduledTask_Backend_RunsSchtasksEnd()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        commands.ResponseFactory = (fileName, args) =>
        {
            if (fileName == "schtasks" && args[0] == "/Query")
                return (0, "Task exists", "");
            return (0, "", "");
        };
        files.WriteAllText(RunnerMetadata, "{\"backend\":\"scheduled-task\"}");
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.StopRunnerAsync(CommandOptions());

        Assert.Equal(0, exitCode);
        var endCommand = commands.ExecutedCommands.FirstOrDefault(c => c.Args[0] == "/End");
        Assert.NotEqual(default, endCommand);
        Assert.Equal("Mohist_Runner", endCommand.Args[2]);
        var deleteCommand = commands.ExecutedCommands.FirstOrDefault(c => c.Args[0] == "/Delete");
        Assert.Equal(default, deleteCommand);
    }

    [Fact]
    public async Task StopRunner_WithLauncherOnly_Backend_ScopesKillToMatchingLauncher()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        files.WriteAllText(RunnerLauncher, "@echo off");
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.StopRunnerAsync(CommandOptions());

        Assert.Equal(0, exitCode);
        var tasklist = commands.ExecutedCommands.FirstOrDefault(c => c.FileName == "tasklist");
        Assert.NotEqual(default, tasklist);
        Assert.Contains("IMAGENAME eq node.exe", tasklist.Args);
    }

    [Fact]
    public async Task RestartRunner_CallsStopThenStart()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        commands.ResponseFactory = (fileName, args) =>
        {
            if (fileName == "schtasks" && args[0] == "/Query")
                return (0, "Task exists", "");
            return (0, "", "");
        };
        files.WriteAllText(RunnerMetadata, "{\"backend\":\"scheduled-task\"}");
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.RestartRunnerAsync(CommandOptions());

        Assert.Equal(0, exitCode);
        var endIndex = commands.ExecutedCommands.FindIndex(c => c.Args[0] == "/End");
        var runIndex = commands.ExecutedCommands.FindIndex(c => c.Args[0] == "/Run");
        Assert.True(endIndex >= 0, "Stop (End) should be called");
        Assert.True(runIndex >= 0, "Start (Run) should be called");
        Assert.True(endIndex < runIndex, "Stop should come before Start");
    }

    [Fact]
    public async Task StatusRunner_WithScheduledTask_Backend_ReportsCorrectState()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        commands.ResponseFactory = (fileName, args) =>
        {
            if (fileName == "schtasks" && args[0] == "/Query")
                return (0, "Task exists", "");
            if (fileName == "tasklist")
                return (0, "\"Image Name\",\"PID\"\r\nnode.exe,1234", "");
            return (0, "", "");
        };
        files.WriteAllText(RunnerLauncher, "@echo off");
        files.WriteAllText(RunnerMetadata, "{\"backend\":\"scheduled-task\"}");
        var output = new StringWriter();
        var installer = CreateInstaller(files, commands, output: output);

        var exitCode = await installer.StatusRunnerAsync(CommandOptions());

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("scheduled-task: yes", text);
        Assert.Contains("launcher file: present", text);
        Assert.Contains("running: yes", text);
    }

    [Fact]
    public async Task LogsRunner_TailsLastNLines()
    {
        var files = new FakeFileSystem();
        var lines = Enumerable.Range(1, 100).Select(i => $"Line {i}");
        files.WriteAllText(RunnerLog, string.Join("\n", lines) + "\n");
        var output = new StringWriter();
        var installer = CreateInstaller(files, new FakeCommandExecutor(), output: output);

        var exitCode = await installer.LogsRunnerAsync(CommandOptions(lines: 10));

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("Line 91", text);
        Assert.Contains("Line 100", text);
        Assert.DoesNotContain("Line 90", text);
    }

    [Fact]
    public async Task LogsRunner_Follow_StreamsNewLinesUntilCancelled()
    {
        var files = new FakeFileSystem();
        files.WriteAllText(RunnerLog, "initial\n");
        var output = new StringWriter();
        var watcher = new FakeFileSystemWatcher();
        var cts = new CancellationTokenSource();
        var followStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var installer = CreateInstaller(
            files,
            new FakeCommandExecutor(),
            output: output,
            logChangeObserverFactory: _ => watcher);
        installer.TestFollowToken = cts.Token;
        installer.TestFollowStarted = () => followStarted.TrySetResult();

        var task = installer.LogsRunnerAsync(CommandOptions(follow: true));
        await TestWait.ForAsync(
            () => followStarted.Task.IsCompleted,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(20),
            "runner log follow watcher to start");

        files.WriteAllText(RunnerLog, "initial\nnew line 1\nnew line 2\n");
        await watcher.RaiseChangedAsync();
        await TestWait.ForAsync(
            () => output.ToString().Contains("new line 1", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(20),
            "runner log follow output to include new line");

        cts.Cancel();
        await task;

        var text = output.ToString();
        Assert.Contains("initial", text);
        Assert.Contains("new line 1", text);
    }

    [Fact]
    public async Task UninstallRunner_RemovesArtifactsButPreservesUserData()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        files.WriteAllText(RunnerLauncher, "@echo off");
        files.WriteAllText(RunnerStartup, "call x");
        files.WriteAllText(RunnerMetadata, "{\"backend\":\"scheduled-task\"}");
        files.WriteAllText(Path.Combine(UserProfile, ".mohist", "mohist.db"), "data");
        files.WriteAllText(RunnerLog, "log content");

        var installer = CreateInstaller(files, commands);
        var exitCode = await installer.UninstallRunnerAsync(CommandOptions());

        Assert.Equal(0, exitCode);
        Assert.False(files.HasFile(RunnerLauncher));
        Assert.False(files.HasFile(RunnerStartup));
        Assert.False(files.HasFile(RunnerMetadata));
        Assert.True(files.HasFile(Path.Combine(UserProfile, ".mohist", "mohist.db")), "mohist.db should be preserved");
        Assert.True(files.HasFile(RunnerLog), "out.log should be preserved");
    }

    [Fact]
    public async Task StartRunner_DryRun_DoesNotExecute()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        files.WriteAllText(RunnerMetadata, "{\"backend\":\"scheduled-task\"}");
        var snapshot = new Dictionary<string, string>(files.Files);
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.StartRunnerAsync(CommandOptions(dryRun: true));

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(commands.ExecutedCommands, c => c.Args[0] == "/Run");
        Assert.Equal(snapshot.Count, files.Files.Count);
    }

    [Fact]
    public async Task StopRunner_DryRun_DoesNotExecute()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        files.WriteAllText(RunnerMetadata, "{\"backend\":\"scheduled-task\"}");
        var snapshot = new Dictionary<string, string>(files.Files);
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.StopRunnerAsync(CommandOptions(dryRun: true));

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(commands.ExecutedCommands, c => c.Args[0] == "/End");
        Assert.Equal(snapshot.Count, files.Files.Count);
    }

    [Fact]
    public async Task RestartRunner_DryRun_DoesNotExecute()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        files.WriteAllText(RunnerMetadata, "{\"backend\":\"scheduled-task\"}");
        var snapshot = new Dictionary<string, string>(files.Files);
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.RestartRunnerAsync(CommandOptions(dryRun: true));

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(commands.ExecutedCommands, c => c.Args[0] == "/End");
        Assert.DoesNotContain(commands.ExecutedCommands, c => c.Args[0] == "/Run");
        Assert.Equal(snapshot.Count, files.Files.Count);
    }

    [Fact]
    public async Task StatusRunner_DryRun_DoesNotExecute()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        files.WriteAllText(RunnerMetadata, "{\"backend\":\"scheduled-task\"}");
        var snapshot = new Dictionary<string, string>(files.Files);
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.StatusRunnerAsync(CommandOptions(dryRun: true));

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(commands.ExecutedCommands, c => c.FileName == "tasklist");
        Assert.Equal(snapshot.Count, files.Files.Count);
    }

    [Fact]
    public async Task LogsRunner_DryRun_DoesNotRead()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        files.WriteAllText(RunnerLog, "log content");
        var snapshot = new Dictionary<string, string>(files.Files);
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.LogsRunnerAsync(CommandOptions(dryRun: true, follow: true));

        Assert.Equal(0, exitCode);
        Assert.Empty(commands.ExecutedCommands);
        Assert.Equal(snapshot.Count, files.Files.Count);
    }

    [Fact]
    public async Task UninstallRunner_DryRun_DoesNotExecuteOrDelete()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        files.WriteAllText(RunnerLauncher, "@echo off");
        files.WriteAllText(RunnerStartup, "call x");
        files.WriteAllText(RunnerMetadata, "{\"backend\":\"scheduled-task\"}");
        var snapshot = new Dictionary<string, string>(files.Files);
        var installer = CreateInstaller(files, commands);

        var exitCode = await installer.UninstallRunnerAsync(CommandOptions(dryRun: true));

        Assert.Equal(0, exitCode);
        Assert.Empty(commands.ExecutedCommands);
        Assert.Equal(snapshot.Count, files.Files.Count);
        foreach (var kvp in snapshot)
            Assert.Equal(kvp.Value, files.Files[kvp.Key]);
    }

    private sealed class FakeCommandExecutor : ICommandExecutor
    {
        public readonly List<(string FileName, string[] Args, string? WorkingDirectory)> ExecutedCommands = new();
        public Func<string, string[], (int ExitCode, string Stdout, string Stderr)>? ResponseFactory { get; set; }

        public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
            string fileName, string[] args, string? workingDirectory = null, CancellationToken cancellationToken = default)
        {
            ExecutedCommands.Add((fileName, args, workingDirectory));
            if (ResponseFactory != null)
                return Task.FromResult(ResponseFactory(fileName, args));
            return Task.FromResult((0, "", ""));
        }
    }

    private sealed class FakeFileSystemWatcher : ILogChangeObserver
    {
        private readonly Channel<TaskCompletionSource> _changes = Channel.CreateUnbounded<TaskCompletionSource>();

        public async Task ObserveAsync(Func<Task> onChanged, CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var completed in _changes.Reader.ReadAllAsync(cancellationToken))
                {
                    await onChanged();
                    completed.TrySetResult();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        public async Task RaiseChangedAsync()
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await _changes.Writer.WriteAsync(completed);
            await completed.Task;
        }

        public void Dispose() => _changes.Writer.TryComplete();
    }
}
