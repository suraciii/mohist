using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class EnsureCliWrapperSpecs
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
        var ops = BuildOperations(out var output, out _, fs: fs, home: "/home/test");
        var wrapperPath = UpdateOperations.ResolveCliWrapperPath("/home/test");

        var exit = await ops.EnsureCliWrapperAsync("/managed/mo", "/home/test");

        Assert.Equal(0, exit);
        Assert.Contains("Installed CLI wrapper", output.ToString());
        Assert.True(fs.Exists(wrapperPath));
        Assert.Contains("/managed/mo", fs.ReadAllText(wrapperPath));
    }

    [Fact]
    public async Task EnsureCliWrapper_ExistingWrapper_OverwritesWithNewContent()
    {
        var fs = new FakeFileSystem();
        var wrapperPath = UpdateOperations.ResolveCliWrapperPath("/home/test");
        var wrapperDir = Path.GetDirectoryName(wrapperPath)!;
        fs.CreateDirectory(wrapperDir);
        fs.AddFile(wrapperPath, "#!/bin/sh\nold-content\n");

        var ops = BuildOperations(out _, out _, fs: fs, home: "/home/test");

        var exit = await ops.EnsureCliWrapperAsync("/managed/mo", "/home/test");

        Assert.Equal(0, exit);
        var content = fs.ReadAllText(wrapperPath);
        Assert.Contains("/managed/mo", content);
        Assert.DoesNotContain("old-content", content);
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

        var ops = BuildOperations(out _, out _, fs: fs, home: "/home/test");

        var exit = await ops.EnsureCliWrapperAsync("/managed/mo", "/home/test");

        Assert.Equal(0, exit);
        Assert.True(fs.Exists(wrapperPath));
        Assert.False(fs.Exists($"{wrapperPath}.tmp"));
    }

    private sealed class ScriptedCommandExecutor : ICommandExecutor
    {
        private readonly Dictionary<string, Queue<(int ExitCode, string Stdout, string Stderr)>> _byFileName = new(StringComparer.Ordinal);

        public void Queue(string fileName, int exitCode, string stdout = "", string stderr = "")
        {
            if (!_byFileName.TryGetValue(fileName, out var bucket))
            {
                bucket = new Queue<(int, string, string)>();
                _byFileName[fileName] = bucket;
            }
            bucket.Enqueue((exitCode, stdout, stderr));
        }

        public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
            string fileName, string[] args, string? workingDirectory = null, CancellationToken cancellationToken = default)
        {
            if (_byFileName.TryGetValue(fileName, out var bucket) && bucket.Count > 0)
                return Task.FromResult(bucket.Dequeue());
            return Task.FromResult((0, string.Empty, string.Empty));
        }
    }

    private sealed class FakeServiceInstaller : IServiceInstaller
    {
        public Task<int> InstallServerAsync(ServiceInstallOptions options) => Task.FromResult(0);
        public Task<int> InstallRunnerAsync(ServiceInstallOptions options) => Task.FromResult(0);
        public Task<int> StartServerAsync(ServiceCommandOptions options) => Task.FromResult(0);
        public Task<int> StopServerAsync(ServiceCommandOptions options) => Task.FromResult(0);
        public Task<int> RestartServerAsync(ServiceCommandOptions options) => Task.FromResult(0);
        public Task<int> StatusServerAsync(ServiceCommandOptions options) => Task.FromResult(0);
        public Task<int> LogsServerAsync(ServiceCommandOptions options) => Task.FromResult(0);
        public Task<int> UninstallServerAsync(ServiceCommandOptions options) => Task.FromResult(0);
        public Task<int> StartRunnerAsync(ServiceCommandOptions options) => Task.FromResult(0);
        public Task<int> StopRunnerAsync(ServiceCommandOptions options) => Task.FromResult(0);
        public Task<int> RestartRunnerAsync(ServiceCommandOptions options) => Task.FromResult(0);
        public Task<int> StatusRunnerAsync(ServiceCommandOptions options) => Task.FromResult(0);
        public Task<int> LogsRunnerAsync(ServiceCommandOptions options) => Task.FromResult(0);
        public Task<int> UninstallRunnerAsync(ServiceCommandOptions options) => Task.FromResult(0);
        public Task<bool> IsRunnerRunningAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> IsRunnerInstalledAsync(string? unitDir = null) => Task.FromResult(false);
    }
}
