using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Slack;

namespace Mohist.Server.Slack;

internal enum SlackChannelIngressDisposition
{
    Continue,
    Ignore,
    Reject,
}

internal sealed record SlackChannelIngressDecision(
    SlackChannelIngressDisposition Disposition,
    string? Reason = null);

/// <summary>
/// Makes the channel ingress decision that is independent of persistence,
/// Orleans, and delivery. The route remains responsible for loading the
/// projections and applying the selected side effect.
/// </summary>
internal static class SlackChannelIngressPolicy
{
    public const string EmptyTaskReason = "Please send a task for the Agent to perform.";
    public const string NonOwnerReason = "This Slack Connection is available only to its owner.";

    public static SlackChannelIngressDecision Decide(
        string currentConnectionId,
        string ownBotUserId,
        bool senderAuthorized,
        string? accessReason,
        bool isRootMessage,
        bool hasThread,
        bool hasPrompt,
        bool hasFiles,
        IReadOnlyList<WorkspaceBoundBot> mentionedWorkspaceBots,
        IReadOnlyList<SlackThreadBinding> threadBindings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentConnectionId);
        ArgumentNullException.ThrowIfNull(mentionedWorkspaceBots);
        ArgumentNullException.ThrowIfNull(threadBindings);

        // Multi-Bot routing and multi-Bot thread ambiguity have their own
        // policy. Leave those branches untouched for the route to handle.
        if (mentionedWorkspaceBots.Count >= 2 || threadBindings.Count >= 2)
            return new(SlackChannelIngressDisposition.Continue);

        if (mentionedWorkspaceBots.Count == 0)
        {
            if (threadBindings.Count == 0 && !hasThread)
                return new(SlackChannelIngressDisposition.Ignore);

            if (threadBindings.Count == 1
                && !string.Equals(threadBindings[0].ConnectionId, currentConnectionId, StringComparison.Ordinal))
            {
                return new(SlackChannelIngressDisposition.Ignore);
            }

            // A thread without a binding may still be recovered from the
            // inbox route or session provenance, so let the route reconcile it.
            return new(SlackChannelIngressDisposition.Continue);
        }

        var addressedBot = mentionedWorkspaceBots[0];
        if (!string.Equals(addressedBot.BotUserId, ownBotUserId, StringComparison.Ordinal))
            return new(SlackChannelIngressDisposition.Ignore);

        if (!senderAuthorized)
            return new(SlackChannelIngressDisposition.Reject, accessReason ?? NonOwnerReason);

        var hasOtherBinding = threadBindings.Count == 1
            && !string.Equals(threadBindings[0].ConnectionId, currentConnectionId, StringComparison.Ordinal);
        if ((!hasPrompt && !hasFiles) && (isRootMessage || hasOtherBinding))
            return new(SlackChannelIngressDisposition.Reject, EmptyTaskReason);

        return new(SlackChannelIngressDisposition.Continue);
    }

    public static SlackSenderKind NormalizeSenderKind(string? rawKind) =>
        rawKind?.Trim().ToLowerInvariant() switch
        {
            "bot" => SlackSenderKind.Bot,
            "unknown" => SlackSenderKind.Unknown,
            _ => SlackSenderKind.Human,
        };
}

internal enum SlackSenderKind
{
    Human,
    Bot,
    Unknown,
}
