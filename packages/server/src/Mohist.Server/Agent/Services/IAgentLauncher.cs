using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Subscriptions;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Agent.Services;

/// <summary>
/// Shared entry point for starting a generic AgentSession for an Agent
/// profile. The HTTP manual launch path (<c>POST /api/projects/{...}/agents/{...}/sessions</c>)
/// and the routing dispatch handler (<c>RoutingDispatchHandler</c>)
/// both go through this service so the mint-session → open →
/// build-input → submit-to-grain chain and the resulting <see cref="GenericAgentSessionContext"/>
/// metadata are composed exactly once and identically. Without this
/// service, the two call sites would duplicate the AgentJob grain submission
/// pipeline and the session metadata labels could drift.
/// </summary>
public interface IAgentLauncher
{
    /// <summary>
    /// Mints a session id, opens a generic AgentSession with the launching
    /// Agent's identity wired into <see cref="GenericAgentSessionContext"/>,
    /// submits an <see cref="AgentJobGrain.AgentJobInput"/> carrying the
    /// Agent's resolved execution-definition snapshot (Instructions,
    /// Runtime, Model, Variant, ordered Skills) plus the provided prompt,
    /// and returns the resulting session identity.
    /// </summary>
    /// <param name="agent">
    /// Resolved Agent read model. Must be a non-archived Agent; the
    /// archived-rejection check is HTTP layer's responsibility (so the
    /// launcher stays path-agnostic). The execution-definition snapshot
    /// is captured verbatim here — every later launch path (mention,
    /// routed, watch, preflight) consumes the same resolver so the
    /// accepted job's stored snapshot cannot drift across paths.
    /// </param>
    /// <param name="prompt">
    /// Caller prompt or (for the subscription path) the rendered
    /// <c>ResponsePrompt</c>. Whitespace-trimmed before being written into
    /// <see cref="Grains.AgentJobInput.Prompt"/>.
    /// </param>
    /// <param name="context">
    /// Launch context carrying the project id and any optional context
    /// references (issue, epic, repository, workspace path) that are
    /// recorded as generic-session labels/annotations.
    /// </param>
    /// <param name="triggerLabels">
    /// Optional subscription-trigger labels merged into the session
    /// metadata label set. <c>null</c> for the manual HTTP path (no
    /// trigger metadata is recorded); the subscription dispatch handler
    /// passes the <c>event-id</c>/<c>subscription-id</c> dictionary so
    /// downstream visibility queries can resolve triggering event back
    /// from session.
    /// </param>
    /// <param name="ct">Cancellation token propagated to grain calls.</param>
    Task<AgentLaunchResult> LaunchAsync(
        AgentInfo agent,
        string prompt,
        AgentLaunchContext context,
        IReadOnlyDictionary<string, string>? triggerLabels = null,
        CancellationToken ct = default);

    /// <summary>
    /// Idempotent manual launch. The route forwards
    /// the caller-supplied <paramref name="idempotencyKey"/> to the
    /// <see cref="Grains.AgentLaunchCoordinatorGrain"/> keyed by
    /// <c>(ProjectId, IdempotencyKey)</c>. The coordinator persists
    /// the canonical launch plan, generates the Job/Session/Input/Turn
    /// ids, and drives the four-step prepare-ensure-submit sequence.
    /// Replays resolve to the same identities; conflicting replays
    /// raise <see cref="Grains.LaunchIdempotencyConflictException"/>.
    /// </summary>
    /// <param name="attachments">
    /// Accepted attachment descriptors the route already validated
    /// and bound to <paramref name="preMintedInputId"/>. Null or
    /// empty when the launch carries no attachments. Carried into
    /// the canonical plan and onto the dispatch envelope so the
    /// Runner sees the same accepted set the API surfaced to the
    /// caller.
    /// </param>
    /// <param name="preMintedInputId">
    /// Optional input id the route mints up front so it can
    /// validate+bind attachments against a stable owner before the
    /// coordinator commits the plan. The coordinator adopts this id
    /// verbatim instead of minting a fresh one. Null/empty when no
    /// attachments are supplied.
    /// </param>
    /// <param name="preMintedTurnId">
    /// Optional turn id mirroring <paramref name="preMintedInputId"/>;
    /// the coordinator adopts it verbatim. Null/empty when no
    /// attachments are supplied.
    /// </param>
    Task<AgentLaunchResult> LaunchIdempotentAsync(
        AgentInfo agent,
        string prompt,
        AgentLaunchContext context,
        string idempotencyKey,
        AgentLaunchCoordinatorRequest request,
        IReadOnlyList<AgentSessionInputAttachmentDescriptor>? attachments = null,
        string? preMintedSessionId = null,
        string? preMintedInputId = null,
        string? preMintedTurnId = null,
        CancellationToken ct = default);

    Task<AgentLaunchResult> LaunchConnectionAsync(
        AgentInfo agent,
        string prompt,
        ConnectionLaunchOrigin origin,
        CancellationToken ct = default);

