using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure;
using Mohist.Server.Otel;
using Xunit;

namespace Mohist.Server.UnitTests.Telemetry;

public sealed class OtelStatusDtoTests
{
    [Fact]
    public void OffSnapshot_PreservesNullableFieldsAndFixedBudget()
    {
        var since = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        var runtime = new RuntimeObservability(
            enabled: false,
            new RuntimeEpoch(since),
            new FakeTimeProvider(since));

        var json = JsonSerializer.Serialize(
            OtelStatusDto.From(runtime.GetSnapshot()),
            JSON.Options);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("off", root.GetProperty("status").GetString());
        Assert.Equal(since, root.GetProperty("since").GetDateTimeOffset());
        Assert.Equal(RuntimeObservability.DefaultStorageBudgetBytes,
            root.GetProperty("storage").GetProperty("budget_bytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("storage").GetProperty("usage_bytes").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("storage").GetProperty("growth_bytes_per_second").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("storage").GetProperty("growth_window_seconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("process").GetProperty("cpu_utilization").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("process").GetProperty("working_set_bytes").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("process").GetProperty("gc_heap_bytes").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("latest_degradation").ValueKind);
        Assert.False(root.TryGetProperty("trace_count", out _));
        Assert.False(root.TryGetProperty("span_count", out _));
    }
}
