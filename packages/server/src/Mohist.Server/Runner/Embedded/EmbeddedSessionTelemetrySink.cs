using Mohist.Runner.Transport;
using Mohist.Server.Sessions;

namespace Mohist.Server.Runner.Embedded;

public sealed class EmbeddedSessionTelemetrySink : ISessionTelemetrySink
{
    private readonly AgentSessionService _sessions;

    public EmbeddedSessionTelemetrySink(AgentSessionService sessions)
    {
        _sessions = sessions;
    }

    public async Task StartedAsync(AgentSessionContext session, SessionStarted started, CancellationToken ct)
    {
        await _sessions.MarkStartedAsync(session.Id, new SessionStartedRequest(
            started.ExternalSessionId,
            started.Model,
            started.WorkDir,
            started.ChangeDir,
            started.ProcessPid), ct);
    }

    public async Task AppendAsync(AgentSessionContext session, IReadOnlyList<SessionEventInput> events, CancellationToken ct)
    {
        if (events.Count == 0) return;
        await _sessions.AppendEventsAsync(session.Id, events.Select(e => new SessionEventRequest(e.Type, e.Payload)).ToList(), ct);
    }

    public async Task CompletedAsync(AgentSessionContext session, SessionCompleted completed, CancellationToken ct)
    {
        await _sessions.MarkCompletedAsync(session.Id, new SessionCompletedRequest(
            completed.Status,
            completed.FailureReason,
            completed.ExitCode), ct);
    }
}
