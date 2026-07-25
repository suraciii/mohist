using System.Diagnostics.Metrics;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Otel;
using Xunit;

namespace Mohist.Server.UnitTests.Telemetry;

public class RuntimeMetricCatalogTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CatalogContainsTheDeclaredInstrumentContract()
    {
        var expected = new Dictionary<string, (RuntimeMetricKind Kind, string Unit, string[] Keys)>
        {
            [RuntimeMetricCatalog.HttpRequestCount] = (RuntimeMetricKind.Counter, "{request}", ["http.route", "http.request.method", "http.response.status_code"]),
            [RuntimeMetricCatalog.HttpRequestDuration] = (RuntimeMetricKind.Histogram, "ms", ["http.route", "http.request.method", "http.response.status_code"]),
            [RuntimeMetricCatalog.HttpRequestDatabaseCalls] = (RuntimeMetricKind.Histogram, "{call}", ["http.route", "http.request.method", "http.response.status_code"]),
            [RuntimeMetricCatalog.HttpRequestDownstreamCalls] = (RuntimeMetricKind.Histogram, "{call}", ["http.route", "http.request.method", "http.response.status_code"]),
            [RuntimeMetricCatalog.PathCandidates] = (RuntimeMetricKind.Histogram, "{item}", ["mohist.path"]),
            [RuntimeMetricCatalog.PathProcessed] = (RuntimeMetricKind.Histogram, "{item}", ["mohist.path"]),
            [RuntimeMetricCatalog.PathTranscriptRecords] = (RuntimeMetricKind.Histogram, "{record}", ["mohist.path"]),
            [RuntimeMetricCatalog.SpansReceived] = (RuntimeMetricKind.Counter, "{span}", []),
            [RuntimeMetricCatalog.SpansSaved] = (RuntimeMetricKind.Counter, "{span}", []),
            [RuntimeMetricCatalog.SpansRejected] = (RuntimeMetricKind.Counter, "{span}", []),
            [RuntimeMetricCatalog.SpansDropped] = (RuntimeMetricKind.Counter, "{span}", []),
            [RuntimeMetricCatalog.StorageUsage] = (RuntimeMetricKind.ObservableGauge, "By", []),
            [RuntimeMetricCatalog.StorageBudget] = (RuntimeMetricKind.ObservableGauge, "By", []),
            [RuntimeMetricCatalog.StorageGrowth] = (RuntimeMetricKind.ObservableGauge, "By/s", []),
            [RuntimeMetricCatalog.ProcessCpuUtilization] = (RuntimeMetricKind.ObservableGauge, "1", []),
            [RuntimeMetricCatalog.ProcessWorkingSet] = (RuntimeMetricKind.ObservableGauge, "By", []),
            [RuntimeMetricCatalog.ProcessGcHeap] = (RuntimeMetricKind.ObservableGauge, "By", []),
        };

        Assert.Equal(expected.Count, RuntimeMetricCatalog.Definitions.Count);
        foreach (var definition in RuntimeMetricCatalog.Definitions)
        {
            var contract = expected[definition.Name];
            Assert.Equal(contract.Kind, definition.Kind);
            Assert.Equal(contract.Unit, definition.Unit);
            Assert.Equal(contract.Keys, definition.AttributeKeys);
        }
    }

    [Fact]
    public void MeterListenerSeesNormalizedRequestPathAndOutcomeMeasurements()
    {
        var time = new FakeTimeProvider(Start);
        using var runtime = new RuntimeObservability(
            true,
            new RuntimeEpoch(Start),
            time,
            initialDegradations: Array.Empty<RuntimeDegradationSeed>());
        var observed = new List<MeasurementObservation>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter == runtime.Meter)
                    current.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            observed.Add(new MeasurementObservation(instrument.Name, measurement, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
            observed.Add(new MeasurementObservation(instrument.Name, measurement, tags.ToArray())));
        listener.Start();

        runtime.CompleteRequest(
            "/api/projects/proj_123/issues/42",
            " get ",
            700,
            -4,
            -2,
            3);
        runtime.RecordAgentPath(" AGENT.ACTIVITY ", 5, 4, 3);
        runtime.RecordIngest(IngestOutcomeBuilder.Build(
            new ClassifiedBatchTotals(2, 0, 0, 0),
            IngestWriteResult.Committed()));
        runtime.PublishProcess(ProcessSampleResult.Success(TimeSpan.FromSeconds(1), 10, 20, 2, .5));
        runtime.PublishStorage(StorageProbeResult.Success(100, 2, 10));
        listener.RecordObservableInstruments();

        Assert.Contains(observed, item => item.Name == RuntimeMetricCatalog.HttpRequestCount && item.Value is long value && value == 1);
        Assert.Contains(observed, item => item.Name == RuntimeMetricCatalog.HttpRequestDuration && item.Value is double value && value == 0);
        Assert.Contains(observed, item => item.Name == RuntimeMetricCatalog.HttpRequestDatabaseCalls && item.Value is long value && value == 0);
        Assert.Contains(observed, item => item.Name == RuntimeMetricCatalog.HttpRequestDownstreamCalls && item.Value is long value && value == 3);
        Assert.Contains(observed, item => item.Name == RuntimeMetricCatalog.PathCandidates && item.Value is long value && value == 5);
        Assert.Contains(observed, item => item.Name == RuntimeMetricCatalog.PathProcessed && item.Value is long value && value == 4);
        Assert.Contains(observed, item => item.Name == RuntimeMetricCatalog.PathTranscriptRecords && item.Value is long value && value == 3);
        Assert.Contains(observed, item => item.Name == RuntimeMetricCatalog.SpansReceived && item.Value is long value && value == 2);

        var request = observed.Single(item => item.Name == RuntimeMetricCatalog.HttpRequestCount);
        Assert.Equal(
            ["http.request.method", "http.response.status_code", "http.route"],
            request.Tags.Select(static tag => tag.Key).OrderBy(static key => key, StringComparer.Ordinal));
        Assert.Equal("GET", request.Tags.Single(tag => tag.Key == "http.request.method").Value);
        Assert.Equal(0, Convert.ToInt32(request.Tags.Single(tag => tag.Key == "http.response.status_code").Value));
        Assert.Equal("/api/projects/{id}/issues/{id}", request.Tags.Single(tag => tag.Key == "http.route").Value);

        var path = observed.Single(item => item.Name == RuntimeMetricCatalog.PathCandidates);
        Assert.Equal("agent.activity", path.Tags.Single().Value);
        Assert.DoesNotContain(observed, item => item.Tags.Any(tag => tag.Value?.ToString()?.Contains("proj_123", StringComparison.Ordinal) == true));
    }

    [Fact]
    public void DisabledRuntimeDoesNotPublishMeasurements()
    {
        var time = new FakeTimeProvider(Start);
        using var runtime = new RuntimeObservability(false, new RuntimeEpoch(Start), time);
        var measurements = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter == runtime.Meter)
                    current.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => measurements++);
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => measurements++);
        listener.Start();

        runtime.CompleteRequest("/api/health", "GET", 200, 1, 1, 1);
        runtime.RecordAgentPath("agent.status", 1, 1, 0);
        runtime.PublishProcess(ProcessSampleResult.Success(TimeSpan.FromSeconds(1), 1, 1, 1, .1));
        runtime.PublishStorage(StorageProbeResult.Success(1, 1, 1));
        listener.RecordObservableInstruments();

        Assert.Equal(0, measurements);
    }

    private sealed record MeasurementObservation(
        string Name,
        object Value,
        KeyValuePair<string, object?>[] Tags);
}