    Task<AgentLaunchResult?> ResumeIdempotentAsync(
        string projectId,
        string idempotencyKey,
        AgentLaunchCoordinatorRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Routed-launch path. Builds the
    /// canonical <see cref="RoutedAgentLaunchPlan"/> from the resolved
    /// Agent execution definition + routing execution context, calls
    /// <c>EnsurePreparedAsync</c> on the AgentJob grain, and advances
    /// the prepared launch to Session open + LaunchReady + dispatch (or
    /// preflight-failed terminal delivery). Redelivery reuses the
    /// persisted canonical plan — the caller's
    /// <paramref name="executionContext"/> is only consulted to mint the
    /// very first plan; subsequent calls observe the persisted
    /// workspace and lineage, never newly resolved caller values.
    /// </summary>
    Task<RoutedAgentLaunchOutcome> LaunchRoutedAsync(
        AgentInfo agent,
        string prompt,
        RoutedExecutionContext executionContext,
        CloudEvent triggeringEvent,
        string ruleId,
        CancellationToken ct = default);

    /// <summary>
    /// Mention-launch path. Reuses the
    /// shared manual-style launch pipeline — workspace-optional, so a
    /// mention fires regardless of workflow-run state — but anchors the
    /// session id and AgentJob grain key on the comment identity
    /// (<paramref name="commentId"/>) instead of the delivering event
    /// guid. Redelivery of the same comment's <c>comment-added</c> event
    /// reuses one session grain and one AgentJob; different comments
    /// launch independently. Trigger labels annotate the
    /// <c>com.mohist.issue.comment-added</c> event id and the comment id
    /// for bidirectional provenance so the launch is distinguishable
    /// from routing-rule / watch launches.
    /// </summary>
    Task<AgentLaunchResult> LaunchMentionAsync(
        AgentInfo agent,
        string prompt,
        AgentLaunchContext context,
        string commentId,
        string triggeringEventId,
        CancellationToken ct = default);
}

/// <summary>
/// Result of a generic AgentSession launch. Carries the minted
/// session id (caller-observable identity) and the agent identity
/// fields the HTTP manual launch path surfaces verbatim in its 201
/// response. The <c>JobKey</c> is the AgentJob grain key the launcher
/// minted for this launch (manual <c>agent-job-launch-{guid}</c>,
/// mention <c>CommentJobKey</c>); the routed path returns its key via
/// <see cref="RoutedAgentLaunchOutcome.JobKey"/>. Surfacing the identity
/// does not change how many entities a launch creates or how dispatch
/// happens — a launch still creates exactly one AgentJob and one
/// AgentSession and issues exactly one dispatch. The
/// <c>TranscriptUrl</c> is composed by the HTTP layer because it depends
/// on route addressing, which the launcher does not own.
/// </summary>
public sealed record AgentLaunchResult(
    string SessionId,
    string JobKey,
    string InputId,
    string TurnId,
    string AgentId,
    string AgentName);

/// <summary>
/// Outcome of a routed launch. Carries the session id the AgentJob
/// opened (the stable <c>projectId/eventId/ruleId</c>-derived id), the
/// agent identity, and the disposition the canonical plan decided
/// (executable or preflight-failed with reason and category).
/// </summary>
public sealed record RoutedAgentLaunchOutcome(
    string SessionId,
    string JobKey,
    string AgentId,
    string AgentName,
    RoutedLaunchDisposition Disposition,
    string? PreflightReason = null,
    string? PreflightCategory = null)
{
    public bool IsPreflightFailed => Disposition == RoutedLaunchDisposition.PreflightFailed;
}

/// <summary>
/// Launch inputs the Agent-side caller hands to <see cref="IAgentLauncher"/>.
/// Carries the project id and any optional context references recorded as
/// generic-session metadata. Independent of the Agent identity, which the
/// caller resolves separately and passes alongside.
/// </summary>
/// <remarks>
/// Subscription-triggered launches do NOT populate the issue / epic /
/// repository / workspace fields here. The Agent obtains issue identity
/// itself via <c>mo workflow get</c> rather than receiving a pre-fetched
/// ref on the session metadata (see spec
/// <c>agent-subscription-dispatch#Triggered Agent pulls its own context</c>).
/// </remarks>
public sealed record AgentLaunchContext(
    string ProjectId,
    int? IssueNumber = null,
    int? EpicNumber = null,
    string? Repository = null,
    string? WorkspacePath = null,
    string? Title = null);

[Orleans.GenerateSerializer]
public sealed record ConnectionLaunchOrigin(
    [property: Orleans.Id(0)] string ConnectionId,
    [property: Orleans.Id(1)] string WorkspaceTeamId,
    [property: Orleans.Id(2)] string SlackUserId,
    [property: Orleans.Id(3)] string ConversationId,
    [property: Orleans.Id(4)] string MessageTs,
    [property: Orleans.Id(5)] string? ThreadTs = null);
