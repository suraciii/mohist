using System.Diagnostics;
using System.Threading.Channels;
using Mohist.Cli;
using Mohist.Server.TestSupport;

namespace Mohist.Server.SpecTests.Specs.SystemSpecs;

public enum WindowsServiceTarget
{
    Server,
    Runner,
}

internal static class WindowsInstallTestSupport
{
    internal const string UserProfile = "/mohist-tests/user";
    internal static string ServiceDir => Path.Combine(UserProfile, ".mohist", "service");
    internal static string ServerLauncher => Path.Combine(ServiceDir, "mohist-server.cmd");
    internal static string RunnerLauncher => Path.Combine(ServiceDir, "mohist-runner.cmd");
    internal static string ServerStartup => Path.Combine(UserProfile, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", "Startup", "Mohist_Server.cmd");
    internal static string RunnerStartup => Path.Combine(UserProfile, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", "Startup", "Mohist_Runner.cmd");
    internal static string ServerMetadata => Path.Combine(ServiceDir, "mohist-server.install.json");
    internal static string RunnerMetadata => Path.Combine(ServiceDir, "mohist-runner.install.json");
    internal static string ServerLog => Path.Combine(UserProfile, ".mohist", "server", "out.log");
    internal static string RunnerLog => Path.Combine(UserProfile, ".mohist", "runner", "out.log");

    internal static WindowsScheduledTaskInstaller CreateInstaller(
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

    internal static ServiceInstallOptions InstallOptions(bool dryRun = false, string? repoRoot = "/repo", string? listenUrl = null, string? serverUrl = null, string? runnerRoot = null)
    {
        return new ServiceInstallOptions(
            DryRun: dryRun,
            UnitDir: "/units",
            RepoRoot: repoRoot,
            ListenUrl: listenUrl,
            ServerUrl: serverUrl,
            RunnerRoot: runnerRoot);
    }

    internal static ServiceCommandOptions CommandOptions(bool dryRun = false, int lines = 50, bool follow = false)
    {
        return new ServiceCommandOptions(
            DryRun: dryRun,
            UnitDir: "/units",
            Lines: lines,
            Follow: follow);
    }

    internal sealed class FakeCommandExecutor : ICommandExecutor
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

    internal sealed class FakeFileSystemWatcher : ILogChangeObserver
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

    internal static string Launcher(WindowsServiceTarget target) => target == WindowsServiceTarget.Server ? ServerLauncher : RunnerLauncher;
    internal static string Startup(WindowsServiceTarget target) => target == WindowsServiceTarget.Server ? ServerStartup : RunnerStartup;
    internal static string Metadata(WindowsServiceTarget target) => target == WindowsServiceTarget.Server ? ServerMetadata : RunnerMetadata;
    internal static string Log(WindowsServiceTarget target) => target == WindowsServiceTarget.Server ? ServerLog : RunnerLog;
    internal static string TaskName(WindowsServiceTarget target) => target == WindowsServiceTarget.Server ? "Mohist_Server" : "Mohist_Runner";
    internal static string ProcessName(WindowsServiceTarget target) => target == WindowsServiceTarget.Server ? "dotnet.exe" : "node.exe";

    internal static ServiceInstallOptions TargetInstallOptions(WindowsServiceTarget target) =>
        target == WindowsServiceTarget.Server
            ? InstallOptions(repoRoot: @"C:\repo", listenUrl: "http://127.0.0.1:3456")
            : InstallOptions(repoRoot: @"C:\repo", serverUrl: "http://example.com:9999", runnerRoot: @"C:\custom-runner");

    internal static Task<int> InstallAsync(WindowsScheduledTaskInstaller installer, WindowsServiceTarget target, ServiceInstallOptions options) =>
        target == WindowsServiceTarget.Server ? installer.InstallServerAsync(options) : installer.InstallRunnerAsync(options);
    internal static Task<int> StartAsync(WindowsScheduledTaskInstaller installer, WindowsServiceTarget target, ServiceCommandOptions options) =>
        target == WindowsServiceTarget.Server ? installer.StartServerAsync(options) : installer.StartRunnerAsync(options);
    internal static Task<int> StopAsync(WindowsScheduledTaskInstaller installer, WindowsServiceTarget target, ServiceCommandOptions options) =>
        target == WindowsServiceTarget.Server ? installer.StopServerAsync(options) : installer.StopRunnerAsync(options);
    internal static Task<int> RestartAsync(WindowsScheduledTaskInstaller installer, WindowsServiceTarget target, ServiceCommandOptions options) =>
        target == WindowsServiceTarget.Server ? installer.RestartServerAsync(options) : installer.RestartRunnerAsync(options);
    internal static Task<int> StatusAsync(WindowsScheduledTaskInstaller installer, WindowsServiceTarget target, ServiceCommandOptions options) =>
        target == WindowsServiceTarget.Server ? installer.StatusServerAsync(options) : installer.StatusRunnerAsync(options);
    internal static Task<int> LogsAsync(WindowsScheduledTaskInstaller installer, WindowsServiceTarget target, ServiceCommandOptions options) =>
        target == WindowsServiceTarget.Server ? installer.LogsServerAsync(options) : installer.LogsRunnerAsync(options);
    internal static Task<int> UninstallAsync(WindowsScheduledTaskInstaller installer, WindowsServiceTarget target, ServiceCommandOptions options) =>
        target == WindowsServiceTarget.Server ? installer.UninstallServerAsync(options) : installer.UninstallRunnerAsync(options);
}
