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
