namespace Mohist.Server.SystemInfo;

public sealed record SystemUpdateJobState(
    string JobId,
    string Status,
    string Stage,
    bool UpdateAvailable,
    string? RunningGitHash,
    string? SourceHead,
    string? SourcePath,
    string? ServerUnit,
    string? RunnerUnit,
    string? Reason,
    IReadOnlyList<SystemUpdateLogEntry> Logs,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    string? Outcome = null,
    string? UnavailableCapability = null,
    IReadOnlyList<SystemUpdateRecoveryWorkOutcome>? Recovery = null)
{
    public static readonly IReadOnlyList<string> ActiveStatuses = ["running", "waiting-for-reconnect"];
    public static readonly IReadOnlyList<string> TerminalStatuses = ["succeeded", "failed", "recovered", "superseded", "cancelled"];
}