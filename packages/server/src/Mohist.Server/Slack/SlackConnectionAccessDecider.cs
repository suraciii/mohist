using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;

namespace Mohist.Server.Slack;

/// <summary>
/// Decision of the single <see cref="SlackConnectionAccessDecider"/> for
/// one inbound Slack message. <see cref="Allowed"/> true means the
/// message may proceed to launch / follow-up; false means it must be
/// rejected with <see cref="Reason"/>. <see cref="Reason"/> is always
/// present and is the actionable text the channel / DM state machine
/// posts back to the sender — the spec demands an actionable reason and
/// no silent drops on a deny.
/// </summary>
public sealed record AccessDecision(bool Allowed, string Reason)
{
    public static AccessDecision Allow(string reason = "allowed") => new(true, reason);
    public static AccessDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// Lease proof the ingress route already validated before any inbox/outbox
/// side effect, plus the narrow capability the decider invokes to re-prove
/// the runtime lease and resolve the verified Agent App Bot token for the
/// live identity gate. The route binds <see cref="ResolveVerifiedBotToken"/>
/// to the lease core, so the decider never depends on the lease target
/// provider: that constructor edge would close a DI cycle
/// (<c>AgentConnectionStore</c> cleanup graph → decider → lease target
/// provider → <c>SlackAgentAppBindingService</c> →
/// <c>ISlackAgentAppBindingPort</c> → <c>AgentConnectionStore</c>). The gate
/// still runs under the same lease fence as every adapter-facing route and
/// never resolves a legacy connection-scoped secret address.
/// </summary>
public sealed record SlackLeaseContext(
    string OperatorId,
    string LeaseId,
    string AdapterId,
    Func<SlackLeaseTargetRef, CancellationToken, Task<string?>> ResolveVerifiedBotToken);

/// <summary>
/// Authorization context for a Server-handled Slack interaction. The
/// receiving lease is the proof for the Connection that owns the prompt or
/// status message. Target resolution is kept on the Server so a selected
/// Connection gets a separately validated lease and credential resolver.
/// </summary>
public sealed record SlackInteractionLeaseContext(
    SlackLeaseContext Receiving,
    Func<SlackLeaseTargetRef, CancellationToken, Task<SlackLeaseContext?>> ResolveCurrentTarget);

/// <summary>
/// Single decision point for who may invoke a Slack-bound Agent in a
/// channel or DM. The decision reads current
/// <see cref="AgentConnection.AccessPolicy"/> on every call; it caches
/// nothing, so a policy or allowlist change takes effect on the next
/// received input without any cache to invalidate.
///
/// <para>Under <c>owner_only</c> (the default) and any direct message the
/// decider authorizes only the Owner, reads no Slack API, and short-circuits
/// on a single equality check.</para>
///
/// <para>Under <c>allowlist</c> a non-Owner sender must appear in the
/// allowlist child table by stable Slack user id <em>and</em> be a current
/// regular workspace member right now per <c>users.info</c> (stable id and
/// team match, not deleted / restricted / ultra-restricted / bot /
/// app-user / Slack Connect stranger, same workspace). The lookup runs on
/// every non-Owner invocation, so a listed member who has since become a
/// guest, been deleted, or been restricted is rejected.</para>
///
/// <para>Under <c>anyone</c> a sender must pass the same current regular
/// member check and the Bot must be a member of the conversation
/// (<c>conversations.info</c>). A Slack Connect external participant, a
/// guest, a Bot, a deleted member, and any identity whose status or
/// channel-membership fact cannot be confirmed are rejected. Slack API
/// failures are safe-denied: an unverifiable identity never triggers the
/// Agent. The Owner is invokable in every policy because the Owner check
/// needs no Slack API call.</para>
/// </summary>
public sealed class SlackConnectionAccessDecider : IScopedService
{
    private const string OwnerOnlyReason = "This Slack Connection is available only to its owner.";
    private const string AllowlistUnlistedReason = "This Slack Connection allows only listed members. You are not on the allowlist.";
    private const string MemberNotEligibleReason = "You are not a current regular member of this Slack workspace.";
    private const string ChannelNotVisibleReason = "The Bot cannot see you in this channel; a workspace member must be in a channel where the Bot is present to invoke the Agent.";
    private const string VerificationFailedReason = "Your Slack identity could not be confirmed right now; please retry.";
    private const string NotInChannelError = "not_in_channel";
    private const string UserNotFoundError = "user_not_found";

