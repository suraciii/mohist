using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Agent.Grains;

// Terminal-delivery envelope construction split from AgentJobGrain to keep
// the main partial within the file-size ratchet.
public sealed partial class AgentJobGrain
{
    internal CloudEvent BuildTerminalDeliveryEnvelope(PendingTerminalDeliveryEvent obligation)
    {
        var extensions = AgentJobLineage.BuildExtensions(State.Input, State.RoutedPlan);
        var sessionLaunchPrompt = State.Input?.Prompt
            ?? State.ManualPlan?.Prompt
            ?? State.RoutedPlan?.Prompt;
        return AgentJobLineage.BuildTerminalDeliveryEnvelope(
            Key,
            obligation,
            extensions,
            sessionLaunchPrompt,
            State.Input?.AgentSessionId,
            State.Input?.InitialTurnId);
    }
}
