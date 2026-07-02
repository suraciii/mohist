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

        public bool HasInFlightWork() => run.Stages.Any(stage =>
            stage.Tasks.Any(t => t.Status == TaskRunStatus.Running)
            || !string.IsNullOrWhiteSpace(stage.ChecksWorkId)
            || stage.Checks.Any(c => c.Status == StageCheckStatus.Running));

        public bool HasDispatchableWork()
        {
            if (run.CurrentStageId is null) return false;
            var current = run.Stages.FirstOrDefault(s => string.Equals(s.Id, run.CurrentStageId, StringComparison.Ordinal));
            if (current is null || !current.Initialized) return false;

            return current.Tasks.Any(t => t.Status == TaskRunStatus.Pending)
                || current.Checks.Any(c => c.Status == StageCheckStatus.Pending);
        }

        public bool ReconcileReadyStatusWithInFlightWork()
        {
            if (run.Status != WorkflowRunStatus.Ready || !run.HasInFlightWork()) return false;

            run.Status = WorkflowRunStatus.Running;
            return true;
        }
    }

    private static WorkflowRunStatus WaitingForDispatchStatus(WorkflowRun run) =>
        run.Assignment is null ? WorkflowRunStatus.Pending : WorkflowRunStatus.Ready;

    private static WorkflowRunStatus ActiveOrWaitingForDispatchStatus(WorkflowRun run) =>
        run.HasInFlightWork() ? WorkflowRunStatus.Running : WaitingForDispatchStatus(run);
}
