using System.Diagnostics.Metrics;

namespace Mohist.Server.Otel;

public enum RuntimeMetricKind
{
    Counter,
    Histogram,
    ObservableGauge,
}

public sealed record RuntimeMetricDefinition(
    string Name,
    RuntimeMetricKind Kind,
    string Unit,
    IReadOnlyList<string> AttributeKeys);

public static class RuntimeMetricCatalog
{
    public const string MeterName = "Mohist.Server.Runtime";
    public const string Name = MeterName;

    public const string HttpRequestCount = "mohist.server.http.request.count";
    public const string HttpRequestDuration = "mohist.server.http.request.duration";
    public const string HttpRequestDatabaseCalls = "mohist.server.http.request.database_calls";
    public const string HttpRequestDownstreamCalls = "mohist.server.http.request.downstream_calls";
    public const string PathCandidates = "mohist.server.path.candidates";
    public const string PathProcessed = "mohist.server.path.processed";
    public const string PathTranscriptRecords = "mohist.server.path.transcript_records";
    public const string SpansReceived = "mohist.otel.spans.received";
    public const string SpansSaved = "mohist.otel.spans.saved";
    public const string SpansRejected = "mohist.otel.spans.rejected";
    public const string SpansDropped = "mohist.otel.spans.dropped";
    public const string StorageUsage = "mohist.otel.storage.usage";
    public const string StorageBudget = "mohist.otel.storage.budget";
    public const string StorageGrowth = "mohist.otel.storage.growth";
    public const string ProcessCpuUtilization = "mohist.process.cpu.utilization";
    public const string ProcessWorkingSet = "mohist.process.memory.working_set";
    public const string ProcessGcHeap = "mohist.process.runtime.dotnet.gc.heap";

    public static readonly IReadOnlyList<string> HttpAttributeKeys =
        Array.AsReadOnly(["http.route", "http.request.method", "http.response.status_code"]);

    public static readonly IReadOnlyList<string> PathAttributeKeys =
        Array.AsReadOnly(["mohist.path"]);

    private static readonly IReadOnlyList<string> HttpAttributes = HttpAttributeKeys;
    private static readonly IReadOnlyList<string> PathAttributes = PathAttributeKeys;
    private static readonly IReadOnlyList<string> EmptyAttributes = Array.Empty<string>();

    public static readonly IReadOnlyList<RuntimeMetricDefinition> Definitions =
        Array.AsReadOnly(
        [
            Definition(HttpRequestCount, RuntimeMetricKind.Counter, "{request}", HttpAttributes),
            Definition(HttpRequestDuration, RuntimeMetricKind.Histogram, "ms", HttpAttributes),
            Definition(HttpRequestDatabaseCalls, RuntimeMetricKind.Histogram, "{call}", HttpAttributes),
            Definition(HttpRequestDownstreamCalls, RuntimeMetricKind.Histogram, "{call}", HttpAttributes),
            Definition(PathCandidates, RuntimeMetricKind.Histogram, "{item}", PathAttributes),
            Definition(PathProcessed, RuntimeMetricKind.Histogram, "{item}", PathAttributes),
            Definition(PathTranscriptRecords, RuntimeMetricKind.Histogram, "{record}", PathAttributes),
            Definition(SpansReceived, RuntimeMetricKind.Counter, "{span}", EmptyAttributes),
            Definition(SpansSaved, RuntimeMetricKind.Counter, "{span}", EmptyAttributes),
            Definition(SpansRejected, RuntimeMetricKind.Counter, "{span}", EmptyAttributes),
            Definition(SpansDropped, RuntimeMetricKind.Counter, "{span}", EmptyAttributes),
            Definition(StorageUsage, RuntimeMetricKind.ObservableGauge, "By", EmptyAttributes),
            Definition(StorageBudget, RuntimeMetricKind.ObservableGauge, "By", EmptyAttributes),
            Definition(StorageGrowth, RuntimeMetricKind.ObservableGauge, "By/s", EmptyAttributes),
            Definition(ProcessCpuUtilization, RuntimeMetricKind.ObservableGauge, "1", EmptyAttributes),
            Definition(ProcessWorkingSet, RuntimeMetricKind.ObservableGauge, "By", EmptyAttributes),
            Definition(ProcessGcHeap, RuntimeMetricKind.ObservableGauge, "By", EmptyAttributes),
        ]);

    public static IReadOnlyList<RuntimeMetricDefinition> Instruments => Definitions;

    public static Meter CreateMeter() => new(MeterName);

    private static RuntimeMetricDefinition Definition(
        string name,
        RuntimeMetricKind kind,
        string unit,
        IReadOnlyList<string> attributeKeys) =>
        new(name, kind, unit, attributeKeys);
}
