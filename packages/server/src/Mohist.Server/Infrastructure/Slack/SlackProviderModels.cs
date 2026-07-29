namespace Mohist.Server.Infrastructure.Slack;

/// <summary>
/// Stable Slack message identity. Slack guarantees <c>MessageTs</c>
/// uniqueness within a channel, so <c>(WorkspaceTeamId, DmConversationId,
/// MessageTs)</c> uniquely identifies one DM message across the entire
/// workspace and survives any number of redeliveries. The launcher uses
/// this same triple to derive the idempotency key for the Agent API
/// dispatch, which is what makes redelivery collapse to the same
/// SessionInput.
/// </summary>
public readonly record struct SlackMessageIdentity(
    string WorkspaceTeamId,
    string DmConversationId,
    string MessageTs)
{
    public string AsKey() => $"{WorkspaceTeamId}/{DmConversationId}/{MessageTs}";

    public string Validate()
    {
        if (string.IsNullOrWhiteSpace(WorkspaceTeamId))
            return "WorkspaceTeamId is required.";
        if (string.IsNullOrWhiteSpace(DmConversationId))
            return "DmConversationId is required.";
        if (string.IsNullOrWhiteSpace(MessageTs))
            return "MessageTs is required.";
        return string.Empty;
    }
}

public static class SlackOutboxKinds
{
    public const string ReplaceableProgress = "replaceable_progress";
    public const string TerminalResult = "terminal_result";
    public const string ExplicitFailure = "explicit_failure";
    public const string UserAction = "user_action";

    public static bool IsDefined(string? value) => value is
        ReplaceableProgress or
        TerminalResult or
        ExplicitFailure or
        UserAction;

    /// <summary>
    /// Kinds that must never be silently dropped: the product loses a
    /// terminal result / explicit failure / actionable message if the
    /// store throws these away. Capacity overflow on these kinds flips
    /// the Connection to Degraded(Backpressured) and stops ingress.
    /// </summary>
    public static bool IsTerminal(string kind) => kind is
        TerminalResult or
        ExplicitFailure or
        UserAction;
}

public static class SlackOutboxStates
{
    public const string Pending = "pending";
    public const string Claimed = "claimed";
    public const string Delivered = "delivered";
    public const string DeliveryUncertain = "delivery_uncertain";
    public const string DeadLettered = "dead_lettered";

    public static bool IsDefined(string? value) => value is
        Pending or
        Claimed or
        Delivered or
        DeliveryUncertain or
        DeadLettered;
}

public static class SlackProviderBackpressureReasons
{
    public const string OutboxOverflow = "slack_outbox_backpressured";
    public const string InboxOverflow = "slack_inbox_backpressured";
}
