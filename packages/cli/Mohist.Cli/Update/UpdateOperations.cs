namespace Mohist.Cli;

internal sealed partial class UpdateOperations
{
    private static readonly TimeSpan SlackActivationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SlackActivationPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly IServiceInstaller _systemd;
    private readonly ICommandExecutor _commandExecutor;
    private readonly IFileSystem _fileSystem;
    private readonly IEnvironmentVariableProvider _environment;
    private readonly string? _unitDir;
    private readonly Func<string?>? _getUserHome;
    private readonly ManagedRuntimeTransaction? _managedRuntime;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _pollWait;

    public UpdateOperations(
        TextWriter output,
        TextWriter error,
        IServiceInstaller systemd,
        ICommandExecutor commandExecutor,
        IFileSystem fileSystem,
        IEnvironmentVariableProvider environment,
        string? unitDir = null,
        Func<string?>? getUserHome = null,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? pollWait = null)
    {
        _out = output;
        _err = error;
        _systemd = systemd;
        _commandExecutor = commandExecutor;
        _fileSystem = fileSystem;
        _environment = environment;
        _unitDir = unitDir;
        _getUserHome = getUserHome;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pollWait = pollWait
            ?? ((delay, cancellationToken) => Task.Delay(delay, _timeProvider, cancellationToken));
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
        Func<CancellationToken, Task<string?>>? beforeActivation = null,
        CancellationToken cancellationToken = default)
    {
        if (_managedRuntime is null)
            return (null, "service installer does not support managed runtime activation");
        return await _managedRuntime.PrepareAsync(
            repoRoot,
            scope,
            transactionId,
            cliPath,
            beforeActivation,
            cancellationToken);
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

        try
        {
            var restart = await _systemd.RestartRunnerAsync(new ServiceCommandOptions(false, null, 100, false));
            if (restart != 0)
            {
                _err.WriteLine("Warning: Failed to restart runner service. You may need to restart manually.");
                await ReleaseRunnerUpdateInterruptAsync(runnerRefreshVerifier, interruption);
                return restart;
            }

            _out.WriteLine("Runner service restarted.");

            var outcome = await runnerRefreshVerifier.VerifyRunnerRuntimeAsync(root);
            outcome.WriteSummary(_out, _err);
            if (outcome.ExitCode != 0)
            {
                _err.WriteLine("Runner update recovery: status=unconfirmed; refreshed runner identity was not confirmed.");
                await ReleaseRunnerUpdateInterruptAsync(runnerRefreshVerifier, interruption);
                return outcome.ExitCode;
            }

            _out.WriteLine("Runner updated successfully.");
            return 0;
        }
        catch
        {
            await ReleaseRunnerUpdateInterruptAsync(runnerRefreshVerifier, interruption);
            throw;
        }
    }

    private async Task ReleaseRunnerUpdateInterruptAsync(
        RunnerRefreshVerifier runnerRefreshVerifier,
        RunnerInterruptResult interruption)
    {
        var releaseError = await runnerRefreshVerifier.CancelRunnerUpdateInterruptAsync(
            interruption,
            CancellationToken.None);
        if (releaseError is null)
        {
            _out.WriteLine(
                $"Runner update interrupt rollback: status=cancelled runnerId={interruption.RunnerId}.");
            return;
        }

        _err.WriteLine(
            $"Runner update interrupt rollback: status=unconfirmed ({releaseError}); runner admission may remain closed.");
    }

