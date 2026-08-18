using System.Net.Http.Json;

namespace Mohist.Cli;

internal partial class SourceCodeUpdater
{
    internal async Task<bool> PostCliOutcomeAsync(UpdateContext context, CancellationToken token)
    {
        if (context.DryRun)
        {
            _out.WriteLine("Dry run: would POST update outcome to server.");
            return false;
        }

        if (context.StageLogEntries.Count == 0)
        {
            // Nothing to report.
            return false;
        }

        var (status, outcomeLabel) = ResolveOutcomeStatus(context);
        var stage = context.StageLogEntries[^1].Stage;
        var unavailableCapability = !string.IsNullOrEmpty(context.UnavailableCapability)
            ? context.UnavailableCapability
            : null;

        var logs = context.StageLogEntries
            .Select(e => new CliOutcomeLogEntry(e.At, e.Stage, e.Message))
            .ToList();

        var recovery = context.RunnerRecovery?.Works
            .Select(work => new CliRecoveryWorkOutcome(
                work.Identity.OwnerKind,
                work.Identity.OwnerId,
                work.Identity.WorkId,
                work.Identity.TaskRunId,
                work.Identity.WorkType,
                work.Status,
                work.State))
            .ToArray();
        var payload = new CliOutcomeRequest(
            JobId: context.JobId,
            Status: status,
            Stage: stage,
            Outcome: outcomeLabel,
            UnavailableCapability: unavailableCapability,
            Logs: logs,
            SourceHead: context.SourceHead,
            Recovery: recovery);

        using var postCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        postCts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/system/update/outcome")
            {
                Content = JsonContent.Create(payload, options: CliOutcomeJson.Options),
            };
            return await _outcomeReporter.PostAsync(request, postCts.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            _out.WriteLine("Could not persist update outcome to server (timed out). The CLI terminal output above is the authoritative result.");
            return false;
        }
        catch (Exception ex)
        {
            _out.WriteLine($"Could not persist update outcome to server: {ex.GetType().Name}: {ex.Message}. The CLI terminal output above is the authoritative result.");
            return false;
        }
    }

    private static (string Status, string Outcome) ResolveOutcomeStatus(UpdateContext context)
    {
        if (!string.IsNullOrEmpty(context.UnavailableCapability))
            return ("failed", "failed");

        if (context.Interrupted)
            return ("cancelled", "failed");

        if (context.RunnerRecovery is { HasAffectedWork: true, FullyRecovered: false })
            return ("failed", "failed");

        return context.Outcome switch
        {
            UpdateOutcome.Recovered => ("recovered", "recovered"),
            UpdateOutcome.Failed => ("failed", "failed"),
            UpdateOutcome.Ready when context.LastExitCode != 0 => ("failed", "failed"),
            UpdateOutcome.Ready => ("succeeded", "succeeded"),
            _ when context.LastExitCode != 0 => ("failed", "failed"),
            _ => ("succeeded", "succeeded"),
        };
    }
}
