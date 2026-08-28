using Mohist.Server.Agent.Grains;

namespace Mohist.Server.Sessions.Services;

/// <summary>
/// The single retryability decision for failed Agent turns. The comparison is
/// deliberately exact and ordinal: the category is an execution fact, while
/// the human-readable failure reason is not a classification input.
/// </summary>
public static class AgentSessionRetryPolicy
{
    private static readonly HashSet<string> RetryableCategories = new(StringComparer.Ordinal)
    {
        AgentJobFailureReasons.RunnerUnavailable,
        AgentJobFailureReasons.RunnerLost,
        AgentJobFailureReasons.ReportTimeout,
        "deadline-exceeded",
        "timeout",
        "generation-drain-timeout",
        "unavailable-runtime",
        "runtime-unavailable",
        "rate-limited",
        "probe-timeout",
        "skill-not-found",
        "retry-safe",
    };

    public static bool IsRetryable(string? failureCategory) =>
        failureCategory is not null
        && RetryableCategories.Contains(failureCategory);
}
