using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mohist.Cli;

internal sealed record ManagedUpdateSession(
    UpdateSourceContext Context,
    RuntimeTargetSet Targets,
    RuntimeTargetSet? PreviousTargets,
    string ReleaseRoot,
    string Scope,
    ManagedRuntimeSnapshot? SourceSnapshot,
    ManagedCliLauncherState? CliLauncher);

/// <summary>
/// Owns the source snapshot to installed release boundary. A service target is only changed after
/// every requested payload has been published into the managed runtime root and its identity has
/// been written. Active and transaction records are replaced with a single file move so recovery
/// never has to infer a release from the source checkout.
/// </summary>
internal sealed class ManagedRuntimeTransaction
{
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly ICommandExecutor _commands;
    private readonly IFileSystem _files;
    private readonly IEnvironmentVariableProvider _environment;
    private readonly UpdateSourceResolver _sourceResolver;
    private readonly IManagedRuntimeActivator _activator;
    private readonly ManagedCliLauncher _cliLauncher;
    private readonly string? _unitDir;

    public ManagedRuntimeTransaction(
        TextWriter output,
        TextWriter error,
        ICommandExecutor commands,
        IFileSystem files,
        IEnvironmentVariableProvider environment,
        UpdateSourceResolver sourceResolver,
        IManagedRuntimeActivator activator,
        string? unitDir = null)
    {
        _out = output;
        _err = error;
        _commands = commands;
        _files = files;
        _environment = environment;
        _sourceResolver = sourceResolver;
        _activator = activator;
        _cliLauncher = new ManagedCliLauncher(output, error, commands, files);
        _unitDir = unitDir;
    }

