using System.Text;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;

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
/// Single decision point for who may invoke a Slack-bound Agent in a
/// channel or DM. Replaces the six inlined <c>sender ==
/// connection.OwnerSlackUserId</c> equality checks in
/// <c>SlackConnectionRoutes</c> + the DM gate in
/// <c>SlackOwnerClaimService</c>. The decision reads current
/// <see cref="AgentConnection.AccessPolicy"/> on every call; it caches
/// nothing, so a policy or allowlist change takes effect on the next
/// received input without any cache to invalidate.
///
/// <para>Under <c>owner_only</c> (the default) and any direct message the
/// decider authorizes only the Owner, reads no Slack API, and short-circuits
/// on a single equality check.</para>
///
/// <para>Under <c>allowlist</c> a non-Owner sender must (a) appear in the
/// allowlist child table by stable Slack user id and (b) be a current
/// regular workspace member right now per
/// <see cref="SlackOwnerClaimService.IsEligibleMember"/> — the lookup
/// hits <c>users.info</c> on every non-Owner invocation so a listed
/// member who has since become a guest, been deleted, or been restricted
/// is rejected, and a display-name match against a different stable
/// identity never authorizes.</para>
///
/// <para>Under <c>anyone</c> a sender must additionally be in a channel
/// the Bot is a member of (<c>conversations.info</c>); a Slack Connect
/// external participant, a guest, a Bot, a deleted member, and any
/// identity whose status or channel-membership fact cannot be confirmed
/// are rejected. Slack API failures are safe-denied: an unverifiable
/// identity never triggers the Agent. The Owner is invokable in every
/// policy because the Owner check needs no Slack API call.</para>
/// </summary>
public sealed class SlackConnectionAccessDecider : IScopedService
{
    private const string OwnerOnlyReason = "This Slack Connection is available only to its owner.";
    private const string AllowlistUnlistedReason = "This Slack Connection allows only listed members. You are not on the allowlist.";
    private const string MemberNotEligibleReason = "You are not a current regular member of this Slack workspace.";
    private const string ChannelNotVisibleReason = "The Bot cannot see you in this channel; a workspace member must be in a channel where the Bot is present to invoke the Agent.";
    private const string VerificationFailedReason = "Your Slack identity could not be confirmed right now; please retry.";

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly SlackConnectionAllowedMemberStore _allowedMembers;
    private readonly ISlackApiClient _slack;
    private readonly ISecretStore _secrets;

    public SlackConnectionAccessDecider(
        IDbContextFactory<MohistDbContext> dbFactory,
        SlackConnectionAllowedMemberStore allowedMembers,
        ISlackApiClient slack,
        ISecretStore secrets)
    {
        _dbFactory = dbFactory;
        _allowedMembers = allowedMembers;
        _slack = slack;
        _secrets = secrets;
    }

    /// <summary>
    /// Evaluates whether <paramref name="senderSlackUserId"/> may invoke
    /// the Connection for this message. The decision reads the current
    /// <c>AccessPolicy</c> column and (under <c>allowlist</c>) the
    /// current child rows on every call; under <c>allowlist</c> and
    /// <c>anyone</c> it additionally loads the Connection's Bot token
    /// and calls <c>users.info</c> (<c>conversations.info</c> under
    /// <c>anyone</c>) so the authorization reflects live Slack state.
    /// The decider caches nothing, so a policy or allowlist change takes
    /// effect on the next received input without any cache to invalidate.
    /// </summary>
    /// <param name="connection">The Connection being invoked.</param>
    /// <param name="senderSlackUserId">Stable Slack user id of the sender.</param>
    /// <param name="workspaceTeamId">Workspace team id of the install.</param>
    /// <param name="conversationId">Channel or DM conversation id.</param>
    /// <param name="isDirectMessage">True iff the message is a 1:1 DM.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AccessDecision> EvaluateAsync(
        AgentConnection connection,
        string senderSlackUserId,
        string workspaceTeamId,
        string conversationId,
        bool isDirectMessage,
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

