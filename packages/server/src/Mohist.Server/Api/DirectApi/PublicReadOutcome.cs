namespace Mohist.Server.Api.DirectApi;

/// <summary>
/// The terminal outcome of one projection-sourced public read: the
/// persisted snapshot was found and is fresh, the requested canonical
/// resource is not available in the authorized Project, or the
/// required durable source watermark is ahead of the stored projection
/// checkpoint (the projection is not yet current enough to serve).
/// <para>
/// The outcome is computed by the projection read service inside the
/// same request as the read itself; lag detection is a pure comparison
/// and never mutates anything. Command responses, replay observations,
/// and the Session event route answer through the same three-state
/// vocabulary so every surface turns lag into the identical
/// retryable transport condition rather than stale state.
/// </para>
/// </summary>
public enum PublicReadStatus
{
    /// <summary>
    /// The persisted public snapshot is current at its checkpoint and
    /// is served verbatim as the response body.
    /// </summary>
    Found,

    /// <summary>
    /// The requested canonical resource is absent from or does not
    /// belong to the authorized Project. Mapped to the route's 404
    /// resource code.
    /// </summary>
    NotFound,

    /// <summary>
    /// The projection checkpoint has not consumed the required
    /// durable source facts yet. Mapped to 503
    /// <c>projection_lag</c> with a retry hint — never to a stale
    /// snapshot and never to the public five-state <c>unknown</c>.
    /// </summary>
    ProjectionLag,
}

/// <summary>
/// The read result contract shared by every projection-sourced direct
/// API answer. <see cref="SnapshotJson"/> carries the already
/// serialized strict allowlist exactly as the projection committed it
/// — present only for <see cref="PublicReadStatus.Found"/>.
/// </summary>
public sealed record PublicReadOutcome(PublicReadStatus Status, string? SnapshotJson)
{
    public static PublicReadOutcome Found(string snapshotJson) =>
        new(PublicReadStatus.Found, snapshotJson);

    public static PublicReadOutcome Missing { get; } = new(PublicReadStatus.NotFound, null);

    public static PublicReadOutcome Lag { get; } = new(PublicReadStatus.ProjectionLag, null);
}
