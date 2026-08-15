namespace Mohist.Cli;

internal partial class SourceCodeUpdater
{
    private async Task<int> ExecuteManagedUpdateAsync(
        string? repoRoot,
        string scope,
        bool dryRun,
        string? cliPath,
        CancellationToken cancellationToken,
        bool postOutcome)
    {
        var resolvedCliPath = IncludesManagedScope(scope, "cli")
            ? await _operations.ResolveManagedCliPathAsync(cliPath)
            : await ResolveCliPathAsync(cliPath);
        var context = new UpdateContext(
            dryRun,
            repoRoot,
            resolvedCliPath,
            cancellationToken,
            _timeProvider);
        context.Stage = UpdateStage.Preflight;
        context.RecordStage("Capturing immutable source", "starting");

        if (IncludesManagedScope(scope, "cli")
            && string.IsNullOrWhiteSpace(resolvedCliPath))
        {
            _err.WriteLine("Managed CLI update refused: --cli-path must name an existing absolute mo entrypoint.");
            _err.WriteLine("Bootstrap or refresh the CLI from the source checkout with: bash scripts/install-mo.sh (or npm run install:cli for initial installation).");
            context.LastExitCode = 1;
            return await FinalizeManagedFailureAsync(context, 1, postOutcome);
        }

        if (dryRun)
        {
            var root = _operations.ResolveRepoRoot(repoRoot);
            _out.WriteLine($"Dry run: would capture commit/tree identity from {root}.");
            _out.WriteLine($"Dry run: would build immutable {scope} candidates from a read-only snapshot.");
            _out.WriteLine("Dry run: would atomically activate stable managed runtime targets and verify identities before success.");
            return 0;
        }

        if (string.Equals(scope, "full", StringComparison.Ordinal)
            && !await _operations.IsRunnerInstalledAsync())
        {
            context.UnavailableCapability = "Runner not installed";
            _err.WriteLine("Full update requires an installed managed runner.");
            _err.WriteLine("Install the runner with: mo install runner");
            context.LastExitCode = 1;
            return await FinalizeManagedFailureAsync(context, 1, postOutcome);
        }

        RunnerInterruptResult? interruption = null;
        Func<CancellationToken, Task<string?>>? beforeActivation = IncludesManagedScope(scope, "runner")
            ? async token =>
            {
                var confirmed = await ConfirmRunnerUpdateInterruptAsync(token);
                if (!confirmed.Succeeded)
                {
                    return $"runner update interrupt was not confirmed: {confirmed.Error ?? "invalid response"}; managed runner service was not restarted";
                }

                interruption = confirmed;
                return null;
            }
            : null;

        try
        {
            var prepared = await _operations.PrepareManagedUpdateAsync(
                repoRoot,
                scope,
                context.JobId,
                resolvedCliPath,
                beforeActivation,
                cancellationToken);
            if (prepared.Session is null)
            {
                await RollbackRunnerUpdateInterruptAsync(interruption);
                context.RecordStage("Capturing immutable source", $"failed: {prepared.Error}");
                context.UnavailableCapability = "Managed runtime candidate unavailable";
                context.LastExitCode = 1;
                _err.WriteLine($"Mohist update refused: {prepared.Error ?? "managed candidate could not be prepared"}.");
                return await FinalizeManagedFailureAsync(context, 1, postOutcome);
            }

            context.ManagedSession = prepared.Session;
            context.SourceContext = prepared.Session.Context;
            context.SourceHead = prepared.Session.Context.Source.GitCommit;
            context.ExpectedTargets = prepared.Session.Targets;
            context.RecordStage("Activating managed runtime", "candidate activated; verifying runtime identities");

            var verificationError = await VerifyManagedRuntimeAsync(prepared.Session, scope, cancellationToken);
            if (verificationError is not null)
            {
                context.UnavailableCapability = "Managed runtime identity mismatch";
                var rollback = await _operations.RollbackManagedUpdateAsync(
                    prepared.Session,
                    verificationError,
                    cancellationToken);
                if (rollback != 0)
                    context.UnavailableCapability = "Managed runtime recovery failed";
                await RollbackRunnerUpdateInterruptAsync(interruption);
                context.RecordStage("Activating managed runtime", $"failed: {verificationError}");
                context.LastExitCode = 1;
                return await FinalizeManagedFailureAsync(context, 1, postOutcome);
            }

            var committed = await _operations.CommitManagedUpdateAsync(prepared.Session, cancellationToken);
            if (committed != 0)
            {
                context.UnavailableCapability = "Managed runtime commit failed";
                var rollback = await _operations.RollbackManagedUpdateAsync(
                    prepared.Session,
                    "active target commit failed",
                    cancellationToken);
                if (rollback != 0)
                    context.UnavailableCapability = "Managed runtime recovery failed";
                await RollbackRunnerUpdateInterruptAsync(interruption);
                context.RecordStage("Committing managed runtime", "failed");
                context.LastExitCode = 1;
                return await FinalizeManagedFailureAsync(context, 1, postOutcome);
            }

            // The candidate Runner registered with the verified identity, so
            // registration durably completed the handoff and owns fence clear.
            interruption = null;
            context.Outcome = UpdateOutcome.Ready;
            context.RecordStage("Committing managed runtime", "server and runner confirmed the same release identity");
            context.LastExitCode = 0;
            _out.WriteLine($"Managed {scope} update committed for source {context.SourceHead}.");
            return postOutcome ? await FinalizeAsync(context, 0) : 0;
        }
        catch
        {
            await RollbackRunnerUpdateInterruptAsync(interruption);
            throw;
        }
    }