    public async Task<int> UpdateSlackAsync(string? repoRoot, bool dryRun, CancellationToken cancellationToken = default)
    {
        var root = ResolveRepoRoot(repoRoot);
        _out.WriteLine($"Updating Slack adapter from source: {root}");
        var adapterDir = Path.Combine(root, "packages", "go", "mohist-slack");
        var stagingOutput = Path.Combine("bin", ".update") + Path.DirectorySeparatorChar;
        var stagingDir = Path.Combine(adapterDir, "bin", ".update");
        var recoveryMarker = Path.Combine(stagingDir, "recovery-required");
        var binaryName = _systemd.SlackBinaryName;
        var stagedBinary = Path.Combine(stagingDir, binaryName);
        var backupBinary = Path.Combine(stagingDir, $"{binaryName}.previous");
        var installedBinary = Path.Combine(adapterDir, "bin", binaryName);
        if (dryRun)
        {
            _out.WriteLine("Dry run: would execute:");
            _out.WriteLine($"  cd {adapterDir} && go build -tags netgo,osusergo -buildvcs=false -o {stagingOutput} ./cmd/mohist-slack");
            _out.WriteLine("  mo service stop slack (if installed)");
            _out.WriteLine($"  replace {installedBinary} from the staged binary");
            _out.WriteLine("  refresh the installed Slack service launcher");
            _out.WriteLine("  mo service start slack");
            return 0;
        }
        var recoveryDirectory = ResolveSlackRecoveryDirectory();
        var globalRecoveryMarker = Path.Combine(recoveryDirectory, "recovery-required");
        using var transactionLock = _fileSystem.TryAcquireFileLock(
            Path.Combine(recoveryDirectory, "transaction.lock"));
        if (transactionLock is null)
        {
            _err.WriteLine("Another Slack install or update is already running for this user.");
            return 1;
        }
        var snapshotId = CreateSlackRecoverySnapshotId(installedBinary);
        var serviceSnapshotPath = ResolveSlackRecoverySnapshotPath(recoveryDirectory, snapshotId);

        if (_fileSystem.Exists(globalRecoveryMarker) && !_fileSystem.Exists(recoveryMarker))
        {
            _err.WriteLine("A Slack update transaction from another repository is unresolved. Complete recovery from its original repository before retrying.");
            return 1;
        }
        if (_fileSystem.Exists(recoveryMarker))
        {
            return await RecoverInterruptedSlackUpdateAsync(
                stagingDir,
                recoveryMarker,
                recoveryDirectory,
                root,
                installedBinary,
                stagedBinary,
                backupBinary,
                globalRecoveryMarker,
                cancellationToken);
        }
        if (_fileSystem.Exists(serviceSnapshotPath)
            || _fileSystem.Exists(Path.Combine(stagingDir, "mohist-slack.previous"))
            || _fileSystem.Exists(Path.Combine(stagingDir, "mohist-slack.exe.previous")))
        {
            _err.WriteLine($"Slack recovery files exist without a valid transaction manifest. Preserve and inspect {stagingDir} before retrying.");
            return 1;
        }
        if (_fileSystem.DirectoryExists(stagingDir))
            _fileSystem.DeleteDirectory(stagingDir);

        if (!await _systemd.IsSlackInstalledAsync(_unitDir))
        {
            _out.WriteLine("Slack refresh skipped: slack service is not installed");
            return 0;
        }
        var preserveStaging = false;
        try
        {
            // Mohist does not consume Go's embedded VCS stamp, and linked worktrees
            // can place Git metadata outside the source tree.
            var (build, buildOut, buildErr) = await _commandExecutor.ExecuteAsync(
                "go", ["build", "-tags", "netgo,osusergo", "-buildvcs=false", "-o", stagingOutput, "./cmd/mohist-slack"], adapterDir, cancellationToken);
            if (build != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteCommandFailureOutput(buildOut, buildErr);
                _err.WriteLine("Build failed. Aborting update.");
                return build;
            }
            if (!_fileSystem.Exists(stagedBinary))
            {
                _err.WriteLine($"Build completed without producing {stagedBinary}.");
                return 1;
            }

            var hasBackup = _fileSystem.Exists(installedBinary);
            if (hasBackup)
            {
                try
                {
                    _fileSystem.CopyFileDurable(installedBinary, backupBinary);
                }
                catch (Exception ex)
                {
                    _err.WriteLine($"Slack binary backup failed: {ex.Message}");
                    return 1;
                }
            }

            SlackServiceSnapshot? snapshot;
            try
            {
                snapshot = await _systemd.CaptureSlackServiceAsync(_unitDir, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _err.WriteLine($"Slack service snapshot failed: {ex.Message}");
                return 1;
            }
            if (snapshot is null)
            {
                _err.WriteLine("Slack service snapshot failed. The service was not stopped.");
                return 1;
            }
            var wasNodeLauncher = IsNodeSlackServiceSnapshot(snapshot);

            cancellationToken.ThrowIfCancellationRequested();
            var recoveryManifest = PersistSlackRecoveryState(
                snapshot,
                serviceSnapshotPath,
                recoveryMarker,
                hasBackup,
                backupBinary,
                binaryName,
                snapshotId,
                wasNodeLauncher,
                globalRecoveryMarker);
            if (recoveryManifest is null) return 1;
            var requiresRollForward = wasNodeLauncher || !hasBackup;

            var serviceOptions = new ServiceCommandOptions(false, _unitDir, 100, false);
            void PreserveRecoveryFiles()
            {
                preserveStaging = true;
            }

            async Task<bool> RecoverPreviousServiceAsync(bool restoreBinary, bool restoreConfiguration, bool stopFirst)
            {
                var canStart = true;
                if (stopFirst)
                {
                    try
                    {
                        var recoveryStop = await _systemd.StopSlackAsync(serviceOptions, CancellationToken.None);
                        if (recoveryStop != 0) canStart = false;
                    }
                    catch (Exception ex)
                    {
                        _err.WriteLine($"Slack recovery stop failed: {ex.Message}");
                        canStart = false;
                    }
                }

                if (requiresRollForward || restoreConfiguration)
                {
                    try
                    {
                        var restore = await _systemd.RestoreSlackServiceAsync(snapshot, CancellationToken.None);
                        if (restore != 0) canStart = false;
                    }
                    catch (Exception ex)
                    {
                        _err.WriteLine($"Slack service rollback failed: {ex.Message}");
                        canStart = false;
                    }
                }

                if (restoreBinary && !requiresRollForward && canStart)
                {
                    try
                    {
                        if (_fileSystem.Exists(backupBinary))
                        {
                            var rollbackBinary = $"{installedBinary}.rollback.tmp";
                            _fileSystem.CopyFileDurable(backupBinary, rollbackBinary);
                            _fileSystem.MoveFile(rollbackBinary, installedBinary);
                        }
                        else canStart = false;
                    }
                    catch (Exception ex)
                    {
                        _err.WriteLine($"Slack binary rollback failed: {ex.Message}");
                        canStart = false;
                    }
                }

                if (requiresRollForward && canStart)
                {
                    try
                    {
                        if (_fileSystem.Exists(stagedBinary))
                        {
                            _fileSystem.MoveFile(stagedBinary, installedBinary);
                        }
                        else if (!_fileSystem.Exists(installedBinary))
                        {
                            throw new FileNotFoundException("The staged Go binary is unavailable for first-migration recovery.", stagedBinary);
                        }
                        var refreshRecovery = await _systemd.RefreshSlackServiceAsync(root, _unitDir, CancellationToken.None);
                        if (refreshRecovery != 0) canStart = false;
                    }
                    catch (Exception ex)
                    {
                        _err.WriteLine($"Slack first-migration recovery failed: {ex.Message}");
                        canStart = false;
                    }
                }

                if (!canStart)
                {
                    PreserveRecoveryFiles();
                    _err.WriteLine($"Slack update recovery is incomplete; staged recovery files remain at {stagingDir}.");
                    return false;
                }

                try
                {
                    var recovery = await _systemd.StartSlackAsync(serviceOptions, CancellationToken.None);
                    var running = recovery == 0 && await WaitForSlackRunningAsync(CancellationToken.None);
                    if (running)
                    {
                        if (!MarkSlackRecoveryCommitted(recoveryMarker, recoveryManifest))
                        {
                            PreserveRecoveryFiles();
                            return false;
                        }
                        _out.WriteLine(!requiresRollForward
                            ? "Previous Slack service was restarted after the failed update."
                            : "Slack first-migration recovery completed the Go launcher activation.");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _err.WriteLine($"Slack update recovery failed: {ex.Message}");
                }
                PreserveRecoveryFiles();
                _err.WriteLine($"Slack update recovery failed; staged recovery files remain at {stagingDir}.");
                return false;
            }

            int stop;
            try
            {
                stop = await _systemd.StopSlackAsync(serviceOptions, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await RecoverPreviousServiceAsync(
                    restoreBinary: false,
                    restoreConfiguration: false,
                    stopFirst: false);
                throw;
            }
            catch (Exception ex)
            {
                PreserveRecoveryFiles();
                _err.WriteLine($"Slack service stop failed with unknown state: {ex.Message}");
                return 1;
            }
            if (stop != 0)
            {
                PreserveRecoveryFiles();
                _err.WriteLine("Failed to stop Slack service. The installed binary was not replaced.");
                return stop;
            }
            try
            {
                _fileSystem.MoveFile(stagedBinary, installedBinary);
            }
            catch (Exception ex)
            {
                _err.WriteLine($"Slack binary replacement failed: {ex.Message}");
                await RecoverPreviousServiceAsync(
                    restoreBinary: false,
                    restoreConfiguration: false,
                    stopFirst: false);
                return 1;
            }

            int refresh;
            try
            {
                refresh = await _systemd.RefreshSlackServiceAsync(root, _unitDir, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await RecoverPreviousServiceAsync(
                    restoreBinary: true,
                    restoreConfiguration: true,
                    stopFirst: false);
                throw;
            }
            catch (Exception ex)
            {
                _err.WriteLine($"Slack service launcher refresh failed: {ex.Message}");
                await RecoverPreviousServiceAsync(
                    restoreBinary: true,
                    restoreConfiguration: true,
                    stopFirst: false);
                return 1;
            }
            if (refresh != 0)
            {
                _err.WriteLine("Failed to refresh the installed Slack service launcher.");
                await RecoverPreviousServiceAsync(
                    restoreBinary: true,
                    restoreConfiguration: true,
                    stopFirst: false);
                cancellationToken.ThrowIfCancellationRequested();
                return refresh;
            }

            int start;
            try
            {
                start = await _systemd.StartSlackAsync(serviceOptions, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await RecoverPreviousServiceAsync(
                    restoreBinary: true,
                    restoreConfiguration: true,
                    stopFirst: true);
                throw;
            }
            catch (Exception ex)
            {
                _err.WriteLine($"Slack service start failed: {ex.Message}");
                await RecoverPreviousServiceAsync(
                    restoreBinary: true,
                    restoreConfiguration: true,
                    stopFirst: true);
                return 1;
            }
            if (start != 0)
            {
                _err.WriteLine("Warning: Failed to start Slack service. You may need to start it manually.");
                await RecoverPreviousServiceAsync(
                    restoreBinary: true,
                    restoreConfiguration: true,
                    stopFirst: true);
                return start;
            }
            bool running;
            try
            {
                running = await WaitForSlackRunningAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await RecoverPreviousServiceAsync(
                    restoreBinary: true,
                    restoreConfiguration: true,
                    stopFirst: true);
                throw;
            }
            catch (Exception ex)
            {
                _err.WriteLine($"Slack service activation check failed: {ex.Message}");
                running = false;
            }
            if (!running)
            {
                _err.WriteLine("Slack service did not remain running after start.");
                await RecoverPreviousServiceAsync(
                    restoreBinary: true,
                    restoreConfiguration: true,
                    stopFirst: true);
                return 1;
            }
            if (!MarkSlackRecoveryCommitted(recoveryMarker, recoveryManifest))
            {
                PreserveRecoveryFiles();
                return 1;
            }
            _out.WriteLine("Slack adapter updated and service launcher refreshed.");
            return 0;
        }
        finally
        {
            if (!preserveStaging)
            {
                try
                {
                    if (_fileSystem.Exists(globalRecoveryMarker))
                        _fileSystem.Delete(globalRecoveryMarker);
                    if (_fileSystem.Exists(serviceSnapshotPath))
                        _fileSystem.Delete(serviceSnapshotPath);
                    if (_fileSystem.DirectoryExists(stagingDir))
                        _fileSystem.DeleteDirectory(stagingDir);
                }
                catch (Exception ex)
                {
                    _err.WriteLine($"Slack recovery cleanup was deferred: {ex.Message}");
                }
            }
        }
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
            return Path.GetFullPath(explicitRoot, _fileSystem.CurrentDirectory).Replace('\\', '/');

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

    public Task<string?> ResolveManagedCliPathAsync(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var normalized = explicitPath.Replace('\\', '/');
            if (!Path.IsPathRooted(normalized) || !_fileSystem.Exists(normalized))
                return Task.FromResult<string?>(null);
            return Task.FromResult<string?>(normalized);
        }

        return Task.FromResult<string?>(ResolveManagedCliLauncherPath());
    }

    public string ResolveManagedCliLauncherPath() =>
        ResolveCliWrapperPath(_getUserHome?.Invoke());

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
        var launcher = new ManagedCliLauncher(_out, _err, _commandExecutor, _fileSystem);
        return await launcher.InstallAsync(wrapperPath, managedCliPath);
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
