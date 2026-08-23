using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Mohist.Cli;

internal sealed partial class WindowsScheduledTaskInstaller : IServiceInstaller
{
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly IFileSystem _fileSystem;
    private readonly ICommandExecutor _commandExecutor;
    private readonly Func<ProcessStartInfo, Process?> _processLauncher;
    private readonly Func<string, ILogChangeObserver> _logChangeObserverFactory;
    private readonly Func<string, Task<bool>> _healthProbe;
    private readonly IEnvironmentVariableProvider _environment;
    private readonly string _userProfilePath;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _pollWait;

    internal CancellationToken TestFollowToken { get; set; }
    internal Action? TestFollowStarted { get; set; }

    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public WindowsScheduledTaskInstaller(
        TextWriter output,
        TextWriter error,
        IFileSystem? fileSystem = null,
        ICommandExecutor? commandExecutor = null,
        Func<ProcessStartInfo, Process?>? processLauncher = null,
        Func<string, ILogChangeObserver>? logChangeObserverFactory = null,
        Func<string, Task<bool>>? healthProbe = null,
        string? userProfilePath = null,
        IEnvironmentVariableProvider? environment = null,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? pollWait = null)
    {
        _out = output;
        _err = error;
        _fileSystem = fileSystem ?? RealFileSystem.Instance;
        _commandExecutor = commandExecutor ?? new SystemCommandExecutor();
        _processLauncher = processLauncher ?? (psi => Process.Start(psi));
        _logChangeObserverFactory = logChangeObserverFactory ?? (path => new FileSystemLogChangeObserver(path));
        _healthProbe = healthProbe ?? (async url =>
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var response = await client.GetAsync(url);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        });
        _userProfilePath = userProfilePath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _environment = environment ?? SystemEnvironmentVariableProvider.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pollWait = pollWait ?? ((delay, cancellationToken) => Task.Delay(delay, _timeProvider, cancellationToken));
    }

    public async Task<int> InstallServerAsync(ServiceInstallOptions options)
    {
        var repoRoot = ResolveRepoRoot(options.RepoRoot);
        var launcherPath = ServerLauncherPath();
        var taskName = ServerTaskName;
        var startupPath = ServerStartupPath();
        var metadataPath = ServerMetadataPath();
        var listenUrl = options.ListenUrl;
        // 默认不写死监听地址，让 server 读 ~/.mohist/config.jsonc；仅当用户显式
        // 传 --listen-url 时才生成 ASPNETCORE_URLS 行。
        var sanitizedListenUrl = listenUrl is null ? null : SanitizeForCmdAssignment(listenUrl);
        var spec = new ServerLauncherSpec(SanitizeForCmdAssignment(repoRoot), sanitizedListenUrl);
        var launcherBody = RenderServerLauncher(spec);

        if (options.DryRun)
        {
            PreviewInstall(launcherPath, launcherBody, taskName);
            return 0;
        }

        // Re-install: if the previous install used the Startup-fallback, clean up the
        // stale Startup-folder shortcut so the new backend owns the install. The new
        // metadata written below will record the active backend.
        RemoveStaleStartupFallbackIfBackendChanges(metadataPath, startupPath, "scheduled-task");

        EnsureDirectory(launcherPath);
        await _fileSystem.WriteAllTextAsync(launcherPath, launcherBody);
        _out.WriteLine($"Wrote {launcherPath}");

        var createArgs = BuildCreateTaskArgs(new TaskCreateSpec(taskName, QuoteForSchtasksTr(launcherPath)));
        var (exitCode, _, stderr) = await _commandExecutor.ExecuteAsync("schtasks", createArgs);
        if (exitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(stderr)) _err.Write(stderr);
            await InstallStartupFallbackAsync(startupPath, launcherPath, metadataPath, repoRoot, listenUrl: listenUrl);
            _out.WriteLine("Installed with Startup-folder fallback (Scheduled Task creation was blocked).");
            return 0;
        }

        await WriteMetadataAsync(metadataPath, "scheduled-task", repoRoot, listenUrl: listenUrl);
        _out.WriteLine($"Registered Scheduled Task {taskName}");
        return 0;
    }

    public async Task<int> InstallRunnerAsync(ServiceInstallOptions options)
    {
        var repoRoot = ResolveRepoRoot(options.RepoRoot);
        var launcherPath = RunnerLauncherPath();
        var taskName = RunnerTaskName;
        var startupPath = RunnerStartupPath();
        var metadataPath = RunnerMetadataPath();
        var serverUrl = options.ServerUrl ?? "http://127.0.0.1:3456";
        var safeRepoRoot = SanitizeForCmdAssignment(repoRoot);
        var safeServerUrl = SanitizeForCmdAssignment(serverUrl);
        var safeRunnerRoot = options.RunnerRoot is null ? null : SanitizeForCmdAssignment(options.RunnerRoot);
        var spec = new RunnerLauncherSpec(safeRepoRoot, safeServerUrl, safeRunnerRoot, options.EnrollmentToken is null ? null : SanitizeForCmdAssignment(options.EnrollmentToken));
        var launcherBody = RenderRunnerLauncher(spec);

        if (options.DryRun)
        {
            PreviewInstall(launcherPath, launcherBody, taskName);
            return 0;
        }

        RemoveStaleStartupFallbackIfBackendChanges(metadataPath, startupPath, "scheduled-task");

        EnsureDirectory(launcherPath);
        await _fileSystem.WriteAllTextAsync(launcherPath, launcherBody);
        _out.WriteLine($"Wrote {launcherPath}");

        var createArgs = BuildCreateTaskArgs(new TaskCreateSpec(taskName, QuoteForSchtasksTr(launcherPath)));
        var (exitCode, _, stderr) = await _commandExecutor.ExecuteAsync("schtasks", createArgs);
        if (exitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(stderr)) _err.Write(stderr);
            await InstallStartupFallbackAsync(startupPath, launcherPath, metadataPath, repoRoot, serverUrl: serverUrl);
            _out.WriteLine("Installed with Startup-folder fallback (Scheduled Task creation was blocked).");
            return 0;
        }

        await WriteMetadataAsync(metadataPath, "scheduled-task", repoRoot, serverUrl: serverUrl);
        _out.WriteLine($"Registered Scheduled Task {taskName}");
        return 0;
    }

    public Task<int> StartServerAsync(ServiceCommandOptions options) =>
        StartAsync(ServerTaskName, ServerLauncherPath(), ServerStartupPath(), ServerMetadataPath(), "Server", options);

    public Task<int> StartRunnerAsync(ServiceCommandOptions options) =>
        StartAsync(RunnerTaskName, RunnerLauncherPath(), RunnerStartupPath(), RunnerMetadataPath(), "Runner", options);

    public Task<int> StopServerAsync(ServiceCommandOptions options) =>
        StopAsync(ServerTaskName, ServerLauncherPath(), ServerStartupPath(), ServerMetadataPath(), "Server", "dotnet", options);

    public Task<int> StopRunnerAsync(ServiceCommandOptions options) =>
        StopAsync(RunnerTaskName, RunnerLauncherPath(), RunnerStartupPath(), RunnerMetadataPath(), "Runner", "node", options);

    public async Task<int> RestartServerAsync(ServiceCommandOptions options)
    {
        var stop = await StopServerAsync(options);
        var start = await StartServerAsync(options);
        return stop != 0 ? stop : start;
    }

    public async Task<int> RestartRunnerAsync(ServiceCommandOptions options)
    {
        var stop = await StopRunnerAsync(options);
        var start = await StartRunnerAsync(options);
        return stop != 0 ? stop : start;
    }

    public Task<int> StatusServerAsync(ServiceCommandOptions options) =>
        StatusAsync(ServerTaskName, ServerLauncherPath(), ServerStartupPath(), ServerMetadataPath(), "Server", "dotnet", probeHealth: true, options);

    public Task<int> StatusRunnerAsync(ServiceCommandOptions options) =>
        StatusAsync(RunnerTaskName, RunnerLauncherPath(), RunnerStartupPath(), RunnerMetadataPath(), "Runner", "node", probeHealth: false, options);

    public async Task<bool> IsRunnerRunningAsync(CancellationToken cancellationToken = default)
    {
        var running = await IsProcessRunningAsync("node");
        return running;
    }

    public Task<int> LogsServerAsync(ServiceCommandOptions options) =>
        LogsAsync(ServerLogPath(), "Server", options);

    public Task<int> LogsRunnerAsync(ServiceCommandOptions options) =>
        LogsAsync(RunnerLogPath(), "Runner", options);

    public Task<int> UninstallServerAsync(ServiceCommandOptions options) =>
        UninstallAsync(ServerTaskName, ServerLauncherPath(), ServerStartupPath(), ServerMetadataPath(), "Server", options);

    public Task<int> UninstallRunnerAsync(ServiceCommandOptions options) =>
        UninstallAsync(RunnerTaskName, RunnerLauncherPath(), RunnerStartupPath(), RunnerMetadataPath(), "Runner", options);

    public Task<int> StartSlackAsync(ServiceCommandOptions options, CancellationToken cancellationToken = default) =>
        StartAsync(SlackTaskName, SlackLauncherPath(), SlackStartupPath(), SlackMetadataPath(), "Slack", options, cancellationToken);

    public Task<int> StopSlackAsync(ServiceCommandOptions options, CancellationToken cancellationToken = default) =>
        StopAsync(SlackTaskName, SlackLauncherPath(), SlackStartupPath(), SlackMetadataPath(), "Slack", "mohist-slack", options, cancellationToken);

    public async Task<int> RestartSlackAsync(
        ServiceCommandOptions options,
        CancellationToken cancellationToken = default)
    {
        var wasRunning = false;
        if (!options.DryRun)
        {
            var (queryCode, pids, queryError) = await QuerySlackProcessPidsAsync(
                SlackLauncherPath(),
                SlackStartupPath(),
                SlackMetadataPath(),
                cancellationToken);
            if (queryCode != 0)
            {
                if (!string.IsNullOrWhiteSpace(queryError)) _err.Write(queryError);
                return queryCode;
            }
            wasRunning = pids.Count > 0;
        }
        try
        {
            var stop = await StopSlackAsync(options, cancellationToken);
            return stop != 0 ? stop : await StartSlackAsync(options, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                var recovery = wasRunning
                    ? await StartSlackAsync(options, CancellationToken.None)
                    : await StopSlackAsync(options, CancellationToken.None);
                if (recovery != 0) _err.WriteLine("Slack restart cancellation recovery could not restore the previous service state.");
            }
            catch (Exception ex)
            {
                _err.WriteLine($"Slack restart cancellation recovery failed: {ex.Message}");
            }
            throw;
        }
    }

    public Task<int> StatusSlackAsync(ServiceCommandOptions options) =>
        StatusAsync(SlackTaskName, SlackLauncherPath(), SlackStartupPath(), SlackMetadataPath(), "Slack", "mohist-slack", probeHealth: false, options);

    public Task<int> LogsSlackAsync(ServiceCommandOptions options) => LogsAsync(SlackLogPath(), "Slack", options);

    public Task<int> UninstallSlackAsync(ServiceCommandOptions options) =>
        UninstallAsync(SlackTaskName, SlackLauncherPath(), SlackStartupPath(), SlackMetadataPath(), "Slack", options);

    public async Task<bool> IsRunnerInstalledAsync(string? unitDir = null)
    {
        _ = unitDir;
        var backend = await DetectBackendAsync(
            RunnerTaskName,
            RunnerStartupPath(),
            RunnerLauncherPath(),
            RunnerMetadataPath());
        return backend != BackendKind.None;
    }

    public async Task<bool> IsSlackInstalledAsync(string? unitDir = null)
    {
        _ = unitDir;
        if (_fileSystem.Exists(SlackLauncherPath()) || _fileSystem.Exists(SlackStartupPath())) return true;
        return await DetectBackendAsync(SlackTaskName, SlackStartupPath(), SlackLauncherPath(), SlackMetadataPath()) != BackendKind.None;
    }

    private async Task<int> StartAsync(
        string taskName,
        string launcherPath,
        string startupPath,
        string metadataPath,
        string kindDisplay,
        ServiceCommandOptions options,
        CancellationToken cancellationToken = default)
    {
        var backend = await DetectBackendAsync(taskName, startupPath, launcherPath, metadataPath, cancellationToken);

        if (options.DryRun)
        {
            _out.WriteLine($"Dry run: would use {BackendLabel(backend)} backend for {kindDisplay}");
            _out.WriteLine($"Dry run: would start {kindDisplay}");
            if (backend == BackendKind.ScheduledTask)
                _out.WriteLine($"Dry run: would run schtasks.exe with args: {string.Join(' ', BuildRunArgs(taskName))}");
            else if (backend != BackendKind.None)
                _out.WriteLine($"Dry run: would start detached process: {launcherPath}");
            return 0;
        }

        if (backend == BackendKind.Unknown)
        {
            _err.WriteLine($"Installed backend state could not be verified for {kindDisplay}.");
            return 1;
        }

        if (taskName == SlackTaskName && backend != BackendKind.None)
        {
            var (queryCode, pids, queryError) = await QuerySlackProcessPidsAsync(
                launcherPath,
                startupPath,
                metadataPath,
                cancellationToken);
            if (queryCode != 0)
            {
                if (!string.IsNullOrWhiteSpace(queryError)) _err.Write(queryError);
                return queryCode;
            }
            if (pids.Count > 0)
            {
                _out.WriteLine($"{kindDisplay} is already running.");
                return 0;
            }
        }

        switch (backend)
        {
            case BackendKind.ScheduledTask:
                {
                    var (code, _, stderr) = await _commandExecutor.ExecuteAsync(
                        "schtasks",
                        BuildRunArgs(taskName),
                        cancellationToken: cancellationToken);
                    if (code != 0 && !string.IsNullOrWhiteSpace(stderr)) _err.Write(stderr);
                    return code;
                }
            case BackendKind.StartupFallback:
            case BackendKind.LauncherOnly:
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var psi = new ProcessStartInfo(launcherPath)
                    {
                        UseShellExecute = true,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        ErrorDialog = false,
                    };
                    if (OperatingSystem.IsWindows())
                        psi.CreateNewProcessGroup = true;
                    var process = _processLauncher(psi);
                    if (process is not null)
                        _out.WriteLine($"Started {kindDisplay} (PID {process.Id})");
                    else
                        _out.WriteLine($"Started {kindDisplay}");
                    return 0;
                }
            default:
                _err.WriteLine($"No installed backend found for {kindDisplay}. Run 'mo install {kindDisplay.ToLowerInvariant()}' first.");
                return 1;
        }
    }

    private async Task<int> StopAsync(
        string taskName,
        string launcherPath,
        string startupPath,
        string metadataPath,
        string kindDisplay,
        string processImage,
        ServiceCommandOptions options,
        CancellationToken cancellationToken = default)
    {
        var backend = await DetectBackendAsync(taskName, startupPath, launcherPath, metadataPath, cancellationToken);

        if (options.DryRun)
        {
            _out.WriteLine($"Dry run: would use {BackendLabel(backend)} backend for {kindDisplay}");
            _out.WriteLine($"Dry run: would stop {kindDisplay}");
            if (backend == BackendKind.ScheduledTask)
                _out.WriteLine($"Dry run: would run schtasks.exe with args: {string.Join(' ', BuildEndArgs(taskName))}");
            if (backend == BackendKind.StartupFallback || backend == BackendKind.LauncherOnly
                || processImage.Equals("mohist-slack", StringComparison.OrdinalIgnoreCase) && backend != BackendKind.None)
                _out.WriteLine($"Dry run: would taskkill matching {kindDisplay} processes ({processImage}.exe)");
            return 0;
        }

        if (backend == BackendKind.Unknown)
        {
            _err.WriteLine($"Installed backend state could not be verified for {kindDisplay}.");
            return 1;
        }

        var exitCode = 0;

        if (processImage.Equals("mohist-slack", StringComparison.OrdinalIgnoreCase)
            && IsLegacyNodeSlackLauncher(launcherPath))
        {
            var (queryCode, launcherPids, queryError) = await QuerySlackProcessPidsAsync(
                launcherPath,
                startupPath,
                metadataPath,
                cancellationToken);
            if (queryCode != 0)
            {
                if (!string.IsNullOrWhiteSpace(queryError)) _err.Write(queryError);
                return queryCode;
            }
            if (launcherPids.Count > 0)
            {
                // The exact launcher cmd.exe is still alive here, so /T can
                // terminate its Node descendants before Task Scheduler can
                // orphan them by ending only the scheduled action.
                return await KillPidsAsync(
                    launcherPids,
                    includeTree: true,
                    cancellationToken: cancellationToken);
            }
        }

        if (backend == BackendKind.ScheduledTask)
        {
            var (code, _, stderr) = await _commandExecutor.ExecuteAsync(
                "schtasks",
                BuildEndArgs(taskName),
                cancellationToken: cancellationToken);
            if (code != 0)
            {
                if (!string.IsNullOrWhiteSpace(stderr)) _err.Write(stderr);
                exitCode = code;
            }
        }

        if (backend == BackendKind.StartupFallback || backend == BackendKind.LauncherOnly
            || processImage.Equals("mohist-slack", StringComparison.OrdinalIgnoreCase) && backend != BackendKind.None)
        {
            var killCode = await KillMatchingProcessesAsync(
                launcherPath,
                startupPath,
                metadataPath,
                processImage,
                cancellationToken);
            if (killCode != 0 && exitCode == 0) exitCode = killCode;
            else if (killCode == 0 && processImage.Equals("mohist-slack", StringComparison.OrdinalIgnoreCase)) exitCode = 0;
        }

        if (backend == BackendKind.None)
        {
            _out.WriteLine($"No installed backend found for {kindDisplay}.");
        }

        return exitCode;
    }

    private async Task<int> StatusAsync(
        string taskName,
        string launcherPath,
        string startupPath,
        string metadataPath,
        string kindDisplay,
        string processImage,
        bool probeHealth,
        ServiceCommandOptions options)
    {
        var backend = await DetectBackendAsync(taskName, startupPath, launcherPath, metadataPath);
        var taskRegistered = backend == BackendKind.ScheduledTask;
        var fallbackInstalled = _fileSystem.Exists(startupPath);
        var launcherPresent = _fileSystem.Exists(launcherPath);

        if (options.DryRun)
        {
            _out.WriteLine($"Dry run: would use {BackendLabel(backend)} backend for {kindDisplay}");
            _out.WriteLine($"Dry run: would report {kindDisplay} status");
            _out.WriteLine($"Dry run: scheduled-task: {(taskRegistered ? "yes" : "no")}");
            _out.WriteLine($"Dry run: startup-fallback: {(fallbackInstalled ? "yes" : "no")}");
            _out.WriteLine($"Dry run: launcher file: {(launcherPresent ? "present" : "missing")}");
            if (probeHealth)
                _out.WriteLine("Dry run: would probe http://localhost:3456/api/health");
            return 0;
        }

        _out.WriteLine($"{kindDisplay} status:");
        _out.WriteLine($"  scheduled-task: {(taskRegistered ? "yes" : "no")}");
        _out.WriteLine($"  startup-fallback: {(fallbackInstalled ? "yes" : "no")}");
        _out.WriteLine($"  launcher file: {(launcherPresent ? "present" : "missing")}");

        var running = await IsProcessRunningAsync(processImage, metadataPath);
        _out.WriteLine($"  running: {(running ? "yes" : "no")}");

        if (probeHealth)
        {
            var metadata = ReadMetadata(metadataPath);
            var healthUrl = metadata?.ListenUrl != null
                ? BuildHealthUrl(metadata.ListenUrl)
                : "http://localhost:3456/api/health";
            var reachable = await _healthProbe(healthUrl);
            _out.WriteLine($"  health: {(reachable ? "reachable" : "unreachable")} ({healthUrl})");
        }

        return 0;
    }

    private async Task<int> LogsAsync(string logPath, string kindDisplay, ServiceCommandOptions options)
    {
        if (options.DryRun)
        {
            _out.WriteLine($"Dry run: would read {logPath} ({kindDisplay}, lines={options.Lines}, follow={options.Follow})");
            return 0;
        }

        if (!_fileSystem.Exists(logPath))
        {
            _out.WriteLine($"Log file not found: {logPath}");
            return 0;
        }

        var tail = await ReadTailLinesAsync(logPath, options.Lines);
        foreach (var line in tail)
            _out.WriteLine(line);

        if (options.Follow)
        {
            var cts = new CancellationTokenSource();
            CancellationToken token;
            ConsoleCancelEventHandler? handler = null;

            if (TestFollowToken != default)
            {
                token = TestFollowToken;
                cts.Dispose();
            }
            else
            {
                handler = (_, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                };
                Console.CancelKeyPress += handler;
                token = cts.Token;
            }

            try
            {
                await FollowLogAsync(logPath, token);
            }
            finally
            {
                if (handler != null)
                {
                    Console.CancelKeyPress -= handler;
                    cts.Dispose();
                }
            }
        }

        return 0;
    }

    private async Task<int> UninstallAsync(
        string taskName,
        string launcherPath,
        string startupPath,
        string metadataPath,
        string kindDisplay,
        ServiceCommandOptions options)
    {
        if (options.DryRun)
        {
            _out.WriteLine($"Dry run: would uninstall {kindDisplay}");
            _out.WriteLine($"Dry run: would run schtasks.exe with args: {string.Join(' ', BuildDeleteArgs(taskName))}");
            _out.WriteLine($"Dry run: would remove {startupPath}");
            _out.WriteLine($"Dry run: would remove {launcherPath}");
            _out.WriteLine($"Dry run: would remove {metadataPath}");
            return 0;
        }

        var (code, _, stderr) = await _commandExecutor.ExecuteAsync("schtasks", BuildDeleteArgs(taskName));
        if (code != 0 && !string.IsNullOrWhiteSpace(stderr)) _err.Write(stderr);

        if (_fileSystem.Exists(startupPath))
        {
            _fileSystem.Delete(startupPath);
            _out.WriteLine($"Removed {startupPath}");
        }

        if (_fileSystem.Exists(launcherPath))
        {
            _fileSystem.Delete(launcherPath);
            _out.WriteLine($"Removed {launcherPath}");
        }

        if (_fileSystem.Exists(metadataPath))
        {
            _fileSystem.Delete(metadataPath);
            _out.WriteLine($"Removed {metadataPath}");
        }

        _out.WriteLine($"Uninstalled {kindDisplay}");
        return 0;
    }

    private async Task<BackendKind> DetectBackendAsync(
        string taskName,
        string startupPath,
        string launcherPath,
        string metadataPath,
        CancellationToken cancellationToken = default)
    {
        var metadata = ReadMetadata(metadataPath);
        if (taskName == SlackTaskName)
        {
            var probe = await ProbeSlackScheduledTaskAsync(taskName, launcherPath, cancellationToken);
            if (probe == ScheduledTaskProbe.Owned) return BackendKind.ScheduledTask;
            if (probe is ScheduledTaskProbe.Unknown or ScheduledTaskProbe.Conflict) return BackendKind.Unknown;
            if (metadata?.Backend == "startup-fallback" && _fileSystem.Exists(startupPath))
                return BackendKind.StartupFallback;
            if (metadata?.Backend == "launcher-only" && _fileSystem.Exists(launcherPath))
                return BackendKind.LauncherOnly;
            return BackendKind.None;
        }

        if (metadata?.Backend == "scheduled-task")
        {
            var (code, _, _) = await _commandExecutor.ExecuteAsync("schtasks", BuildQueryArgs(taskName));
            if (code == 0) return BackendKind.ScheduledTask;
        }
        else if (metadata?.Backend == "startup-fallback" && _fileSystem.Exists(startupPath))
        {
            return BackendKind.StartupFallback;
        }
        else if (metadata?.Backend == "launcher-only" && _fileSystem.Exists(launcherPath))
        {
            return BackendKind.LauncherOnly;
        }

        if (_fileSystem.Exists(startupPath)) return BackendKind.StartupFallback;
        if (_fileSystem.Exists(launcherPath)) return BackendKind.LauncherOnly;
        return BackendKind.None;
    }

    private async Task<bool> IsProcessRunningAsync(string imageName, string? metadataPath = null)
    {
        if (imageName.Equals("mohist-slack", StringComparison.OrdinalIgnoreCase))
        {
            var (queryCode, pids, queryError) = metadataPath is null
                ? (1, new List<int>(), "Cannot identify the installed Slack process because its installation metadata is unavailable.\n")
                : await QuerySlackProcessPidsAsync(SlackLauncherPath(), SlackStartupPath(), metadataPath);
            if (queryCode != 0 && !string.IsNullOrWhiteSpace(queryError)) _err.Write(queryError);
            return queryCode == 0 && pids.Count > 0;
        }
        var (code, stdout, _) = await _commandExecutor.ExecuteAsync("tasklist", ["/FI", $"IMAGENAME eq {imageName}.exe", "/FO", "CSV"]);
        if (code != 0) return false;
        var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 1;
    }

    private async Task<IReadOnlyList<string>> ReadTailLinesAsync(string logPath, int lineCount)
    {
        if (!_fileSystem.Exists(logPath)) return Array.Empty<string>();

        // Bounded tail: Windows log files grow unbounded in practice, so we
        // cap the read to the last `TailReadCapBytes` bytes and only then split
        // into lines. This is O(lineCount) memory and fast even for very long
        // logs. The Linux equivalent (journalctl -n) returns only the requested
        // lines, so this matches the documented "-n" semantics.
        const int TailReadCapBytes = 1 * 1024 * 1024;
        using var stream = _fileSystem.OpenRead(logPath);
        if (stream.Length > TailReadCapBytes)
            stream.Seek(-TailReadCapBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();
        var allLines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        if (allLines.Length > 0 && string.IsNullOrEmpty(allLines[^1]) && content.EndsWith('\n'))
            allLines = allLines[..^1];
        if (allLines.Length <= lineCount) return allLines;
        return allLines.Skip(allLines.Length - lineCount).ToArray();
    }

    private async Task FollowLogAsync(string logPath, CancellationToken token)
    {
        var position = 0L;
        if (_fileSystem.Exists(logPath))
        {
            using var stream = _fileSystem.OpenRead(logPath);
            position = stream.Length;
        }

        using var observer = _logChangeObserverFactory(logPath);
        var observing = observer.ObserveAsync(async () =>
        {
            try
            {
                position = await ReadAndPrintNewLinesAsync(logPath, position);
            }
            catch
            {
                // Best-effort: ignore read errors during follow.
            }
        }, token);
        TestFollowStarted?.Invoke();
        await observing;
    }

    private async Task<long> ReadAndPrintNewLinesAsync(string logPath, long position)
    {
        if (!_fileSystem.Exists(logPath)) return position;
        using var stream = _fileSystem.OpenRead(logPath);
        if (position > stream.Length) position = 0;
        stream.Position = position;
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true))
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
                _out.WriteLine(line);
        }
        return stream.Position;
    }

    private void PreviewInstall(string launcherPath, string launcherBody, string taskName)
    {
        _out.WriteLine($"Dry run: would write {launcherPath}");
        var summary = launcherBody.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (summary.Length > 120) summary = summary[..120] + "...";
        _out.WriteLine($"Dry run: launcher summary: {summary}");
        var args = BuildCreateTaskArgs(new TaskCreateSpec(taskName, QuoteForSchtasksTr(launcherPath)));
        _out.WriteLine($"Dry run: would run schtasks.exe with args: {string.Join(' ', args)}");
        _out.WriteLine("Dry run: would use Startup-folder fallback if Scheduled Task creation is blocked");
    }

    private async Task InstallStartupFallbackAsync(
        string startupPath,
        string launcherPath,
        string metadataPath,
        string repoRoot,
        string? listenUrl = null,
        string? serverUrl = null)
    {
        EnsureDirectory(startupPath);
        // The launcher path is built by ServerLauncherPath() / RunnerLauncherPath()
        // and is therefore trusted, but we still sanitize defensively in case a
        // future change makes the path user-controlled.
        var safeLauncherPath = SanitizeForCmdAssignment(launcherPath);
        var body = $"@echo off{Environment.NewLine}call \"{safeLauncherPath}\"{Environment.NewLine}";
        await _fileSystem.WriteAllTextAsync(startupPath, body);
        _out.WriteLine($"Wrote Startup fallback {startupPath}");
        await WriteMetadataAsync(metadataPath, "startup-fallback", repoRoot, listenUrl: listenUrl, serverUrl: serverUrl);
    }

    private bool RemoveStaleStartupFallbackIfBackendChanges(
        string metadataPath,
        string startupPath,
        string newBackend)
    {
        if (newBackend != "scheduled-task") return true;
        var metadata = ReadMetadata(metadataPath);
        if (metadata?.Backend != "startup-fallback") return true;
        if (!_fileSystem.Exists(startupPath)) return true;
        try
        {
            _fileSystem.Delete(startupPath);
            _out.WriteLine($"Removed stale Startup-folder fallback {startupPath}");
            return true;
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Failed to remove stale Startup-folder fallback {startupPath}: {ex.Message}");
            return false;
        }
    }

    private async Task WriteMetadataAsync(
        string metadataPath,
        string backend,
        string repoRoot,
        string? listenUrl = null,
        string? serverUrl = null)
    {
        EnsureDirectory(metadataPath);
        var metadata = new InstallMetadata(backend, repoRoot, listenUrl, serverUrl);
        var tempPath = $"{metadataPath}.tmp";
        await _fileSystem.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(metadata, MetadataJsonOptions));
        _fileSystem.MoveFile(tempPath, metadataPath);
    }

    private InstallMetadata? ReadMetadata(string metadataPath)
    {
        if (!_fileSystem.Exists(metadataPath)) return null;
        try
        {
            var json = _fileSystem.ReadAllText(metadataPath);
            return JsonSerializer.Deserialize<InstallMetadata>(json, MetadataJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void EnsureDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory) && !_fileSystem.DirectoryExists(directory))
            _fileSystem.CreateDirectory(directory);
    }

    private static string BuildHealthUrl(string listenUrl)
    {
        var baseUrl = listenUrl.TrimEnd('/');
        return baseUrl.EndsWith("/api/health", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : baseUrl + "/api/health";
    }

    private static string BackendLabel(BackendKind backend) => backend switch
    {
        BackendKind.ScheduledTask => "scheduled-task",
        BackendKind.StartupFallback => "startup-fallback",
        BackendKind.LauncherOnly => "launcher-only",
        BackendKind.Unknown => "unknown",
        _ => "none",
    };

    private static string ResolveRepoRoot(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
            return CanonicalizeWindowsPath(explicitRoot!);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Mohist.sln")))
                return CanonicalizeWindowsPath(dir.FullName);
            dir = dir.Parent;
        }

        return CanonicalizeWindowsPath(Directory.GetCurrentDirectory());
    }

    private static string CanonicalizeWindowsPath(string path) =>
        OperatingSystem.IsWindows() ? Path.GetFullPath(path) : path;

    private string ServiceDirectory() => Path.Combine(_userProfilePath, ".mohist", "service");
    private string StartupDirectory() => Path.Combine(_userProfilePath, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", "Startup");

    private const string ServerTaskName = "Mohist_Server";
    private const string RunnerTaskName = "Mohist_Runner";
    private const string SlackTaskName = "Mohist_Slack";
    // The Slack adapter's service credential env name is the server's own
    // (the adapter presents the content as Authorization: Bearer).
    private const string SlackAdapterTokenEnvironmentVariable = "MOHIST_OPERATOR_TOKEN";
    private const string RunnerEnrollmentTokenEnvironmentVariable = "MOHIST_ENROLLMENT_TOKEN";

    private string ServerLauncherPath() => Path.Combine(ServiceDirectory(), "mohist-server.cmd");
    private string RunnerLauncherPath() => Path.Combine(ServiceDirectory(), "mohist-runner.cmd");
    private string SlackLauncherPath() => Path.Combine(ServiceDirectory(), "mohist-slack.cmd");
    private string ServerStartupPath() => Path.Combine(StartupDirectory(), "Mohist_Server.cmd");
    private string RunnerStartupPath() => Path.Combine(StartupDirectory(), "Mohist_Runner.cmd");
    private string SlackStartupPath() => Path.Combine(StartupDirectory(), "Mohist_Slack.cmd");
    private string ServerMetadataPath() => Path.Combine(ServiceDirectory(), "mohist-server.install.json");
    private string RunnerMetadataPath() => Path.Combine(ServiceDirectory(), "mohist-runner.install.json");
    private string SlackMetadataPath() => Path.Combine(ServiceDirectory(), "mohist-slack.install.json");
    private string ServerLogPath() => Path.Combine(_userProfilePath, ".mohist", "server", "out.log");
    private string RunnerLogPath() => Path.Combine(_userProfilePath, ".mohist", "runner", "out.log");
    private string SlackLogPath() => Path.Combine(_userProfilePath, ".mohist", "slack", "out.log");

    internal string RenderServerLauncher(ServerLauncherSpec spec)
    {
        var logFile = @"%USERPROFILE%\.mohist\server\out.log";
        var project = @"packages\server\src\Mohist.Server\Mohist.Server.csproj";
        var repoRoot = QuoteForCmdBody(spec.RepoRoot);

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine($"cd /d {repoRoot}");
        if (!string.IsNullOrEmpty(spec.ListenUrl))
            sb.AppendLine($"set \"ASPNETCORE_URLS={spec.ListenUrl}\"");
        sb.AppendLine($"dotnet run --project {project} >> \"{logFile}\" 2>&1");
        return sb.ToString();
    }

    internal string RenderRunnerLauncher(RunnerLauncherSpec spec)
    {
        var logFile = @"%USERPROFILE%\.mohist\runner\out.log";
        var repoRoot = QuoteForCmdBody(spec.RepoRoot);
        if (string.IsNullOrEmpty(spec.ServerUrl))
            throw new ArgumentException("RunnerLauncherSpec.ServerUrl must be provided", nameof(spec));

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine($"cd /d {repoRoot}");
        sb.AppendLine($"set \"SERVER_URL={spec.ServerUrl}\"");
        if (!string.IsNullOrEmpty(spec.RunnerRoot))
            sb.AppendLine($"set \"RUNNER_ROOT={spec.RunnerRoot}\"");
        if (!string.IsNullOrEmpty(spec.EnrollmentToken))
            sb.AppendLine($"set \"{RunnerEnrollmentTokenEnvironmentVariable}={spec.EnrollmentToken}\"");
        sb.AppendLine($"node packages\\runner\\dist\\cli.js >> \"{logFile}\" 2>&1");
        return sb.ToString();
    }

    internal string RenderSlackLauncher(SlackLauncherSpec spec)
    {
        var repoRoot = QuoteForCmdBody(spec.RepoRoot);
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine($"cd /d {repoRoot}");
        sb.AppendLine($"set \"SERVER_URL={spec.ServerUrl}\"");
        if (!string.IsNullOrEmpty(spec.OperatorToken))
            sb.AppendLine($"set \"{SlackAdapterTokenEnvironmentVariable}={spec.OperatorToken}\"");
        sb.AppendLine("\"packages\\go\\mohist-slack\\bin\\mohist-slack.exe\" >> \"%USERPROFILE%\\.mohist\\slack\\out.log\" 2>&1");
        return sb.ToString();
    }

    internal static string QuoteForCmdBody(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '&' or '|' or '<' or '>' or '^' or '"')
                sb.Append('^');
            sb.Append(c);
        }
        var escaped = sb.ToString();
        if (escaped.Contains(' ') && !escaped.StartsWith('"'))
            return '"' + escaped + '"';
        return escaped;
    }

    /// <summary>
    /// Rejects values that would inject a new command into the generated .cmd
    /// launcher. CRLF, LF, NUL, and embedded double-quote are all blocked
    /// outright: the launcher file is later executed by schtasks /Run or by
    /// double-click from the Startup folder, with no human review.
    /// </summary>
    internal static string SanitizeForCmdAssignment(string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        if (value.Any(c => c is '\r' or '\n' or '\0' or '"'))
            throw new ArgumentException(
                "Value contains characters that are unsafe in a generated .cmd file.",
                nameof(value));
        return value;
    }

    internal sealed record ServerLauncherSpec(string RepoRoot, string? ListenUrl);
    internal sealed record RunnerLauncherSpec(string RepoRoot, string? ServerUrl, string? RunnerRoot, string? EnrollmentToken = null);
    internal sealed record SlackLauncherSpec(string RepoRoot, string ServerUrl, string? OperatorToken = null);
    internal sealed record TaskCreateSpec(string TaskName, string TrPayload);
    internal sealed record InstallMetadata(string Backend, string? RepoRoot, string? ListenUrl, string? ServerUrl);

    private enum BackendKind
    {
        None,
        Unknown,
        ScheduledTask,
        StartupFallback,
        LauncherOnly,
    }

    private enum ScheduledTaskProbe
    {
        Owned,
        Absent,
        Conflict,
        Unknown,
    }

    internal static string QuoteForSchtasksTr(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var escaped = value.Replace("\"", "\\\"");
        return escaped.Contains(' ') ? "\"" + escaped + "\"" : escaped;
    }

    internal static string[] BuildCreateTaskArgs(TaskCreateSpec spec)
    {
        return ["/Create", "/SC", "ONLOGON", "/RL", "LIMITED", "/TN", spec.TaskName, "/TR", spec.TrPayload, "/F"];
    }

    internal static string[] BuildRunArgs(string taskName)
    {
        return ["/Run", "/TN", taskName];
    }

    internal static string[] BuildEndArgs(string taskName)
    {
        return ["/End", "/TN", taskName];
    }

    internal static string[] BuildDeleteArgs(string taskName)
    {
        return ["/Delete", "/TN", taskName, "/F"];
    }

    internal static string[] BuildQueryArgs(string taskName)
    {
        return ["/Query", "/TN", taskName];
    }
}