    private readonly ISlackConnectionAllowedMemberStore _allowedMembers;
    private readonly ISlackMemberIdentityPort _memberIdentity;

    public SlackConnectionAccessDecider(
        ISlackConnectionAllowedMemberStore allowedMembers,
        ISlackMemberIdentityPort memberIdentity)
    {
        _allowedMembers = allowedMembers;
        _memberIdentity = memberIdentity;
    }

    /// <summary>
    /// Evaluates whether <paramref name="senderSlackUserId"/> may invoke
    /// the Connection for this message. The decision reads the current
    /// <c>AccessPolicy</c> column and (under <c>allowlist</c>) the
    /// current child rows on every call; under <c>allowlist</c> and
    /// <c>anyone</c> it additionally re-proves <paramref name="leaseContext"/>
    /// through the runtime lease seam, resolves the verified Bot token and
    /// calls <c>users.info</c> (<c>conversations.info</c> under
    /// <c>anyone</c>) so the authorization reflects live Slack state.
    /// The decider caches nothing, so a policy or allowlist change takes
    /// effect on the next received input without any cache to invalidate.
    /// </summary>
    /// <param name="connection">The Connection being invoked.</param>
    /// <param name="senderSlackUserId">Stable Slack user id of the sender.</param>
    /// <param name="workspaceTeamId">Workspace team id of the install.</param>
    /// <param name="conversationId">Channel or DM conversation id.</param>
    /// <param name="isDirectMessage">True iff the message is a 1:1 DM.</param>
    /// <param name="leaseContext">Lease proof from the ingress route; required
    /// when the policy needs the live member gate, ignored on the Owner and
    /// DM fast paths.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AccessDecision> EvaluateAsync(
        AgentConnection connection,
        string senderSlackUserId,
        string workspaceTeamId,
        string conversationId,
        bool isDirectMessage,
        SlackLeaseContext? leaseContext = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // DM is unconditionally Owner-only regardless of policy (proposal:
        // "私聊不变：无论频道策略如何，一对一 DM 恒为 Owner only"). Short-circuit
        // before any policy / member lookup so the DM hot path stays a
        // single equality check and never calls Slack API.
        if (isDirectMessage)
            return IsOwner(connection, senderSlackUserId)
                ? AccessDecision.Allow("dm_owner")
                : AccessDecision.Deny(OwnerOnlyReason);

        var policy = string.IsNullOrEmpty(connection.AccessPolicy)
            ? AccessPolicyKind.OwnerOnly
            : connection.AccessPolicy;

        // Owner check first so the Owner is always invokable in every
        // policy branch without burning a Slack API call.
        if (IsOwner(connection, senderSlackUserId))
            return policy switch
            {
                AccessPolicyKind.OwnerOnly => AccessDecision.Allow("owner_only_owner"),
                AccessPolicyKind.Allowlist => AccessDecision.Allow("allowlist_owner"),
                AccessPolicyKind.Anyone => AccessDecision.Allow("anyone_owner"),
                _ => AccessDecision.Allow("default_owner"),
            };

        return policy switch
        {
            AccessPolicyKind.OwnerOnly => AccessDecision.Deny(OwnerOnlyReason),
            AccessPolicyKind.Allowlist => await EvaluateAllowlistAsync(
                connection, senderSlackUserId, workspaceTeamId, leaseContext, ct),
            AccessPolicyKind.Anyone => await EvaluateAnyoneAsync(
                connection, senderSlackUserId, workspaceTeamId, conversationId, leaseContext, ct),
            _ => AccessDecision.Deny(OwnerOnlyReason),
        };
    }

