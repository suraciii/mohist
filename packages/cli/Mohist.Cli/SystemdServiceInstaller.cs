using System.Diagnostics;
using System.Text;

namespace Mohist.Cli;

internal sealed class SystemdServiceInstaller : IServiceInstaller, IManagedRuntimeActivator
{
    private const string ServerUnit = "mohist.service";
    private const string RunnerUnit = "mohist-runner.service";
    private const string SlackUnit = "mohist-slack.service";
    // The Slack adapter's service credential env names are the server's
    // own (the adapter presents the content as Authorization: Bearer).
    private const string AdapterTokenEnvironmentVariable = "MOHIST_OPERATOR_TOKEN";
    private const string AdapterTokenPathEnvironmentVariable = "MOHIST_OPERATOR_TOKEN_PATH";
    private const string RunnerEnrollmentTokenEnvironmentVariable = "MOHIST_ENROLLMENT_TOKEN";
    private const string OperatorCredentialName = "operator-token";
    private const string DefaultOperatorCredentialSource = "%h/.mohist/operator-token";

    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly IFileSystem _fileSystem;
    private readonly ICommandExecutor _commandExecutor;
    private readonly IEnvironmentVariableProvider _environment;
    private readonly Func<string?> _getLocalHostname;

    public SystemdServiceInstaller(
        TextWriter output,
        TextWriter error,
        IFileSystem? fileSystem = null,
        ICommandExecutor? commandExecutor = null,
        IEnvironmentVariableProvider? environment = null,
        Func<string?>? getLocalHostname = null)
    {
        _out = output;
        _err = error;
        _fileSystem = fileSystem ?? RealFileSystem.Instance;
        _commandExecutor = commandExecutor ?? new SystemCommandExecutor();
        _environment = environment ?? SystemEnvironmentVariableProvider.Instance;
        _getLocalHostname = getLocalHostname ?? (() => Environment.MachineName);
    }

