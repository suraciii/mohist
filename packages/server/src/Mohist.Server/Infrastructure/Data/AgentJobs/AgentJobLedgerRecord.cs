namespace Mohist.Server.Infrastructure.Data.AgentJobs;

/// <summary>
/// Full AgentJob ledger record returned by
/// <see cref="IAgentJobStore.LoadLedgerAsync"/>. Carries the serialized
/// lifecycle JSON plus the indexed scheduling columns and the optimistic
/// revision ETag the grain uses to fence concurrent writes.
/// </summary>
public sealed record AgentJobLedgerRecord(
    string JobKey,
    string StateJson,
    long Revision,
    string? AssignedRunnerId,
    string? WorkId,
    DateTimeOffset? ReadySince,
    DateTimeOffset? RunningSince,
    string? DispatchJson,
    string? WorkType,
    string? Stage,
    string? Title,
    string? IssueProjectId,
    int? IssueNumber,
    string? AgentSessionId,
    string? InitialInputId,
    string? InitialTurnId,
    string? PinnedRunnerId = null,
    string LaunchVisibility = "visible");

/// <summary>
/// Sentinel for an optimistic-concurrency conflict on a save. Thrown when
/// the supplied <see cref="AgentJobLedgerRecord.Revision"/> no longer
/// matches the row's current revision; the grain reloads and retries.
/// </summary>
public sealed class AgentJobLedgerConflictException : Exception
{
    public AgentJobLedgerConflictException(string message) : base(message) { }
}

/// <summary>
/// Raised when the migration or the grain cannot reconstruct a dispatch
/// ledger from a nonterminal legacy row. The migration aborts inside the
/// transaction when this is raised; the grain surfaces the failure to the
/// caller instead of creating a half-baked ledger row.
/// </summary>
public sealed class AgentJobLedgerReconstructionException : Exception
{
    public AgentJobLedgerReconstructionException(string message) : base(message) { }
}
