using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Otel;
using Mohist.Server.SpecTests.Support;
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
    public async Task GetTraces_OrderedByStartTimeDescending()
    {
        SeedTrace("trace-a", "runner", "2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z", 3);
        SeedTrace("trace-b", "server", "2026-01-02T00:00:00Z", "2026-01-02T00:00:05Z", 1);
        SeedTrace("trace-c", "agent", "2026-01-03T00:00:00Z", "2026-01-03T00:00:02Z", 7);

        using var client = _factory.CreateMainApiClient();
        using var response = await client.GetAsync(ListPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(3, data.GetArrayLength());
        Assert.Equal("trace-c", data[0].GetProperty("trace_id").GetString());
        Assert.Equal("trace-b", data[1].GetProperty("trace_id").GetString());
        Assert.Equal("trace-a", data[2].GetProperty("trace_id").GetString());

        // Each row carries the spec'd columns.
        var first = data[0];
        Assert.True(first.TryGetProperty("trace_id", out _));
        Assert.True(first.TryGetProperty("service_name", out _));
        Assert.True(first.TryGetProperty("start_time", out _));
        Assert.True(first.TryGetProperty("end_time", out _));
        Assert.True(first.TryGetProperty("span_count", out _));
    }

    [Fact]
    public async Task GetTraces_LimitParameter_CapsResultCount()
    {
        for (var i = 0; i < 10; i++)
        {
            SeedTrace(
                $"trace-{i:D4}",
                "svc",
                $"2026-01-{(i + 1):D2}T00:00:00Z",
                "2026-01-01T00:00:01Z",
                1);
        }

        using var client = _factory.CreateMainApiClient();
        using var response = await client.GetAsync($"{ListPath}?limit=5");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(5, doc.RootElement.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task GetTraces_ServiceFilter_OnlyReturnsMatchingService()
    {
        SeedTrace("t1", "runner", "2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z", 1);
        SeedTrace("t2", "server", "2026-01-02T00:00:00Z", "2026-01-02T00:00:01Z", 1);
        SeedTrace("t3", "runner", "2026-01-03T00:00:00Z", "2026-01-03T00:00:01Z", 1);

        using var client = _factory.CreateMainApiClient();
        using var response = await client.GetAsync($"{ListPath}?service=runner");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(2, data.GetArrayLength());
        foreach (var row in data.EnumerateArray())
        {
            Assert.Equal("runner", row.GetProperty("service_name").GetString());
        }
    }

    [Fact]
    public async Task GetTraces_LimitAndService_CombineFilters()
    {
        SeedTrace("t1", "server", "2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z", 1);
        SeedTrace("t2", "server", "2026-01-02T00:00:00Z", "2026-01-02T00:00:01Z", 1);
        SeedTrace("t3", "server", "2026-01-03T00:00:00Z", "2026-01-03T00:00:01Z", 1);
        SeedTrace("t4", "runner", "2026-01-04T00:00:00Z", "2026-01-04T00:00:01Z", 1);

        using var client = _factory.CreateMainApiClient();
        using var response = await client.GetAsync($"{ListPath}?limit=10&service=server");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(3, data.GetArrayLength());
        foreach (var row in data.EnumerateArray())
        {
            Assert.Equal("server", row.GetProperty("service_name").GetString());
        }
    }

    [Fact]
    public async Task GetTraces_LimitAboveMax_ResponseIsAtMostMaxListLimit()
    {
        // The route must accept ?limit=5000 without error and return
        // at most MaxListLimit rows. We only seed a handful of traces
        // so the response is bounded by what we have rather than the
        // cap. The TraceQuerierSpecs covers the cap-by-clamping unit
        // semantics directly; here we just confirm the route accepts
        // over-large limits and never returns more than the cap.
        for (var i = 0; i < 10; i++)
        {
            SeedTrace(
                $"trace-{i:D4}",
                "svc",
                $"2026-02-{(i + 1):D2}T00:00:00Z",
                "2026-02-01T00:00:01Z",
                1);
        }

        using var client = _factory.CreateMainApiClient();
        using var response = await client.GetAsync($"{ListPath}?limit=5000");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var length = doc.RootElement.GetProperty("data").GetArrayLength();
        Assert.Equal(10, length);
        Assert.True(length <= TraceQuerier.MaxListLimit);
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
        var rows = data.GetProperty("rows");
        Assert.Equal(1, rows.GetArrayLength());
        Assert.Equal(2L, rows[0].GetProperty("total").GetInt64());
        Assert.False(data.GetProperty("truncated").GetBoolean());
        Assert.False(data.TryGetProperty("truncate_reason", out _));
    }

    [Fact]
    public async Task PostQuery_ExecutionBudget_ReturnsStructuredErrorAndCancelsExecutor()
    {
        var executor = _factory.FakeQueryExecutor;
        executor.BlockNextExecution();
        using var client = _factory.CreateMainApiClient();
        using var content = new StringContent(
            "{\"sql\":\"SELECT 1\"}", Encoding.UTF8, "application/json");

        var responseTask = client.PostAsync(QueryPath, content);
        await executor.BlockStarted.Task;
        _factory.TimeProvider.Advance(TimeSpan.FromSeconds(TraceQuerier.QueryExecutionBudgetSeconds));

        using var response = await responseTask;
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("query_execution_budget_exhausted", await ReadCodeAsync(response));
        Assert.True(executor.CancellationObserved);
    }

    [Fact]
    public async Task PostQuery_ClientCancellationCancelsExecutorWithoutResponseBody()
    {
        var executor = _factory.FakeQueryExecutor;
        executor.BlockNextExecution();
        using var client = _factory.CreateMainApiClient();
        using var requestCancellation = new CancellationTokenSource();
        using var content = new StringContent(
            "{\"sql\":\"SELECT 1\"}", Encoding.UTF8, "application/json");

        var responseTask = client.PostAsync(QueryPath, content, requestCancellation.Token);
        await executor.BlockStarted.Task;
        requestCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await responseTask);
        Assert.True(executor.CancellationObserved);
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
    public async Task PostQuery_MoreThanRowCap_ReturnsRowLimitTruncationIndicator()
    {
        const int target = TraceQuerier.MaxQueryResponseRows + 500;
        var sql = "WITH RECURSIVE cnt(x) AS (" +
                  "SELECT 1 UNION ALL SELECT x + 1 FROM cnt WHERE x < " + target + ") " +
                  "SELECT x FROM cnt;";

        using var client = _factory.CreateMainApiClient();
        using var content = new StringContent(
            "{\"sql\":\"" + sql + "\"}",
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync(QueryPath, content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        var rows = data.GetProperty("rows");
        Assert.Equal(TraceQuerier.MaxQueryResponseRows, rows.GetArrayLength());
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.Equal("row_limit", data.GetProperty("truncate_reason").GetString());
    }

    [Fact]
    public async Task PostQuery_SingleLargeCell_ReturnsByteLimitTruncationIndicator()
    {
        const int oversizedChars = 6 * 1024 * 1024;
        var sql = "SELECT substr(replace(hex(zeroblob(" + oversizedChars + ")), '0', 'x'), 1, " + oversizedChars + ") AS big";

        using var client = _factory.CreateMainApiClient();
        using var content = new StringContent(
            "{\"sql\":\"" + sql + "\"}",
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync(QueryPath, content);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected OK but got {(int)response.StatusCode}: {responseBody}");
        AssertResponseWithinByteCap(responseBody);

        using var doc = JsonDocument.Parse(responseBody);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(0, data.GetProperty("rows").GetArrayLength());
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.Equal("byte_limit", data.GetProperty("truncate_reason").GetString());
    }

    [Fact]
    public async Task PostQuery_ModerateRowsUnderRowCap_TruncatesByByteLimit()
    {
        const int rowCount = TraceQuerier.MaxQueryResponseRows;
        const int cellBytes = 6 * 1024;
        var sql = "WITH RECURSIVE cnt(x) AS (" +
                  "SELECT 1 UNION ALL SELECT x + 1 FROM cnt WHERE x < " + rowCount + ") " +
                  "SELECT hex(randomblob(" + (cellBytes / 2) + ")) AS payload FROM cnt;";

        using var client = _factory.CreateMainApiClient();
        using var content = new StringContent(
            "{\"sql\":\"" + sql + "\"}",
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync(QueryPath, content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var responseBody = await response.Content.ReadAsStringAsync();
        AssertResponseWithinByteCap(responseBody);
        using var doc = JsonDocument.Parse(responseBody);
        var data = doc.RootElement.GetProperty("data");
        var rows = data.GetProperty("rows");
        Assert.True(rows.GetArrayLength() < TraceQuerier.MaxQueryResponseRows);
        Assert.True(rows.GetArrayLength() > 0);
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.Equal("byte_limit", data.GetProperty("truncate_reason").GetString());
    }

    [Fact]
    public async Task PostQuery_RecursiveCteAmplification_BoundedByFirstLimitReached()
    {
        const int target = 50_000;
        var sql = "WITH RECURSIVE cnt(x) AS (" +
                  "SELECT 1 UNION ALL SELECT x + 1 FROM cnt WHERE x < " + target + ") " +
                  "SELECT x FROM cnt;";

        using var client = _factory.CreateMainApiClient();
        using var content = new StringContent(
            "{\"sql\":\"" + sql + "\"}",
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync(QueryPath, content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        var rows = data.GetProperty("rows");
        Assert.Equal(TraceQuerier.MaxQueryResponseRows, rows.GetArrayLength());
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.Equal("row_limit", data.GetProperty("truncate_reason").GetString());
    }

    [Fact]
    public async Task PostQuery_WithinBothCaps_OmitsTruncationIndicator()
    {
        SeedTrace("t1", "svc", "2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z", 1);

        using var client = _factory.CreateMainApiClient();
        using var content = new StringContent(
            "{\"sql\":\"SELECT service_name FROM traces\"}",
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync(QueryPath, content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        Assert.False(data.GetProperty("truncated").GetBoolean());
        Assert.False(data.TryGetProperty("truncate_reason", out var reason) && reason.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task GetStatus_OnMainApi_ReturnsCollectorStatus()
    {
        using var client = _factory.CreateMainApiClient();
        using var response = await client.GetAsync(StatusPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        Assert.True(data.TryGetProperty("collector_online", out _));
        Assert.True(data.TryGetProperty("db_size_bytes", out _));
        Assert.True(data.TryGetProperty("trace_count", out _));
        Assert.True(data.TryGetProperty("span_count", out _));
    }

    [Fact]
    public async Task GetStatus_ReportsCollectorOnline()
    {
        using var scope = _factory.Services.CreateScope();
        var status = scope.ServiceProvider.GetRequiredService<OtelCollectorStatus>();
        status.SetPortBound(true);

        using var client = _factory.CreateMainApiClient();
        using var response = await client.GetAsync(StatusPath);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(doc.RootElement.GetProperty("data").GetProperty("collector_online").GetBoolean());
    }

    [Fact]
    public async Task GetStatus_ReportsCollectorOffline()
    {
        using var scope = _factory.Services.CreateScope();
        var status = scope.ServiceProvider.GetRequiredService<OtelCollectorStatus>();
        status.SetPortBound(false);

        using var client = _factory.CreateMainApiClient();
        using var response = await client.GetAsync(StatusPath);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.False(doc.RootElement.GetProperty("data").GetProperty("collector_online").GetBoolean());
    }

    [Fact]
    public async Task GetStatus_WithTraces_ReportsCountsAndSize()
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

        Assert.Equal(3L, data.GetProperty("trace_count").GetInt64());
        Assert.True(data.GetProperty("span_count").GetInt64() >= 3L);
        // db_size_bytes is the otel.db file size; it is 0 against the in-memory
        // database this factory uses. The file-size contract (> 0 for a
        // file-backed db) is covered by TraceQuerierSpecs.GetStatusAsync_ReportsCountsAndFileSize.
        Assert.True(data.GetProperty("db_size_bytes").GetInt64() >= 0L);
    }

    [Fact]
    public async Task GetTraces_OnOtlpPortWithSpoofedMainHost_Returns404()
    {
        using var client = _factory.CreateOtlpClient();
        client.DefaultRequestHeaders.Host = "localhost:3456";

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

    private static void AssertResponseWithinByteCap(string responseBody)
    {
        Assert.InRange(
            Encoding.UTF8.GetByteCount(responseBody),
            0,
            TraceQuerier.MaxQueryResponseBytes);
    }
}
