using Mohist.Server.Infrastructure.Events.Matching;

namespace Mohist.Server.Events.WebSocket;

public sealed class EventSocketMatchFailureSink(ILogger<EventSocketMatchFailureSink> log) : IEventMatchFailureSink
{
    public void Record(string source, Exception exception) =>
        log.LogWarning(exception, "Event socket match evaluation failed for expression {Expression}", source);
}
