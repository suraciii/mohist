namespace Mohist.Server.Infrastructure.Data.PublicApi;

/// <summary>
/// One row per public read anchor (an AgentJob, a Session Input, or a
/// Session Turn). The row is the durable public projection surface for
/// its anchor: <see cref="SnapshotJson"/> holds the serialized strict
/// <see cref="Mohist.Server.Api.DirectApi.PublicExecutionRead"/>
/// allowlist (all 22 keys, explicit nulls), and the remaining columns
/// are internal projection bookkeeping that never leaves the boundary.
/// <para>
/// The terminal-fence columns (<see cref="TerminalFact"/> …
/// <see cref="TerminalSequence"/>) store the first winning terminal
/// fact for the anchor. Once they are set, later stale outbox facts,
/// delayed Runner results, or replayed projector input cannot revert
/// the anchor to a non-terminal public state or replace its outcome,
/// output, error, or sequence.
/// </para>
/// <para>
/// The public execution projector is the only writer to this table.
/// </para>
/// </summary>
public sealed class PublicExecutionSnapshotRow
{
    /// <summary>Anchor discriminator: <c>job</c>, <c>input</c>, or <c>turn</c>.</summary>
    public string AnchorType { get; set; } = string.Empty;

    /// <summary>Canonical ID of the anchored record (job key, input id, or turn id).</summary>
    public string AnchorId { get; set; } = string.Empty;

    public string ProjectId { get; set; } = string.Empty;

    /// <summary>Canonical Agent identity when known; null when the anchor has none.</summary>
    public string? AgentId { get; set; }

    /// <summary>
    /// The public Session this anchor belongs to once the Session is
    /// joined. Null while a prepared launch Job has no live Session
    /// projection yet.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// The serialized public execution snapshot: exactly the 22
    /// allowlisted keys of <see cref="Mohist.Server.Api.DirectApi.PublicExecutionRead"/>.
    /// </summary>
    public string SnapshotJson { get; set; } = string.Empty;

    /// <summary>
    /// Internal identity of the first terminal fact that won the
    /// anchor's terminal fence (for example
    /// <c>turn:{id}:terminal</c> or <c>input:{id}:rejected</c>). Null
    /// while the anchor is not terminal.
    /// </summary>
    public string? TerminalFact { get; set; }

    /// <summary>The public outcome frozen by the terminal fence.</summary>
    public string? TerminalOutcome { get; set; }

    /// <summary>The terminal instant frozen by the terminal fence.</summary>
    public string? TerminalAt { get; set; }

    /// <summary>
    /// The public sequence of the terminal public event frozen by the
    /// fence, when one was emitted.
    /// </summary>
    public long? TerminalSequence { get; set; }

    /// <summary>
    /// The latest public Session sequence this snapshot reflects; null
    /// semantics match the public <c>sequence</c> key (no Session event
    /// could exist yet).
    /// </summary>
    public long? LastSequence { get; set; }

    /// <summary>When the snapshot was written (projection observation time).</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One persisted public Session event. The composite key
/// (<see cref="SessionId"/>, <see cref="Generation"/>,
/// <see cref="Sequence"/>) makes each Session's sequence strictly
/// increasing within a stream generation; the global allocator that
/// keeps sequences strictly increasing across generations lives on
/// <see cref="PublicStreamStateRow"/>. The public execution projector
/// is the only writer to this table.
/// </summary>
public sealed class PublicSessionEventRow
{
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Stream generation this journal row belongs to.</summary>
    public long Generation { get; set; }

    /// <summary>
    /// Strictly increasing positive per-Session public sequence,
    /// allocated from the Session's global allocator so a sequence is
    /// never reused or renumbered when the active generation changes.
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>
    /// One public event type from the fixed vocabulary (the seven
    /// execution types plus <c>session.context_reset</c>).
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Public ordering timestamp of the projected fact (RFC 3339 UTC).</summary>
    public string OccurredAt { get; set; } = string.Empty;

    /// <summary>
    /// The allowlisted event payload: the serialized
    /// <see cref="Mohist.Server.Api.DirectApi.PublicExecutionRead"/> for
    /// execution events, or the six-key session payload for
    /// <c>session.context_reset</c>. It never contains raw canonical
    /// event data.
    /// </summary>
    public string PayloadJson { get; set; } = string.Empty;

    /// <summary>
    /// Internal normalized identity of the canonical source transition
    /// that produced this event (for example
    /// <c>turn:{turnId}:terminal</c>). Stored for replay deduplication:
    /// after a crash the projector resumes past the checkpoint, and a
    /// replayed source transition already present under the active
    /// generation is never given a second sequence.
    /// </summary>
    public string SourceTransition { get; set; } = string.Empty;

    /// <summary>When the projector persisted the row.</summary>
    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>
/// Per-Session public stream bookkeeping: the active stream
/// generation, the Session's global next-sequence allocator (independent
/// of generation), the retained floor and safe head used for cursor
/// expiry bounds, and the closed-stream tombstone flag. The public
/// execution projector is the only writer to this table.
/// </summary>
public sealed class PublicStreamStateRow
{
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// The currently active stream generation. The first committed
    /// public projection creates generation one; ordinary restart,
    /// crash recovery, and replay never change it; a rebuild flips it
    /// atomically to a freshly built generation.
    /// </summary>
    public long ActiveGeneration { get; set; }

    /// <summary>
    /// The Session's global sequence allocator. It is preserved across
    /// generation switches so a sequence is never reused or renumbered
    /// when the active generation changes.
    /// </summary>
    public long NextSequence { get; set; }

    /// <summary>
    /// Retained floor of the active generation (its first sequence);
    /// null while the active generation has no events.
    /// </summary>
    public long? EarliestSequence { get; set; }

    /// <summary>
    /// Last published sequence in the active generation; null while the
    /// active generation has no events.
    /// </summary>
    public long? LatestSequence { get; set; }

    /// <summary>
    /// Closed-stream tombstone flag. Set when an authorized
    /// control-plane action deletes the Session; a valid
    /// current-generation cursor against the closed tombstone reports
    /// cursor expiry with safe bounds instead of events.
    /// </summary>
    public bool Closed { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// The projection's per-feed source checkpoint: which durable canonical
/// facts the public snapshot and journal include. A checkpoint row is
/// committed in the same projection transaction as the snapshots,
/// journal entries, and sequence allocations it proves, so a crash
/// before commit leaves no partial state and a crash after commit
/// resumes exactly past the consumed watermark. The public execution
/// projector is the only writer to this table.
/// </summary>
public sealed class PublicProjectionCheckpointRow
{
    /// <summary>
    /// The canonical feed this checkpoint tracks: the AgentJob ledger,
    /// the AgentJob event journal, the AgentSession ledger, or the
    /// AgentSession event journal.
    /// </summary>
    public string Feed { get; set; } = string.Empty;

    /// <summary>
    /// Feed-local source identity: a job key or session id for the
    /// aggregate feeds, the CloudEvents source string for the journal
    /// feeds.
    /// </summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>
    /// Consumed watermark: the ledger revision (aggregate feeds), the
    /// last consumed per-source journal sequence (journal feeds), or
    /// the consumed state digest (AgentSession ledger rows, which have
    /// no revision column).
    /// </summary>
    public string Watermark { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }
}
