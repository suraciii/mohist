using System.Text.Json;

namespace Mohist.Server.Infrastructure.Slack;

public static class SlackFollowupRejection
{
    public const string RuntimeSessionMissingCategory = "runtime-session-missing";
    public const string OwnerTerminalText = "This Session cannot continue automatically because its execution state is unresolved. Reconcile or reset it in Mohist, then send the message again.";

    public static string Text(bool isDirectMessage) => isDirectMessage
        ? OwnerTerminalText
        : "This Session cannot continue automatically because its execution state is unresolved. Reconcile or reset it in Mohist, then mention the Bot again.";

    public static string DispatchRef(SlackMessageIdentity identity) =>
        $"slack-followup-rejected:{identity.AsKey()}";

    public static Task<SlackOutboxEnqueueResult> EnqueueAsync(
        SlackOutboxStore outbox,
        string projectId,
        string connectionId,
        SlackMessageIdentity identity,
        string text,
        string? threadTs,
        CancellationToken ct) =>
        outbox.EnqueueRequiredAsync(new SlackOutboxDraft(
            projectId,
            connectionId,
            identity.WorkspaceTeamId,
            identity.ConversationId,
            SlackOutboxKinds.UserAction,
            DispatchRef(identity),
            JsonSerializer.Serialize(new SlackDeliveryPayload(
                SlackDeliveryOperations.PostMessage,
                text,
                ClientMessageId: DispatchRef(identity),
                FallbackText: text)),
            threadTs), ct);
}
