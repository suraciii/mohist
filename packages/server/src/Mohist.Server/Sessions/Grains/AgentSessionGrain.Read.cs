namespace Mohist.Server.Sessions.Grains;

public sealed partial class AgentSessionGrain
{
    public async Task<AgentSessionInfo?> GetAsync()
    {
        // A quarantined activation holds a session mutated past a rolled-back
        // state/event transaction. Do not expose that dirty view; reject until
        // the grain reactivates and reloads from storage.
        if (_sessionReloadRequired)
            throw new InvalidOperationException($"Agent session {SessionId} must reload after a failed event-aware save");
        return _session is null ? null : await ToInfoAsync(_session);
    }
}
