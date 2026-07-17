using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Otel;
using Mohist.Server.Otel.OtlpJson;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Telemetry;

public class TraceIngesterSpecs : IDisposable
{
    private const string TraceId1 = "00000000000000000000000000000001";
    private const string SpanId1 = "0000000000000001";
    // 2026-01-01T00:00:00Z = 1767225600 seconds
    private const string StartNanos = "1767225600000000000";
    private const string EndNanos = "1767225601000000000";

    private const string ValidPayloadTemplate = """
        {
          "resourceSpans": [
            {
              "resource": {
                "attributes": [
                  {"key":"service.name","value":{"stringValue":"__SVC__"}},
                  {"key":"service.version","value":{"stringValue":"1.2.3"}}
                ]
              },
              "scopeSpans": [
                {
                  "scope": {"name":"my.lib","version":"0.0.1"},
                  "spans": [
                    {
                      "traceId": "__TRACE__",
                      "spanId": "__SPAN__",
                      "name": "GET /resource",
                      "kind": 1,
                      "startTimeUnixNano": "__START__",
                      "endTimeUnixNano": "__END__",
                      "attributes": [
                        {"key":"http.status_code","value":{"intValue":"200"}}
                      ],
                      "status": {"code": 1, "message": "ok"}
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private const string SingleSpanTemplate = """
        {
          "resourceSpans": [{
            "resource": {"attributes":[{"key":"service.name","value":{"stringValue":"__SVC__"}}]},
            "scopeSpans": [{
              "spans": [{
                "traceId":"__TRACE__","spanId":"__SPAN__","name":"__NAME__",
                "startTimeUnixNano":"__START__",
                "endTimeUnixNano":"__END__"
              }]
            }]
          }]
        }
        """;

    private readonly OtelDb _db;
    private readonly TraceIngester _ingester;
    // Keeper keeps the in-memory SQLite database alive for the test's lifetime.
    private readonly Microsoft.Data.Sqlite.SqliteConnection _keeper;

    public TraceIngesterSpecs()
    {
        (_db, _keeper) = InMemoryOtelDb.Create();
        _ingester = new TraceIngester(_db, NullLogger<TraceIngester>.Instance);
    }

    public void Dispose()
    {
        _keeper.Dispose();
    }

    [Fact]
    public void IngestJson_ValidPayload_PersistsSpanAndTrace()
    {
        var payload = ValidPayloadTemplate
            .Replace("__SVC__", "my-service")
            .Replace("__TRACE__", TraceId1)
            .Replace("__SPAN__", SpanId1)
            .Replace("__START__", StartNanos)
            .Replace("__END__", EndNanos);

        var count = _ingester.IngestJson(payload);

        Assert.Equal(1, count);

        using var connection = _db.OpenReadOnlyConnection();

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT {OtelDb.TracesServiceNameColumn}, {OtelDb.TracesStartTimeColumn}, {OtelDb.TracesEndTimeColumn}, {OtelDb.TracesSpanCountColumn} FROM {OtelDb.TracesTable} WHERE {OtelDb.TracesTraceIdColumn} = $id";
            cmd.Parameters.AddWithValue("$id", TraceId1);
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("my-service", reader.GetString(0));
            Assert.Equal("2026-01-01T00:00:00.0000000Z", reader.GetString(1));
            Assert.Equal("2026-01-01T00:00:01.0000000Z", reader.GetString(2));
            Assert.Equal(1L, reader.GetInt64(3));
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT {OtelDb.SpansNameColumn}, {OtelDb.SpansKindColumn}, {OtelDb.SpansStatusCodeColumn}, {OtelDb.SpansAttributesColumn}, {OtelDb.SpansResourceAttributesColumn} FROM {OtelDb.SpansTable} WHERE {OtelDb.SpansTraceIdColumn} = $id";
            cmd.Parameters.AddWithValue("$id", TraceId1);
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("GET /resource", reader.GetString(0));
            Assert.Equal(1, reader.GetInt32(1));
            Assert.Equal(1, reader.GetInt32(2));
            var attributes = reader.GetString(3);
            var resourceAttrs = reader.GetString(4);

            using (var attrDoc = System.Text.Json.JsonDocument.Parse(attributes))
            {
                Assert.Equal(System.Text.Json.JsonValueKind.Array, attrDoc.RootElement.ValueKind);
                Assert.Single(attrDoc.RootElement.EnumerateArray());
                var entry = attrDoc.RootElement[0];
                Assert.Equal("http.status_code", entry.GetProperty("key").GetString());
                Assert.Equal(200L, entry.GetProperty("value").GetInt64());
            }

            using (var resDoc = System.Text.Json.JsonDocument.Parse(resourceAttrs))
            {
                Assert.Equal(System.Text.Json.JsonValueKind.Array, resDoc.RootElement.ValueKind);
                Assert.Equal(2, resDoc.RootElement.GetArrayLength());
            }
        }
    }

    [Fact]
    public void IngestJson_SpansMissingFields_AreSkipped()
    {
        const string payload = """
            {
              "resourceSpans": [
                {
                  "resource": {"attributes":[{"key":"service.name","value":{"stringValue":"svc"}}]},
                  "scopeSpans": [
                    {
                      "spans": [
                        {"traceId":"abc","spanId":"def","name":"valid","startTimeUnixNano":"1","endTimeUnixNano":"2"},
                        {"traceId":"abc","name":"missing-spanid","startTimeUnixNano":"1","endTimeUnixNano":"2"},
                        {"traceId":"abc","spanId":"def"}
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        var count = _ingester.IngestJson(payload);

        Assert.Equal(1, count);
    }

    [Fact]
    public void IngestJson_NullOrEmptyResourceSpans_ReturnsZero()
    {
        Assert.Equal(0, _ingester.IngestJson("""{"resourceSpans":null}"""));
        Assert.Equal(0, _ingester.IngestJson("""{"resourceSpans":[]}"""));
        Assert.Equal(0, _ingester.IngestJson("{}"));
    }

    [Fact]
    public void IngestJson_MissingServiceName_DefaultsToUnknownService()
    {
        const string traceId = "00000000000000000000000000000010";
        var payload = """
            {
              "resourceSpans": [
                {
                  "resource": {"attributes":[{"key":"host.name","value":{"stringValue":"x"}}]},
                  "scopeSpans": [
                    {
                      "spans": [
                        {
                          "traceId": "00000000000000000000000000000010",
                          "spanId": "0000000000000010",
                          "name": "no-svc",
                          "startTimeUnixNano": "1767225600000000000",
                          "endTimeUnixNano":   "1767225601000000000"
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        _ingester.IngestJson(payload);

        using var connection = _db.OpenReadOnlyConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {OtelDb.TracesServiceNameColumn} FROM {OtelDb.TracesTable} WHERE {OtelDb.TracesTraceIdColumn} = $id";
        cmd.Parameters.AddWithValue("$id", traceId);
        var service = (string?)cmd.ExecuteScalar();
        Assert.Equal("unknown_service", service);
    }

    [Fact]
    public void IngestJson_SameSpanTwice_IsIdempotent()
    {
        const string traceId = "00000000000000000000000000000020";
        const string spanId = "0000000000000020";
        var payload = SingleSpanTemplate
            .Replace("__SVC__", "svc")
            .Replace("__TRACE__", traceId)
            .Replace("__SPAN__", spanId)
            .Replace("__NAME__", "idem")
            .Replace("__START__", StartNanos)
            .Replace("__END__", EndNanos);

        _ingester.IngestJson(payload);
        _ingester.IngestJson(payload);
        _ingester.IngestJson(payload);

        using var connection = _db.OpenReadOnlyConnection();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM {OtelDb.SpansTable} WHERE {OtelDb.SpansTraceIdColumn} = $id";
            cmd.Parameters.AddWithValue("$id", traceId);
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM {OtelDb.TracesTable} WHERE {OtelDb.TracesTraceIdColumn} = $id";
            cmd.Parameters.AddWithValue("$id", traceId);
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }
    }

    [Fact]
    public void IngestJson_SameTraceAcrossBatches_UpdatesSpanCount()
    {
        const string traceId = "00000000000000000000000000000030";
        for (var i = 1; i <= 3; i++)
        {
            var spanId = $"000000000000003{i}";
            var payload = SingleSpanTemplate
                .Replace("__SVC__", "svc")
                .Replace("__TRACE__", traceId)
                .Replace("__SPAN__", spanId)
                .Replace("__NAME__", $"n{i}")
                .Replace("__START__", StartNanos)
                .Replace("__END__", EndNanos);
            _ingester.IngestJson(payload);
        }

        using var connection = _db.OpenReadOnlyConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {OtelDb.TracesSpanCountColumn} FROM {OtelDb.TracesTable} WHERE {OtelDb.TracesTraceIdColumn} = $id";
        cmd.Parameters.AddWithValue("$id", traceId);
        Assert.Equal(3L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void IngestJson_MultipleResources_TakesFirstServiceName()
    {
        const string traceId = "00000000000000000000000000000040";
        const string payload = """
            {
              "resourceSpans": [
                {
                  "resource": {"attributes":[{"key":"service.name","value":{"stringValue":"first-svc"}}]},
                  "scopeSpans": [{
                    "spans": [{
                      "traceId":"00000000000000000000000000000040","spanId":"0000000000000041","name":"a",
                      "startTimeUnixNano":"1767225600000000000",
                      "endTimeUnixNano":"1767225601000000000"
                    }]
                  }]
                },
                {
                  "resource": {"attributes":[{"key":"service.name","value":{"stringValue":"second-svc"}}]},
                  "scopeSpans": [{
                    "spans": [{
                      "traceId":"00000000000000000000000000000040","spanId":"0000000000000042","name":"b",
                      "startTimeUnixNano":"1767225600000000000",
                      "endTimeUnixNano":"1767225601000000000"
                    }]
                  }]
                }
              ]
            }
            """;

        _ingester.IngestJson(payload);

        using var connection = _db.OpenReadOnlyConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {OtelDb.TracesServiceNameColumn} FROM {OtelDb.TracesTable} WHERE {OtelDb.TracesTraceIdColumn} = $id";
        cmd.Parameters.AddWithValue("$id", traceId);
        Assert.Equal("first-svc", (string?)cmd.ExecuteScalar());
    }

    [Theory]
    [InlineData("0", "1970-01-01T00:00:00.0000000Z")]
    [InlineData("1767225600000000000", "2026-01-01T00:00:00.0000000Z")]
    [InlineData("100", "1970-01-01T00:00:00.0000001Z")]
    [InlineData("1000", "1970-01-01T00:00:00.0000010Z")]
    [InlineData("1000000000", "1970-01-01T00:00:01.0000000Z")]
    [InlineData("1767225601000000000", "2026-01-01T00:00:01.0000000Z")]
    public void TryConvertUnixNanoToUtcIso_HandlesCommonValues(string raw, string expectedIso)
    {
        Assert.True(TraceIngester.TryConvertUnixNanoToUtcIso(raw, out var iso));
        Assert.Equal(expectedIso, iso);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("999999999999999999999999999")]
    public void TryConvertUnixNanoToUtcIso_RejectsBadInputs(string? raw)
    {
        Assert.False(TraceIngester.TryConvertUnixNanoToUtcIso(raw, out _));
    }
}