    public async Task<(ManagedUpdateSession? Session, string? Error)> PrepareAsync(
        string? repoRoot,
        string scope,
        string transactionId,
        string? cliPath,
        Func<CancellationToken, Task<string?>>? beforeActivation = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await _sourceResolver.ResolveAsync(
            repoRoot,
            scope,
            transactionId,
            cliPath,
            cancellationToken);
        if (resolved.Context is null)
            return (null, resolved.Error ?? "source identity could not be established");

        var context = resolved.Context;
        var unchanged = await _sourceResolver.VerifyUnchangedAsync(context, cancellationToken);
        if (unchanged is not null)
            return (null, unchanged);

        var previous = ReadVerifiedTargets(context.RuntimeRoot);
        var generation = previous?.Generation is > 0 ? previous.Generation + 1 : 1;
        var built = new Dictionary<string, BuiltRuntime>(StringComparer.Ordinal);
        RunnerLaunchIdentity? runnerLaunchIdentity = null;
        RuntimeTargetSet? activatedTargets = null;
        ManagedRuntimeSnapshot? sourceSnapshot = null;
        ManagedCliLauncherState? cliLauncher = null;
        string? stagedReleaseRoot = null;
        var activePointerWritten = false;
        var activationPreconditionInvoked = false;

        try
        {
            if (Includes(scope, "runner"))
            {
                var launchIdentity = await _activator.ResolveRunnerLaunchIdentityAsync(_unitDir, cancellationToken);
                if (launchIdentity.Identity is not { IsComplete: true })
                    return (null, launchIdentity.Error ?? "runner launch identity is unavailable");
                runnerLaunchIdentity = launchIdentity.Identity;
            }

            var nodeDependencyError = await PrepareNodeDependenciesAsync(
                context,
                scope,
                cancellationToken);
            if (nodeDependencyError is not null)
                return (null, nodeDependencyError);

            if (Includes(scope, "cli"))
            {
                var cli = await PublishDotnetAsync(
                    context,
                    "cli",
                    Path.Combine("packages", "cli", "Mohist.Cli", "Mohist.Cli.csproj"),
                    generation,
                    cancellationToken);
                if (cli is null)
                    return (null, "CLI candidate publish failed");
                built["cli"] = cli;
            }

            if (Includes(scope, "server"))
            {
                var server = await PublishDotnetAsync(
                    context,
                    "server",
                    Path.Combine("packages", "server", "src", "Mohist.Server", "Mohist.Server.csproj"),
                    generation,
                    cancellationToken);
                if (server is null)
                    return (null, "Server candidate publish failed");
                built["server"] = server;
            }

            if (Includes(scope, "runner"))
            {
                var runner = await BuildRunnerAsync(context, generation, runnerLaunchIdentity!, cancellationToken);
                if (runner is null)
                    return (null, "Runner candidate build or install failed");
                built["runner"] = runner;
            }

            unchanged = await _sourceResolver.VerifyUnchangedAsync(context, cancellationToken);
            if (unchanged is not null)
                return (null, unchanged);

            var releaseId = context.Source.ReleaseId(scope);
            var releaseRoot = Path.Combine(
                context.RuntimeRoot,
                "releases",
                $"{releaseId}-g{generation}").Replace('\\', '/');
            if (_files.Exists(releaseRoot))
                return (null, $"managed release target already exists: {releaseRoot}");

            var releaseParent = Path.GetDirectoryName(releaseRoot);
            if (string.IsNullOrWhiteSpace(releaseParent))
                return (null, "managed release parent is unavailable");
            _files.CreateDirectory(releaseParent);
            _files.Move(context.CandidateRoot, releaseRoot);
            stagedReleaseRoot = releaseRoot;

            var targets = BuildTargetSet(context, releaseRoot, generation, previous, built);
            if (!targets.IsCompleteFor(scope))
                return (null, "candidate target set is incomplete");
            if (Includes(scope, "cli") && string.IsNullOrWhiteSpace(context.CliPath))
                return (null, "stable CLI launcher path is unavailable");
            if (!targets.Cli?.IsAbsoluteTarget ?? false)
                return (null, "candidate CLI target is not absolute");
            if (targets.Server is not null && !targets.Server.IsAbsoluteTarget)
                return (null, "candidate Server target is not absolute");
            if (targets.Runner is not null
                && (!targets.Runner.IsAbsoluteTarget || !targets.Runner.UsesCanonicalEntrypoint))
                return (null, "candidate Runner target does not use the canonical entrypoint");

            sourceSnapshot = await _activator.CaptureManagedRuntimeSnapshotAsync(
                scope,
                _unitDir,
                cancellationToken);
            targets = targets with { SourceSnapshot = sourceSnapshot };

            // The candidate is complete and the current launch state is captured,
            // but no active target or unit has changed yet. This is the only safe
            // point to require runner interruption before service activation.
            activatedTargets = targets;
            if (beforeActivation is not null)
            {
                activationPreconditionInvoked = true;
                var preconditionError = await beforeActivation(cancellationToken);
                if (!string.IsNullOrWhiteSpace(preconditionError))
                    return (null, preconditionError);
            }

            var transactionPath = Path.Combine(
                context.RuntimeRoot,
                "transactions",
                context.TransactionId,
                "state.json").Replace('\\', '/');
            WriteAtomic(transactionPath, targets with { Status = "candidate-staged", Previous = previous });
            WriteAtomic(
                Path.Combine(context.RuntimeRoot, "active.json").Replace('\\', '/'),
                targets with { Status = "candidate-activated", Previous = null });
            activePointerWritten = true;

            if (Includes(scope, "cli"))
            {
                var launcher = await _cliLauncher.ActivateAsync(
                    context.CliPath!,
                    targets.Cli!.Entrypoint,
                    targets.Cli.Identity,
                    Path.Combine(
                        context.RuntimeRoot,
                        "transactions",
                        context.TransactionId,
                        "cli-launcher.previous").Replace('\\', '/'),
                    cancellationToken);
                cliLauncher = launcher.State;
                if (launcher.Error is not null)
                {
                    await RestoreAfterFailureAsync(
                        context,
                        activatedTargets,
                        previous,
                        sourceSnapshot,
                        scope,
                        1,
                        launcher.Error,
                        cliLauncher,
                        CancellationToken.None);
                    return (null, launcher.Error);
                }
            }

            var activation = await _activator.ApplyManagedRuntimeAsync(
                targets,
                scope,
                _unitDir,
                cancellationToken,
                sourceSnapshot);
            if (activation != 0)
            {
                await RestoreAfterFailureAsync(
                    context,
                    activatedTargets,
                    previous,
                    sourceSnapshot,
                    scope,
                    activation,
                    "service activation failed",
                    cliLauncher,
                    CancellationToken.None);
                return (null, $"managed service activation failed with exit code {activation}");
            }

            _out.WriteLine($"Staged managed {scope} release {releaseId} from source {context.Source.GitCommit}.");
            return (new ManagedUpdateSession(context, targets, previous, releaseRoot, scope, sourceSnapshot, cliLauncher), null);
        }
        catch (OperationCanceledException)
        {
            if ((activePointerWritten || activationPreconditionInvoked) && activatedTargets is not null)
                await RestoreAfterFailureAsync(
                    context,
                    activatedTargets,
                    previous,
                    sourceSnapshot,
                    scope,
                    1,
                    "managed update was cancelled",
                    cliLauncher,
                    CancellationToken.None);
            return (null, "managed update was cancelled before activation completed");
        }
        catch (Exception ex)
        {
            if ((activePointerWritten || activationPreconditionInvoked) && activatedTargets is not null)
                await RestoreAfterFailureAsync(
                    context,
                    activatedTargets,
                    previous,
                    sourceSnapshot,
                    scope,
                    1,
                    "managed update staging failed",
                    cliLauncher,
                    CancellationToken.None);
            _err.WriteLine($"Managed update staging failed: {ex.Message}");
            return (null, "managed update staging failed");
        }
        finally
        {
            if (!activePointerWritten && stagedReleaseRoot is not null)
                DiscardStagedRelease(stagedReleaseRoot);
        }
    }

