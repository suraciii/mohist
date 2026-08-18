namespace Mohist.Server.Sessions.Grains;

public sealed partial class AgentSessionGrain
{
    private void RejectIfReloadRequired()
    {
        if (_sessionReloadRequired)
            throw new InvalidOperationException($"Agent session {SessionId} must reload after a failed event-aware save");
    }
}
