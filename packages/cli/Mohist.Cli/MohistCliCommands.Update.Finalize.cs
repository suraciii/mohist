namespace Mohist.Cli;

internal partial class SourceCodeUpdater
{
    private async Task<int> FinalizeAsync(UpdateContext context, int exitCode)
    {
        context.Stage = UpdateStage.Complete;

        var commitBatch = exitCode == 0 && !context.Interrupted;
        if (commitBatch)
        {
            context.RuntimeBatch.Commit();
            context.RunnerCandidate?.Dispose();
            context.RunnerCandidate = null;
        }
        else
        {
            await RestoreStagedRuntimeBatchAsync(context, exitCode);
            await RestoreUnverifiedRuntimeTransactionsAsync(context);
            context.RunnerCandidate?.Dispose();
            context.RunnerCandidate = null;
            await RestorePriorRunnerIfStoppedAsync(context);
        }

        if (context.RunnerWasRunning && !context.RunnerRestored)
        {
            if (context.Interrupted)
                _err.WriteLine("Update was interrupted and the runner was stopped. Runner restore was attempted.");
            else
                _err.WriteLine("Update failed after the runner was stopped. Runner restore was attempted.");
        }

        await Task.CompletedTask;

        var finalExit = FinalizeExitCode(context, exitCode);

        if (ShouldPostOutcome(context))
        {
            await PostCliOutcomeAsync(context, context.CancellationToken);
        }
        else if (context.Interrupted)
        {
            _out.WriteLine("Update was cancelled. The local terminal output above is the authoritative result; no outcome was posted to the server.");
        }

        return finalExit;
    }

    private async Task RestoreStagedRuntimeBatchAsync(UpdateContext context, int exitCode)
    {
        if (!context.RuntimeBatch.HasStagedUpdates)
            return;

        var reason = context.Interrupted
            ? "update batch was interrupted after runtime identity verification"
            : $"update batch ended with exit {exitCode} after runtime identity verification";
        foreach (var prepared in context.RuntimeBatch.ReverseStaged())
        {
            var result = await _operations.RollBackRuntimeWithResultAsync(
                prepared,
                actualHash: null,
                reason,
                CancellationToken.None);
            context.RuntimeBatch.Remove(prepared);

            if (prepared.Activation.Candidate.Component == ManagedRuntimeComponent.Runner)
            {
                context.RunnerRuntime = null;
                context.RunnerRestored = result.Restored && context.RunnerWasRunning;
            }
            else
            {
                context.ServerRuntime = null;
                context.ServerRuntimeVerified = false;
            }

            if (result.Restored)
                continue;

            context.UnavailableCapability ??= "Runtime recovery failed";
            _err.WriteLine($"Batch recovery failed for {prepared.Activation.Candidate.Component}: {result.Description}.");
        }
    }

    private async Task RestoreUnverifiedRuntimeTransactionsAsync(UpdateContext context)
    {
        if (context.ServerRuntime is not null && !context.ServerRuntimeVerified)
        {
            await _operations.RollBackRuntimeAsync(
                context.ServerRuntime,
                actualHash: null,
                "update ended before server runtime verification completed",
                CancellationToken.None);
            context.ServerRuntime = null;
        }

        if (context.RunnerRuntime is not null && !context.RunnerRestored)
        {
            await _operations.RollBackRuntimeAsync(
                context.RunnerRuntime,
                actualHash: null,
                "update ended before runner runtime verification completed",
                CancellationToken.None);
            context.RunnerRuntime = null;
        }
    }

    private async Task RestorePriorRunnerIfStoppedAsync(UpdateContext context)
    {
        if (!context.RunnerStopped || !context.RunnerWasRunning || context.RunnerRestored)
            return;

        _out.WriteLine(StageLabels.RestoreRunner);
        var start = await _operations.StartRunnerAsync(dryRun: false);
        if (start == 0)
        {
            context.RunnerRestored = true;
            _out.WriteLine("Runner service restored to its pre-update target.");
            return;
        }

        context.UnavailableCapability ??= "Runner unavailable";
        _err.WriteLine("Could not restore the prior Runner service target after the failed update batch.");
    }

    private static bool ShouldPostOutcome(UpdateContext context)
    {
        if (context.DryRun)
            return false;

        if (!string.IsNullOrEmpty(context.UnavailableCapability))
            return true;

        if (context.Interrupted)
            return false;

        return true;
    }

    private int FinalizeExitCode(UpdateContext context, int? overrideExitCode = null)
    {
        var exit = overrideExitCode ?? context.LastExitCode;

        if (context.RunnerWasRunning && !context.RunnerRestored)
        {
            context.UnavailableCapability ??= "Runner unavailable";
        }

        if (context.Interrupted)
        {
            _out.WriteLine("Update was interrupted.");
        }

        if (!string.IsNullOrEmpty(context.UnavailableCapability))
        {
            _err.WriteLine($"Mohist is not fully usable. Unavailable capability: {context.UnavailableCapability}.");
            if (string.Equals(context.UnavailableCapability, "Runner unavailable", StringComparison.Ordinal)
                || (context.RunnerWasRunning && !context.RunnerRestored))
            {
                _err.WriteLine("Start the runner manually with: mo service start runner");
            }
        }
        else if (context.Warnings.Count > 0)
        {
            _out.WriteLine("Mohist is recovered with warnings.");
            foreach (var warning in context.Warnings)
                _out.WriteLine($"  - {warning}");
        }
        else if (exit == 0)
        {
            _out.WriteLine("Update complete. Mohist is ready.");
        }
        else
        {
            _err.WriteLine("Mohist update did not complete successfully.");
        }

        return exit == 0 && context.Interrupted ? 130 : exit;
    }
}
