using System.Text.RegularExpressions;
namespace Mohist.Server.Workflow.Domain.Run;

// ContextExhaustion is legacy: produced only by the removed session-health
// gate. Kept so persisted runs that still carry it deserialize
// and can be retried. Nothing in the system produces it anymore.
public enum FailureReason { TaskFailed, CheckFailed, ApprovalRejected, ContextExhaustion, DefinitionResolutionFailed }

[GenerateSerializer]
public sealed record ExecutionError(
    [property: Id(0)] string Code,
    [property: Id(1)] string Message);

public sealed record FailureDetails(
    FailureReason Reason,
    string Stage,
    string? TaskId = null,
    string? CheckName = null,
    string? Message = null,
    ExecutionError? Error = null);

public sealed record TaskResult(
    string Status,
    string? Reason = null,
    ExecutionError? Error = null);

/// <summary>
/// The per-run head ref the execution plane prepares inside the workflow
/// workspace. MUST stay in sync with the runner's <c>runBranchName()</c>
/// helper in <c>packages/runner/src/runtime/workspace.ts</c>.
/// </summary>
public static class WorkflowRunBranch
{
    private static readonly Regex SafeChars = new("[^A-Za-z0-9_-]", RegexOptions.Compiled);

    public static string For(string? runId)
    {
        if (string.IsNullOrEmpty(runId)) return "mohist/run";
        var safe = SafeChars.Replace(runId, string.Empty);
        return string.IsNullOrEmpty(safe) ? "mohist/run" : $"mohist/run-{safe}";
    }
}

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun run)
    {
        public bool IsTerminal() => run.Status.IsTerminal();

        /// <summary>
        /// True when the <see cref="CurrentStage"/> has work currently being
        /// executed by a worker (a Running task, an open checks batch, or a
        /// Running check). The workflow invariant is that at most one stage
        /// executes at a time, so only the current stage can carry in-flight
        /// work; scanning completed stages would make this predicate depend
        /// on stale dispatch metadata (e.g. a stale <c>ChecksWorkId</c>)
        /// that is irrelevant to whether work is in flight <em>now</em>.
        /// </summary>
        public bool HasInFlightWork()
        {
            if (run.CurrentStageId is null) return false;
            var current = run.Stages.FirstOrDefault(s => string.Equals(s.Id, run.CurrentStageId, StringComparison.Ordinal));
            if (current is null) return false;
            return current.Tasks.Any(t => t.Status == TaskRunStatus.Running)
                || !string.IsNullOrWhiteSpace(current.ChecksWorkId)
                || current.Checks.Any(c => c.Status == StageCheckStatus.Running);
        }

        public bool HasDispatchableWork()
        {
            if (run.CurrentStageId is null) return false;
            var current = run.Stages.FirstOrDefault(s => string.Equals(s.Id, run.CurrentStageId, StringComparison.Ordinal));
            if (current is null || !current.Initialized) return false;

            return current.Tasks.Any(t => t.Status == TaskRunStatus.Pending)
                || current.Checks.Any(c => c.Status == StageCheckStatus.Pending);
        }

        /// <summary>
        /// Clears an unresolved approval gate without resolving approval. The
        /// stage remains Running; the run status is the terminal signal.
        /// </summary>
        public bool ClearStaleApprovalGate()
        {
            if (run.CurrentStageId is null) return false;
            var current = run.Stages.FirstOrDefault(s => string.Equals(s.Id, run.CurrentStageId, StringComparison.Ordinal));
            if (current is null || !current.IsAwaitingApproval) return false;

            current.ApprovalStatus = null;
            current.Status = StageRunStatus.Running;
            return true;
        }
    }

    private static WorkflowRunStatus WaitingForDispatchStatus(WorkflowRun run) =>
        run.Assignment is null ? WorkflowRunStatus.Pending : WorkflowRunStatus.Ready;

    private static WorkflowRunStatus ActiveOrWaitingForDispatchStatus(WorkflowRun run) =>
        run.HasInFlightWork() ? WorkflowRunStatus.Running : WaitingForDispatchStatus(run);

    private static void ApplyWaitingForDispatchStatus(WorkflowRun run, DateTimeOffset now)
        => SetStatusAndTrackReadySince(run, WaitingForDispatchStatus(run), now);

    private static void ApplyActiveOrWaitingForDispatchStatus(WorkflowRun run, DateTimeOffset now)
        => SetStatusAndTrackReadySince(run, ActiveOrWaitingForDispatchStatus(run), now);

    private static void SetStatusAndTrackReadySince(WorkflowRun run, WorkflowRunStatus next, DateTimeOffset now)
    {
        if (next == WorkflowRunStatus.Ready && run.Status != WorkflowRunStatus.Ready)
            run.ReadySince = now;
        run.Status = next;
    }
}