        var policy = connection.AccessPolicy;
        if (string.IsNullOrEmpty(policy))
            policy = AccessPolicyKind.OwnerOnly;

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
                connection, senderSlackUserId, workspaceTeamId, ct),
            AccessPolicyKind.Anyone => await EvaluateAnyoneAsync(
                connection, senderSlackUserId, workspaceTeamId, conversationId, ct),
            _ => AccessDecision.Deny(OwnerOnlyReason),
        };
    }

    private async Task<AccessDecision> EvaluateAllowlistAsync(
        AgentConnection connection,
        string senderSlackUserId,
        string workspaceTeamId,
        CancellationToken ct)
    {
        var allowed = await _allowedMembers.IsAllowedAsync(
            connection.ProjectId, connection.Id, senderSlackUserId, ct);
        if (!allowed)
            return AccessDecision.Deny(AllowlistUnlistedReason);

        return await ConfirmLiveWorkspaceMemberAsync(
            connection, senderSlackUserId, workspaceTeamId, ct);
    }

    private async Task<AccessDecision> EvaluateAnyoneAsync(
        AgentConnection connection,
        string senderSlackUserId,
        string workspaceTeamId,
        string conversationId,
        CancellationToken ct)
    {
        var memberDecision = await ConfirmLiveWorkspaceMemberAsync(
            connection, senderSlackUserId, workspaceTeamId, ct);
        if (!memberDecision.Allowed)
            return memberDecision;

        var botToken = await TryLoadBotTokenAsync(connection, ct);
        if (botToken is null)
            return AccessDecision.Deny(VerificationFailedReason);

        SlackConversationInfoResponse channel;
        try
        {
            channel = await _slack.ConversationsInfoAsync(conversationId, botToken, ct);
        }
        catch
        {
            // Safe-degrade: an unverifiable channel-membership fact
            // never authorizes. Matches the spec ("any identity that
            // cannot be confirmed SHALL NOT trigger the Agent") and the
            // proposal's safe-deny direction.
            return AccessDecision.Deny(VerificationFailedReason);
        }

        if (!channel.Ok || channel.Channel is null)
            return AccessDecision.Deny(VerificationFailedReason);

        if (!channel.Channel.IsMember)
            return AccessDecision.Deny(ChannelNotVisibleReason);

        return AccessDecision.Allow("anyone_workspace_member");
    }

    private async Task<AccessDecision> ConfirmLiveWorkspaceMemberAsync(
        AgentConnection connection,
        string senderSlackUserId,
        string workspaceTeamId,
        CancellationToken ct)
    {
        var botToken = await TryLoadBotTokenAsync(connection, ct);
        if (botToken is null)
            return AccessDecision.Deny(VerificationFailedReason);

        SlackUserInfoResponse userResponse;
        try
        {
            userResponse = await _slack.UsersInfoAsync(senderSlackUserId, botToken, ct);
        }
        catch
        {
            return AccessDecision.Deny(VerificationFailedReason);
        }

        if (!userResponse.Ok || userResponse.User is null)
            return AccessDecision.Deny(VerificationFailedReason);

        if (!SlackOwnerClaimService.IsEligibleMember(userResponse, workspaceTeamId, senderSlackUserId))
            return AccessDecision.Deny(MemberNotEligibleReason);

        return AccessDecision.Allow("live_workspace_member");
    }

    private async Task<string?> TryLoadBotTokenAsync(AgentConnection connection, CancellationToken ct)
    {
        try
        {
            var token = await _secrets.LoadAsync(
                new SecretStoreAddress(connection.ProjectId, connection.Id, SecretKind.BotToken), ct);
            if (token is null || token.Length == 0)
                return null;
            return Encoding.UTF8.GetString(token);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsOwner(AgentConnection connection, string senderSlackUserId) =>
        !string.IsNullOrEmpty(connection.OwnerSlackUserId)
        && string.Equals(connection.OwnerSlackUserId, senderSlackUserId, StringComparison.Ordinal);
}