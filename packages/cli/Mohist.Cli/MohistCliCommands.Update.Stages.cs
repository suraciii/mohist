namespace Mohist.Cli;

internal partial class SourceCodeUpdater
{
    private async Task<int> UpdateCliStageAsync(UpdateContext context, CancellationToken token)
    {
        context.Stage = UpdateStage.UpdateCli;
        _out.WriteLine(StageLabels.CliUpdate);
        context.RecordStage(StageLabels.CliUpdate, "starting");

        var exitCode = await UpdateCliAsync(context.RepoRoot, context.DryRun, context.CliPath, token);
        if (exitCode != 0)
        {
            context.RecordStage(StageLabels.CliUpdate, "failed");
            return exitCode;
        }
        context.RecordStage(StageLabels.CliUpdate, "complete");
        return 0;
    }

    private async Task<int> PrepareRunnerStageAsync(UpdateContext context, CancellationToken token)
    {
        context.Stage = UpdateStage.PrepareRunner;
        _out.WriteLine(StageLabels.PrepareRunner);
        context.RecordStage(StageLabels.PrepareRunner, "querying runner state");

        context.RunnerInstalled = context.DryRun || await _operations.IsRunnerInstalledAsync();
        if (!context.RunnerInstalled)
        {
            var reason = "runner service is not installed";
            context.RecordStage(StageLabels.PrepareRunner, reason);
            _out.WriteLine("Runner service is not installed; skipping managed runner candidate preparation.");
            _out.WriteLine($"Runner refresh skipped: {reason}");
            _runnerRefreshVerifier.WriteSkippedSummary(reason, _out, _err);
            return 0;
        }

        if (!context.DryRun)
        {
            context.RunnerWasRunning = await _operations.IsRunnerRunningAsync(token);
        }
        else
        {
            context.RunnerWasRunning = true;
            _out.WriteLine("Dry run: would query systemctl --user is-active mohist-runner.service");
        }

        if (!context.RunnerWasRunning)
        {
            context.RecordStage(StageLabels.PrepareRunner, "runner not running; no candidate activation needed");
            _out.WriteLine("Runner was not running; its active target remains unchanged.");
            return 0;
        }

        context.RecordStage(StageLabels.PrepareRunner, "runner remains active until candidate is prepared");
        _out.WriteLine("Runner remains active while Server and Runner candidates are prepared.");
        return 0;
    }

    private async Task<int> PrepareRunnerCandidateStageAsync(UpdateContext context, CancellationToken token)
    {
        context.Stage = UpdateStage.PrepareRunnerCandidate;
        _out.WriteLine(StageLabels.PrepareRunnerCandidate);
        context.RecordStage(StageLabels.PrepareRunnerCandidate, "building validated runner candidate without stopping current runner");

        if (!context.RunnerWasRunning)
        {
            context.RecordStage(StageLabels.PrepareRunnerCandidate, "skipped; runner was not running");
            return 0;
        }

        if (context.DryRun)
        {
            _out.WriteLine("Dry run: would build and validate the Runner artifact before stopping its current service.");
            context.RecordStage(StageLabels.PrepareRunnerCandidate, "complete (dry run)");
            return 0;
        }

        if (context.UpdateSource is null)
        {
            context.RecordStage(StageLabels.PrepareRunnerCandidate, "source missing");
            return 1;
        }

        var candidate = await _operations.PrepareRunnerRuntimeCandidateAsync(
            context.UpdateSource,
            null,
            token,
            context.RuntimeManager);
        if (candidate is null)
        {
            context.RecordStage(StageLabels.PrepareRunnerCandidate, "runner candidate build or validation failed");
            return 1;
        }

        context.RunnerCandidate = candidate;
        context.RecordStage(StageLabels.PrepareRunnerCandidate, "runner candidate built and recovery snapshot held");
        return 0;
    }

