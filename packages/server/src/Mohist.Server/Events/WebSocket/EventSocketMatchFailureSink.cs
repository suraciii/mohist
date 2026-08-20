using System.Diagnostics.Metrics;
using Mohist.Server.Infrastructure.Events.Matching;

namespace Mohist.Server.Events.WebSocket;

public sealed class EventSocketMatchFailureSink(ILogger<EventSocketMatchFailureSink> log) : IEventMatchFailureSink
{
    public const string MeterName = "Mohist.Server.EventSocket";
    public const string MatchEvaluationFailureCounterName = "mohist.event_socket.match_evaluation_failures";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> MatchEvaluationFailures = Meter.CreateCounter<long>(
        MatchEvaluationFailureCounterName,
        "{failure}",
        "Number of event socket match expression evaluation failures.");

    public void Record(string source, Exception exception)
    {
        MatchEvaluationFailures.Add(1);
        log.LogWarning(exception, "Event socket match evaluation failed for expression {Expression}", source);
    }
}
