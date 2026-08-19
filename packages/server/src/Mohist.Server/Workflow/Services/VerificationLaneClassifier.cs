using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Services;

/// <summary>
/// Classifies task reports at the verification boundary. A successful script
/// report becomes <see cref="VerificationLaneOutcome.Pass"/>; a normal script
/// failure becomes <see cref="VerificationLaneOutcome.Fail"/>; an
/// <c>error.code=timeout</c> becomes <see cref="VerificationLaneOutcome.Timeout"/>.
///
/// The classification is performed ONLY for tasks whose <c>DefinitionId</c> is
/// a recognized lane id in <see cref="VerificationLaneCatalog"/>; the
/// <c>recover:fix-ci</c> helper is intentionally NOT a verification lane and
/// cannot promote a lane to <c>pass</c>. The Runner's recovery scheduling
/// envelope (<c>status=completed</c>, <c>addTasks</c>, underlying error) is
/// not a pass either; only a direct successful lane report for the failed
/// lane can change the lane to <c>pass</c>.
/// </summary>
public static class VerificationLaneClassifier
{
    public static bool IsRecognizedLaneTask(string? definitionId) =>
        VerificationLaneCatalog.IsKnownLane(definitionId);

    /// <summary>
    /// Classifies a task report into a lane outcome for a recognized lane
    /// attempt. Returns <c>null</c> when the task is not a recognized lane
    /// (e.g. a <c>recover:fix-ci</c> helper); callers must not synthesize
    /// lane state for non-lane tasks.
    /// </summary>
    public static VerificationLaneOutcome? Classify(string? definitionId, TaskReport report)
    {
        if (!IsRecognizedLaneTask(definitionId)) return null;

        if (report.Status == TaskReportStatus.Succeeded)
        {
            // The Runner's recovery scheduling envelope marks outer
            // status=completed with addTasks; that is NOT a lane pass even
            // though the underlying status is "completed". A pass requires a
            // direct successful lane report with no recovery follow-ups.
            if (report.AddTasks is { Count: > 0 })
            {
                return string.Equals(report.Error?.Code, "timeout", StringComparison.Ordinal)
                    ? VerificationLaneOutcome.Timeout
                    : VerificationLaneOutcome.Fail;
            }
            return VerificationLaneOutcome.Pass;
        }

        // TaskReportStatus.Failed: distinguish timeout from ordinary failure
        // via the underlying ExecutionError code. The Runner's core/script
        // timeout code is `timeout` per the runner timeout contract.
        if (string.Equals(report.Error?.Code, "timeout", StringComparison.Ordinal))
            return VerificationLaneOutcome.Timeout;
        return VerificationLaneOutcome.Fail;
    }
}