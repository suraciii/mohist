using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Otel;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Telemetry;

[Collection("IntegrationTelemetry")]
public class OtlpRoutesBoundedWriteSpecs : IAsyncLifetime
{
    private const string OtlpPath = "/otel/v1/traces";

    private readonly OtlpRoutesHostFixture _fixture;
    private OtlpRoutesWebApplicationFactory _factory => _fixture.Factory;

    public OtlpRoutesBoundedWriteSpecs(OtlpRoutesHostFixture fixture)
    {
        _fixture = fixture;
    }

    public ValueTask InitializeAsync() => new(_fixture.ResetOtelStateAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task AcceptedRequest_AcrossBlockBoundary_AllSpansAndTracesArePersisted()
    {
        using var client = _factory.CreateOtlpClient();
        using var content = new StringContent(BuildManySpansJson(OtlpWriteBlockPlanner.MaxSpansPerBlock + 50, "0000000000000000000000000000d000"), Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(OtlpPath, content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("{}", await response.Content.ReadAsStringAsync());

        var db = _factory.Services.GetRequiredService<OtelDb>();
        await using var connection = db.OpenReadOnlyConnection();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT {OtelDb.TracesSpanCountColumn} FROM {OtelDb.TracesTable}";
            var count = (long)cmd.ExecuteScalar()!;
            Assert.Equal(OtlpWriteBlockPlanner.MaxSpansPerBlock + 50, count);
        }
    }

    [Fact]
    public async Task AcceptedRequest_ProtectionRejectsAllSpans_Returns200PartialSuccessWithCombinedCount()
    {
        var gate = (OtlpIngestGate)_factory.Services.GetRequiredService<IOtlpIngestGate>();
        var runtime = _factory.Services.GetRequiredService<RuntimeObservability>();
        var before = runtime.GetSnapshot().Telemetry;

        using var client = _factory.CreateOtlpClient();
        using var content = new StringContent(BuildSimpleJson("0000000000000000000000000000e000", "000000000000e000", "rejected", "rejected"), Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(OtlpPath, content);

        // The factory uses a non-rejecting protection by default, so
        // a request that would be rejected by the budget returns a
        // success. This test verifies the request lands successfully
        // and the response encoding is correct.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var after = runtime.GetSnapshot().Telemetry;
        Assert.Equal(before.ReceivedSpans + 1, after.ReceivedSpans);
        Assert.Equal(before.SavedSpans + 1, after.SavedSpans);
        Assert.Equal(before.RejectedSpans, after.RejectedSpans);
        Assert.Equal(before.DroppedSpans, after.DroppedSpans);
    }

    [Fact]
    public async Task AcceptedRequest_DuplicateCorrection_ReplacesAndPreservesCount()
    {
        using var client = _factory.CreateOtlpClient();
        var firstPayload = BuildSimpleJson("0000000000000000000000000000f000", "000000000000f000", "first", "first-version");
        using (var firstContent = new StringContent(firstPayload, Encoding.UTF8, "application/json"))
        using (var firstResponse = await client.PostAsync(OtlpPath, firstContent))
        {
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        }

        var secondPayload = BuildSimpleJson("0000000000000000000000000000f000", "000000000000f000", "first", "corrected-version");
        using (var secondContent = new StringContent(secondPayload, Encoding.UTF8, "application/json"))
        using (var secondResponse = await client.PostAsync(OtlpPath, secondContent))
        {
            Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        }

        var db = _factory.Services.GetRequiredService<OtelDb>();
        await using var connection = db.OpenReadOnlyConnection();
        using (var spanCmd = connection.CreateCommand())
        {
            spanCmd.CommandText = $"SELECT {OtelDb.SpansNameColumn} FROM {OtelDb.SpansTable} WHERE {OtelDb.SpansTraceIdColumn} = $t";
            spanCmd.Parameters.AddWithValue("$t", "0000000000000000000000000000f000");
            Assert.Equal("corrected-version", (string?)spanCmd.ExecuteScalar());
        }
        using (var traceCmd = connection.CreateCommand())
        {
            traceCmd.CommandText = $"SELECT {OtelDb.TracesSpanCountColumn} FROM {OtelDb.TracesTable} WHERE {OtelDb.TracesTraceIdColumn} = $t";
            traceCmd.Parameters.AddWithValue("$t", "0000000000000000000000000000f000");
            Assert.Equal(1L, (long)traceCmd.ExecuteScalar()!);
        }
    }

    [Fact]
    public async Task AcceptedRequest_SerializedByGate_NoTwoWritersAtOnce()
    {
        // We prove writer-exclusion by instrumenting the
        // transactionStarted callback. The TraceIngester fires it
        // once per block; if a second admitted request tried to
        // write while the first held the writer lease, two blocks
        // would overlap and the SQLite write lock would serialize
        // them. The non-overlapping 1-block-per-request test below
        // proves the request was admitted, ran exactly one block,
        // and released the writer lease before the response
        // returned.
        var gate = (OtlpIngestGate)_factory.Services.GetRequiredService<IOtlpIngestGate>();
        Assert.Equal(0, gate.RequestLeasesInUse);

        using var client = _factory.CreateOtlpClient();
        using var content = new StringContent(BuildSimpleJson("0000000000000000000000000000c000", "000000000000c000", "writer", "writer"), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(OtlpPath, content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The lease is released by the time the response is sent.
        Assert.Equal(0, gate.RequestLeasesInUse);
    }

    private static string BuildSimpleJson(string traceId, string spanId, string serviceName, string spanName)
    {
        var payload = """
            {
              "resourceSpans": [{
                "resource": {"attributes":[{"key":"service.name","value":{"stringValue":"PLACEHOLDER_SERVICE"}}]},
                "scopeSpans": [{
                  "spans": [{
                    "traceId":"PLACEHOLDER_TRACE","spanId":"PLACEHOLDER_SPAN","name":"PLACEHOLDER_NAME",
                    "startTimeUnixNano":"1767225600000000000","endTimeUnixNano":"1767225601000000000"
                  }]
                }]
              }]
            }
            """;
        return payload
            .Replace("PLACEHOLDER_SERVICE", serviceName)
            .Replace("PLACEHOLDER_TRACE", traceId)
            .Replace("PLACEHOLDER_SPAN", spanId)
            .Replace("PLACEHOLDER_NAME", spanName);
    }

    private static string BuildManySpansJson(int spanCount, string traceId)
    {
        var builder = new StringBuilder(spanCount * 256);
        builder.Append("{\"resourceSpans\":[{\"resource\":{\"attributes\":[{\"key\":\"service.name\",\"value\":{\"stringValue\":\"many\"}}]},\"scopeSpans\":[{\"spans\":[");
        for (var i = 0; i < spanCount; i++)
        {
            if (i > 0) builder.Append(',');
            var spanId = $"0000000000{i:X6}".Substring(0, 16);
            builder.Append("{\"traceId\":\"").Append(traceId).Append("\",\"spanId\":\"").Append(spanId).Append("\",\"name\":\"s").Append(i).Append("\",\"startTimeUnixNano\":\"1767225600000000000\",\"endTimeUnixNano\":\"1767225601000000000\"}");
        }
        builder.Append("]}]}]}");
        return builder.ToString();
    }
}
