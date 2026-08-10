using System.Globalization;

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
    private readonly IRuntimeUpdateLeaseProvider _runtimeUpdateLeases;

    public UpdateOperations(
        TextWriter output,
        TextWriter error,
        IServiceInstaller systemd,
        ICommandExecutor commandExecutor,
        IFileSystem fileSystem,
        IEnvironmentVariableProvider environment,
        string? unitDir = null,
        Func<string?>? getUserHome = null,
        IRuntimeUpdateLeaseProvider? runtimeUpdateLeases = null)
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
        _runtimeUpdateLeases = runtimeUpdateLeases ?? new RuntimeUpdateLeaseProvider(fileSystem);
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
        Func<InstalledRuntimeArtifact, CancellationToken, Task<RuntimeIdentityVerification>> verifyRuntimeIdentity,
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

        try
        {
            _out.WriteLine("Server candidate service started; verifying runtime identity.");
            var ready = await readinessProbe.WaitForServerReadyAsync(serverReadyTimeout, cancellationToken);
            if (!ready.Ready)
            {
                _err.WriteLine($"Server candidate did not become ready within {(int)serverReadyTimeout.TotalSeconds} seconds.");
                if (!string.IsNullOrWhiteSpace(ready.LastFailure))
                    _err.WriteLine($"Last readiness error: {ready.LastFailure}");
                return await RollBackRuntimeAsync(prepared, actualHash: null, "readiness did not pass", cancellationToken);
            }

            var identity = await verifyRuntimeIdentity(prepared.Activation.Candidate, cancellationToken);
            if (!identity.Matches)
                return await RollBackRuntimeAsync(prepared, identity.ActualHash, identity.Reason, cancellationToken);

            if (!TryMarkRuntimeVerified(prepared))
                return await RollBackRuntimeAsync(prepared, identity.ActualHash, "could not record the verified server version", cancellationToken);
            _out.WriteLine($"Server runtime verification: current (expected {identity.ExpectedHash}, actual {identity.ActualHash}).");
            await WriteServerScopeMessageAsync();
            return 0;
        }
        catch
        {
            await RollBackRuntimeAsync(prepared, null, "unexpected exception during server runtime verification", CancellationToken.None);
            throw;
        }
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

        try
        {
            var outcome = await runnerRefreshVerifier.VerifyRunnerRuntimeAsync(RunnerExpectation(prepared), cancellationToken);
            outcome.WriteSummary(_out, _err);
            if (outcome.ExitCode != 0)
                return await RollBackRuntimeAsync(prepared, RunnerActualHash(outcome), RunnerFailureReason(outcome), cancellationToken);

            if (!TryMarkRuntimeVerified(prepared))
                return await RollBackRuntimeAsync(prepared, RunnerActualHash(outcome), "could not record the verified runner version", cancellationToken);
            _out.WriteLine("Runner update is verified and current.");
            return 0;
        }
        catch
        {
            await RollBackRuntimeAsync(prepared, null, "unexpected exception during runner runtime verification", CancellationToken.None);
            throw;
        }
    }

    public Task<UpdateSource?> ResolveUpdateSourceAsync(string? repoRoot, CancellationToken cancellationToken)
    {
        return _artifacts.ResolveSourceAsync(ResolveRepoRoot(repoRoot), cancellationToken);
    }

    public Task<ServiceManagerProbe> ProbeRuntimeManagerAsync(CancellationToken cancellationToken)
    {
        return _systemd.ProbeRuntimeManagerAsync(cancellationToken);
    }

    public async Task<PreparedRuntimeUpdate?> PrepareServerRuntimeAsync(
        UpdateSource source,
        ServiceInstallOptions? requestedOptions,
        CancellationToken cancellationToken,
        ServiceManagerProbe? manager = null)
    {
        return await PrepareRuntimeAsync(
            ManagedRuntimeComponent.Server,
            source,
            requestedOptions,
            cancellationToken,
            manager);
    }

    public async Task<PreparedRuntimeUpdate?> PrepareRunnerRuntimeAsync(
        UpdateSource source,
        ServiceInstallOptions? requestedOptions,
        CancellationToken cancellationToken,
        ServiceManagerProbe? manager = null)
    {
        return await PrepareRuntimeAsync(
            ManagedRuntimeComponent.Runner,
            source,
            requestedOptions,
            cancellationToken,
            manager);
    }

    /// <summary>
    /// Builds and validates a Runner artifact while holding only its scoped
    /// lease and recovery snapshot. The existing Runner service keeps running
    /// until a caller explicitly activates this candidate.
    /// </summary>
    public Task<PreparedRuntimeCandidate?> PrepareRunnerRuntimeCandidateAsync(
        UpdateSource source,
        ServiceInstallOptions? requestedOptions,
        CancellationToken cancellationToken,
        ServiceManagerProbe? manager = null)
    {
        return PrepareRuntimeCandidateAsync(
            ManagedRuntimeComponent.Runner,
            source,
            requestedOptions,
            cancellationToken,
            manager);
    }

    public async Task<PreparedRuntimeUpdate?> ActivatePreparedRuntimeAsync(
        PreparedRuntimeCandidate candidate,
        CancellationToken cancellationToken)
    {
        var component = candidate.Artifact.Component;
        var lease = candidate.TakeLease();
        PreparedRuntimeUpdate? prepared = null;
        try
        {
            RuntimeActivation activation;
            try
            {
                activation = _artifacts.Activate(candidate.Artifact);
            }
            catch (RuntimeActivationException ex)
            {
                var failedActivation = new PreparedRuntimeUpdate(
                    candidate.Source,
                    ex.Activation,
                    candidate.ServiceSnapshot,
                    candidate.CandidateOptions,
                    lease);
                var activationRecovery = await RestorePreparedRuntimeAsync(failedActivation, CancellationToken.None);
                _err.WriteLine($"{ComponentLabel(component)} runtime activation failed: expected {candidate.Source.Hash}, actual <not-started>; {ex.Message}. Recovery: {activationRecovery.Description}.");
                return null;
            }
            catch (Exception ex)
            {
                lease.Dispose();
                _err.WriteLine($"{ComponentLabel(component)} runtime activation failed: expected {candidate.Source.Hash}, actual <not-started>; {ex.Message}. Recovery: service target was left unchanged.");
                return null;
            }

            prepared = new PreparedRuntimeUpdate(
                candidate.Source,
                activation,
                candidate.ServiceSnapshot,
                candidate.CandidateOptions,
                lease);
            var install = component == ManagedRuntimeComponent.Server
                ? await _systemd.InstallServerAsync(candidate.CandidateOptions)
                : await _systemd.InstallRunnerAsync(candidate.CandidateOptions);
            if (install == 0)
                return prepared;

            var installRecovery = await RestorePreparedRuntimeAsync(prepared, CancellationToken.None);
            _err.WriteLine($"{ComponentLabel(component)} service target activation failed: expected {candidate.Source.Hash}, actual <not-started>; installer exited {install}. Recovery: {installRecovery.Description}.");
            return null;
        }
        catch (Exception ex)
        {
            if (prepared is not null)
            {
                var exceptionRecovery = await RestorePreparedRuntimeAsync(prepared, CancellationToken.None);
                _err.WriteLine($"{ComponentLabel(component)} service target activation failed: expected {candidate.Source.Hash}, actual <not-started>; {ex.Message}. Recovery: {exceptionRecovery.Description}.");
            }
            else
            {
                lease.Dispose();
            }
            return null;
        }
        finally
        {
            candidate.Dispose();
        }
    }

    public bool TryMarkRuntimeVerified(PreparedRuntimeUpdate prepared, bool retainLease = false)
    {
        try
        {
            _artifacts.MarkVerified(prepared.Activation);
            if (!TryReadBackCandidateTarget(prepared, out var reason))
                throw new InvalidOperationException(reason);
            if (!_artifacts.IsCommitted(prepared.Activation))
                throw new InvalidOperationException("current and verified links did not read back as the candidate artifact");
            if (!retainLease)
                prepared.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Could not mark {ComponentLabel(prepared.Activation.Candidate.Component)} runtime '{prepared.Source.Hash}' as verified: {ex.Message}");
            return false;
        }
    }

    internal static RunnerIdentityExpectation CreateRunnerIdentityExpectation(PreparedRuntimeUpdate prepared) =>
        RunnerExpectation(prepared);

    public async Task<int> RollBackRuntimeAsync(
        PreparedRuntimeUpdate prepared,
        string? actualHash,
        string reason,
        CancellationToken cancellationToken)
    {
        await RollBackRuntimeWithResultAsync(prepared, actualHash, reason, cancellationToken);
        return 1;
    }

    public async Task<RuntimeRollbackResult> RollBackRuntimeWithResultAsync(
        PreparedRuntimeUpdate prepared,
        string? actualHash,
        string reason,
        CancellationToken cancellationToken)
    {
        var component = prepared.Activation.Candidate.Component;
        var expected = prepared.Source.Hash;
        var actual = string.IsNullOrWhiteSpace(actualHash) ? "<unavailable>" : actualHash;
        var recovery = await RestorePreparedRuntimeAsync(prepared, CancellationToken.None);
        _err.WriteLine($"{ComponentLabel(component)} runtime verification failed: expected {expected}, actual {actual}; {reason}. Recovery: {recovery.Description}.");
        return recovery;
    }

    public async Task<int> InstallServerRuntimeAsync(
        ServiceInstallOptions options,
        TimeSpan serverReadyTimeout,
        ServiceReadinessProbe readinessProbe,
        Func<InstalledRuntimeArtifact, CancellationToken, Task<RuntimeIdentityVerification>> verifyRuntimeIdentity,
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

        try
        {
            var ready = await readinessProbe.WaitForServerReadyAsync(serverReadyTimeout, cancellationToken);
            if (!ready.Ready)
                return await RollBackRuntimeAsync(prepared, null, ready.LastFailure ?? "readiness did not pass", cancellationToken);

            var identity = await verifyRuntimeIdentity(prepared.Activation.Candidate, cancellationToken);
            if (!identity.Matches)
                return await RollBackRuntimeAsync(prepared, identity.ActualHash, identity.Reason, cancellationToken);

            if (!TryMarkRuntimeVerified(prepared))
                return await RollBackRuntimeAsync(prepared, identity.ActualHash, "could not record the verified server version", cancellationToken);
            _out.WriteLine($"Server runtime verification: current (expected {identity.ExpectedHash}, actual {identity.ActualHash}).");
            return 0;
        }
        catch
        {
            await RollBackRuntimeAsync(prepared, null, "unexpected exception during server runtime verification", CancellationToken.None);
            throw;
        }
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

        try
        {
            var outcome = await runnerRefreshVerifier.VerifyRunnerRuntimeAsync(RunnerExpectation(prepared), cancellationToken);
            outcome.WriteSummary(_out, _err);
            if (outcome.ExitCode != 0)
                return await RollBackRuntimeAsync(prepared, RunnerActualHash(outcome), RunnerFailureReason(outcome), cancellationToken);

            if (!TryMarkRuntimeVerified(prepared))
                return await RollBackRuntimeAsync(prepared, RunnerActualHash(outcome), "could not record the verified runner version", cancellationToken);
            _out.WriteLine("Runner runtime verification: current.");
            return 0;
        }
        catch
        {
            await RollBackRuntimeAsync(prepared, null, "unexpected exception during runner runtime verification", CancellationToken.None);
            throw;
        }
    }

    private async Task<PreparedRuntimeUpdate?> PrepareRuntimeAsync(
        ManagedRuntimeComponent component,
        UpdateSource source,
        ServiceInstallOptions? requestedOptions,
        CancellationToken cancellationToken,
        ServiceManagerProbe? manager = null)
    {
        var candidate = await PrepareRuntimeCandidateAsync(
            component,
            source,
            requestedOptions,
            cancellationToken,
            manager);
        if (candidate is null)
            return null;

        return await ActivatePreparedRuntimeAsync(candidate, cancellationToken);
    }

    private async Task<PreparedRuntimeCandidate?> PrepareRuntimeCandidateAsync(
        ManagedRuntimeComponent component,
        UpdateSource source,
        ServiceInstallOptions? requestedOptions,
        CancellationToken cancellationToken,
        ServiceManagerProbe? manager = null)
    {
        var componentRoot = _artifacts.ResolveComponentRoot(component);
        manager ??= await _systemd.ProbeRuntimeManagerAsync(cancellationToken);
        if (!manager.Available)
        {
            _err.WriteLine($"{ComponentLabel(component)} runtime update cannot start because the service manager is unavailable: {manager.Reason ?? "unknown reason"}. No runtime was changed.");
            return null;
        }

        var lease = _runtimeUpdateLeases.TryAcquire(component, componentRoot);
        if (lease is null)
        {
            _err.WriteLine($"{ComponentLabel(component)} update is already in progress for this installed runtime. No runtime was changed.");
            return null;
        }

        try
        {
            var snapshot = CaptureServiceSnapshot(component, source.Root, requestedOptions, dryRun: false);
            var artifact = component == ManagedRuntimeComponent.Server
                ? await _artifacts.BuildServerAsync(source, cancellationToken)
                : await _artifacts.BuildRunnerAsync(source, cancellationToken);
            if (artifact is null)
            {
                lease.Dispose();
                return null;
            }

            var candidateOptions = RuntimeServiceOptions(
                requestedOptions ?? snapshot.PreviousOptions,
                source,
                artifact,
                dryRun: false);

            return new PreparedRuntimeCandidate(source, artifact, snapshot, candidateOptions, lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private async Task<RuntimeRollbackResult> RestorePreparedRuntimeAsync(
        PreparedRuntimeUpdate prepared,
        CancellationToken cancellationToken)
    {
        try
        {
            RuntimeRecovery linkRecovery;
            try
            {
                linkRecovery = _artifacts.Restore(prepared.Activation);
            }
            catch (Exception ex)
            {
                linkRecovery = new RuntimeRecovery(
                    Restored: false,
                    RestoredCurrent: null,
                    RestoredVerified: null,
                    Failure: $"could not restore runtime links: {ex.Message}");
            }

            if (!RestoreUnitSnapshot(prepared.ServiceSnapshot, out var unitFailure))
                return new RuntimeRollbackResult(false, $"could not restore prior service unit: {unitFailure}");

            var reload = await _systemd.ReloadRuntimeManagerAsync(cancellationToken);
            if (reload != 0)
                return new RuntimeRollbackResult(false, $"restored prior unit and runtime links, but service-manager reload failed with exit {reload}");

            if (!linkRecovery.Restored)
            {
                var stop = await StopRuntimeAsync(prepared.Activation.Candidate.Component);
                return stop == 0
                    ? new RuntimeRollbackResult(false, $"service unit was restored but runtime link recovery was not confirmed ({linkRecovery.Failure}); stopped the candidate service target")
                    : new RuntimeRollbackResult(false, $"service unit was restored but runtime link recovery was not confirmed ({linkRecovery.Failure}); stopping the candidate service target failed with exit {stop}");
            }
            if (!SnapshotReadBackMatches(prepared.ServiceSnapshot))
                return new RuntimeRollbackResult(false, "restored service target did not read back as the pre-update unit");

            var component = prepared.Activation.Candidate.Component;
            var serviceAction = prepared.ServiceSnapshot.UnitExisted
                ? await RestartRuntimeAsync(component)
                : await StopRuntimeAsync(component);
            if (serviceAction != 0)
                return new RuntimeRollbackResult(false, $"restored prior unit and runtime links, but service action failed with exit {serviceAction}");

            if (linkRecovery.RestoredVerified is not null)
                return new RuntimeRollbackResult(true, $"restored verified version {linkRecovery.RestoredVerified.SourceHash} and its prior service target");
            if (linkRecovery.RestoredCurrent is not null)
                return new RuntimeRollbackResult(true, $"restored prior current version {linkRecovery.RestoredCurrent.SourceHash} and its prior service target");
            return prepared.ServiceSnapshot.UnitExisted
                ? new RuntimeRollbackResult(true, "restored prior local-source service target with no verified runtime version")
                : new RuntimeRollbackResult(true, "no prior service target existed; stopped candidate service target");
        }
        finally
        {
            prepared.Dispose();
        }
    }

    private bool TryReadBackCandidateTarget(PreparedRuntimeUpdate prepared, out string reason)
    {
        reason = string.Empty;
        var unitPath = prepared.ServiceSnapshot.UnitPath;
        if (!_fileSystem.Exists(unitPath))
        {
            reason = "candidate service unit is missing after installation";
            return false;
        }

        string unit;
        try
        {
            unit = _fileSystem.ReadAllText(unitPath);
        }
        catch (Exception ex)
        {
            reason = $"candidate service unit could not be read: {ex.Message}";
            return false;
        }

        var artifact = prepared.Activation.Candidate;
        var runtimeRoot = artifact.ComponentRoot.Replace('\\', '/');
        if (!unit.Contains($"WorkingDirectory={runtimeRoot}", StringComparison.Ordinal)
            || !unit.Contains(runtimeRoot + "/current", StringComparison.Ordinal))
        {
            reason = "candidate service unit does not target the installed current runtime";
            return false;
        }

        if (artifact.Component == ManagedRuntimeComponent.Server)
        {
            if (!unit.Contains(runtimeRoot + "/current/Mohist.Server.dll", StringComparison.Ordinal))
            {
                reason = "candidate server unit does not use the installed server entry point";
                return false;
            }
            return true;
        }

        if (!unit.Contains(runtimeRoot + "/current/dist/cli.js", StringComparison.Ordinal)
            || !UnitEnvironmentMatches(unit, "RUNNER_ID", prepared.CandidateOptions.RunnerId)
            || !UnitEnvironmentMatches(unit, "MOHIST_RUNTIME_GENERATION", prepared.CandidateOptions.RuntimeGeneration)
            || !UnitEnvironmentMatches(unit, "MOHIST_RUNTIME_SESSION_TOKEN", prepared.CandidateOptions.RuntimeSessionToken)
            || !UnitEnvironmentMatches(unit, "MOHIST_ARTIFACT_DIGEST", artifact.ArtifactDigest))
        {
            reason = "candidate runner unit does not read back with its exact runtime identity";
            return false;
        }
        return true;
    }

    private static bool UnitEnvironmentMatches(string unit, string key, string? expected) =>
        !string.IsNullOrWhiteSpace(expected)
        && unit.Contains($"Environment=\"{key}={expected}\"", StringComparison.Ordinal);

    private static RunnerIdentityExpectation RunnerExpectation(PreparedRuntimeUpdate prepared)
    {
        var options = prepared.CandidateOptions;
        if (string.IsNullOrWhiteSpace(options.RunnerId)
            || string.IsNullOrWhiteSpace(options.RuntimeGeneration))
        {
            throw new InvalidOperationException("runner candidate was installed without a durable runner identity");
        }
        return new RunnerIdentityExpectation(
            options.RunnerId,
            options.RuntimeGeneration,
            prepared.Activation.Candidate.SourceHash,
            prepared.Activation.Candidate.ArtifactDigest);
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
        return await _systemd.IsRunnerInstalledAsync(ResolveUnitDirectory(_unitDir));
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
        var options = baseline with
        {
            DryRun = dryRun,
            RepoRoot = source.Root,
            RuntimeRoot = artifact.ComponentRoot,
        };
        if (artifact.Component != ManagedRuntimeComponent.Runner)
            return options;

        return options with
        {
            RunnerId = string.IsNullOrWhiteSpace(options.RunnerId)
                ? GenerateRunnerId()
                : options.RunnerId,
            RuntimeGeneration = NextRuntimeGeneration(options.RuntimeGeneration),
            RuntimeSessionToken = GenerateRuntimeSessionToken(),
            ArtifactDigest = artifact.ArtifactDigest,
        };
    }

    private RuntimeServiceSnapshot CaptureServiceSnapshot(
        ManagedRuntimeComponent component,
        string sourceRoot,
        ServiceInstallOptions? requestedOptions,
        bool dryRun)
    {
        var unitDir = requestedOptions?.UnitDir ?? _unitDir;
        var unitName = component == ManagedRuntimeComponent.Server ? "mohist.service" : "mohist-runner.service";
        var unitPath = Path.Combine(ResolveUnitDirectory(unitDir), unitName);
        var unitExisted = _fileSystem.Exists(unitPath);
        string? unitContents = null;
        if (unitExisted)
            unitContents = _fileSystem.ReadAllText(unitPath);

        var existing = ExistingRuntimeServiceOptions(component, sourceRoot, dryRun, unitDir);
        var baseline = requestedOptions is null
            ? existing
            : existing with
            {
                DryRun = dryRun,
                UnitDir = requestedOptions.UnitDir ?? existing.UnitDir,
                RepoRoot = requestedOptions.RepoRoot ?? sourceRoot,
                ListenUrl = requestedOptions.ListenUrl ?? existing.ListenUrl,
                ServerUrl = requestedOptions.ServerUrl ?? existing.ServerUrl,
                RunnerRoot = requestedOptions.RunnerRoot ?? existing.RunnerRoot,
                EnrollmentToken = requestedOptions.EnrollmentToken,
                RunnerId = requestedOptions.RunnerId ?? existing.RunnerId,
                RuntimeGeneration = requestedOptions.RuntimeGeneration ?? existing.RuntimeGeneration,
                RuntimeSessionToken = requestedOptions.RuntimeSessionToken ?? existing.RuntimeSessionToken,
            };
        return new RuntimeServiceSnapshot(unitPath, unitExisted, unitContents, baseline);
    }

    private ServiceInstallOptions ExistingRuntimeServiceOptions(
        ManagedRuntimeComponent component,
        string sourceRoot,
        bool dryRun,
        string? unitDir = null)
    {
        var unitName = component == ManagedRuntimeComponent.Server ? "mohist.service" : "mohist-runner.service";
        var unitPath = Path.Combine(ResolveUnitDirectory(unitDir), unitName);
        var serverUrl = component == ManagedRuntimeComponent.Runner
            ? ReadUnitEnvironment(unitPath, "SERVER_URL")
            : null;
        var runnerRoot = component == ManagedRuntimeComponent.Runner
            ? ReadUnitEnvironment(unitPath, "RUNNER_ROOT")
            : null;
        var runnerId = component == ManagedRuntimeComponent.Runner
            ? ReadUnitEnvironment(unitPath, "RUNNER_ID")
            : null;
        var runtimeGeneration = component == ManagedRuntimeComponent.Runner
            ? ReadUnitEnvironment(unitPath, "MOHIST_RUNTIME_GENERATION")
            : null;
        var runtimeSessionToken = component == ManagedRuntimeComponent.Runner
            ? ReadUnitEnvironment(unitPath, "MOHIST_RUNTIME_SESSION_TOKEN")
            : null;
        return new ServiceInstallOptions(
            DryRun: dryRun,
            UnitDir: ResolveUnitDirectory(unitDir ?? _unitDir),
            RepoRoot: sourceRoot,
            ListenUrl: component == ManagedRuntimeComponent.Server ? ReadServerListenUrl(unitPath) : null,
            ServerUrl: serverUrl,
            RunnerRoot: runnerRoot,
            RunnerId: runnerId,
            RuntimeGeneration: runtimeGeneration,
            RuntimeSessionToken: runtimeSessionToken);
    }

    private string ResolveUnitDirectory(string? preferredUnitDir = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredUnitDir))
            return preferredUnitDir!;
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

    private string? ReadServerListenUrl(string unitPath)
    {
        if (!_fileSystem.Exists(unitPath))
            return null;

        try
        {
            var unit = _fileSystem.ReadAllText(unitPath);
            var exec = SystemdUnitParser.ParseSystemdUnit(unit).ExecStart;
            if (string.IsNullOrWhiteSpace(exec))
                return null;
            var marker = exec.IndexOf("--urls", StringComparison.Ordinal);
            if (marker < 0)
                return null;
            var value = exec[(marker + "--urls".Length)..].TrimStart();
            if (value.StartsWith('\''))
            {
                var close = value.IndexOf('\'', 1);
                return close > 0 ? value[1..close] : null;
            }
            if (value.StartsWith('"'))
            {
                var close = value.IndexOf('"', 1);
                return close > 0 ? value[1..close] : null;
            }
            return value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string GenerateRunnerId() => "runner-" + Guid.NewGuid().ToString("N");

    private static string GenerateRuntimeSessionToken() => Guid.NewGuid().ToString("N");

    private static string NextRuntimeGeneration(string? previous)
    {
        if (ulong.TryParse(previous, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            && parsed < ulong.MaxValue)
        {
            return (parsed + 1).ToString(CultureInfo.InvariantCulture);
        }

        return "1";
    }

    private bool RestoreUnitSnapshot(RuntimeServiceSnapshot snapshot, out string? failure)
    {
        failure = null;
        try
        {
            if (snapshot.UnitExisted)
            {
                _fileSystem.WriteAllText(snapshot.UnitPath, snapshot.UnitContents ?? string.Empty);
                return true;
            }
            if (_fileSystem.Exists(snapshot.UnitPath))
                _fileSystem.Delete(snapshot.UnitPath);
            return true;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            return false;
        }
    }

    private bool SnapshotReadBackMatches(RuntimeServiceSnapshot snapshot)
    {
        try
        {
            if (_fileSystem.Exists(snapshot.UnitPath) != snapshot.UnitExisted)
                return false;
            return !snapshot.UnitExisted
                || string.Equals(_fileSystem.ReadAllText(snapshot.UnitPath), snapshot.UnitContents, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
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