    public async Task<int> InstallServerAsync(ServiceInstallOptions options)
    {
        var repoRoot = ResolveRepoRoot(options.RepoRoot);
        var environment = BuildServiceEnvironment();
        // 默认不在 unit 里写死监听地址，让 server 读 ~/.mohist/config.jsonc；
        // 仅当用户显式传 --listen-url 时才追加 --urls。
        var serverArgs = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.ListenUrl))
        {
            serverArgs.Add("--urls");
            serverArgs.Add(options.ListenUrl);
        }
        var unit = new SystemdUnit(
            Name: ServerUnit,
            Description: "Mohist Server",
            WorkingDirectory: repoRoot,
            ExecStart: DotnetRun(ResolveExecutable("dotnet"), repoRoot, "packages/server/src/Mohist.Server/Mohist.Server.csproj", serverArgs),
            Environment: environment);

        return await InstallAsync(unit, options);
    }

    public async Task<int> InstallRunnerAsync(ServiceInstallOptions options)
    {
        var repoRoot = ResolveRepoRoot(options.RepoRoot);
        var environment = BuildServiceEnvironment();
        environment["SERVER_URL"] = options.ServerUrl ?? "http://127.0.0.1:3456";
        if (!string.IsNullOrWhiteSpace(options.RunnerRoot))
            environment["RUNNER_ROOT"] = options.RunnerRoot;
        if (!string.IsNullOrWhiteSpace(options.EnrollmentToken))
            environment[RunnerEnrollmentTokenEnvironmentVariable] = options.EnrollmentToken!;

        var unit = new SystemdUnit(
            Name: RunnerUnit,
            Description: "Mohist Runner",
            WorkingDirectory: repoRoot,
            ExecStart: $"{ResolveExecutable("node")} packages/runner/{ManagedRuntimeLayout.RunnerEntrypoint}",
            Environment: environment);

        return await InstallAsync(unit, options);
    }

    public async Task<int> InstallSlackAsync(ServiceInstallOptions options)
    {
        var repoRoot = ResolveRepoRoot(options.RepoRoot);
        var environment = BuildServiceEnvironment(includeOperatorToken: true);
        var loadCredentials = Array.Empty<string>();
        if (!environment.ContainsKey(AdapterTokenEnvironmentVariable))
        {
            environment[AdapterTokenPathEnvironmentVariable] = $"%d/{OperatorCredentialName}";
            loadCredentials = [$"{OperatorCredentialName}:{ResolveOperatorCredentialSource()}"];
        }
        environment["SERVER_URL"] = options.ServerUrl ?? "http://127.0.0.1:3456";
        var unit = new SystemdUnit(
            Name: SlackUnit,
            Description: "Mohist Slack adapter",
            WorkingDirectory: repoRoot,
            ExecStart: $"{ResolveExecutable("node")} packages/mohist-slack/dist/cli.js",
            Environment: environment,
            LoadCredentials: loadCredentials);
        return await InstallAsync(unit, options);
    }

    public Task<int> StartServerAsync(ServiceCommandOptions options) => StartAsync(ServerUnit, options);
    public Task<int> StopServerAsync(ServiceCommandOptions options) => StopAsync(ServerUnit, options);
    public Task<int> RestartServerAsync(ServiceCommandOptions options) => RestartAsync(ServerUnit, options);
    public Task<int> StatusServerAsync(ServiceCommandOptions options) => StatusAsync(ServerUnit, options);
    public Task<int> LogsServerAsync(ServiceCommandOptions options) => LogsAsync(ServerUnit, options);
    public Task<int> UninstallServerAsync(ServiceCommandOptions options) => UninstallAsync(ServerUnit, options);

    public Task<int> StartRunnerAsync(ServiceCommandOptions options) => StartAsync(RunnerUnit, options);
    public Task<int> StopRunnerAsync(ServiceCommandOptions options) => StopAsync(RunnerUnit, options);
    public Task<int> RestartRunnerAsync(ServiceCommandOptions options) => RestartAsync(RunnerUnit, options);
    public Task<int> StatusRunnerAsync(ServiceCommandOptions options) => StatusAsync(RunnerUnit, options);
    public Task<int> LogsRunnerAsync(ServiceCommandOptions options) => LogsAsync(RunnerUnit, options);
    public Task<int> UninstallRunnerAsync(ServiceCommandOptions options) => UninstallAsync(RunnerUnit, options);
    public Task<int> StartSlackAsync(ServiceCommandOptions options) => StartAsync(SlackUnit, options);
    public Task<int> StopSlackAsync(ServiceCommandOptions options) => StopAsync(SlackUnit, options);
    public Task<int> RestartSlackAsync(ServiceCommandOptions options) => RestartAsync(SlackUnit, options);
    public Task<int> StatusSlackAsync(ServiceCommandOptions options) => StatusAsync(SlackUnit, options);
    public Task<int> LogsSlackAsync(ServiceCommandOptions options) => LogsAsync(SlackUnit, options);
    public Task<int> UninstallSlackAsync(ServiceCommandOptions options) => UninstallAsync(SlackUnit, options);

    public Task<(RunnerLaunchIdentity? Identity, string? Error)> ResolveRunnerLaunchIdentityAsync(
        string? unitDir,
        CancellationToken cancellationToken = default)
    {
        if (!EnsureSystemdSupported(dryRun: false))
            return Task.FromResult<(RunnerLaunchIdentity?, string?)>((null, "managed systemd runtime is unavailable"));

        var path = Path.Combine(ResolveUnitDir(unitDir), RunnerUnit).Replace('\\', '/');
        if (!_fileSystem.Exists(path))
        {
            return Task.FromResult<(RunnerLaunchIdentity?, string?)>(
                (null, "runner launch configuration is unavailable"));
        }

        var setting = SystemdUnitParser.ReadRunnerIdSetting(_fileSystem.ReadAllText(path));
        if (setting.Error is not null)
            return Task.FromResult<(RunnerLaunchIdentity?, string?)>((null, setting.Error));
        if (setting.RunnerId is { Length: > 0 } configured)
            return Task.FromResult<(RunnerLaunchIdentity?, string?)>((new RunnerLaunchIdentity(configured), null));

        var hostname = _getLocalHostname()?.Trim();
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return Task.FromResult<(RunnerLaunchIdentity?, string?)>(
                (null, "runner launch identity is unavailable because the local hostname is unavailable"));
        }

        return Task.FromResult<(RunnerLaunchIdentity?, string?)>((new RunnerLaunchIdentity($"runner-{hostname}"), null));
    }

    public async Task<int> ApplyManagedRuntimeAsync(
        RuntimeTargetSet targets,
        string scope,
        string? unitDir,
        CancellationToken cancellationToken = default,
        ManagedRuntimeSnapshot? snapshot = null)
    {
        if (!EnsureSystemdSupported(dryRun: false))
            return 1;

        if (Includes(scope, "server") && targets.Server is null
            || Includes(scope, "runner") && targets.Runner is null)
        {
            _err.WriteLine("Managed runtime activation was requested without a complete service target.");
            return 1;
        }

        var resolvedUnitDir = ResolveUnitDir(unitDir);
        try
        {
            if (Includes(scope, "server"))
                await WriteManagedUnitAsync(resolvedUnitDir, ServerUnit, "Mohist Server", targets.Server!, cancellationToken);
            if (Includes(scope, "runner"))
                await WriteManagedUnitAsync(resolvedUnitDir, RunnerUnit, "Mohist Runner", targets.Runner!, cancellationToken);
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Managed service target installation failed: {ex.Message}");
            return 1;
        }

        var (reload, _, reloadErr) = await _commandExecutor.ExecuteAsync(
            "systemctl", ["--user", "daemon-reload"], cancellationToken: cancellationToken);
        if (reload != 0)
        {
            if (!string.IsNullOrWhiteSpace(reloadErr)) _err.Write(reloadErr);
            return reload;
        }

        if (Includes(scope, "server"))
        {
            var server = await RestartAsync(ServerUnit, new ServiceCommandOptions(false, unitDir, 100, false));
            if (server != 0) return server;
        }
        if (Includes(scope, "runner"))
        {
            var runner = await RestartAsync(RunnerUnit, new ServiceCommandOptions(false, unitDir, 100, false));
            if (runner != 0) return runner;
        }

        return 0;
    }

    public async Task<ManagedRuntimeSnapshot?> CaptureManagedRuntimeSnapshotAsync(
        string scope,
        string? unitDir,
        CancellationToken cancellationToken = default)
    {
        if (!EnsureSystemdSupported(dryRun: false))
            throw new InvalidOperationException("managed systemd runtime is unavailable");

        var resolvedUnitDir = ResolveUnitDir(unitDir);
        var server = Includes(scope, "server")
            ? await CaptureUnitSnapshotAsync(resolvedUnitDir, ServerUnit, cancellationToken)
            : null;
        var runner = Includes(scope, "runner")
            ? await CaptureUnitSnapshotAsync(resolvedUnitDir, RunnerUnit, cancellationToken)
            : null;
        return new ManagedRuntimeSnapshot(server, runner);
    }

    public async Task<ManagedRuntimeRestoreResult> RestoreManagedRuntimeAsync(
        RuntimeTargetSet? targets,
        string scope,
        string? unitDir,
        CancellationToken cancellationToken = default,
        ManagedRuntimeSnapshot? snapshot = null)
    {
        if (!EnsureSystemdSupported(dryRun: false))
            return ManagedRuntimeRestoreResult.FromExitCode(1, scope, "managed systemd runtime is unavailable");

        if (snapshot is not null)
            return await RestoreSnapshotAsync(snapshot, scope, unitDir, cancellationToken);

        if (targets is null)
        {
            var stopCode = 0;
            if (Includes(scope, "runner"))
                stopCode = await StopAsync(RunnerUnit, new ServiceCommandOptions(false, unitDir, 100, false));
            if (Includes(scope, "server"))
            {
                var serverStop = await StopAsync(ServerUnit, new ServiceCommandOptions(false, unitDir, 100, false));
                if (stopCode == 0) stopCode = serverStop;
            }
            return ManagedRuntimeRestoreResult.FromExitCode(stopCode, scope);
        }

        var applyCode = await ApplyManagedRuntimeAsync(targets, scope, unitDir, cancellationToken);
        return ManagedRuntimeRestoreResult.FromExitCode(applyCode, scope);
    }

    private async Task WriteManagedUnitAsync(
        string unitDir,
        string unitName,
        string description,
        RuntimeTarget target,
        CancellationToken cancellationToken)
    {
        if (!target.IsAbsoluteTarget || !target.UsesCanonicalEntrypoint || !target.Identity.IsComplete)
            throw new InvalidOperationException($"managed target {target.Component} is not trusted");

        var environment = BuildServiceEnvironment();
        var identityRoot = target.Component == "runner"
            ? target.DependencyRoot ?? target.WorkingDirectory
            : target.WorkingDirectory;
        environment["MOHIST_RUNTIME_IDENTITY_PATH"] = Path.Combine(identityRoot, "runtime-identity.json").Replace('\\', '/');
        if (target.Component == "runner")
        {
            if (target.Identity.RunnerId is not { Length: > 0 } runnerId)
                throw new InvalidOperationException("managed Runner target does not declare an instance identity");
            environment["RUNNER_ID"] = runnerId;
            environment["SERVER_URL"] = _environment.GetEnvironmentVariable("MOHIST_SERVER_URL") ?? "http://127.0.0.1:3456";
        }

        var executable = target.LaunchMode == RuntimeLaunchMode.Node
            ? $"{ShellQuote(target.NodeExecutable!)} {ShellQuote(target.Entrypoint)}"
            : ShellQuote(target.Entrypoint);
        if (target.Arguments.Length > 0)
            executable += " " + string.Join(' ', target.Arguments.Select(ShellQuote));

        var unit = new SystemdUnit(
            unitName,
            description,
            target.WorkingDirectory,
            executable,
            environment);
        var unitPath = Path.Combine(unitDir, unitName).Replace('\\', '/');
        _fileSystem.CreateDirectory(unitDir);
        var tempPath = $"{unitPath}.{target.Identity.ReleaseId}.{target.Identity.Generation}.tmp";
        await _fileSystem.WriteAllTextAsync(tempPath, unit.Render());
        _fileSystem.MoveFile(tempPath, unitPath);
    }

    private static bool Includes(string scope, string component) =>
        string.Equals(scope, "full", StringComparison.Ordinal)
        || string.Equals(scope, component, StringComparison.Ordinal);

    private async Task<ManagedUnitSnapshot> CaptureUnitSnapshotAsync(
        string unitDir,
        string unitName,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(unitDir, unitName).Replace('\\', '/');
        var exists = _fileSystem.Exists(path);
        var contents = exists ? ReadBytes(path) : null;
        var active = await IsUnitActiveAsync(unitName, cancellationToken);
        var enabled = await IsUnitEnabledAsync(unitName, cancellationToken);
        return new ManagedUnitSnapshot(unitName, exists, contents, active, enabled);
    }

    private async Task<ManagedRuntimeRestoreResult> RestoreSnapshotAsync(
        ManagedRuntimeSnapshot snapshot,
        string scope,
        string? unitDir,
        CancellationToken cancellationToken)
    {
        var resolvedUnitDir = ResolveUnitDir(unitDir);
        var units = new[]
        {
            (Component: "server", Snapshot: snapshot.Server),
            (Component: "runner", Snapshot: snapshot.Runner),
        };
        var failures = new List<string>();
        var exitCode = 0;
        var serverState = ManagedRuntimeRestoreState.NotAttempted;
        var runnerState = ManagedRuntimeRestoreState.NotAttempted;
        void MarkFailed(string component)
        {
            if (component == "server") serverState = ManagedRuntimeRestoreState.Failed;
            if (component == "runner") runnerState = ManagedRuntimeRestoreState.Failed;
        }

        void MarkRestored(string component)
        {
            if (component == "server") serverState = ManagedRuntimeRestoreState.Restored;
            if (component == "runner") runnerState = ManagedRuntimeRestoreState.Restored;
        }

        async Task AttemptAsync(string operation, Func<Task<int>> action)
        {
            try
            {
                var result = await action();
                if (result != 0)
                {
                    failures.Add($"{operation} exited with code {result}");
                    if (exitCode == 0) exitCode = result;
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{operation} failed: {ex.Message}");
                if (exitCode == 0) exitCode = 1;
            }
        }

        foreach (var (component, unit) in units)
        {
            if (!Includes(scope, component) || unit is null)
                continue;

            var path = Path.Combine(resolvedUnitDir, unit.UnitName).Replace('\\', '/');
            try
            {
                if (unit.Exists)
                {
                    var temp = $"{path}.restore.tmp";
                    using (var stream = _fileSystem.OpenWrite(temp))
                    {
                        stream.Write(unit.Contents ?? []);
                    }
                    _fileSystem.MoveFile(temp, path);
                }
                else if (_fileSystem.Exists(path))
                {
                    _fileSystem.Delete(path);
                }
            }
            catch (Exception ex)
            {
                failures.Add($"restore {unit.UnitName} failed: {ex.Message}");
                MarkFailed(component);
                if (exitCode == 0) exitCode = 1;
            }
        }

        var reloadFailed = false;
        try
        {
            var reload = await ExecuteSystemctlAsync(["--user", "daemon-reload"], cancellationToken);
            if (reload != 0)
            {
                reloadFailed = true;
                failures.Add($"daemon-reload exited with code {reload}");
                if (exitCode == 0) exitCode = reload;
            }
        }
        catch (Exception ex)
        {
            reloadFailed = true;
            failures.Add($"daemon-reload failed: {ex.Message}");
            if (exitCode == 0) exitCode = 1;
        }

        foreach (var (component, unit) in units)
        {
            if (!Includes(scope, component) || unit is null)
                continue;

            var stateCommand = unit.WasEnabled ? "enable" : "disable";
            await AttemptAsync(
                $"{stateCommand} {unit.UnitName}",
                () => ExecuteSystemctlAsync(["--user", stateCommand, unit.UnitName], cancellationToken));

            var lifecycleCommand = unit.WasActive ? "restart" : "stop";
            await AttemptAsync(
                $"{lifecycleCommand} {unit.UnitName}",
                () => ExecuteSystemctlAsync(["--user", lifecycleCommand, unit.UnitName], cancellationToken));

            await AttemptAsync(
                $"verify enabled {unit.UnitName}",
                async () => (await IsUnitEnabledAsync(unit.UnitName, cancellationToken)) == unit.WasEnabled ? 0 : 1);
            await AttemptAsync(
                $"verify active {unit.UnitName}",
                async () => (await IsUnitActiveAsync(unit.UnitName, cancellationToken)) == unit.WasActive ? 0 : 1);

            if (reloadFailed)
            {
                MarkFailed(component);
            }
            else if (!failures.Any(failure => failure.Contains(unit.UnitName, StringComparison.Ordinal)))
            {
                MarkRestored(component);
            }
            else
            {
                MarkFailed(component);
            }
        }

        if (failures.Count > 0)
            _err.WriteLine($"Managed runtime source restore failed: {string.Join("; ", failures)}");
        return new ManagedRuntimeRestoreResult(
            exitCode,
            serverState,
            runnerState,
            failures.Count == 0 ? null : string.Join("; ", failures));
    }

    private async Task<bool> IsUnitActiveAsync(string unitName, CancellationToken cancellationToken)
    {
        var result = await _commandExecutor.ExecuteAsync(
            "systemctl",
            ["--user", "is-active", unitName],
            cancellationToken: cancellationToken);
        var state = result.Stdout.Trim().ToLowerInvariant();
        if (result.ExitCode == 0 && state == "active")
            return true;
        if (result.ExitCode != 0 && state is "inactive" or "failed" or "dead" or "unknown" or "not-found")
            return false;
        throw new InvalidOperationException($"systemctl is-active {unitName} returned unrecognized state '{result.Stdout.Trim()}' with exit code {result.ExitCode}");
    }

    private async Task<bool> IsUnitEnabledAsync(string unitName, CancellationToken cancellationToken)
    {
        var result = await _commandExecutor.ExecuteAsync(
            "systemctl",
            ["--user", "is-enabled", unitName],
            cancellationToken: cancellationToken);
        var state = result.Stdout.Trim().ToLowerInvariant();
        if (result.ExitCode == 0 && state is "enabled" or "enabled-runtime" or "linked" or "linked-runtime")
            return true;
        if (result.ExitCode != 0 && state is "disabled" or "static" or "indirect" or "generated" or "transient" or "masked" or "not-found")
            return false;
        throw new InvalidOperationException($"systemctl is-enabled {unitName} returned unrecognized state '{result.Stdout.Trim()}' with exit code {result.ExitCode}");
    }

    private async Task<int> ExecuteSystemctlAsync(string[] args, CancellationToken cancellationToken)
    {
        var result = await _commandExecutor.ExecuteAsync(
            "systemctl",
            args,
            cancellationToken: cancellationToken);
        return result.ExitCode;
    }

    private byte[] ReadBytes(string path)
    {
        using var stream = _fileSystem.OpenRead(path);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public async Task<bool> IsRunnerRunningAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux() && _commandExecutor is SystemCommandExecutor) return false;
        var (code, stdout, _) = await _commandExecutor.ExecuteAsync(
            "systemctl",
            ["--user", "is-active", RunnerUnit]);
        var trimmed = stdout?.Trim() ?? string.Empty;
        return code == 0 && string.Equals(trimmed, "active", StringComparison.OrdinalIgnoreCase);
    }

    public Task<bool> IsRunnerInstalledAsync(string? unitDir = null) => Task.FromResult(IsRunnerUnitInstalled(unitDir));

    public Task<bool> IsSlackInstalledAsync(string? unitDir = null) => Task.FromResult(
        _fileSystem.Exists(Path.Combine(ResolveUnitDir(unitDir), SlackUnit)));

    private bool IsRunnerUnitInstalled(string? unitDir)
    {
        var unitPath = Path.Combine(ResolveUnitDir(unitDir), RunnerUnit);
        return _fileSystem.Exists(unitPath);
    }

    private async Task<int> InstallAsync(SystemdUnit unit, ServiceInstallOptions options)
    {
        if (!EnsureSystemdSupported(options.DryRun)) return 1;

        var unitDir = ResolveUnitDir(options.UnitDir);

        var unitPath = Path.Combine(unitDir, unit.Name);
        await _fileSystem.WriteAllTextAsync(unitPath, unit.Render());
        _out.WriteLine($"Wrote {unitPath}");

        if (options.DryRun)
        {
            _out.WriteLine("Dry run: skipped systemctl enable/start");
            return 0;
        }

        var (daemonReload, _, daemonReloadErr) = await _commandExecutor.ExecuteAsync("systemctl", ["--user", "daemon-reload"]);
        if (daemonReload != 0)
        {
            if (!string.IsNullOrWhiteSpace(daemonReloadErr)) _err.Write(daemonReloadErr);
            return daemonReload;
        }

        var (enable, _, enableErr) = await _commandExecutor.ExecuteAsync("systemctl", ["--user", "enable", unit.Name]);
        if (enable != 0)
        {
            if (!string.IsNullOrWhiteSpace(enableErr)) _err.Write(enableErr);
            return enable;
        }

        var (start, _, startErr) = await _commandExecutor.ExecuteAsync("systemctl", ["--user", "restart", unit.Name]);
        if (start != 0)
        {
            if (!string.IsNullOrWhiteSpace(startErr)) _err.Write(startErr);
            return start;
        }

        _out.WriteLine($"Installed and started {unit.Name}");
        await TryEnableLingerAsync();
        return 0;
    }

    private async Task<int> StartAsync(string unitName, ServiceCommandOptions options)
    {
        if (!EnsureSystemdSupported(options.DryRun)) return 1;
        return await RunSystemctlAsync(unitName, options, "start");
    }

    private async Task<int> StopAsync(string unitName, ServiceCommandOptions options)
    {
        if (!EnsureSystemdSupported(options.DryRun)) return 1;
        return await RunSystemctlAsync(unitName, options, "stop");
    }

    private async Task<int> RestartAsync(string unitName, ServiceCommandOptions options)
    {
        if (!EnsureSystemdSupported(options.DryRun)) return 1;
        return await RunSystemctlAsync(unitName, options, "restart");
    }

    private async Task<int> StatusAsync(string unitName, ServiceCommandOptions options)
    {
        if (!EnsureSystemdSupported(options.DryRun)) return 1;
        return await RunSystemctlAsync(unitName, options, "status", "--no-pager");
    }

    private async Task<int> LogsAsync(string unitName, ServiceCommandOptions options)
    {
        if (!EnsureSystemdSupported(options.DryRun)) return 1;
        var args = new List<string> { "--user", "-u", unitName, "--no-pager", "-n", options.Lines.ToString() };
        if (options.Follow)
            args.Add("-f");

        if (options.DryRun)
        {
            _out.WriteLine("Dry run: journalctl " + string.Join(' ', args.Select(ShellQuote)));
            return 0;
        }

        var (code, stdout, stderr) = await _commandExecutor.ExecuteAsync("journalctl", args.ToArray());
        if (!string.IsNullOrWhiteSpace(stdout)) _out.Write(stdout);
        if (!string.IsNullOrWhiteSpace(stderr)) _err.Write(stderr);
        return code;
    }

    private async Task<int> UninstallAsync(string unitName, ServiceCommandOptions options)
    {
        if (!EnsureSystemdSupported(options.DryRun)) return 1;
        var unitPath = Path.Combine(ResolveUnitDir(options.UnitDir), unitName);

        if (options.DryRun)
        {
            _out.WriteLine($"Dry run: systemctl --user disable --now {unitName}");
            _out.WriteLine($"Dry run: remove {unitPath}");
            _out.WriteLine("Dry run: systemctl --user daemon-reload");
            return 0;
        }

        var (disable, _, disableErr) = await _commandExecutor.ExecuteAsync("systemctl", ["--user", "disable", "--now", unitName]);
        if (disable != 0)
        {
            if (!string.IsNullOrWhiteSpace(disableErr)) _err.Write(disableErr);
            return disable;
        }

        if (_fileSystem.Exists(unitPath))
        {
            _fileSystem.Delete(unitPath);
            _out.WriteLine($"Removed {unitPath}");
        }
        else
        {
            _out.WriteLine($"Unit file not found: {unitPath}");
        }

        var (reload, _, reloadErr) = await _commandExecutor.ExecuteAsync("systemctl", ["--user", "daemon-reload"]);
        if (reload != 0 && !string.IsNullOrWhiteSpace(reloadErr)) _err.Write(reloadErr);
        return reload;
    }

    private async Task<int> RunSystemctlAsync(string unitName, ServiceCommandOptions options, params string[] command)
    {
        var args = new List<string> { "--user" };
        args.AddRange(command);
        args.Add(unitName);

        if (options.DryRun)
        {
            _out.WriteLine("Dry run: systemctl " + string.Join(' ', args.Select(ShellQuote)));
            return 0;
        }

        var (code, stdout, stderr) = await _commandExecutor.ExecuteAsync("systemctl", args.ToArray());
        if (!string.IsNullOrWhiteSpace(stdout)) _out.Write(stdout);
        if (!string.IsNullOrWhiteSpace(stderr)) _err.Write(stderr);
        return code;
    }

    private bool EnsureSystemdSupported(bool dryRun)
    {
        if (OperatingSystem.IsLinux() || dryRun || _commandExecutor is not SystemCommandExecutor) return true;
        _err.WriteLine("systemd service management is only supported on Linux. Use --dry-run to preview commands.");
        return false;
    }

    private async Task TryEnableLingerAsync()
    {
        var user = Environment.UserName;
        if (string.IsNullOrWhiteSpace(user)) return;
        var (code, _, stderr) = await _commandExecutor.ExecuteAsync("loginctl", ["enable-linger", user]);
        if (code != 0)
            _err.WriteLine("Warning: loginctl enable-linger failed; service may stop when the user logs out.");
    }

    private static string ResolveRepoRoot(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
            return explicitRoot.Replace('\\', '/');

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Mohist.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static string ResolveUnitDir(string? explicitUnitDir) =>
        (explicitUnitDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config",
            "systemd",
            "user")).Replace('\\', '/');

    private static string DotnetRun(string dotnetPath, string repoRoot, string projectPath, IReadOnlyList<string> args)
    {
        var combinedPath = (repoRoot + "/" + projectPath).Replace('\\', '/');
        var parts = new List<string>
        {
            dotnetPath,
            "run",
            "--project",
            ShellQuote(combinedPath),
        };
        if (args.Count > 0)
        {
            parts.Add("--");
            parts.AddRange(args.Select(ShellQuote));
        }
        return string.Join(' ', parts);
    }

    private static string NormalizePath(string value) => value.Replace('\\', '/');

    private Dictionary<string, string> BuildServiceEnvironment(bool includeOperatorToken = false)
    {
        var environment = new Dictionary<string, string>
        {
            ["PATH"] = BuildServicePath(),
        };
        if (includeOperatorToken)
        {
            var operatorToken = _environment.GetEnvironmentVariable(AdapterTokenEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(operatorToken))
                environment[AdapterTokenEnvironmentVariable] = operatorToken;
        }
        var dotnetRoot = ResolveDotnetRoot();
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            environment["DOTNET_ROOT"] = dotnetRoot;
            environment["DOTNET_ROOT_X64"] = dotnetRoot;
        }

        return environment;
    }

    private string? ResolveDotnetRoot()
    {
        var configured = _environment.GetEnvironmentVariable("DOTNET_ROOT_X64")
            ?? _environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var home = _environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return null;

        var userDotnetRoot = Path.Combine(home, ".dotnet");
        return _fileSystem.Exists(Path.Combine(userDotnetRoot, "dotnet"))
            ? userDotnetRoot
            : null;
    }

    private static string BuildServicePath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var entries = new[]
        {
            Path.Combine(home, ".opencode", "bin"),
            Path.Combine(home, ".local", "bin"),
            "/usr/local/bin",
            "/usr/bin",
            "/bin",
        };
        return string.Join(':', entries.Select(NormalizePath));
    }

    private string ResolveExecutable(string name)
    {
        if (Path.IsPathRooted(name))
            return name.Replace('\\', '/');

        var path = _environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return name;

        foreach (var raw in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = raw.Trim();
            if (dir.Length == 0) continue;
            var candidate = (dir.EndsWith('/') ? dir + name : dir + "/" + name).Replace('\\', '/');
            if (File.Exists(candidate))
                return candidate;
        }

        return name;
    }

    private string ResolveOperatorCredentialSource()
    {
        var configured = _environment.GetEnvironmentVariable(AdapterTokenPathEnvironmentVariable);
        return string.IsNullOrWhiteSpace(configured)
            ? DefaultOperatorCredentialSource
            : NormalizePath(Path.GetFullPath(configured));
    }

    private static string ShellQuote(string value)
    {
        if (value.Length == 0) return "''";
        if (value.All(c => char.IsLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or ':' or '='))
            return value;
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}

