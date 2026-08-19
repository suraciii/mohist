using System.Text.Json.Serialization;

namespace Mohist.Server.Workflow.Domain.Run;

/// <summary>
/// Terminal outcome of a verification lane attempt. Three values, deliberately
/// not modelled as a third <see cref="TaskReportStatus"/>: a recognized lane's
/// pass/fail/timeout is derived from the existing task-report contract at the
/// verification boundary, so unrelated task consumers see no protocol change.
/// </summary>
public enum VerificationLaneOutcome
{
    Pending = 0,
    Pass = 1,
    Fail = 2,
    Timeout = 3,
}

[GenerateSerializer]
public sealed record VerificationLaneAttempt(
    [property: Id(0)] string LaneId,
    [property: Id(1)] int Order,
    [property: Id(2)] int ConfiguredBudgetMs,
    [property: Id(3)] VerificationLaneOutcome Outcome,
    [property: Id(4)] string TaskRunId,
    [property: Id(5)] string? WorkId = null,
    [property: Id(6)] ExecutionError? Error = null,
    [property: Id(7)] string? Detail = null,
    [property: Id(8)] DateTimeOffset? FinishedAt = null);

public static class VerificationLaneOutcomeExtensions
{
    public static string WireValue(this VerificationLaneOutcome outcome) => outcome switch
    {
        VerificationLaneOutcome.Pass => "pass",
        VerificationLaneOutcome.Fail => "fail",
        VerificationLaneOutcome.Timeout => "timeout",
        _ => "pending",
    };
}