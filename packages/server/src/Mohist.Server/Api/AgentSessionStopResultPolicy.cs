using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Api;

internal static class AgentSessionStopResultPolicy
{
    public static bool CompletesDirectMapping(TurnControlResult result) => result.Kind switch
    {
        TurnControlResultKind.AlreadyEnded => result.Status != AgentTurnStatus.Unknown,
        TurnControlResultKind.Cancelled
            or TurnControlResultKind.Stopped
            or TurnControlResultKind.NotCancellable => true,
        _ => false,
    };
}
