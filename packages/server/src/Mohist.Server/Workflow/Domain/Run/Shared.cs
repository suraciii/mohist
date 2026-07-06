using System.Text.RegularExpressions;
namespace Mohist.Server.Workflow.Domain.Run;

public enum FailureReason { TaskFailed, CheckUnrepaired, ApprovalRejected, ContextExhaustion }

public sealed record FailureDetails(
    FailureReason Reason,
    string Stage,
    string? TaskId = null,
    string? CheckName = null,
    string? Message = null);

public sealed record TaskResult(
    string Status,
    string? Reason = null);

/// <summary>
/// The per-run head ref the runner prepares inside the workflow
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
        /// executed by a runner (a Running task, an open checks batch, or a
        /// Running check). The workflow invariant is that at most one stage
        /// executes at a time, so only the current stage can carry in-flight
        /// work; scanning completed stages would make this predicate depend
        /// on residual dispatch metadata (e.g. a stale <c>ChecksWorkId</c>)
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
        /// Clears the current stage's residual awaiting-approval gate, when one
        /// is present. Idempotent: returns <c>true</c> only when it actually
        /// mutated state, so repeated activations do not write-amplify.
        ///
        /// Called from <see cref="WorkflowRunExtensions.Stop"/> before the run
        /// is transitioned to <see cref="WorkflowRunStatus.Stopped"/>, and from
        /// <c>WorkflowGrain.OnActivateAsync</c> over rehydrated state when the
        /// run is already <see cref="WorkflowRunStatus.Stopped"/>. The guard is
        /// the residual-gate predicate (<see cref="StageRun.IsAwaitingApproval"/>)
        /// rather than the run status, so the same method serves both call
        /// sites: at the <c>Stop()</c> site the run is not yet <c>Stopped</c>,
        /// while the grain-activate caller scopes invocation to <c>Stopped</c>
        /// runs so a live run genuinely awaiting approval is never disturbed.
        ///
        /// The stage is left as <see cref="StageRunStatus.Running"/> — matching
        /// the <c>AddRuntimeTasks</c> approval-invalidation pattern and the
        /// existing stop-from-Ready semantics. The run-level <c>Stopped</c> is
        /// the authoritative terminal signal.
        /// </summary>
        public bool ReconcileStoppedApprovalGate()
        {
            if (run.CurrentStageId is null) return false;
            var current = run.Stages.FirstOrDefault(s => string.Equals(s.Id, run.CurrentStageId, StringComparison.Ordinal));
            if (current is null || !current.IsAwaitingApproval) return false;

            current.ApprovalStatus = null;
            current.Status = StageRunStatus.Running;
            return true;
        }
    }

    /// <summary>
    /// Resolves the waiting-for-dispatch status. Does NOT mutate; callers that
    /// assign the result to <see cref="WorkflowRun.Status"/> should route the
    /// assignment through <see cref="SetStatus"/>, or use
    /// <see cref="ApplyWaitingForDispatchStatus"/> directly, so that
    /// <see cref="WorkflowRun.ReadySince"/> is seeded on Ready entry.
    /// </summary>
    private static WorkflowRunStatus WaitingForDispatchStatus(WorkflowRun run) =>
        run.Assignment is null ? WorkflowRunStatus.Pending : WorkflowRunStatus.Ready;

    private static WorkflowRunStatus ActiveOrWaitingForDispatchStatus(WorkflowRun run) =>
        run.HasInFlightWork() ? WorkflowRunStatus.Running : WaitingForDispatchStatus(run);

    /// <summary>
    /// Applies <see cref="WaitingForDispatchStatus"/>, seeding
    /// <see cref="WorkflowRun.ReadySince"/> whenever the run (re-)enters Ready.
    /// Single chokepoint for every Ready transition driven by work draining or
    /// stages advancing; <see cref="WorkflowAssignment.AssignTo"/> covers the
    /// first Ready entry (assignment).
    /// </summary>
    private static void ApplyWaitingForDispatchStatus(WorkflowRun run)
        => SetStatus(run, WaitingForDispatchStatus(run));

    private static void ApplyActiveOrWaitingForDispatchStatus(WorkflowRun run)
        => SetStatus(run, ActiveOrWaitingForDispatchStatus(run));

    /// <summary>
    /// Assigns <see cref="WorkflowRun.Status"/> and seeds
    /// <see cref="WorkflowRun.ReadySince"/> (the fairness ordering key) at the
    /// moment the run enters Ready. Leaving Ready does not clear it; re-entry
    /// overwrites. Use this for every run-status assignment that may resolve to
    /// Ready so seeding stays consistent.
    /// </summary>
    private static void SetStatus(WorkflowRun run, WorkflowRunStatus next)
    {
        if (next == WorkflowRunStatus.Ready && run.Status != WorkflowRunStatus.Ready)
            run.ReadySince = DateTimeOffset.UtcNow;
        run.Status = next;
    }
}
