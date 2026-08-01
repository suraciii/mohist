using Orleans;

namespace Mohist.Server.Contracts;

/// <summary>
/// Optional bounded external discussion the caller attaches to an
/// Agent launch as first-launch-only background. The Server composes
/// this onto the dispatched user input as a delimited
/// <c>read-only background</c> block preceding the task prompt, so the
/// background cannot override the Agent's privileged
/// <c>Instructions</c> / Runtime / Model / Variant / Skills or expand
/// the Agent's configured capabilities. The structured value is
/// surfaced on the durable <see cref="Mohist.Server.Sessions.Domain.AgentSessionInputRecord"/>
/// and on the session-input observation so the audit is inspectable.
///
/// <para>
/// Append-only Orleans field ids; first/last ids stay free for later
/// fields without renumbering older records.
/// </para>
/// </summary>
[GenerateSerializer]
public sealed record AgentStartupContext(
    /// <summary>
    /// Rendered bounded external discussion the caller is handing the
    /// Agent. Composed verbatim as the read-only background block;
    /// never folded into the launch fingerprint (the background is a
    /// volatile snapshot read at processing time, unlike
    /// <c>AttachmentIds</c> which are caller-validated before launch).
    /// </summary>
    [property: Id(0)] string Text,
    /// <summary>
    /// Provenance attestation the caller states explicitly: how the
    /// bounded range was captured and whether oldest-first
    /// truncation occurred. Surfaced verbatim on the session-input
    /// audit so neither the Agent nor a later observer is misled about
    /// what was or was not read. Stable truncation marker is "N
    /// oldest messages omitted" — the marker appears in BOTH the
    /// caller-side acceptance reply (provider responsibility) and the
    /// composed agent input.
    /// </summary>
    [property: Id(1)] AgentStartupContextProvenance Provenance);

[GenerateSerializer]
public sealed record AgentStartupContextProvenance(
    [property: Id(0)] string Source,
    /// <summary>
    /// True when the caller truncated oldest messages first to fit a
    /// bound; false when the bounded range was captured completely.
    /// </summary>
    [property: Id(1)] bool Truncated,
    /// <summary>
    /// Stable marker the composed background prepends when
    /// <see cref="Truncated"/> is true. Null when no truncation
    /// occurred. Same string the caller surfaces in the Slack
    /// acceptance reply so the two attestations cannot drift.
    /// </summary>
    [property: Id(2)] string? TruncationMarker,
    /// <summary>
    /// Number of oldest messages the caller dropped when
    /// <see cref="Truncated"/> is true. Zero when no truncation
    /// occurred. Surfaced for audit so the audit can attribute the
    /// omission.
    /// </summary>
    [property: Id(3)] int OmittedOldestMessageCount = 0);