internal record SystemdUnit(
    string Name,
    string Description,
    string WorkingDirectory,
    string ExecStart,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string>? LoadCredentials = null)
{
    public string Render()
    {
        var builder = new StringBuilder();
        builder.AppendLine("[Unit]");
        builder.AppendLine($"Description={Description}");
        builder.AppendLine("After=network.target");
        builder.AppendLine();
        builder.AppendLine("[Service]");
        builder.AppendLine("Type=simple");
        builder.AppendLine($"WorkingDirectory={EscapeValue(NormalizePath(WorkingDirectory))}");
        foreach (var credential in LoadCredentials ?? [])
            builder.AppendLine($"LoadCredential={EscapeValue(credential)}");
        foreach (var (key, value) in Environment)
            builder.AppendLine($"Environment=\"{EscapeEnvironment(key)}={EscapeEnvironment(NormalizePath(value))}\"");
        builder.AppendLine($"ExecStart={ExecStart}");
        builder.AppendLine("Restart=on-failure");
        builder.AppendLine("RestartSec=5");
        builder.AppendLine("SuccessExitStatus=0 143");
        builder.AppendLine("TimeoutStopSec=30");
        builder.AppendLine("StandardOutput=journal");
        builder.AppendLine("StandardError=journal");
        builder.AppendLine();
        builder.AppendLine("[Install]");
        builder.AppendLine("WantedBy=default.target");
        return builder.ToString().Replace("\r\n", "\n");
    }

    private static string NormalizePath(string value) => value.Replace('\\', '/');

    private static string EscapeValue(string value) => RejectControlChars(value);

    private static string EscapeEnvironment(string value) => RejectControlChars(value)
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string RejectControlChars(string value)
    {
        if (value.Any(c => c is '\r' or '\n' or '\0'))
            throw new ArgumentException("systemd unit values cannot contain control characters");
        return value;
    }
}
