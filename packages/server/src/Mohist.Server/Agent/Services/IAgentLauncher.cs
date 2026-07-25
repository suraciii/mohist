using Mohist.Server.Agent.Grains;
using Mohist.Server.Events.Subscriptions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Agent.Services;

/// <summary>
/// Shared entry point for starting a generic AgentSession for an Agent
/// profile. The HTTP manual launch path (<c>POST /api/projects/{...}/agents/{...}/sessions</c>)
/// and the routing dispatch handler (<c>RoutingDispatchHandler</c>,
/// issue-391 T-003) both go through this service so the mint-session → open →
/// build-input → submit-to-grain chain and the resulting <see cref="GenericAgentSessionContext"/>
/// metadata are composed exactly once and identically. Without this
/// service, the two call sites would duplicate the AgentJob grain submission
/// pipeline and the session metadata labels could drift, breaking the
/// shared launch contract documented in <c>specs/agent-subscription-dispatch/spec.md#Subscription-triggered
/// launch reuses the shared Agent launcher</c>.
/// </summary>
public interface IAgentLauncher
{
    /// <summary>
    /// Mints a session id, opens a generic AgentSession with the launching
    /// Agent's identity wired into <see cref="GenericAgentSessionContext"/>,
    /// submits an <see cref="AgentJobGrain.AgentJobInput"/> carrying the
    /// Agent's <c>Instructions</c> + <c>AgentConfig</c> snapshot plus the
    /// provided prompt, and returns the resulting session identity.
    /// </summary>
    /// <param name="agent">
    /// Resolved Agent read model. Must be a non-archived Agent; the
    /// archived-rejection check is HTTP layer's responsibility (so the
    /// launcher stays path-agnostic), but the snapshot fields
    /// (<see cref="AgentInfo.Id"/>, <see cref="AgentInfo.Instructions"/>,
    /// <see cref="AgentInfo.AgentConfig"/>) are captured verbatim here.
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
    /// <param name="runtimeOverride">
    /// Optional launch-time override of the execution backend
    /// (issue-452 design D2). When non-null, wins over the Agent's
    /// configured backend; when null, the Agent's configured backend
    /// resolves, defaulting to <c>opencode</c>. Manual HTTP launch
    /// passes the caller-supplied <c>runtime</c> from the request body;
    /// the subscription dispatch path passes <c>null</c> because the
    /// routed backend is the Agent's configured value.
    /// </param>
    /// <param name="ct">Cancellation token propagated to grain calls.</param>
    Task<AgentLaunchResult> LaunchAsync(
        AgentInfo agent,
        string prompt,
        AgentLaunchContext context,
        IReadOnlyDictionary<string, string>? triggerLabels = null,
        string? runtimeOverride = null,
        CancellationToken ct = default);

    /// <summary>
    /// Routed-launch path (issue-449 design decisions 1-3). Builds the
    /// canonical <see cref="RoutedAgentLaunchPlan"/> from the resolved
    /// routing execution context, calls <c>EnsurePreparedAsync</c> on
    /// the AgentJob grain, and advances the prepared launch to Session
    /// open + LaunchReady + dispatch (or preflight-failed terminal
    /// delivery). Redelivery reuses the persisted canonical plan — the
    /// caller's <paramref name="executionContext"/> is only consulted to
    /// mint the very first plan; subsequent calls observe the persisted
    /// workspace and lineage, never newly resolved caller values.
    /// </summary>
    Task<RoutedAgentLaunchOutcome> LaunchRoutedAsync(
        AgentInfo agent,
        string prompt,
        RoutedExecutionContext executionContext,
        CloudEvent triggeringEvent,
        string ruleId,
        string? runtimeOverride = null,
        CancellationToken ct = default);

    /// <summary>
    /// Mention-launch path (issue-490 T-002, design D1/D3/D6). Reuses the
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
/// response. The <c>TranscriptUrl</c> is composed by the HTTP layer
/// because it depends on route addressing, which the launcher does not own.
/// </summary>
public sealed record AgentLaunchResult(
    string SessionId,
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
