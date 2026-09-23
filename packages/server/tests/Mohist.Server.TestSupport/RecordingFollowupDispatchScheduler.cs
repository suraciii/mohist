using Mohist.Server.Sessions.Services;

namespace Mohist.Server.TestSupport;

/// <summary>
/// Records the wake-up dispatcher the current scenario's Session calls. The
/// grain cluster is shared across scenarios in a collection, so a Session from
/// an earlier scenario — still holding queued work when its persist timer or
/// durable follow-up wake fires — reaches this recorder. Such a stale wake is
/// not this scenario's fact, and admitting one hides the current scenario's
/// request under a pile of unrelated entries; the observation is therefore
/// scoped exactly like the transcript store's.
/// </summary>
public sealed class RecordingFollowupDispatchScheduler : IFollowupDispatchScheduler
{
    private readonly Func<string, bool>? _isCurrentScenario;

    public RecordingFollowupDispatchScheduler(Func<string, bool>? isCurrentScenario = null)
    {
        _isCurrentScenario = isCurrentScenario;
    }

    public List<(string ProjectId, string SessionId)> Requests { get; } = [];

    public void Schedule(string projectId, string sessionId)
    {
        if (_isCurrentScenario is not null && !_isCurrentScenario(sessionId))
            return;
        Requests.Add((projectId, sessionId));
    }

    public void Reset() => Requests.Clear();
}
