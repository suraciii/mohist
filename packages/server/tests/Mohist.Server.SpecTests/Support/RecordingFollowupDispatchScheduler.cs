using Mohist.Server.Sessions.Services;

namespace Mohist.Server.SpecTests.Support;

public sealed class RecordingFollowupDispatchScheduler : IFollowupDispatchScheduler
{
    public List<(string ProjectId, string SessionId)> Requests { get; } = [];

    public void Schedule(string projectId, string sessionId) => Requests.Add((projectId, sessionId));

    public void Reset() => Requests.Clear();
}
