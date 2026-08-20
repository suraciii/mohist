using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.WebSocket;
using Xunit;

namespace Mohist.Server.UnitTests.Events;

public sealed class EventSocketMatchFailureSinkTests
{
    [Fact]
    public async Task RecordEmitsMatchEvaluationFailureCounter()
    {
        var measurement = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter.Name == EventSocketMatchFailureSink.MeterName
                    && instrument.Name == EventSocketMatchFailureSink.MatchEvaluationFailureCounterName)
                    current.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Meter.Name == EventSocketMatchFailureSink.MeterName
                && instrument.Name == EventSocketMatchFailureSink.MatchEvaluationFailureCounterName)
                measurement.TrySetResult(value);
        });
        listener.Start();

        var sink = new EventSocketMatchFailureSink(NullLogger<EventSocketMatchFailureSink>.Instance);
        sink.Record("event.type.matches(\"x\")", new InvalidOperationException("evaluation failed"));

        Assert.Equal(1, await measurement.Task.WaitAsync(TestContext.Current.CancellationToken));
    }
}
