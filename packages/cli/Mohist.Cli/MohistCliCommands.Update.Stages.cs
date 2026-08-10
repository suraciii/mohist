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
            context.UnavailableCapability ??= "Runner not installed";
            _err.WriteLine("Full update requires an installed managed runner.");
            _err.WriteLine("Install the runner with: mo install runner");
            return 1;
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
            context.RecordStage(StageLabels.PrepareRunner, "runner not running; nothing to stop");
            _out.WriteLine("Runner was not running; nothing to stop for the server update.");
            return 0;
        }

        context.RecordStage(StageLabels.PrepareRunner, "stopping runner for server update");
        var stop = await _operations.StopRunnerAsync(context.DryRun);
        if (stop != 0)
        {
            context.RecordStage(StageLabels.PrepareRunner, "stop failed");
            return stop;
        }

        context.RunnerStopped = true;
        _out.WriteLine("Runner is stopped. Workflows cannot run until the runner is restored.");
        context.RecordStage(StageLabels.PrepareRunner, "runner stopped; workflows paused");
        return 0;
    }

    private async Task<int> UpdateServerStageAsync(UpdateContext context, CancellationToken token)
    {
        context.Stage = UpdateStage.UpdateServer;
        _out.WriteLine(StageLabels.UpdateServer);
        context.RecordStage(StageLabels.UpdateServer, "building and restarting server");

        if (context.DryRun)
        {
            _out.WriteLine($"  cd {_operations.ResolveRepoRoot(context.RepoRoot)} && dotnet build Mohist.sln");
            _out.WriteLine($"  {UpdateOperations.RestartCommandLine("server")} (if installed)");
            context.RecordStage(StageLabels.UpdateServer, "complete (dry run)");
            return 0;
        }

        var root = _operations.ResolveRepoRoot(context.RepoRoot);
        var exitCode = await _operations.BuildAndRestartServerAsync(root, token);
        if (exitCode != 0)
        {
            context.RecordStage(StageLabels.UpdateServer, "failed");
            return exitCode;
        }
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
            return 1;
        }
        context.RecordStage(StageLabels.WaitingForReady, "server is ready");
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

        if (!context.DryRun)
        {
            var root = _operations.ResolveRepoRoot(context.RepoRoot);
            var build = await _operations.BuildRunnerAsync(root);
            if (build != 0)
            {
                context.RecordStage(StageLabels.RestoreRunner, "runner build failed");
                context.UnavailableCapability ??= "Runner unavailable";
                return build;
            }
            _out.WriteLine("Runner updated successfully.");
        }
        else
        {
            var root = _operations.ResolveRepoRoot(context.RepoRoot);
            _out.WriteLine($"  cd {root} && npm run build -w packages/runner");
        }

        var start = await _operations.StartRunnerAsync(context.DryRun);
        if (start != 0)
        {
            context.RecordStage(StageLabels.RestoreRunner, "failed to start");
            context.UnavailableCapability ??= "Runner unavailable";
            return start;
        }

        if (!context.DryRun)
        {
            _out.WriteLine("Waiting for runner service to become active...");
            using var activeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var activeTimer = StartTimeoutTimer(activeCts, RunnerActiveTimeout);
            var becameActive = false;
            while (!activeCts.IsCancellationRequested)
            {
                if (await _operations.IsRunnerRunningAsync(activeCts.Token))
                {
                    becameActive = true;
                    break;
                }

                try
                {
                    await Task.Delay(RunnerActivePollInterval, _timeProvider, activeCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (!becameActive)
            {
                context.RecordStage(StageLabels.RestoreRunner, "runner did not become active in time");
                context.UnavailableCapability ??= "Runner unavailable";
                return 1;
            }
        }

        context.RunnerRestored = true;
        _out.WriteLine("Runner service restored.");
        context.RecordStage(StageLabels.RestoreRunner, "runner started");
        return 0;
    }

    private async Task<int> VerifyRuntimeStageAsync(UpdateContext context, CancellationToken token)
    {
        context.Stage = UpdateStage.VerifyRuntime;
        _out.WriteLine(StageLabels.VerifyRuntime);
        context.RecordStage(StageLabels.VerifyRuntime, "starting runtime consistency checks");

        if (context.DryRun)
        {
            _out.WriteLine("Dry run: would verify CLI binary, server identity, web assets, runner connection, runner identity, and managed skill assets.");
            context.Outcome = UpdateOutcome.Ready;
            context.RecordStage(StageLabels.VerifyRuntime, "skipped (dry run)");
            return 0;
        }

        var checks = new List<RuntimeCheckResult>
        {
            await _validator.CheckCliBinaryAsync(context, token),
            await _validator.CheckServerIdentityAsync(context, token),
            await _validator.CheckWebAssetsAsync(context, token),
            await _validator.CheckRunnerConnectionAsync(context, token),
            await _validator.CheckRunnerIdentityAsync(context, token),
            await _validator.CheckManagedSkillAssetsAsync(context, token),
        };

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
