using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Api;

public static partial class SlackConnectionRoutes
{
    private static AgentSessionInputProvenance BuildSlackInputProvenance(
        string connectionId,
        SlackIngressBody body,
        string? threadTs) =>
        new(
            ProviderKind: "slack",
            WorkspaceId: body.TeamId,
            ConversationId: body.ConversationId,
            ThreadId: threadTs,
            MemberId: body.SenderSlackUserId!,
            MessageId: body.MessageTs,
            ConnectionId: connectionId,
            BoundThreadRootMessageId: threadTs);
}
