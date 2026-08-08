using Mohist.Cli;
using Mohist.Server.TestSupport;
using Xunit;
using static Mohist.Server.UnitTests.SystemSpecs.WindowsInstallTestSupport;

namespace Mohist.Server.UnitTests.SystemSpecs;

public class WindowsInstallRegistrationTests
{
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