    private async Task<string?> VerifyManagedRuntimeAsync(
        ManagedUpdateSession session,
        string scope,
        CancellationToken cancellationToken)
    {
        if (IncludesManagedScope(scope, "cli"))
        {
            var cli = session.Targets.Cli;
            if (cli is null)
                return "CLI target is missing from the candidate";
            var cliIdentity = await _validator.VerifyCliRuntimeIdentityAsync(
                session.Context.CliPath,
                cli.Identity,
                cancellationToken);
            if (cliIdentity.Outcome == RuntimeCheckOutcome.Fail)
                return cliIdentity.Message;
        }

        if (IncludesManagedScope(scope, "server"))
        {
            var ready = await _readinessProbe.WaitForServerReadyWithProgressAsync(
                _serverReadyTimeout,
                cancellationToken);
            if (!ready.Ready)
                return ready.LastFailure ?? "Server readiness was not proven";

            var server = session.Targets.Server;
            if (server is null)
                return "Server target is missing from the candidate";
            var serverIdentity = await _validator.VerifyServerRuntimeIdentityAsync(
                server.Identity,
                cancellationToken);
            if (serverIdentity.Outcome == RuntimeCheckOutcome.Fail)
                return serverIdentity.Message;
        }

        if (IncludesManagedScope(scope, "runner"))
        {
            var runner = session.Targets.Runner;
            if (runner is null)
                return "Runner target is missing from the candidate";
            var runnerOutcome = await _runnerRefreshVerifier.VerifyRunnerRuntimeAsync(runner.Identity);
            runnerOutcome.WriteSummary(_out, _err);
            if (runnerOutcome.ExitCode != 0)
                return runnerOutcome switch
                {
                    RunnerRefreshOutcome.UnknownIdentity unknown => unknown.Reason,
                    RunnerRefreshOutcome.StaleRunnerRuntime stale => stale.Reason,
                    RunnerRefreshOutcome.NotReconnected => "Runner did not reconnect with its candidate identity",
                    _ => "Runner identity verification failed",
                };
        }

        return null;
    }

    private async Task<RunnerInterruptResult> ConfirmRunnerUpdateInterruptAsync(CancellationToken cancellationToken)
    {
        var interruption = await _runnerRefreshVerifier.InterruptRunnerAsync(cancellationToken);
        if (interruption.Succeeded)
        {
            _out.WriteLine(
                $"Runner update interrupt: status=interrupted runnerId={interruption.RunnerId} interruptedWorkCount={interruption.InterruptedWorkCount}.");
        }
        return interruption;
    }

    private async Task RollbackRunnerUpdateInterruptAsync(RunnerInterruptResult? interruption)
    {
        if (interruption is not { Succeeded: true })
            return;

        var releaseError = await _runnerRefreshVerifier.CancelRunnerUpdateInterruptAsync(
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

    private async Task<int> FinalizeManagedFailureAsync(
        UpdateContext context,
        int exitCode,
        bool postOutcome)
    {
        context.Outcome = UpdateOutcome.Failed;
        if (postOutcome)
            return await FinalizeAsync(context, exitCode);

        _err.WriteLine("Mohist update did not complete successfully; no success was emitted.");
        return exitCode;
    }

    private static bool IncludesManagedScope(string scope, string component) =>
        string.Equals(scope, "full", StringComparison.Ordinal)
        || string.Equals(scope, component, StringComparison.Ordinal);
}
