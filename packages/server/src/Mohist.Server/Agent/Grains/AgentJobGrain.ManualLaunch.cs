using Mohist.Server.Contracts;

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
                $"slack:{command.SessionId}:{command.InputId}");
    }
}
