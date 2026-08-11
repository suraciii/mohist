namespace Mohist.Cli;

internal sealed class UpdateOperations
{
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly IServiceInstaller _systemd;
    private readonly ICommandExecutor _commandExecutor;
    private readonly IFileSystem _fileSystem;
    private readonly IEnvironmentVariableProvider _environment;
    private readonly string? _unitDir;
    private readonly Func<string?>? _getUserHome;
    private readonly ManagedRuntimeTransaction? _managedRuntime;

    public UpdateOperations(
        TextWriter output,
        TextWriter error,
        IServiceInstaller systemd,
        ICommandExecutor commandExecutor,
        IFileSystem fileSystem,
        IEnvironmentVariableProvider environment,
        string? unitDir = null,
        Func<string?>? getUserHome = null)
    {
        _out = output;
        _err = error;
        _systemd = systemd;
        _commandExecutor = commandExecutor;
        _fileSystem = fileSystem;
        _environment = environment;
        _unitDir = unitDir;
        _getUserHome = getUserHome;
        if (systemd is IManagedRuntimeActivator activator)
        {
            var sourceResolver = new UpdateSourceResolver(
                commandExecutor,
                fileSystem,
                getUserHome ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            _managedRuntime = new ManagedRuntimeTransaction(
                output,
                error,
                commandExecutor,
                fileSystem,
                environment,
                sourceResolver,
                activator,
                unitDir);
        }
    }

    public async Task<(ManagedUpdateSession? Session, string? Error)> PrepareManagedUpdateAsync(
        string? repoRoot,
        string scope,
        string transactionId,
        string? cliPath,
        CancellationToken cancellationToken = default)
    {
        if (_managedRuntime is null)
            return (null, "service installer does not support managed runtime activation");
        return await _managedRuntime.PrepareAsync(repoRoot, scope, transactionId, cliPath, cancellationToken);
    }

    public Task<int> CommitManagedUpdateAsync(
        ManagedUpdateSession session,
        CancellationToken cancellationToken = default) =>
        _managedRuntime is null
            ? Task.FromResult(1)
            : _managedRuntime.CommitAsync(session, cancellationToken);

    public Task<int> RollbackManagedUpdateAsync(
        ManagedUpdateSession session,
        string reason,
        CancellationToken cancellationToken = default) =>
        _managedRuntime is null
            ? Task.FromResult(1)
            : _managedRuntime.RollbackAsync(session, reason, cancellationToken);

    public Task<int> UpdateCliAsync(string? repoRoot, bool dryRun, string? cliPath = null, CancellationToken cancellationToken = default)
    {
        var root = ResolveRepoRoot(repoRoot);
        return UpdateCliResolvedAsync(root, cliPath, dryRun);
    }

    public async Task<int> UpdateServerAsync(
        string? repoRoot,
        bool dryRun,
        TimeSpan serverReadyTimeout,
        ServiceReadinessProbe readinessProbe,
        CancellationToken cancellationToken = default)
    {
        var root = ResolveRepoRoot(repoRoot);

        _out.WriteLine($"Updating server from source: {root}");

        if (dryRun)
        {
            _out.WriteLine("Dry run: would execute:");
            _out.WriteLine($"  cd {root} && dotnet build Mohist.sln");
            _out.WriteLine($"  {RestartCommandLine("server")} (if installed)");
            _out.WriteLine("  wait for /api/health, /, and referenced /assets/* response headers readiness checks");
            await WriteServerScopeMessageAsync();
            return 0;
        }

        var exitCode = await BuildAndRestartServerAsync(root, cancellationToken);
        if (exitCode != 0)
        {
            return exitCode;
        }

        _out.WriteLine("Server service restarted.");
        var ready = await readinessProbe.WaitForServerReadyAsync(serverReadyTimeout, cancellationToken);
        if (!ready.Ready)
        {
            _err.WriteLine($"Server service restarted, but Mohist readiness checks did not pass within {(int)serverReadyTimeout.TotalSeconds} seconds.");
            if (!string.IsNullOrWhiteSpace(ready.LastFailure))
                _err.WriteLine($"Last readiness error: {ready.LastFailure}");
            return 1;
        }

        _out.WriteLine("Server is ready.");
        await WriteServerScopeMessageAsync();
        return 0;
    }

    public async Task<int> UpdateRunnerAsync(
        string? repoRoot,
        bool dryRun,
        RunnerRefreshVerifier runnerRefreshVerifier,
        CancellationToken cancellationToken = default)
    {
        var root = ResolveRepoRoot(repoRoot);

        _out.WriteLine($"Updating runner from source: {root}");

        if (dryRun)
        {
            _out.WriteLine("Dry run: would execute:");
            _out.WriteLine($"  cd {root} && npm run build -w packages/runner");
            _out.WriteLine($"  {RestartCommandLine("runner")} (if installed)");
            _out.WriteLine("  wait for runner to reconnect, then read its buildGitHash from /api/runner/identity");
            return 0;
        }

        var installed = await IsRunnerInstalledAsync();
        if (!installed)
        {
            var reason = "runner service is not installed";
            _out.WriteLine($"Runner refresh skipped: {reason}");
            runnerRefreshVerifier.WriteSkippedSummary(reason, _out, _err);
            return 0;
        }

        var buildExit = await BuildRunnerAsync(root);
        if (buildExit != 0)
            return buildExit;

        var interruption = await runnerRefreshVerifier.InterruptRunnerAsync(cancellationToken);
        if (!interruption.Succeeded)
        {
            _err.WriteLine($"Runner update interrupt: status=unconfirmed ({interruption.Error ?? "invalid response"}); runner service was not restarted.");
            return 1;
        }

        _out.WriteLine(
            $"Runner update interrupt: status=interrupted runnerId={interruption.RunnerId} interruptedWorkCount={interruption.InterruptedWorkCount}.");
        _out.WriteLine("Runner updated successfully.");

        var restart = await _systemd.RestartRunnerAsync(new ServiceCommandOptions(false, null, 100, false));
        if (restart != 0)
        {
            _err.WriteLine("Warning: Failed to restart runner service. You may need to restart manually.");
            return restart;
        }

        _out.WriteLine("Runner service restarted.");

        var outcome = await runnerRefreshVerifier.VerifyRunnerRuntimeAsync(root);
        outcome.WriteSummary(_out, _err);
        if (outcome.ExitCode != 0)
            _err.WriteLine("Runner update recovery: status=unconfirmed; refreshed runner identity was not confirmed.");
        return outcome.ExitCode;
    }

    public async Task<int> UpdateSlackAsync(string? repoRoot, bool dryRun, CancellationToken cancellationToken = default)
    {
        var root = ResolveRepoRoot(repoRoot);
        _out.WriteLine($"Updating Slack adapter from source: {root}");
        if (dryRun)
        {
            _out.WriteLine("Dry run: would execute:");
            _out.WriteLine($"  cd {root} && npm run build -w packages/mohist-slack");
            _out.WriteLine("  mo service restart slack (if installed)");
            return 0;
        }

        if (!await _systemd.IsSlackInstalledAsync(_unitDir))
        {
            _out.WriteLine("Slack refresh skipped: slack service is not installed");
            return 0;
        }

        var (build, buildOut, buildErr) = await _commandExecutor.ExecuteAsync(
            "npm", ["run", "build", "-w", "packages/mohist-slack"], root, cancellationToken);
        if (build != 0)
        {
            WriteCommandFailureOutput(buildOut, buildErr);
            _err.WriteLine("Build failed. Aborting update.");
            return build;
        }

        var restart = await _systemd.RestartSlackAsync(new ServiceCommandOptions(false, null, 100, false));
        if (restart != 0)
        {
            _err.WriteLine("Warning: Failed to restart Slack service. You may need to restart manually.");
            return restart;
        }
        _out.WriteLine("Slack adapter updated and service restarted.");
        return 0;
    }

    public async Task<int> UpdateCliResolvedAsync(string root, string? cliPath, bool dryRun)
    {
        var home = _getUserHome?.Invoke();
        var primaryTarget = ResolveManagedCliPath(home);
        var alternateTarget = ResolveAlternateManagedCliPath(home);
        var currentProcessPath = Environment.ProcessPath?.Replace('\\', '/');
        var managedTarget = string.Equals(currentProcessPath, primaryTarget, StringComparison.OrdinalIgnoreCase)
            ? alternateTarget
            : primaryTarget;
        var target = !string.IsNullOrWhiteSpace(cliPath) ? await ResolveCliPathAsync(cliPath) : managedTarget;
        if (string.IsNullOrWhiteSpace(target))
        {
            _err.WriteLine("Could not resolve mo executable path. Pass --cli-path to update the CLI explicitly.");
            return 1;
        }

        var publishDir = Path.Combine(root, ".publish", "cli");
        var binary = Path.Combine(publishDir, "Mohist.Cli");
        var tempTarget = $"{target}.tmp";
        var sourceSkillData = Path.Combine(publishDir, "skill-data");
        var sourcePresets = Path.Combine(publishDir, "presets");
        var managedSkillData = ResolveManagedAssetRoot(ManagedAssetKind.Skill);
        var managedPresets = ResolveManagedAssetRoot(ManagedAssetKind.Preset);

        _out.WriteLine($"Updating CLI from source: {root}");

        if (dryRun)
        {
            _out.WriteLine("Dry run: would execute:");
            _out.WriteLine($"  cd {root} && dotnet publish packages/cli/Mohist.Cli/Mohist.Cli.csproj -c Release -r {RuntimeIdentifier()} --self-contained true /p:PublishSingleFile=true -o {publishDir}");
            _out.WriteLine($"  cp {binary} {tempTarget}");
            _out.WriteLine($"  chmod +x {tempTarget}");
            _out.WriteLine($"  mv {tempTarget} {target}");
            _out.WriteLine($"  synchronize {sourceSkillData} into {managedSkillData} (prepare temp dir, replace managed root)");
            _out.WriteLine($"  synchronize {sourcePresets} into {managedPresets} (prepare temp dir, replace managed root)");
            if (target == primaryTarget || target == alternateTarget)
            {
                var wrapper = ResolveCliWrapperPath(home);
                _out.WriteLine($"  ensure wrapper script at {wrapper} -> {target}");
            }
            return 0;
        }

        var publishArgs = new[]
        {
            "publish",
            "packages/cli/Mohist.Cli/Mohist.Cli.csproj",
            "-c",
            "Release",
            "-r",
            RuntimeIdentifier(),
            "--self-contained",
            "true",
            "/p:PublishSingleFile=true",
            "-o",
            publishDir,
        };
        var (publish, publishOut, publishErr) = await _commandExecutor.ExecuteAsync("dotnet", publishArgs, root);
        if (publish != 0)
        {
            WriteCommandFailureOutput(publishOut, publishErr);
            _err.WriteLine("CLI publish failed. Aborting update.");
            return publish;
        }

        _fileSystem.CreateDirectory(Path.GetDirectoryName(target)!);

        var (copy, _, copyErr) = await _commandExecutor.ExecuteAsync("cp", [binary, tempTarget], root);
        if (copy != 0)
        {
            if (!string.IsNullOrWhiteSpace(copyErr)) _err.WriteLine(copyErr);
            _err.WriteLine("CLI install failed. Aborting update.");
            return copy;
        }

        var (chmod, _, chmodErr) = await _commandExecutor.ExecuteAsync("chmod", ["+x", tempTarget], root);
        if (chmod != 0)
        {
            if (!string.IsNullOrWhiteSpace(chmodErr)) _err.WriteLine(chmodErr);
            _err.WriteLine("CLI chmod failed. Aborting update.");
            return chmod;
        }

        var (move, _, moveErr) = await _commandExecutor.ExecuteAsync("mv", [tempTarget, target], root);
        if (move != 0)
        {
            if (!string.IsNullOrWhiteSpace(moveErr)) _err.WriteLine(moveErr);
            _err.WriteLine("CLI replace failed. Aborting update.");
            return move;
        }

        if (target == primaryTarget || target == alternateTarget)
        {
            var wrapperExit = await EnsureCliWrapperAsync(target, home);
            if (wrapperExit != 0)
            {
                _err.WriteLine("CLI wrapper installation failed. Aborting update.");
                return wrapperExit;
            }
        }

        var synchronizer = new ManagedAssetSynchronizer(_out, _err, _fileSystem);
        var syncExitCode = await synchronizer.SyncAsync(sourceSkillData, managedSkillData, ManagedAssetKind.Skill);
        if (syncExitCode != 0)
        {
            _err.WriteLine("Managed skill asset sync failed. Aborting update.");
            return syncExitCode;
        }

        var presetSyncExitCode = await synchronizer.SyncAsync(sourcePresets, managedPresets, ManagedAssetKind.Preset);
        if (presetSyncExitCode != 0)
        {
            _err.WriteLine("Managed preset asset sync failed. Aborting update.");
            return presetSyncExitCode;
        }

        _out.WriteLine($"CLI updated: {target}");
        return 0;
    }

    public async Task<int> SyncSkillsAsync(string? repoRoot, string? sourceSkillData, bool dryRun, CancellationToken cancellationToken = default)
    {
        var managedSkillData = ResolveManagedAssetRoot(ManagedAssetKind.Skill);

        string sourceSkillDataDir;
        if (!string.IsNullOrWhiteSpace(sourceSkillData))
        {
            sourceSkillDataDir = sourceSkillData!;
        }
        else
        {
            var root = ResolveRepoRoot(repoRoot);
            sourceSkillDataDir = Path.Combine(root, "packages", "cli", "Mohist.Cli", "skill-data");
        }

        _out.WriteLine($"Syncing skill data: {sourceSkillDataDir} -> {managedSkillData}");

        if (dryRun)
        {
            _out.WriteLine("Dry run: would synchronize managed skill assets (copy to temp dir, validate, replace managed root).");
            return 0;
        }

        var synchronizer = new ManagedAssetSynchronizer(_out, _err, _fileSystem);
        return await synchronizer.SyncAsync(sourceSkillDataDir, managedSkillData, ManagedAssetKind.Skill);
    }

    public async Task<int> BuildAndRestartServerAsync(string root, CancellationToken cancellationToken)
    {
        var (build, buildOut, buildErr) = await _commandExecutor.ExecuteAsync("dotnet", ["build", "Mohist.sln"], root);
        if (build != 0)
        {
            WriteCommandFailureOutput(buildOut, buildErr);
            _err.WriteLine("Build failed. Aborting update.");
            return build;
        }

        _out.WriteLine("Server updated successfully.");

        var restart = await _systemd.RestartServerAsync(new ServiceCommandOptions(false, null, 100, false));
        if (restart != 0)
        {
            _err.WriteLine("Warning: Failed to restart server service. You may need to restart manually.");
            return restart;
        }

        return 0;
    }

    public async Task<bool> IsRunnerInstalledAsync()
    {
        return await _systemd.IsRunnerInstalledAsync(_unitDir);
    }

    public async Task<bool> IsRunnerRunningAsync(CancellationToken cancellationToken)
    {
        return await _systemd.IsRunnerRunningAsync(cancellationToken);
    }

    public async Task<int> StopRunnerAsync(bool dryRun)
    {
        return await _systemd.StopRunnerAsync(new ServiceCommandOptions(dryRun, null, 100, false));
    }

    public async Task<int> StartRunnerAsync(bool dryRun)
    {
        return await _systemd.StartRunnerAsync(new ServiceCommandOptions(dryRun, null, 100, false));
    }

    public async Task<int> BuildRunnerAsync(string root)
    {
        var (build, buildOut, buildErr) = await _commandExecutor.ExecuteAsync("npm", ["run", "build", "-w", "packages/runner"], root);
        if (build != 0)
        {
            WriteCommandFailureOutput(buildOut, buildErr);
            _err.WriteLine("Build failed. Aborting update.");
            return build;
        }
        return 0;
    }

    public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteCommandAsync(
        string fileName,
        string[] args,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        return _commandExecutor.ExecuteAsync(fileName, args, workingDirectory, cancellationToken);
    }

    public async Task WriteServerScopeMessageAsync()
    {
        _out.WriteLine("Note: 'mo update server' did not refresh the runner build output or runner runtime.");
        _out.WriteLine("Local runner code may now be stale relative to the updated server.");
        var installed = await IsRunnerInstalledAsync();
        if (installed)
        {
            _out.WriteLine("To refresh the runner, run: mo update runner");
            _out.WriteLine("Or, to refresh CLI + server + runner together, run: mo update");
        }
        else
        {
            _out.WriteLine("No runner service is installed locally; runner refresh is not required.");
        }
    }

    internal string ResolveManagedAssetRoot(ManagedAssetKind kind)
    {
        var home = _getUserHome?.Invoke();
        if (string.IsNullOrWhiteSpace(home))
            home = _environment.GetEnvironmentVariable(SkillAssetRootResolver.HomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(home))
            return Path.Combine(home, ".mohist", "cli", kind.SourceDirectoryName);
        return Path.Combine(AppContext.BaseDirectory, kind.SourceDirectoryName);
    }

    public string ResolveRepoRoot(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
            return explicitRoot.Replace('\\', '/');

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Mohist.sln")))
                return dir.FullName.Replace('\\', '/');
            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory().Replace('\\', '/');
    }

    public async Task<string?> ResolveCliPathAsync(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath;

        var envPath = _environment.GetEnvironmentVariable(SourceCodeUpdater.CliPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envPath))
            return envPath;

        var pathEnv = _environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir, "mo");
                if (_fileSystem.Exists(candidate))
                    return candidate;
            }
        }

        var (which, stdout, _) = await _commandExecutor.ExecuteAsync("sh", ["-lc", "command -v mo"]);
        if (which == 0 && !string.IsNullOrWhiteSpace(stdout))
            return stdout.Trim().Split('\n').FirstOrDefault()?.Trim();

        return null;
    }

    public static string ResolveManagedCliPath(string? home = null)
    {
        var root = !string.IsNullOrWhiteSpace(home)
            ? home
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(root, ".local", "share", "mohist", "cli", "mo").Replace('\\', '/');
    }

    public static string ResolveAlternateManagedCliPath(string? home = null)
    {
        var root = !string.IsNullOrWhiteSpace(home)
            ? home
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(root, ".local", "share", "mohist", "cli", "mo.next").Replace('\\', '/');
    }

    public static string ResolveCliWrapperPath(string? home = null)
    {
        var root = !string.IsNullOrWhiteSpace(home)
            ? home
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(root, ".local", "bin", "mo").Replace('\\', '/');
    }

    public async Task<int> EnsureCliWrapperAsync(string managedCliPath, string? home = null)
    {
        var wrapperPath = ResolveCliWrapperPath(home);
        var wrapperDir = Path.GetDirectoryName(wrapperPath);
        if (!string.IsNullOrWhiteSpace(wrapperDir))
            _fileSystem.CreateDirectory(wrapperDir);

        var wrapper = "#!/bin/sh" + Environment.NewLine
            + $"exec \"{managedCliPath}\" \"$@\"" + Environment.NewLine;
        var tempPath = $"{wrapperPath}.tmp";

        try
        {
            await _fileSystem.WriteAllTextAsync(tempPath, wrapper);
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Could not write wrapper script to {tempPath}: {ex.Message}");
            return 1;
        }

        var (chmod, _, chmodErr) = await _commandExecutor.ExecuteAsync("chmod", ["+x", tempPath], null);
        if (chmod != 0)
        {
            if (!string.IsNullOrWhiteSpace(chmodErr)) _err.WriteLine(chmodErr);
            _err.WriteLine($"Could not make wrapper script at {tempPath} executable.");
            CleanupTempFile(tempPath);
            return chmod;
        }

        try
        {
            _fileSystem.MoveFile(tempPath, wrapperPath);
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Could not install wrapper script at {wrapperPath}: {ex.Message}");
            CleanupTempFile(tempPath);
            return 1;
        }

        _out.WriteLine($"Installed CLI wrapper: {wrapperPath}");
        return 0;
    }

    private void CleanupTempFile(string path)
    {
        try
        {
            if (_fileSystem.Exists(path))
                _fileSystem.Delete(path);
        }
        catch
        {
        }
    }

    public static string RuntimeIdentifier()
    {
        if (OperatingSystem.IsMacOS()) return "osx-x64";
        if (OperatingSystem.IsWindows()) return "win-x64";
        return "linux-x64";
    }

    public static string RestartCommandLine(string service) => service switch
    {
        "server" => "systemctl --user restart mohist.service",
        "runner" => "systemctl --user restart mohist-runner.service",
        _ => $"systemctl --user restart {service}",
    };

    private void WriteCommandFailureOutput(string stdout, string stderr)
    {
        if (!string.IsNullOrWhiteSpace(stdout)) _err.WriteLine(stdout.TrimEnd());
        if (!string.IsNullOrWhiteSpace(stderr)) _err.WriteLine(stderr.TrimEnd());
    }
}
