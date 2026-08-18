using Mohist.Server.Contracts;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Grains;

public sealed partial class AgentSessionGrain
{
    public async Task ApplyInterruptionAsync(AgentWorkInterruptionTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        var session = await GetRequiredAsync();
        var events = session.ApplyInterruption(transition, Now());
        if (events.Count == 0)
        {
            await _stateStore.SaveAsync(SessionId, session);
            _session = session;
            return;
        }
        await CommitAsync(session, events);
    }
}
