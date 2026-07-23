namespace Mohist.Cli;

internal partial class SourceCodeUpdater
{
    private async Task<int> FinalizeAsync(UpdateContext context, int exitCode)
    {
        context.Stage = UpdateStage.Complete;

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
            if (string.Equals(context.UnavailableCapability, "Runner unavailable", StringComparison.Ordinal))
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
