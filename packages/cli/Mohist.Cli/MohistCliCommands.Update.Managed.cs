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
        var resolvedCliPath = IncludesManagedScope(scope, "cli") && string.IsNullOrWhiteSpace(cliPath)
            ? _operations.ResolveManagedCliLauncherPath()
            : await ResolveCliPathAsync(cliPath);
        var context = new UpdateContext(
            dryRun,
            repoRoot,
            resolvedCliPath,
            cancellationToken,
            _timeProvider);
        context.Stage = UpdateStage.Preflight;
        context.RecordStage("Capturing immutable source", "starting");

        if (string.Equals(scope, "full", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(resolvedCliPath))
        {
            _err.WriteLine("Could not resolve mo executable path. Pass --cli-path to update the CLI explicitly.");
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

        var prepared = await _operations.PrepareManagedUpdateAsync(
            repoRoot,
            scope,
            context.JobId,
            resolvedCliPath,
            cancellationToken);
        if (prepared.Session is null)
        {
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
            context.RecordStage("Committing managed runtime", "failed");
            context.LastExitCode = 1;
            return await FinalizeManagedFailureAsync(context, 1, postOutcome);
        }

        context.Outcome = UpdateOutcome.Ready;
        context.RecordStage("Committing managed runtime", "server and runner confirmed the same release identity");
        context.LastExitCode = 0;
        _out.WriteLine($"Managed {scope} update committed for source {context.SourceHead}.");
        return postOutcome ? await FinalizeAsync(context, 0) : 0;
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