    private async Task<int> StopRunnerStageAsync(UpdateContext context, CancellationToken token)
    {
        context.Stage = UpdateStage.StopRunner;
        _out.WriteLine(StageLabels.StopRunner);
        context.RecordStage(StageLabels.StopRunner, "stopping current runner after candidate validation");

        if (!context.RunnerWasRunning)
        {
            context.RecordStage(StageLabels.StopRunner, "skipped; runner was not running");
            return 0;
        }

        if (context.DryRun)
        {
            _out.WriteLine("Dry run: would stop the current Runner immediately before candidate activation.");
            context.RunnerStopped = true;
            context.RecordStage(StageLabels.StopRunner, "complete (dry run)");
            return 0;
        }

        if (context.RunnerCandidate is null)
        {
            context.RecordStage(StageLabels.StopRunner, "candidate missing");
            return 1;
        }

        var stop = await _operations.StopRunnerAsync(dryRun: false);
        if (stop != 0)
        {
            context.RecordStage(StageLabels.StopRunner, "stop failed");
            return stop;
        }

        context.RunnerStopped = true;
        _out.WriteLine("Runner is stopped; its validated candidate will now be activated.");
        context.RecordStage(StageLabels.StopRunner, "runner stopped after candidate validation");
        return 0;
    }

    private async Task<int> UpdateServerStageAsync(UpdateContext context, CancellationToken token)
    {
        context.Stage = UpdateStage.UpdateServer;
        _out.WriteLine(StageLabels.UpdateServer);
        context.RecordStage(StageLabels.UpdateServer, "installing versioned server runtime");

        if (context.DryRun)
        {
            var root = _operations.ResolveRepoRoot(context.RepoRoot);
            _out.WriteLine($"  cd {root} && git rev-parse HEAD");
            _out.WriteLine($"  cd {root} && dotnet publish packages/server/src/Mohist.Server/Mohist.Server.csproj -c Release -o <stable-server-version>");
            _out.WriteLine("  point the absolute server service target at the installed current version");
            context.RecordStage(StageLabels.UpdateServer, "complete (dry run)");
            return 0;
        }

        if (context.UpdateSource is null)
        {
            _err.WriteLine("Update source was not resolved. No server runtime was changed.");
            context.RecordStage(StageLabels.UpdateServer, "source missing");
            return 1;
        }

        var prepared = await _operations.PrepareServerRuntimeAsync(
            context.UpdateSource,
            null,
            token,
            context.RuntimeManager);
        if (prepared is null)
        {
            context.RecordStage(StageLabels.UpdateServer, "failed");
            return 1;
        }
        context.ServerRuntime = prepared;
        context.RecordStage(StageLabels.UpdateServer, "complete");
        return 0;
    }

    private async Task<int> WaitingForReadyStageAsync(UpdateContext context, CancellationToken token)
    {
        context.Stage = UpdateStage.WaitingForReady;
        _out.WriteLine(StageLabels.WaitingForReady);
        context.RecordStage(StageLabels.WaitingForReady, "starting readiness checks");

        if (context.DryRun)
        {
            _out.WriteLine("Dry run: would wait for /api/health, /, and referenced /assets/* readiness checks.");
            return 0;
        }

        var ready = await _readinessProbe.WaitForServerReadyWithProgressAsync(_serverReadyTimeout, token);
        if (!ready.Ready)
        {
            context.RecordStage(StageLabels.WaitingForReady, $"timed out: {ready.LastFailure ?? "no readiness signal"}");
            if (context.ServerRuntime is not null)
            {
                await _operations.RollBackRuntimeAsync(
                    context.ServerRuntime,
                    actualHash: null,
                    ready.LastFailure ?? "readiness did not pass",
                    token);
                context.ServerRuntime = null;
            }
            return 1;
        }

        if (context.ServerRuntime is null || context.UpdateSource is null)
        {
            _err.WriteLine("Server runtime candidate was not available for identity verification.");
            context.RecordStage(StageLabels.WaitingForReady, "server candidate missing");
            return 1;
        }

        var identity = await _validator.VerifyServerRuntimeIdentityAsync(
            context.ServerRuntime.Activation.Candidate,
            token);
        if (!identity.Matches)
        {
            await _operations.RollBackRuntimeAsync(context.ServerRuntime, identity.ActualHash, identity.Reason, token);
            context.ServerRuntime = null;
            context.RecordStage(StageLabels.WaitingForReady, "server identity mismatch");
            return 1;
        }

        if (!_operations.TryMarkRuntimeVerified(context.ServerRuntime, retainLease: true))
        {
            await _operations.RollBackRuntimeAsync(
                context.ServerRuntime,
                identity.ActualHash,
                "could not record the verified server version",
                token);
            context.ServerRuntime = null;
            context.RecordStage(StageLabels.WaitingForReady, "could not record verified server version");
            return 1;
        }
        context.ServerRuntimeVerified = true;
        context.RuntimeBatch.Stage(context.ServerRuntime);
        _out.WriteLine($"Server runtime identity matched (expected {identity.ExpectedHash}, actual {identity.ActualHash}); batch commit is pending.");
        context.RecordStage(StageLabels.WaitingForReady, "server identity matched; batch commit pending");
        return 0;
    }

