using System.Diagnostics;
using System.Text;
using Mohist.Cli;
using Mohist.Server.SpecTests.Support;
using Xunit;
using static Mohist.Server.TestSupport.SystemSpecs.WindowsInstallTestSupport;

namespace Mohist.Server.UnitTests.SystemSpecs;

public class WindowsServerInstallEdgeTests
{
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

}