    public async Task<int> CommitAsync(ManagedUpdateSession session, CancellationToken cancellationToken = default)
    {
        try
        {
            var committed = session.Targets with
            {
                Status = "verified",
                Previous = null,
                SourceSnapshot = null,
                RecoveryDiagnostic = null,
                Recovery = null,
            };
            WriteAtomic(
                Path.Combine(session.Context.RuntimeRoot, "active.json").Replace('\\', '/'),
                committed);
            WriteAtomic(
                Path.Combine(session.Context.RuntimeRoot, "verified.json").Replace('\\', '/'),
                committed);
            WriteAtomic(
                Path.Combine(session.Context.RuntimeRoot, "transactions", session.Context.TransactionId, "state.json").Replace('\\', '/'),
                committed);
            var launcherFinalized = await _cliLauncher.FinalizeAsync(session.CliLauncher);
            if (launcherFinalized != 0)
                return launcherFinalized;

            ReclaimCommittedTransactionPayload(session);
            return 0;
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Managed runtime commit failed: {ex.Message}");
            return 1;
        }
    }

    public async Task<int> RollbackAsync(
        ManagedUpdateSession session,
        string reason,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ManagedRuntimeRestoreResult restoreResult;
            try
            {
                restoreResult = await _activator.RestoreManagedRuntimeAsync(
                    session.PreviousTargets,
                    session.Scope,
                    _unitDir,
                    cancellationToken,
                    session.SourceSnapshot);
            }
            catch (Exception ex)
            {
                restoreResult = ManagedRuntimeRestoreResult.FromExitCode(
                    1,
                    session.Scope,
                    $"Managed runtime service restoration failed: {ex.Message}");
            }

            var launcherRestore = await _cliLauncher.RestoreAsync(session.CliLauncher);
            restoreResult = IncludeLauncherRestoreResult(restoreResult, launcherRestore);
            if (restoreResult.ExitCode != 0)
            {
                var diagnostic = $"Managed runtime rollback failed with exit code {restoreResult.ExitCode}; affected scope={session.Scope}. Reason: {reason}";
                PersistRecoveryFailure(
                    session.Context,
                    session.Targets,
                    session.PreviousTargets,
                    session.Scope,
                    restoreResult,
                    diagnostic,
                    launcherRestore == 0);
                _err.WriteLine(diagnostic);
                return restoreResult.ExitCode;
            }

            var restoredTargets = session.PreviousTargets is null
                ? NoneTargets(session)
                : session.PreviousTargets with
                {
                    Status = "verified",
                    Previous = null,
                    SourceSnapshot = null,
                    RecoveryDiagnostic = null,
                    Recovery = null,
                };
            WriteAtomic(
                Path.Combine(session.Context.RuntimeRoot, "active.json").Replace('\\', '/'),
                restoredTargets);
            var verifiedPath = Path.Combine(session.Context.RuntimeRoot, "verified.json").Replace('\\', '/');
            var verifiedCandidate = ReadVerifiedTargets(session.Context.RuntimeRoot) is
            {
                Status: "verified",
                TransactionId: var transactionId,
                Generation: var generation,
            }
                && string.Equals(transactionId, session.Context.TransactionId, StringComparison.Ordinal)
                && generation == session.Targets.Generation;
            if (verifiedCandidate && session.PreviousTargets is null)
            {
                if (_files.Exists(verifiedPath))
                    _files.Delete(verifiedPath);
            }
            else if (verifiedCandidate)
            {
                WriteAtomic(verifiedPath, restoredTargets);
            }
            WriteAtomic(
                Path.Combine(session.Context.RuntimeRoot, "transactions", session.Context.TransactionId, "state.json").Replace('\\', '/'),
                session.Targets with { Status = "rolled-back", Previous = session.PreviousTargets });
            _err.WriteLine($"Managed runtime rolled back after {reason}.");
            return 0;
        }
        catch (Exception ex)
        {
            var diagnostic = $"Managed runtime rollback could not be proven: {ex.Message}";
            PersistRecoveryFailure(
                session.Context,
                session.Targets,
                session.PreviousTargets,
                session.Scope,
                ManagedRuntimeRestoreResult.FromExitCode(1, session.Scope, diagnostic),
                diagnostic);
            _err.WriteLine(diagnostic);
            return 1;
        }
    }

    public RuntimeTargetSet? ReadVerifiedTargets(string runtimeRoot)
    {
        var path = Path.Combine(runtimeRoot, "verified.json").Replace('\\', '/');
        if (!_files.Exists(path))
            return null;

        try
        {
            var value = JsonSerializer.Deserialize<RuntimeTargetSet>(_files.ReadAllText(path), JsonOptions);
            return value is { Status: "verified" } && IsTrusted(value) ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string?> PrepareNodeDependenciesAsync(
        UpdateSourceContext context,
        string scope,
        CancellationToken cancellationToken)
    {
        if (!Includes(scope, "server") && !Includes(scope, "runner"))
            return null;

        var (exitCode, stdout, stderr) = await _commands.ExecuteAsync(
            "npm",
            ["ci", "--include=dev"],
            context.BuildWorkspaceRoot,
            cancellationToken);
        if (exitCode == 0)
            return null;

        WriteCommandFailure(stdout, stderr);
        return $"managed update could not prepare Node dependencies: npm ci exited with code {exitCode}; verify the committed package-lock.json and npm registry access, then retry";
    }

    private async Task<BuiltRuntime?> PublishDotnetAsync(
        UpdateSourceContext context,
        string component,
        string projectRelativePath,
        long generation,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(context.CandidateRoot, component).Replace('\\', '/');
        _files.CreateDirectory(root);
        var project = Path.Combine(context.BuildWorkspaceRoot, projectRelativePath).Replace('\\', '/');
        var args = new[]
        {
            "publish",
            project,
            "-c", "Release",
            "-r", UpdateOperations.RuntimeIdentifier(),
            "--self-contained", "true",
            "/p:PublishSingleFile=true",
            $"/p:InformationalVersion={context.Version}",
            $"/p:SourceRevisionId={context.Source.GitCommit}",
            "-o", root,
        };
        var (exitCode, stdout, stderr) = await _commands.ExecuteAsync(
            "dotnet", args, context.BuildWorkspaceRoot, cancellationToken);
        if (exitCode != 0)
        {
            WriteCommandFailure(stdout, stderr);
            return null;
        }

        var entryName = ManagedRuntimeLayout.EntrypointFor(component);
        return await CompleteArtifactAsync(context, component, root, entryName, generation, null, cancellationToken);
    }

    private async Task<BuiltRuntime?> BuildRunnerAsync(
        UpdateSourceContext context,
        long generation,
        RunnerLaunchIdentity launchIdentity,
        CancellationToken cancellationToken)
    {
        var runnerSource = Path.Combine(context.BuildWorkspaceRoot, "packages", "runner").Replace('\\', '/');
        var runnerRoot = Path.Combine(context.CandidateRoot, "runner").Replace('\\', '/');
        var distRoot = Path.Combine(runnerRoot, "dist").Replace('\\', '/');
        if (_files.DirectoryExists(runnerRoot))
            _files.DeleteDirectory(runnerRoot);
        _files.CreateDirectory(runnerRoot);
        _files.CreateDirectory(distRoot);

        var (build, buildOut, buildErr) = await _commands.ExecuteAsync(
            "npm", ["run", "build", "-w", "packages/runner"], context.BuildWorkspaceRoot, cancellationToken);
        if (build != 0)
        {
            WriteCommandFailure(buildOut, buildErr);
            return null;
        }

        var copies = new[]
        {
            ($"{Path.Combine(runnerSource, "dist").Replace('\\', '/')}/.", distRoot),
            (Path.Combine(runnerSource, "package.json").Replace('\\', '/'), Path.Combine(runnerRoot, "package.json").Replace('\\', '/')),
            (Path.Combine(context.BuildWorkspaceRoot, "node_modules").Replace('\\', '/'), Path.Combine(runnerRoot, "node_modules").Replace('\\', '/')),
        };
        foreach (var (source, target) in copies)
        {
            var (copy, copyOut, copyErr) = await _commands.ExecuteAsync(
                "cp", ["-RL", source, target], context.BuildWorkspaceRoot, cancellationToken);
            if (copy != 0)
            {
                WriteCommandFailure(copyOut, copyErr);
                return null;
            }
        }

        return await CompleteArtifactAsync(
            context,
            "runner",
            runnerRoot,
            ManagedRuntimeLayout.RunnerEntrypoint,
            generation,
            launchIdentity.RunnerId,
            cancellationToken);
    }

    private async Task<BuiltRuntime?> CompleteArtifactAsync(
        UpdateSourceContext context,
        string component,
        string root,
        string entryRelativePath,
        long generation,
        string? runnerId,
        CancellationToken cancellationToken)
    {
        var payload = _files.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !IsMetadataPath(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (payload.Length == 0)
        {
            _err.WriteLine($"{component} publish completed without an installed payload at {root}.");
            return null;
        }

        var entry = Path.Combine(root, entryRelativePath).Replace('\\', '/');
        if (!_files.Exists(entry))
        {
            _err.WriteLine($"{component} publish did not produce required entrypoint {entry}.");
            return null;
        }

        var artifactDigest = ComputeArtifactDigest(root, payload);
        var identity = new RuntimeIdentity(
            component,
            context.Version,
            context.Source.GitCommit,
            context.Source.TreeHash,
            artifactDigest,
            context.Source.ReleaseId(context.Scope),
            generation,
            runnerId,
            BuildGitHash: component == "runner" ? context.Source.GitCommit : null);
        if (!identity.IsComplete)
            return null;

        _files.WriteAllText(
            Path.Combine(root, "runtime-identity.json").Replace('\\', '/'),
            identity.ToJson());
        _files.WriteAllText(
            Path.Combine(root, "release.json").Replace('\\', '/'),
            JsonSerializer.Serialize(new
            {
                identity,
                sourceRoot = context.Source.RepositoryRoot,
                snapshotRoot = context.SnapshotRoot,
            }, JsonOptions));
        if (component == "runner")
        {
            _files.WriteAllText(
                Path.Combine(root, ManagedRuntimeLayout.RunnerBuildInfo).Replace('\\', '/'),
                JsonSerializer.Serialize(new
                {
                    component = "runner",
                    version = identity.Version,
                    gitHash = identity.SourceRevision,
                    sourceRevision = identity.SourceRevision,
                    treeHash = identity.TreeHash,
                    artifactDigest = identity.ArtifactDigest,
                    releaseId = identity.ReleaseId,
                    generation = identity.Generation,
                    runnerId = identity.RunnerId,
                }, JsonOptions));
        }

        await Task.CompletedTask;
        return new BuiltRuntime(component, root, entry, entryRelativePath, identity);
    }

    private RuntimeTargetSet BuildTargetSet(
        UpdateSourceContext context,
        string releaseRoot,
        long generation,
        RuntimeTargetSet? previous,
        IReadOnlyDictionary<string, BuiltRuntime> built)
    {
        RuntimeTarget? Target(string component) {
            if (!built.TryGetValue(component, out var runtime))
                return previous is null ? null : component switch
                {
                    "cli" => previous.Cli,
                    "server" => previous.Server,
                    "runner" => previous.Runner,
                    _ => null,
                };

            var working = Path.Combine(releaseRoot, component).Replace('\\', '/');
            var entry = Path.Combine(releaseRoot, component, runtime.EntrypointRelativePath).Replace('\\', '/');
            var node = component == "runner" ? ResolveExecutable("node") : null;
            return new RuntimeTarget(
                component,
                entry,
                working,
                [],
                UpdateOperations.RuntimeIdentifier(),
                runtime.Identity,
                node,
                component == "runner" ? working : null,
                component == "runner" ? RuntimeLaunchMode.Node : RuntimeLaunchMode.SelfContained);
        }

        return new RuntimeTargetSet(
            "candidate-staged",
            generation,
            context.TransactionId,
            Target("cli"),
            Target("server"),
            Target("runner"),
            previous);
    }

    private async Task RestoreAfterFailureAsync(
        UpdateSourceContext context,
        RuntimeTargetSet? candidate,
        RuntimeTargetSet? previous,
        ManagedRuntimeSnapshot? sourceSnapshot,
        string scope,
        int activationCode,
        string reason,
        ManagedCliLauncherState? cliLauncher,
        CancellationToken cancellationToken)
    {
        try
        {
            ManagedRuntimeRestoreResult restoreResult;
            try
            {
                restoreResult = await _activator.RestoreManagedRuntimeAsync(
                    previous,
                    scope,
                    _unitDir,
                    cancellationToken,
                    sourceSnapshot);
            }
            catch (Exception ex)
            {
                restoreResult = ManagedRuntimeRestoreResult.FromExitCode(
                    1,
                    scope,
                    $"Managed runtime service restoration failed: {ex.Message}");
            }

            var launcherRestore = await _cliLauncher.RestoreAsync(cliLauncher);
            restoreResult = IncludeLauncherRestoreResult(restoreResult, launcherRestore);
            if (restoreResult.ExitCode != 0)
            {
                var diagnostic = $"Recovery after activation exit {activationCode} failed with exit code {restoreResult.ExitCode}; affected scope={scope}.";
                PersistRecoveryFailure(
                    context,
                    candidate,
                    previous,
                    scope,
                    restoreResult,
                    diagnostic,
                    launcherRestore == 0);
                _err.WriteLine(diagnostic);
                return;
            }

            WriteAtomic(
                Path.Combine(context.RuntimeRoot, "active.json").Replace('\\', '/'),
                previous is null
                    ? new RuntimeTargetSet("none", 0, context.TransactionId, null, null, null, null)
                    : previous with
                    {
                        Status = "verified",
                        Previous = null,
                        SourceSnapshot = null,
                        RecoveryDiagnostic = null,
                        Recovery = null,
                    });
        }
        catch (Exception ex)
        {
            try
            {
                var diagnostic = $"Recovery after activation exit {activationCode} failed: {ex.Message}; no success was emitted.";
                PersistRecoveryFailure(
                    context,
                    candidate,
                    previous,
                    scope,
                    ManagedRuntimeRestoreResult.FromExitCode(1, scope, diagnostic),
                    diagnostic);
            }
            catch
            {
                // The original failure remains fail-closed even if the diagnostic pointer cannot be rewritten.
            }
            _err.WriteLine($"Recovery after activation exit {activationCode} failed: {ex.Message}; no success was emitted.");
        }
        _err.WriteLine($"Managed runtime activation was rejected: {reason}.");
    }

    private void DiscardStagedRelease(string releaseRoot)
    {
        try
        {
            _files.DeleteDirectory(releaseRoot);
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Managed staged release cleanup failed for {releaseRoot}: {ex.Message}");
        }
    }

    private void ReclaimCommittedTransactionPayload(ManagedUpdateSession session)
    {
        string transactionRoot;
        try
        {
            transactionRoot = Path.Combine(
                session.Context.RuntimeRoot,
                "transactions",
                session.Context.TransactionId).Replace('\\', '/');
            if (_files.IsSymbolicLink(transactionRoot))
                return;
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Managed transaction payload cleanup could not inspect {session.Context.TransactionId}: {ex.Message}");
            return;
        }

        // These directories are only inputs to staging and rollback. The committed
        // pointers and immutable release remain available after launcher finalization.
        foreach (var name in new[] { "snapshot", "build", "candidate" })
        {
            var payloadRoot = Path.Combine(transactionRoot, name).Replace('\\', '/');
            try
            {
                if (!_files.DirectoryExists(payloadRoot) || _files.IsSymbolicLink(payloadRoot))
                    continue;
                _files.DeleteDirectory(payloadRoot);
            }
            catch (Exception ex)
            {
                _err.WriteLine($"Managed transaction payload cleanup failed for {payloadRoot}: {ex.Message}");
            }
        }
    }

    private RuntimeTargetSet NoneTargets(ManagedUpdateSession session) =>
        new("none", session.Targets.Generation, session.Context.TransactionId, null, null, null, null);

    private void PersistRecoveryFailure(
        UpdateSourceContext context,
        RuntimeTargetSet? candidate,
        RuntimeTargetSet? previous,
        string scope,
        ManagedRuntimeRestoreResult restoreResult,
        string diagnostic,
        bool cliRestored = true)
    {
        var active = FailClosedTargets(
            previous,
            scope,
            context.TransactionId,
            candidate?.Generation ?? previous?.Generation ?? 0,
            restoreResult,
            diagnostic,
            cliRestored);
        WriteAtomic(
            Path.Combine(context.RuntimeRoot, "active.json").Replace('\\', '/'),
            active);
        if (candidate is not null)
        {
            WriteAtomic(
                Path.Combine(context.RuntimeRoot, "transactions", context.TransactionId, "state.json").Replace('\\', '/'),
                candidate with
                {
                    Status = "recovery-failed",
                    Previous = previous,
                    RecoveryDiagnostic = diagnostic,
                    Recovery = restoreResult.ToRecovery(diagnostic),
                });
        }
    }

    private static RuntimeTargetSet FailClosedTargets(
        RuntimeTargetSet? previous,
        string scope,
        string transactionId,
        long generation,
        ManagedRuntimeRestoreResult restoreResult,
        string diagnostic,
        bool cliRestored)
    {
        if (previous is null)
            return new RuntimeTargetSet(
                "recovery-failed",
                generation,
                transactionId,
                null,
                null,
                null,
                null,
                null,
                null,
                diagnostic,
                restoreResult.ToRecovery(diagnostic));

        return previous with
        {
            Status = "recovery-failed",
            TransactionId = transactionId,
            Cli = Includes(scope, "cli") && !cliRestored ? null : previous.Cli,
            Server = Includes(scope, "server") && restoreResult.Server != ManagedRuntimeRestoreState.Restored ? null : previous.Server,
            Runner = Includes(scope, "runner") && restoreResult.Runner != ManagedRuntimeRestoreState.Restored ? null : previous.Runner,
            Previous = null,
            SourceSnapshot = null,
            RecoveryDiagnostic = diagnostic,
            Recovery = restoreResult.ToRecovery(diagnostic),
        };
    }

    private static ManagedRuntimeRestoreResult IncludeLauncherRestoreResult(
        ManagedRuntimeRestoreResult restoreResult,
        int launcherRestore)
    {
        if (launcherRestore == 0)
            return restoreResult;

        var launcherDiagnostic = $"CLI launcher restoration failed with exit code {launcherRestore}";
        return restoreResult with
        {
            ExitCode = restoreResult.ExitCode != 0 ? restoreResult.ExitCode : launcherRestore,
            Diagnostic = string.IsNullOrWhiteSpace(restoreResult.Diagnostic)
                ? launcherDiagnostic
                : $"{restoreResult.Diagnostic}; {launcherDiagnostic}",
        };
    }

    private void WriteAtomic(string path, RuntimeTargetSet value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            _files.CreateDirectory(directory);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            _files.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions));
            _files.MoveFile(temp, path);
        }
        catch
        {
            if (_files.Exists(temp))
                _files.Delete(temp);
            throw;
        }
    }

    private static bool IsTrusted(RuntimeTargetSet value)
    {
        if (value.Generation <= 0 || string.IsNullOrWhiteSpace(value.TransactionId))
            return false;
        return (value.Cli is null || value.Cli.IsAbsoluteTarget && value.Cli.Identity.IsComplete)
            && (value.Server is null || value.Server.IsAbsoluteTarget && value.Server.Identity.IsComplete)
            && (value.Runner is null || value.Runner.IsAbsoluteTarget && value.Runner.UsesCanonicalEntrypoint && value.Runner.Identity.IsComplete);
    }

    private static bool Includes(string scope, string component) =>
        string.Equals(scope, "full", StringComparison.Ordinal)
            || string.Equals(scope, component, StringComparison.Ordinal);

    private string ResolveExecutable(string name)
    {
        var path = _environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var raw in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(raw.Trim(), name).Replace('\\', '/');
                if (_files.Exists(candidate))
                    return candidate;
            }
        }

        return OperatingSystem.IsWindows()
            ? $"C:/Program Files/nodejs/{name}.exe"
            : $"/usr/bin/{name}";
    }

    private string ComputeArtifactDigest(string root, IReadOnlyList<string> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in files)
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            var bytes = ReadBytes(path);
            hash.AppendData(Encoding.UTF8.GetBytes($"{relative}\n{bytes.Length}\n"));
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private byte[] ReadBytes(string path)
    {
        using var stream = _files.OpenRead(path);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static bool IsMetadataPath(string path)
    {
        var name = Path.GetFileName(path);
        return string.Equals(name, "runtime-identity.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "release.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "build-info.json", StringComparison.OrdinalIgnoreCase);
    }

    private void WriteCommandFailure(string stdout, string stderr)
    {
        if (!string.IsNullOrWhiteSpace(stdout)) _err.WriteLine(stdout.TrimEnd());
        if (!string.IsNullOrWhiteSpace(stderr)) _err.WriteLine(stderr.TrimEnd());
    }

    private sealed record BuiltRuntime(
        string Component,
        string Root,
        string Entrypoint,
        string EntrypointRelativePath,
        RuntimeIdentity Identity);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}