    private async Task<int> RestoreRunnerStageAsync(UpdateContext context, CancellationToken token)
    {
        context.Stage = UpdateStage.RestoreRunner;
        _out.WriteLine(StageLabels.RestoreRunner);
        context.RecordStage(StageLabels.RestoreRunner, "starting runner restore");

        if (!context.RunnerWasRunning)
        {
            _out.WriteLine("Runner was not running before the update; no restore needed.");
            context.RecordStage(StageLabels.RestoreRunner, "skipped; runner was not running");
            return 0;
        }

        if (context.DryRun)
        {
            var root = _operations.ResolveRepoRoot(context.RepoRoot);
            _out.WriteLine($"  cd {root} && git rev-parse HEAD");
            _out.WriteLine($"  cd {root} && npm run build -w packages/runner");
            _out.WriteLine("  install runner dist and dependencies into a stable versioned runtime directory");
            context.RunnerRestored = true;
            context.RecordStage(StageLabels.RestoreRunner, "complete (dry run)");
            return 0;
        }

        if (!context.ServerRuntimeVerified || context.RunnerCandidate is null)
        {
            context.RecordStage(StageLabels.RestoreRunner, "validated runner candidate missing");
            return 1;
        }

        var candidate = context.RunnerCandidate;
        context.RunnerCandidate = null;
        var prepared = await _operations.ActivatePreparedRuntimeAsync(candidate, token);
        if (prepared is null)
        {
            context.RecordStage(StageLabels.RestoreRunner, "runner artifact install failed");
            return 1;
        }

        context.RunnerRuntime = prepared;
        var outcome = await _runnerRefreshVerifier.VerifyRunnerRuntimeAsync(
            UpdateOperations.CreateRunnerIdentityExpectation(prepared),
            token);
        outcome.WriteSummary(_out, _err);
        var reportedRunnerHash = outcome is RunnerRefreshOutcome.StaleRunnerRuntime staleRuntime
            ? staleRuntime.ReportedHash
            : null;
        if (outcome.ExitCode != 0)
        {
            var recovery = await _operations.RollBackRuntimeWithResultAsync(
                prepared,
                reportedRunnerHash,
                outcome switch
                {
                    RunnerRefreshOutcome.UnknownIdentity unknown => unknown.Reason,
                    RunnerRefreshOutcome.StaleRunnerRuntime stale => stale.Reason,
                    RunnerRefreshOutcome.NotReconnected => "runner did not reconnect with a runtime identity",
                    _ => "runner runtime verification did not pass",
                },
                token);
            context.RunnerRuntime = null;
            context.RunnerRestored = recovery.Restored;
            context.RecordStage(StageLabels.RestoreRunner, "runner identity mismatch");
            if (!recovery.Restored)
                context.UnavailableCapability ??= "Runtime recovery failed";
            return 1;
        }

        if (!_operations.TryMarkRuntimeVerified(prepared, retainLease: true))
        {
            var recovery = await _operations.RollBackRuntimeWithResultAsync(
                prepared,
                reportedRunnerHash,
                "could not record the verified runner version",
                token);
            context.RunnerRuntime = null;
            context.RunnerRestored = recovery.Restored;
            context.RecordStage(StageLabels.RestoreRunner, "could not record verified runner version");
            if (!recovery.Restored)
                context.UnavailableCapability ??= "Runtime recovery failed";
            return 1;
        }
        context.RuntimeBatch.Stage(prepared);
        context.RunnerRestored = true;
        _out.WriteLine("Runner runtime identity matched; batch commit is pending.");
        context.RecordStage(StageLabels.RestoreRunner, "runner identity matched; batch commit pending");
        return 0;
    }

