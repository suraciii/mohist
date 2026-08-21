using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Grains;

public sealed partial class AgentSessionGrain
{
    private static void EnsureRuntimeSessionPresent(AgentSession session)
    {
        if (!session.IsRuntimeSessionMissing(IsRuntimeRegistered)) return;
        throw new RuntimeSessionMissingException(session.Id, session.Status.AgentRuntimeSessionId, session.Runtime.Runtime);
    }

    private static bool HasInitialLaunch(AgentSession session) =>
        (session.Status.Turns ?? [])
            .Any(turn => !string.IsNullOrWhiteSpace(turn.JobId));

    private static bool IsRuntimeRegistered(string runtime) =>
        string.Equals(runtime, OpenCodeRuntime, StringComparison.OrdinalIgnoreCase)
        || string.Equals(runtime, PiRuntime, StringComparison.OrdinalIgnoreCase);
}
