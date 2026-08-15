using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Otel;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Telemetry;

[Collection("IntegrationTelemetry")]
public class OtelQueryRoutesIntegrationSpecs : IAsyncLifetime
{
    private const string ListPath = "/otel/api/traces";
    private const string QueryPath = "/otel/api/query";
    private const string StatusPath = "/otel/api/status";

    private readonly OtlpRoutesHostFixture _fixture;
    private OtlpRoutesWebApplicationFactory _factory => _fixture.Factory;

    public OtelQueryRoutesIntegrationSpecs(OtlpRoutesHostFixture fixture)
    {
        _fixture = fixture;
    }

    public ValueTask InitializeAsync() => new(_fixture.ResetOtelStateAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task GetTraces_OnMainApi_ReturnsEnvelopeWithArray()
    {
        using var client = _factory.CreateMainApiClient();

        using var response = await client.GetAsync(ListPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task GetTraces_EmptyDatabase_ReturnsEmptyArray()
    {
        using var client = _factory.CreateMainApiClient();

        using var response = await client.GetAsync(ListPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task PostQuery_SelectCount_ReturnsQueryResultEnvelope()
    {
        SeedTrace("t1", "svc", "2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z", 1);
        SeedTrace("t2", "svc", "2026-01-02T00:00:00Z", "2026-01-02T00:00:01Z", 1);

        using var client = _factory.CreateMainApiClient();
        using var content = new StringContent(
            "{\"sql\":\"SELECT COUNT(*) AS total FROM traces\"}",
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync(QueryPath, content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        var data = doc.RootElement.GetProperty("data");
        var columns = data.GetProperty("columns");
        Assert.Equal(1, columns.GetArrayLength());
        Assert.Equal("total", columns[0].GetString());
        var rows = data.GetProperty("rows");
        Assert.Equal(1, rows.GetArrayLength());
        Assert.Equal(2L, rows[0].GetProperty("total").GetInt64());
        Assert.False(data.GetProperty("truncated").GetBoolean());
        Assert.False(data.TryGetProperty("truncate_reason", out _));
    }

    [Fact]
    public async Task PostQuery_EmptyResult_ReturnsColumns()
    {
        using var client = _factory.CreateMainApiClient();
        using var content = new StringContent(
            "{\"sql\":\"SELECT service_name, span_count FROM traces\"}",
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync(QueryPath, content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        Assert.Empty(data.GetProperty("rows").EnumerateArray());
        var columns = data.GetProperty("columns");
        Assert.Equal(2, columns.GetArrayLength());
        Assert.Equal("service_name", columns[0].GetString());
        Assert.Equal("span_count", columns[1].GetString());
    }

    [Fact]
    public async Task PostQuery_BodyAtLimit_ProceedsToJsonAndAdmission()
    {
        using var client = _factory.CreateMainApiClient();
        var prefix = "{\"sql\":\"SELECT 1\",\"padding\":\"";
        var suffix = "\"}";
        var paddingLength = TraceQuerier.MaxQueryRequestBodyBytes -
            Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(suffix);
        var body = prefix + new string('x', paddingLength) + suffix;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(QueryPath, content);

        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostQuery_OversizedBody_Returns413WithStableCodeBeforeParsing()
    {
        using var client = _factory.CreateMainApiClient();
        using var content = new StringContent(
            new string('x', TraceQuerier.MaxQueryRequestBodyBytes + 1),
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync(QueryPath, content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("query_request_too_large", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task PostQuery_MultiStatementWithNonSelectTail_Returns400WithStableCode()
    {
        using var client = _factory.CreateMainApiClient();
        using var content = new StringContent(
            "{\"sql\":\"SELECT 1; DROP TABLE traces\"}",
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync(QueryPath, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("query_not_select", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task PostQuery_NonSelectStatement_Returns400WithStableCode()
    {
        using var client = _factory.CreateMainApiClient();
        using var content = new StringContent(
            "{\"sql\":\"DELETE FROM traces\"}",
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync(QueryPath, content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal("query_not_select", await ReadCodeAsync(response));
    }

    [Theory]
    [InlineData("INSERT INTO traces VALUES ('x','y','z','w',1)")]
    [InlineData("UPDATE traces SET service_name='x'")]
    [InlineData("DROP TABLE traces")]
    [InlineData("ALTER TABLE traces ADD COLUMN x TEXT")]
    [InlineData("ATTACH DATABASE 'x.db' AS x")]
    [InlineData("PRAGMA writable_schema = 1")]
    public async Task PostQuery_VariousNonSelectStatements_Returns400WithStableCode(string sql)
    {
        using var client = _factory.CreateMainApiClient();
        using var content = new StringContent(
            $"{{\"sql\":\"{sql}\"}}",
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync(QueryPath, content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal("query_not_select", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task PostQuery_MissingSqlField_Returns400()
    {
        using var client = _factory.CreateMainApiClient();
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(QueryPath, content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal("query_missing_sql", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task PostQuery_NullSqlValue_Returns400()
    {
        using var client = _factory.CreateMainApiClient();
        using var content = new StringContent("{\"sql\":null}", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(QueryPath, content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal("query_missing_sql", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task PostQuery_InvalidJson_Returns400()
    {
        using var client = _factory.CreateMainApiClient();
        using var content = new StringContent("not json {", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(QueryPath, content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("query_malformed", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task PostQuery_SqlSyntaxError_Returns400WithSqliteErrorCode()
    {
        using var client = _factory.CreateMainApiClient();
        using var content = new StringContent(
            "{\"sql\":\"SELECT FROM traces\"}",
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync(QueryPath, content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal("query_sqlite_error", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task PostQuery_NoSuchTable_Returns400WithSqliteErrorCode()
    {
        using var client = _factory.CreateMainApiClient();
        using var content = new StringContent(
            "{\"sql\":\"SELECT * FROM nonexistent\"}",
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync(QueryPath, content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal("query_sqlite_error", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task PostQuery_AttachStatement_RejectedByKeywordLayer()
    {
        using var client = _factory.CreateMainApiClient();
        using var content = new StringContent(
            "{\"sql\":\"ATTACH DATABASE ':memory:' AS attached\"}",
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync(QueryPath, content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("query_not_select", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task GetStatus_OnMainApi_ReturnsBoundedRuntimeStatus()
    {
        using var client = _factory.CreateMainApiClient();
        using var response = await client.GetAsync(StatusPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        foreach (var field in new[] { "status", "collector_online", "since", "storage", "telemetry", "process", "latest_degradation", "routes" })
            Assert.True(data.TryGetProperty(field, out _));
        Assert.False(data.TryGetProperty("trace_count", out _));
        Assert.False(data.TryGetProperty("span_count", out _));
    }

    [Fact]
    public async Task GetStatus_ReportsCollectorOnline()
    {
        using var scope = _factory.Services.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<RuntimeObservability>();
        runtime.PublishCollector(CollectorResult.Online());

        using var client = _factory.CreateMainApiClient();
        using var response = await client.GetAsync(StatusPath);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(doc.RootElement.GetProperty("data").GetProperty("collector_online").GetBoolean());
    }

    [Fact]
    public async Task GetStatus_ReportsCollectorOffline()
    {
        using var scope = _factory.Services.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<RuntimeObservability>();
        runtime.PublishCollector(CollectorResult.Unverified());

        using var client = _factory.CreateMainApiClient();
        using var response = await client.GetAsync(StatusPath);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.False(doc.RootElement.GetProperty("data").GetProperty("collector_online").GetBoolean());
    }

    [Fact]
    public async Task GetStatus_WithTraces_DoesNotInspectHistory()
    {
        SeedTrace("t1", "svc", "2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z", 1);
        SeedTrace("t2", "svc", "2026-01-02T00:00:00Z", "2026-01-02T00:00:01Z", 1);
        SeedTrace("t3", "svc", "2026-01-03T00:00:00Z", "2026-01-03T00:00:01Z", 1);
        SeedSpan("t1", "s1", "2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z");
        SeedSpan("t2", "s1", "2026-01-02T00:00:00Z", "2026-01-02T00:00:01Z");
        SeedSpan("t3", "s1", "2026-01-03T00:00:00Z", "2026-01-03T00:00:01Z");

        using var client = _factory.CreateMainApiClient();
        using var response = await client.GetAsync(StatusPath);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");

        Assert.False(data.TryGetProperty("trace_count", out _));
        Assert.False(data.TryGetProperty("span_count", out _));
        Assert.Equal(0L, data.GetProperty("telemetry").GetProperty("received_spans").GetInt64());
    }

    [Fact]
    public async Task GetTraces_OnOtlpPortWithSpoofedMainHost_Returns404()
    {
        using var client = _factory.CreateOtlpClient();
        client.DefaultRequestHeaders.Host = "localhost:1";

        using var response = await client.GetAsync(ListPath);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private void SeedTrace(string traceId, string serviceName, string startTime, string endTime, long spanCount)
    {
        var db = _factory.Services.GetRequiredService<OtelDb>();
        using var connection = db.OpenReadWriteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {OtelDb.TracesTable} (
                {OtelDb.TracesTraceIdColumn},
                {OtelDb.TracesServiceNameColumn},
                {OtelDb.TracesStartTimeColumn},
                {OtelDb.TracesEndTimeColumn},
                {OtelDb.TracesSpanCountColumn}
            ) VALUES ($trace_id, $service_name, $start_time, $end_time, $span_count);
            """;
        cmd.Parameters.AddWithValue("$trace_id", traceId);
        cmd.Parameters.AddWithValue("$service_name", serviceName);
        cmd.Parameters.AddWithValue("$start_time", startTime);
        cmd.Parameters.AddWithValue("$end_time", endTime);
        cmd.Parameters.AddWithValue("$span_count", spanCount);
        cmd.ExecuteNonQuery();
    }

    private void SeedSpan(string traceId, string spanId, string startTime, string endTime)
    {
        var db = _factory.Services.GetRequiredService<OtelDb>();
        using var connection = db.OpenReadWriteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {OtelDb.SpansTable} (
                {OtelDb.SpansTraceIdColumn},
                {OtelDb.SpansSpanIdColumn},
                {OtelDb.SpansNameColumn},
                {OtelDb.SpansKindColumn},
                {OtelDb.SpansStartTimeColumn},
                {OtelDb.SpansEndTimeColumn},
                {OtelDb.SpansStatusCodeColumn}
            ) VALUES ($trace_id, $span_id, 'test', 1, $start_time, $end_time, 0);
            """;
        cmd.Parameters.AddWithValue("$trace_id", traceId);
        cmd.Parameters.AddWithValue("$span_id", spanId);
        cmd.Parameters.AddWithValue("$start_time", startTime);
        cmd.Parameters.AddWithValue("$end_time", endTime);
        cmd.ExecuteNonQuery();
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("code").GetString();
    }
}
