using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Slack;

namespace Mohist.Server.Agent.Grains;

public sealed partial class AgentJobGrain
{
    private static AgentSlackExecutionContext? SlackExecutionContextFor(PrepareManualLaunchCommand command)
    {
        var origin = command.ConnectionOrigin;
        return origin is null
            ? null
            : SlackExecutionContextFactory.Create(
                origin.WorkspaceTeamId,
                origin.ConversationId,
                origin.ThreadTs ?? origin.MessageTs,
                origin.MessageTs,
                origin.SlackUserId,
                origin.ConnectionId,
                command.SessionId,
                $"slack:{command.SessionId}:{command.InputId}",
                projectId: string.Equals(command.ProjectId, SlackDeliveryOwnerIds.ManagerProjectId, StringComparison.Ordinal)
                    ? command.ProjectId
                    : null,
                ownerKind: string.Equals(command.ProjectId, SlackDeliveryOwnerIds.ManagerProjectId, StringComparison.Ordinal)
                    ? SlackDeliveryOwnerKinds.Manager
                    : null);
    }
}
