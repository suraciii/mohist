using System.Text.Json.Serialization;
using Mohist.Server.Otel;

namespace Mohist.Server.Api;

public sealed record OtelStatusDto(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("collector_online")] bool CollectorOnline,
    [property: JsonPropertyName("since")] DateTimeOffset Since,
    [property: JsonPropertyName("storage")] OtelStorageDto Storage,
    [property: JsonPropertyName("telemetry")] OtelTelemetryDto Telemetry,
    [property: JsonPropertyName("process")] OtelProcessDto Process,
    [property: JsonPropertyName("latest_degradation"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] OtelDegradationDto? LatestDegradation,
    [property: JsonPropertyName("routes")] IReadOnlyList<OtelRouteDto> Routes)
{
    public static OtelStatusDto From(RuntimeObservabilitySnapshot snapshot) => new(
        snapshot.StatusName,
        snapshot.CollectorOnline,
        snapshot.Since,
        new(snapshot.Storage.UsageBytes, snapshot.Storage.BudgetBytes, snapshot.Storage.GrowthBytesPerSecond, snapshot.Storage.GrowthWindowSeconds),
        new(snapshot.Telemetry.ReceivedSpans, snapshot.Telemetry.SavedSpans, snapshot.Telemetry.RejectedSpans, snapshot.Telemetry.DroppedSpans),
        new(snapshot.Process.CpuUtilization, snapshot.Process.WorkingSetBytes, snapshot.Process.GcHeapBytes),
        snapshot.LatestDegradation is null ? null : new(snapshot.LatestDegradation.Code, snapshot.LatestDegradation.Message, snapshot.LatestDegradation.At),
        snapshot.Routes.Select(static route => new OtelRouteDto(
            route.Route,
            route.RequestCount,
            route.AverageDurationMilliseconds,
            route.MaxDurationMilliseconds,
            route.DatabaseCallsPerRequest,
            route.DownstreamCallsPerRequest)).ToArray());
}

public sealed record OtelStorageDto(
    [property: JsonPropertyName("usage_bytes"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? UsageBytes,
    [property: JsonPropertyName("budget_bytes")] long BudgetBytes,
    [property: JsonPropertyName("growth_bytes_per_second"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? GrowthBytesPerSecond,
    [property: JsonPropertyName("growth_window_seconds"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? GrowthWindowSeconds);

public sealed record OtelTelemetryDto(
    [property: JsonPropertyName("received_spans")] long ReceivedSpans,
    [property: JsonPropertyName("saved_spans")] long SavedSpans,
    [property: JsonPropertyName("rejected_spans")] long RejectedSpans,
    [property: JsonPropertyName("dropped_spans")] long DroppedSpans);

public sealed record OtelProcessDto(
    [property: JsonPropertyName("cpu_utilization"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? CpuUtilization,
    [property: JsonPropertyName("working_set_bytes"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? WorkingSetBytes,
    [property: JsonPropertyName("gc_heap_bytes"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? GcHeapBytes);

public sealed record OtelDegradationDto(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("at")] DateTimeOffset At);

public sealed record OtelRouteDto(
    [property: JsonPropertyName("route")] string Route,
    [property: JsonPropertyName("request_count")] long RequestCount,
    [property: JsonPropertyName("average_duration_ms")] double AverageDurationMilliseconds,
    [property: JsonPropertyName("max_duration_ms")] double MaxDurationMilliseconds,
    [property: JsonPropertyName("database_calls_per_request")] double DatabaseCallsPerRequest,
    [property: JsonPropertyName("downstream_calls_per_request")] double DownstreamCallsPerRequest);
