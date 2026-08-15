namespace Mohist.Server.Infrastructure.Data.AgentJobs;

/// <summary>
/// Indexed AgentJob owner ledger. The row is the authoritative durable
/// source for an AgentJob's lifecycle JSON, scheduled dispatch
/// projection, and immutable dispatch snapshot. Owners read and write the
/// row through <see cref="IAgentJobStore"/> with optimistic revision
/// checking; one transaction updates the state JSON and every scheduling
/// column so a poll either sees the complete old ledger or the complete
/// new ledger.
/// </summary>
public class AgentJobRow
{
    public string JobKey { get; set; } = string.Empty;
    public string State { get; set; } = "{}";

    /// <summary>
    /// Optimistic-concurrency ETag. Bumped on every successful save by
    /// the grain; concurrent writers retry against the new value.
    /// </summary>
    public long Revision { get; set; }

    public string? ProjectId { get; set; }
    public string? AgentId { get; set; }
    public string? Status { get; set; }
    public string? SubmittedAt { get; set; }
    public string? TerminalAt { get; set; }

    /// <summary>Runner the AgentJob is assigned to (Pending) or running on
    /// (Running). Null for terminal jobs and for unassigned pending jobs
    /// that have not yet found an eligible runner.</summary>
    public string? AssignedRunnerId { get; set; }

    /// <summary>Stable work identity the runner uses to track this job.
    /// Persisted alongside the dispatch snapshot so redelivery and
    /// reconciliation can recover without the legacy Runner work record.
    /// </summary>
    public string? WorkId { get; set; }

    /// <summary>UTC timestamp the AgentJob entered the pending state
    /// (admission time for newly admitted jobs; the migration timestamp
    /// for migrated legacy rows). Drives the owner readiness timeout.
    /// Null for non-pending jobs.</summary>
    public string? ReadySince { get; set; }

    /// <summary>UTC timestamp the AgentJob entered the running state.
    /// Drives the existing execution timeout. Null for non-running jobs.
    /// </summary>
    public string? RunningSince { get; set; }

    /// <summary>Immutable dispatch envelope snapshot persisted at
    /// admission. Populated only when the row represents a dispatchable
    /// work item (Pending with an assigned runner, or Running).</summary>
    public string? DispatchJson { get; set; }

    public string? WorkType { get; set; }
    public string? Stage { get; set; }
    public string? Title { get; set; }

    /// <summary>ProjectId snapshot for issue/work-tree lookups. Mirrors
    /// the Input.ProjectId field; populated by the migration and by the
    /// grain on save so poll-time queries can read it without scanning
    /// state JSON.</summary>
    public string? IssueProjectId { get; set; }

    public int? IssueNumber { get; set; }
    public string? AgentSessionId { get; set; }
    public string? InitialInputId { get; set; }
    public string? InitialTurnId { get; set; }
    public string? PinnedRunnerId { get; set; }
    public string LaunchVisibility { get; set; } = "visible";

    /// <summary>
    /// Strict, allowlisted direct-API snapshot. It is committed in the same
    /// transaction as the source ledger state and never aliases State.
    /// </summary>
    public string? DirectApiProjectionJson { get; set; }

    /// <summary>AgentJob ledger revision covered by DirectApiProjectionJson.</summary>
    public long? DirectApiProjectionRevision { get; set; }
}
