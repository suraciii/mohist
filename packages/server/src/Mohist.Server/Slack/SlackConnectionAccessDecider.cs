using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;
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
/// received input without any cache to invalidate. The initial
/// implementation handles the <c>owner_only</c> default and the
/// unconditional DM Owner-only rule. The <c>allowlist</c> and
/// <c>anyone</c> branches are layered on top by the same entry point
/// once their live-validation predicates land.
/// </summary>
public sealed class SlackConnectionAccessDecider : IScopedService
{
    private const string OwnerOnlyReason = "This Slack Connection is available only to its owner.";

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly SlackConnectionAllowedMemberStore _allowedMembers;

    public SlackConnectionAccessDecider(
        IDbContextFactory<MohistDbContext> dbFactory,
        SlackConnectionAllowedMemberStore allowedMembers)
    {
        _dbFactory = dbFactory;
        _allowedMembers = allowedMembers;
    }

    /// <summary>
    /// Evaluates whether <paramref name="senderSlackUserId"/> may invoke
    /// the Connection for this message. The decision reads the current
    /// <c>AccessPolicy</c> column and (under <c>allowlist</c>) the
    /// current child rows on every call. For the <c>owner_only</c>
    /// default and any direct message, the decider reads no Slack API
    /// and short-circuits on a single equality check; the cost is one
    /// indexed DB read of the parent column at most.
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
        // single equality check.
        if (isDirectMessage)
            return IsOwner(connection, senderSlackUserId)
                ? AccessDecision.Allow("dm_owner")
                : AccessDecision.Deny(OwnerOnlyReason);

        var policy = connection.AccessPolicy;
        if (string.IsNullOrEmpty(policy))
            policy = AccessPolicyKind.OwnerOnly;

        return policy switch
        {
            AccessPolicyKind.OwnerOnly => IsOwner(connection, senderSlackUserId)
                ? AccessDecision.Allow("owner_only_owner")
                : AccessDecision.Deny(OwnerOnlyReason),
            // The allowlist branch delegates to a row-reader
            // whose data is mutated only by an Owner through the
            // Manage-access surface. Before that surface lands,
            // an allowlist Connection seen here is in a half-upgraded
            // state and the decider defaults to Owner-only
            // semantics so it is never accidentally widened.
            AccessPolicyKind.Allowlist => await EvaluateAllowlistAsync(
                connection, senderSlackUserId, ct),
            AccessPolicyKind.Anyone => IsOwner(connection, senderSlackUserId)
                ? AccessDecision.Allow("anyone_owner")
                : AccessDecision.Deny(OwnerOnlyReason),
            _ => IsOwner(connection, senderSlackUserId)
                ? AccessDecision.Allow("default_owner")
                : AccessDecision.Deny(OwnerOnlyReason),
        };
    }

    private async Task<AccessDecision> EvaluateAllowlistAsync(
        AgentConnection connection,
        string senderSlackUserId,
        CancellationToken ct)
    {
        if (IsOwner(connection, senderSlackUserId))
            return AccessDecision.Allow("allowlist_owner");

        var allowed = await _allowedMembers.IsAllowedAsync(
            connection.ProjectId, connection.Id, senderSlackUserId, ct);
        return allowed
            ? AccessDecision.Allow("allowlist_member")
            : AccessDecision.Deny(OwnerOnlyReason);
    }

    private static bool IsOwner(AgentConnection connection, string senderSlackUserId) =>
        !string.IsNullOrEmpty(connection.OwnerSlackUserId)
        && string.Equals(connection.OwnerSlackUserId, senderSlackUserId, StringComparison.Ordinal);
}