    private async Task<AccessDecision> EvaluateAllowlistAsync(
        AgentConnection connection,
        string senderSlackUserId,
        string workspaceTeamId,
        SlackLeaseContext? leaseContext,
        CancellationToken ct)
    {
        // Local allowlist first: an unlisted sender is rejected before any
        // Slack API call.
        var allowed = await _allowedMembers.IsAllowedAsync(
            connection.ProjectId, connection.Id, senderSlackUserId, ct);
        if (!allowed)
            return AccessDecision.Deny(AllowlistUnlistedReason);

        var botToken = await ResolveLeasedBotTokenAsync(connection, leaseContext, ct);
        if (botToken is null)
            return AccessDecision.Deny(VerificationFailedReason);

        var member = await _memberIdentity.LookupMemberAsync(
            new SlackMemberIdentityRequest(botToken, senderSlackUserId), ct);
        return IsEligibleRegularMember(member, senderSlackUserId, workspaceTeamId)
            ? AccessDecision.Allow("allowlist_member")
            : DenyForMember(member);
    }

    private async Task<AccessDecision> EvaluateAnyoneAsync(
        AgentConnection connection,
        string senderSlackUserId,
        string workspaceTeamId,
        string conversationId,
        SlackLeaseContext? leaseContext,
        CancellationToken ct)
    {
        var botToken = await ResolveLeasedBotTokenAsync(connection, leaseContext, ct);
        if (botToken is null)
            return AccessDecision.Deny(VerificationFailedReason);

        var member = await _memberIdentity.LookupMemberAsync(
            new SlackMemberIdentityRequest(botToken, senderSlackUserId), ct);
        if (!IsEligibleRegularMember(member, senderSlackUserId, workspaceTeamId))
            return DenyForMember(member);

        var conversation = await _memberIdentity.LookupConversationAsync(
            new SlackConversationMembershipRequest(botToken, conversationId), ct);
        if (!conversation.Confirmed)
        {
            // not_in_channel is a definite Slack fact (the Bot is not a
            // member), so it gets the actionable visibility reason; every
            // other failure is uncertain and gets the retry reason.
            return AccessDecision.Deny(
                string.Equals(conversation.ErrorClass, NotInChannelError, StringComparison.Ordinal)
                    ? ChannelNotVisibleReason
                    : VerificationFailedReason);
        }

        return conversation.IsMember
            ? AccessDecision.Allow("anyone_member")
            : AccessDecision.Deny(ChannelNotVisibleReason);
    }

    private static async Task<string?> ResolveLeasedBotTokenAsync(
        AgentConnection connection,
        SlackLeaseContext? leaseContext,
        CancellationToken ct)
    {
        if (leaseContext is null)
            return null;
        return await leaseContext.ResolveVerifiedBotToken(
            new SlackLeaseTargetRef.Connection(connection.ProjectId, connection.Id),
            ct);
    }

    private static bool IsEligibleRegularMember(
        SlackMemberIdentityResult member,
        string senderSlackUserId,
        string workspaceTeamId) =>
        member.Confirmed
        && string.Equals(member.UserId, senderSlackUserId, StringComparison.Ordinal)
        && string.Equals(member.TeamId, workspaceTeamId, StringComparison.Ordinal)
        && !member.Deleted
        && !member.IsBot
        && !member.IsAppUser
        && !member.IsRestricted
        && !member.IsUltraRestricted
        && !member.IsStranger;

    private static AccessDecision DenyForMember(SlackMemberIdentityResult member) =>
        member.Confirmed || string.Equals(member.ErrorClass, UserNotFoundError, StringComparison.Ordinal)
            ? AccessDecision.Deny(MemberNotEligibleReason)
            : AccessDecision.Deny(VerificationFailedReason);

    private static bool IsOwner(AgentConnection connection, string senderSlackUserId) =>
        !string.IsNullOrEmpty(connection.OwnerSlackUserId)
        && string.Equals(connection.OwnerSlackUserId, senderSlackUserId, StringComparison.Ordinal);
}
