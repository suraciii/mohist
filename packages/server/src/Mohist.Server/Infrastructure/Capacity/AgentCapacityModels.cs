using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Infrastructure.Capacity;

public interface IAgentCapacityStore
{
    Task<IReadOnlyDictionary<string, AgentCapacitySnapshot>> ReadAsync(
        string projectId,
        IReadOnlyCollection<string> agentIds,
        CancellationToken ct = default);

    Task<AgentJobCapacityClaimResult> ClaimJobAsync(
        string jobKey,
        long expectedRevision,
        CancellationToken ct = default);

    /// <summary>
    /// Claims a queued follow-up Turn against the exact State document read
    /// after the Session owner flushed. The token must be the actual persisted
    /// document, not a reserialization of a hydrated Session. A successful call
    /// returns the complete committed Session for the owner to install.
    /// </summary>
    Task<AgentTurnCapacityClaimResult> ClaimTurnAsync(
        string sessionId,
        string expectedStateJson,
        string turnId,
        CancellationToken ct = default);
}

public enum AgentCapacityClaimDisposition
{
    Claimed,
    AlreadyClaimed,
    CapacityFull,
    NotEligible,
    NotInOrder,
    Incomplete,
    Conflict,
}

public enum AgentCapacityEvidenceStatus
{
    Complete,
    MissingDefinition,
    MalformedDefinition,
    IncompleteOwnerEvidence,
}

public enum AgentCapacityOwnerKind
{
    Job,
    Turn,
}

public sealed record AgentCapacityQueueEntry(
    AgentCapacityOwnerKind Kind,
    string OwnerId,
    string? TurnId,
    DateTimeOffset AcceptedAt,
    long TurnSequence = 0);

/// <summary>
/// Transient derived read of one Agent's capacity facts.
/// <see cref="Eligible"/> is the admission FIFO (unclaimed heads only);
/// <see cref="Queued"/> is the complete waiting-work projection — every
/// accepted visible Pending Job and every current nonsuperseded ordinary
/// queued Turn, claimed or locally blocked. Neither list is persisted
/// authority; both are re-derived per read.
/// </summary>
public sealed record AgentCapacitySnapshot(
    string ProjectId,
    string AgentId,
    AgentCapacityEvidenceStatus EvidenceStatus,
    int? MaxConcurrentRuns,
    int? Occupied,
    IReadOnlyList<AgentCapacityQueueEntry> Eligible,
    IReadOnlyList<AgentCapacityQueueEntry> Queued)
{
    public bool IsComplete => EvidenceStatus == AgentCapacityEvidenceStatus.Complete;
    public bool IsUnlimited => IsComplete && MaxConcurrentRuns is null;
}

public sealed record AgentJobCapacityClaimResult(
    AgentCapacityClaimDisposition Disposition,
    AgentCapacitySnapshot? Capacity,
    AgentJobLedgerRecord? Job = null);

public sealed record AgentTurnCapacityClaimResult(
    AgentCapacityClaimDisposition Disposition,
    AgentCapacitySnapshot? Capacity,
    AgentSession? Session = null);
