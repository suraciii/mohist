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
    private readonly InstalledRuntimeArtifacts _artifacts;

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
        _artifacts = new InstalledRuntimeArtifacts(error, commandExecutor, fileSystem, environment, getUserHome);
    }

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
        Func<string, CancellationToken, Task<RuntimeIdentityVerification>> verifyRuntimeIdentity,
        CancellationToken cancellationToken = default)
    {
        var root = ResolveRepoRoot(repoRoot);

        _out.WriteLine($"Updating server from source: {root}");

        if (dryRun)
        {
            _out.WriteLine("Dry run: would execute:");
            _out.WriteLine($"  cd {root} && git rev-parse HEAD");
            _out.WriteLine($"  cd {root} && dotnet publish packages/server/src/Mohist.Server/Mohist.Server.csproj -c Release -o <stable-server-version>");
            _out.WriteLine("  point the absolute server service target at the installed current version");
            _out.WriteLine($"  {RestartCommandLine("server")}");
            _out.WriteLine("  wait for /api/health, /, and referenced /assets/* response headers readiness checks");
            _out.WriteLine("  compare the running server gitHash with the source hash before reporting success");
            await WriteServerScopeMessageAsync();
            return 0;
        }

        var source = await _artifacts.ResolveSourceAsync(root, cancellationToken);
        if (source is null)
            return 1;
        var prepared = await PrepareServerRuntimeAsync(source, null, cancellationToken);
        if (prepared is null)
            return 1;

        _out.WriteLine("Server candidate service started; verifying runtime identity.");
        var ready = await readinessProbe.WaitForServerReadyAsync(serverReadyTimeout, cancellationToken);
        if (!ready.Ready)
        {
            _err.WriteLine($"Server candidate did not become ready within {(int)serverReadyTimeout.TotalSeconds} seconds.");
            if (!string.IsNullOrWhiteSpace(ready.LastFailure))
                _err.WriteLine($"Last readiness error: {ready.LastFailure}");
            return await RollBackRuntimeAsync(prepared, actualHash: null, "readiness did not pass", cancellationToken);
        }

        var identity = await verifyRuntimeIdentity(source.Hash, cancellationToken);
        if (!identity.Matches)
            return await RollBackRuntimeAsync(prepared, identity.ActualHash, identity.Reason, cancellationToken);

        if (!TryMarkRuntimeVerified(prepared))
            return await RollBackRuntimeAsync(prepared, identity.ActualHash, "could not record the verified server version", cancellationToken);
        _out.WriteLine($"Server runtime verification: current (expected {identity.ExpectedHash}, actual {identity.ActualHash}).");
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
            _out.WriteLine($"  cd {root} && git rev-parse HEAD");
            _out.WriteLine($"  cd {root} && npm run build -w packages/runner");
            _out.WriteLine("  install runner dist and dependencies into a stable versioned runtime directory");
            _out.WriteLine($"  {RestartCommandLine("runner")}");
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

        var source = await _artifacts.ResolveSourceAsync(root, cancellationToken);
        if (source is null)
            return 1;
        var prepared = await PrepareRunnerRuntimeAsync(source, null, cancellationToken);
        if (prepared is null)
            return 1;

        var outcome = await runnerRefreshVerifier.VerifyRunnerRuntimeAsync(source.Hash);
        outcome.WriteSummary(_out, _err);
        if (outcome.ExitCode != 0)
            return await RollBackRuntimeAsync(prepared, RunnerActualHash(outcome), RunnerFailureReason(outcome), cancellationToken);

        if (!TryMarkRuntimeVerified(prepared))
            return await RollBackRuntimeAsync(prepared, RunnerActualHash(outcome), "could not record the verified runner version", cancellationToken);
        _out.WriteLine("Runner update is verified and current.");
        return 0;
    }

    public Task<UpdateSource?> ResolveUpdateSourceAsync(string? repoRoot, CancellationToken cancellationToken)
    {
        return _artifacts.ResolveSourceAsync(ResolveRepoRoot(repoRoot), cancellationToken);
    }

    public async Task<PreparedRuntimeUpdate?> PrepareServerRuntimeAsync(
        UpdateSource source,
        ServiceInstallOptions? requestedOptions,
        CancellationToken cancellationToken)
    {
        var artifact = await _artifacts.BuildServerAsync(source, cancellationToken);
        if (artifact is null)
            return null;

        RuntimeActivation activation;
        try
        {
            activation = _artifacts.Activate(artifact);
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Server runtime activation failed: expected {source.Hash}, actual <not-started>; {ex.Message}. Recovery: service target was left unchanged.");
            return null;
        }
        var options = RuntimeServiceOptions(requestedOptions, source, artifact, dryRun: false);
        int install;
        try
        {
            install = await _systemd.InstallServerAsync(options);
        }
        catch (Exception ex)
        {
            var recovery = await RestoreAfterInstallFailureAsync(activation, ManagedRuntimeComponent.Server);
            _err.WriteLine($"Server service target activation failed: expected {source.Hash}, actual <not-started>; {ex.Message}. Recovery: {recovery}.");
            return null;
        }
        if (install == 0)
            return new PreparedRuntimeUpdate(source, activation);

        var serverRecovery = await RestoreAfterInstallFailureAsync(activation, ManagedRuntimeComponent.Server);
        _err.WriteLine($"Server service target activation failed: expected {source.Hash}, actual <not-started>; installer exited {install}. Recovery: {serverRecovery}.");
        return null;
    }

    public async Task<PreparedRuntimeUpdate?> PrepareRunnerRuntimeAsync(
        UpdateSource source,
        ServiceInstallOptions? requestedOptions,
        CancellationToken cancellationToken)
    {
        var artifact = await _artifacts.BuildRunnerAsync(source, cancellationToken);
        if (artifact is null)
            return null;

        RuntimeActivation activation;
        try
        {
            activation = _artifacts.Activate(artifact);
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Runner runtime activation failed: expected {source.Hash}, actual <not-started>; {ex.Message}. Recovery: service target was left unchanged.");
            return null;
        }
        var options = RuntimeServiceOptions(requestedOptions, source, artifact, dryRun: false);
        int install;
        try
        {
            install = await _systemd.InstallRunnerAsync(options);
        }
        catch (Exception ex)
        {
            var recovery = await RestoreAfterInstallFailureAsync(activation, ManagedRuntimeComponent.Runner);
            _err.WriteLine($"Runner service target activation failed: expected {source.Hash}, actual <not-started>; {ex.Message}. Recovery: {recovery}.");
            return null;
        }
        if (install == 0)
            return new PreparedRuntimeUpdate(source, activation);

        var runnerRecovery = await RestoreAfterInstallFailureAsync(activation, ManagedRuntimeComponent.Runner);
        _err.WriteLine($"Runner service target activation failed: expected {source.Hash}, actual <not-started>; installer exited {install}. Recovery: {runnerRecovery}.");
        return null;
    }

    public bool TryMarkRuntimeVerified(PreparedRuntimeUpdate prepared)
    {
        try
        {
            _artifacts.MarkVerified(prepared.Activation);
            return true;
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Could not mark {ComponentLabel(prepared.Activation.Candidate.Component)} runtime '{prepared.Source.Hash}' as verified: {ex.Message}");
            return false;
        }
    }

    public async Task<int> RollBackRuntimeAsync(
        PreparedRuntimeUpdate prepared,
        string? actualHash,
        string reason,
        CancellationToken cancellationToken)
    {
        var component = prepared.Activation.Candidate.Component;
        var restored = _artifacts.Restore(prepared.Activation);
        var expected = prepared.Source.Hash;
        var actual = string.IsNullOrWhiteSpace(actualHash) ? "<unavailable>" : actualHash;
        if (!restored)
        {
            await StopRuntimeAsync(component);
            _err.WriteLine($"{ComponentLabel(component)} runtime verification failed: expected {expected}, actual {actual}; {reason}. Recovery: no verified version existed, stopped the candidate service target.");
            return 1;
        }

        var restart = await RestartRuntimeAsync(component);
        var recovery = restart == 0
            ? $"restored verified version {prepared.Activation.PreviousVerified!.SourceHash}"
            : $"restored verified version {prepared.Activation.PreviousVerified!.SourceHash}, but its service restart failed with exit {restart}";
        _err.WriteLine($"{ComponentLabel(component)} runtime verification failed: expected {expected}, actual {actual}; {reason}. Recovery: {recovery}.");
        return 1;
    }

    public async Task<int> InstallServerRuntimeAsync(
        ServiceInstallOptions options,
        TimeSpan serverReadyTimeout,
        ServiceReadinessProbe readinessProbe,
        Func<string, CancellationToken, Task<RuntimeIdentityVerification>> verifyRuntimeIdentity,
        CancellationToken cancellationToken)
    {
        if (options.DryRun)
        {
            var root = ResolveRepoRoot(options.RepoRoot);
            _out.WriteLine($"Dry run: would resolve source hash from {root}, publish an immutable server version, and install an absolute current service target.");
            return 0;
        }

        var source = await ResolveUpdateSourceAsync(options.RepoRoot, cancellationToken);
        if (source is null)
            return 1;
        var prepared = await PrepareServerRuntimeAsync(source, options, cancellationToken);
        if (prepared is null)
            return 1;

        var ready = await readinessProbe.WaitForServerReadyAsync(serverReadyTimeout, cancellationToken);
        if (!ready.Ready)
            return await RollBackRuntimeAsync(prepared, null, ready.LastFailure ?? "readiness did not pass", cancellationToken);

        var identity = await verifyRuntimeIdentity(source.Hash, cancellationToken);
        if (!identity.Matches)
            return await RollBackRuntimeAsync(prepared, identity.ActualHash, identity.Reason, cancellationToken);

        if (!TryMarkRuntimeVerified(prepared))
            return await RollBackRuntimeAsync(prepared, identity.ActualHash, "could not record the verified server version", cancellationToken);
        _out.WriteLine($"Server runtime verification: current (expected {identity.ExpectedHash}, actual {identity.ActualHash}).");
        return 0;
    }

    public async Task<int> InstallRunnerRuntimeAsync(
        ServiceInstallOptions options,
        RunnerRefreshVerifier runnerRefreshVerifier,
        CancellationToken cancellationToken)
    {
        if (options.DryRun)
        {
            var root = ResolveRepoRoot(options.RepoRoot);
            _out.WriteLine($"Dry run: would resolve source hash from {root}, install an immutable runner version, and install an absolute current service target.");
            return 0;
        }

        var source = await ResolveUpdateSourceAsync(options.RepoRoot, cancellationToken);
        if (source is null)
            return 1;
        var prepared = await PrepareRunnerRuntimeAsync(source, options, cancellationToken);
        if (prepared is null)
            return 1;

        var outcome = await runnerRefreshVerifier.VerifyRunnerRuntimeAsync(source.Hash);
        outcome.WriteSummary(_out, _err);
        if (outcome.ExitCode != 0)
            return await RollBackRuntimeAsync(prepared, RunnerActualHash(outcome), RunnerFailureReason(outcome), cancellationToken);

        if (!TryMarkRuntimeVerified(prepared))
            return await RollBackRuntimeAsync(prepared, RunnerActualHash(outcome), "could not record the verified runner version", cancellationToken);
        _out.WriteLine("Runner runtime verification: current.");
        return 0;
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

    private ServiceInstallOptions RuntimeServiceOptions(
        ServiceInstallOptions? requestedOptions,
        UpdateSource source,
        InstalledRuntimeArtifact artifact,
        bool dryRun)
    {
        var baseline = requestedOptions ?? ExistingRuntimeServiceOptions(artifact.Component, source.Root, dryRun);
        return baseline with
        {
            DryRun = dryRun,
            RepoRoot = source.Root,
            RuntimeRoot = artifact.ComponentRoot,
        };
    }

    private ServiceInstallOptions ExistingRuntimeServiceOptions(
        ManagedRuntimeComponent component,
        string sourceRoot,
        bool dryRun)
    {
        var unitName = component == ManagedRuntimeComponent.Server ? "mohist.service" : "mohist-runner.service";
        var unitPath = Path.Combine(ResolveUnitDirectory(), unitName);
        var serverUrl = component == ManagedRuntimeComponent.Runner
            ? ReadUnitEnvironment(unitPath, "SERVER_URL")
            : null;
        var runnerRoot = component == ManagedRuntimeComponent.Runner
            ? ReadUnitEnvironment(unitPath, "RUNNER_ROOT")
            : null;
        return new ServiceInstallOptions(
            DryRun: dryRun,
            UnitDir: _unitDir,
            RepoRoot: sourceRoot,
            ListenUrl: null,
            ServerUrl: serverUrl,
            RunnerRoot: runnerRoot);
    }

    private string ResolveUnitDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_unitDir))
            return _unitDir!;
        var home = _getUserHome?.Invoke() ?? _environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "systemd", "user");
    }

    private string? ReadUnitEnvironment(string unitPath, string key)
    {
        if (!_fileSystem.Exists(unitPath))
            return null;

        try
        {
            var prefix = $"Environment=\"{key}=";
            foreach (var line in _fileSystem.ReadAllText(unitPath).Split('\n'))
            {
                if (!line.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                var value = line[prefix.Length..];
                var closingQuote = value.LastIndexOf('"');
                return closingQuote >= 0 ? value[..closingQuote] : value;
            }
        }
        catch
        {
        }

        return null;
    }

    private async Task<string> RestoreAfterInstallFailureAsync(RuntimeActivation activation, ManagedRuntimeComponent component)
    {
        if (_artifacts.Restore(activation))
        {
            var restart = await RestartRuntimeAsync(component);
            return restart == 0
                ? $"restored verified version {activation.PreviousVerified!.SourceHash}"
                : $"restored verified version {activation.PreviousVerified!.SourceHash}, but its service restart failed with exit {restart}";
        }

        await StopRuntimeAsync(component);
        return "no verified version existed; stopped candidate service target";
    }

    private Task<int> RestartRuntimeAsync(ManagedRuntimeComponent component) => component switch
    {
        ManagedRuntimeComponent.Server => _systemd.RestartServerAsync(new ServiceCommandOptions(false, null, 100, false)),
        ManagedRuntimeComponent.Runner => _systemd.RestartRunnerAsync(new ServiceCommandOptions(false, null, 100, false)),
        _ => throw new ArgumentOutOfRangeException(nameof(component)),
    };

    private Task<int> StopRuntimeAsync(ManagedRuntimeComponent component) => component switch
    {
        ManagedRuntimeComponent.Server => _systemd.StopServerAsync(new ServiceCommandOptions(false, null, 100, false)),
        ManagedRuntimeComponent.Runner => _systemd.StopRunnerAsync(new ServiceCommandOptions(false, null, 100, false)),
        _ => throw new ArgumentOutOfRangeException(nameof(component)),
    };

    private static string ComponentLabel(ManagedRuntimeComponent component) => component switch
    {
        ManagedRuntimeComponent.Server => "Server",
        ManagedRuntimeComponent.Runner => "Runner",
        _ => throw new ArgumentOutOfRangeException(nameof(component)),
    };

    private static string? RunnerActualHash(RunnerRefreshOutcome outcome) => outcome switch
    {
        RunnerRefreshOutcome.StaleRunnerRuntime stale => stale.ReportedHash,
        _ => null,
    };

    private static string RunnerFailureReason(RunnerRefreshOutcome outcome) => outcome switch
    {
        RunnerRefreshOutcome.UnknownIdentity unknown => unknown.Reason,
        RunnerRefreshOutcome.StaleRunnerRuntime stale => stale.Reason,
        RunnerRefreshOutcome.NotReconnected => "runner did not reconnect with a runtime identity",
        _ => "runner runtime verification did not pass",
    };

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
