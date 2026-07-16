using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class EnsureCliWrapperTests
{
    private static UpdateOperations BuildOperations(
        out StringWriter output,
        out StringWriter error,
        FakeFileSystem? fs = null,
        ICommandExecutor? executor = null,
        string? home = "/home/test")
    {
        output = new StringWriter();
        error = new StringWriter();
        var fileSystem = fs ?? new FakeFileSystem();
        return new UpdateOperations(
            output,
            error,
            new FakeServiceInstaller(),
            executor ?? new FakeCommandExecutor(),
            fileSystem,
            new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false),
            getUserHome: () => home);
    }

    [Fact]
    public async Task EnsureCliWrapper_NoExistingWrapper_CreatesWrapper()
    {
        var fs = new FakeFileSystem();
        var executor = new FakeCommandExecutor();
        var wrapperPath = UpdateOperations.ResolveCliWrapperPath("/home/test");
        executor.QueueExpected("chmod", ["+x", $"{wrapperPath}.tmp"], null, 0);
        var ops = BuildOperations(out var output, out _, fs: fs, executor: executor, home: "/home/test");

        var exit = await ops.EnsureCliWrapperAsync("/managed/mo", "/home/test");

        Assert.Equal(0, exit);
        Assert.Contains("Installed CLI wrapper", output.ToString());
        Assert.True(fs.Exists(wrapperPath));
        Assert.Contains("/managed/mo", fs.ReadAllText(wrapperPath));
        executor.AssertExpectedCommandsExecuted();
    }

    [Fact]
    public async Task EnsureCliWrapper_ExistingWrapper_OverwritesWithNewContent()
    {
        var fs = new FakeFileSystem();
        var wrapperPath = UpdateOperations.ResolveCliWrapperPath("/home/test");
        var wrapperDir = Path.GetDirectoryName(wrapperPath)!;
        fs.CreateDirectory(wrapperDir);
        fs.AddFile(wrapperPath, "#!/bin/sh\nold-content\n");

        var executor = new FakeCommandExecutor();
        executor.QueueExpected("chmod", ["+x", $"{wrapperPath}.tmp"], null, 0);
        var ops = BuildOperations(out _, out _, fs: fs, executor: executor, home: "/home/test");

        var exit = await ops.EnsureCliWrapperAsync("/managed/mo", "/home/test");

        Assert.Equal(0, exit);
        var content = fs.ReadAllText(wrapperPath);
        Assert.Contains("/managed/mo", content);
        Assert.DoesNotContain("old-content", content);
        executor.AssertExpectedCommandsExecuted();
    }

    [Fact]
    public async Task EnsureCliWrapper_ChmodFailure_PreservesExistingWrapperAndCleansTemp()
    {
        var fs = new FakeFileSystem();
        var wrapperPath = UpdateOperations.ResolveCliWrapperPath("/home/test");
        var wrapperDir = Path.GetDirectoryName(wrapperPath)!;
        fs.CreateDirectory(wrapperDir);
        fs.AddFile(wrapperPath, "#!/bin/sh\nexisting\n");

        var executor = new ScriptedCommandExecutor();
        executor.Queue("chmod", 1, stderr: "permission denied");

        var ops = BuildOperations(out _, out var error, fs: fs, executor: executor, home: "/home/test");

        var exit = await ops.EnsureCliWrapperAsync("/managed/mo", "/home/test");

        Assert.NotEqual(0, exit);
        Assert.Contains("permission denied", error.ToString());
        var content = fs.ReadAllText(wrapperPath);
        Assert.Contains("existing", content);
        Assert.False(fs.Exists($"{wrapperPath}.tmp"), "temp file should be cleaned up");
    }

    [Fact]
    public async Task EnsureCliWrapper_Success_LeavesNoTempFile()
    {
        var fs = new FakeFileSystem();
        var wrapperPath = UpdateOperations.ResolveCliWrapperPath("/home/test");
        var wrapperDir = Path.GetDirectoryName(wrapperPath)!;
        fs.CreateDirectory(wrapperDir);

        var executor = new FakeCommandExecutor();
        executor.QueueExpected("chmod", ["+x", $"{wrapperPath}.tmp"], null, 0);
        var ops = BuildOperations(out _, out _, fs: fs, executor: executor, home: "/home/test");

        var exit = await ops.EnsureCliWrapperAsync("/managed/mo", "/home/test");

        Assert.Equal(0, exit);
        Assert.True(fs.Exists(wrapperPath));
        Assert.False(fs.Exists($"{wrapperPath}.tmp"));
        executor.AssertExpectedCommandsExecuted();
    }
}