    private async Task<int> VerifyRuntimeStageAsync(UpdateContext context, CancellationToken token)
    {
        context.Stage = UpdateStage.VerifyRuntime;
        _out.WriteLine(StageLabels.VerifyRuntime);
        context.RecordStage(StageLabels.VerifyRuntime, "starting runtime consistency checks");

        if (context.DryRun)
        {
            _out.WriteLine("Dry run: would verify CLI binary, web assets, runner connection, and managed skill assets after activation verifies server and runner identities.");
            context.Outcome = UpdateOutcome.Ready;
            context.RecordStage(StageLabels.VerifyRuntime, "skipped (dry run)");
            return 0;
        }

        var checks = new List<RuntimeCheckResult>
        {
            await _validator.CheckCliBinaryAsync(context, token),
            await _validator.CheckWebAssetsAsync(context, token),
        };
        if (context.RunnerInstalled)
            checks.Add(await _validator.CheckRunnerConnectionAsync(context, token));
        checks.Add(await _validator.CheckManagedSkillAssetsAsync(context, token));

        foreach (var check in checks)
        {
            context.RecordRuntimeCheck(check);
            switch (check.Outcome)
            {
                case RuntimeCheckOutcome.Pass:
                    _out.WriteLine($"  [ok] {check.Component}: {check.Message}");
                    break;
                case RuntimeCheckOutcome.Warn:
                    _out.WriteLine($"  [warn] {check.Component}: {check.Message}");
                    break;
                case RuntimeCheckOutcome.Fail:
                    _err.WriteLine($"  [fail] {check.Component}: {check.Message}");
                    break;
            }
        }

        if (checks.Any(c => c.Outcome == RuntimeCheckOutcome.Fail))
        {
            var firstFailure = checks.First(c => c.Outcome == RuntimeCheckOutcome.Fail);
            var capability = string.Equals(firstFailure.Component, "Runner connection", StringComparison.Ordinal)
                ? "Runner unavailable"
                : firstFailure.Component;
            context.UnavailableCapability ??= capability;
            context.Outcome = UpdateOutcome.Failed;
            context.RecordStage(StageLabels.VerifyRuntime, $"failed: {capability}");
            return 1;
        }

        if (checks.Any(c => c.Outcome == RuntimeCheckOutcome.Warn))
        {
            context.Outcome = UpdateOutcome.Recovered;
            context.RecordStage(StageLabels.VerifyRuntime, "recovered with warnings");
            return 0;
        }

        context.Outcome = UpdateOutcome.Ready;
        context.RecordStage(StageLabels.VerifyRuntime, "all checks passed");
        return 0;
    }

    private async Task<StageOutcome> RunStageMachineAsync(UpdateContext context, Func<UpdateContext, CancellationToken, Task<int>> stage)
    {
        if (context.CancellationToken.IsCancellationRequested)
        {
            context.Interrupted = true;
            return new StageOutcome(false, 130, new OperationCanceledException(context.CancellationToken));
        }

        try
        {
            var exitCode = await stage(context, context.CancellationToken);
            context.LastExitCode = exitCode;
            if (context.CancellationToken.IsCancellationRequested)
            {
                context.Interrupted = true;
                return new StageOutcome(false, 130, new OperationCanceledException(context.CancellationToken));
            }
            return new StageOutcome(exitCode == 0, exitCode, null);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            context.Interrupted = true;
            return new StageOutcome(false, 130, null);
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Update stage failed: {ex.GetType().Name}: {ex.Message}");
            return new StageOutcome(false, 1, ex);
        }
    }

    private async Task<StageOutcome> RunRecoveryStageAsync(UpdateContext context, Func<UpdateContext, CancellationToken, Task<int>> stage)
    {
        using var recoveryCts = new CancellationTokenSource();
        using var recoveryTimer = StartTimeoutTimer(recoveryCts, TimeSpan.FromSeconds(30));

        try
        {
            var exitCode = await stage(context, recoveryCts.Token);
            return new StageOutcome(exitCode == 0, exitCode, null);
        }
        catch (OperationCanceledException) when (recoveryCts.IsCancellationRequested)
        {
            return new StageOutcome(false, 1, new OperationCanceledException(recoveryCts.Token));
        }
        catch (Exception ex)
        {
            _err.WriteLine($"Update recovery stage failed: {ex.GetType().Name}: {ex.Message}");
            return new StageOutcome(false, 1, ex);
        }
    }

    private ITimer? StartTimeoutTimer(CancellationTokenSource cts, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            cts.Cancel();
            return null;
        }

        return _timeProvider.CreateTimer(
            static state =>
            {
                try
                {
                    ((CancellationTokenSource)state!).Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            },
            cts,
            timeout,
            Timeout.InfiniteTimeSpan);
    }
}
