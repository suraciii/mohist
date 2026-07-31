using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliOtelCommandSpecs
{
    [Fact]
    public void OtelRoot_Help_DescribesServerRoutedCommands()
    {
        var exitCode = Run(["otel", "--help"], out var output, out _);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("query", text);
        Assert.Contains("status", text);
        Assert.Contains("through the Server", text, StringComparison.Ordinal);
        Assert.DoesNotContain("otel.db directly", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OtelQuery_NoArgs_FailsWithGuidanceAndNoHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => throw new InvalidOperationException("API must not be called"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["otel", "query"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("SQL", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OtelQuery_Help_ListsSubcommands()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["otel", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("query", output.ToString());
        Assert.Contains("status", output.ToString());
    }

    [Fact]
    public async Task OtelQuery_SelectOne_SendsPostAndRendersServerResult()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            async (req, _) =>
            {
                Assert.Equal(HttpMethod.Post, req.Method);
                Assert.Equal("/otel/api/query", req.RequestUri?.AbsolutePath);
                var bodyText = req.Content is null ? null : await req.Content.ReadAsStringAsync();
                Assert.NotNull(bodyText);
                var payload = JsonNode.Parse(bodyText!) as JsonObject;
                Assert.NotNull(payload);
                Assert.Equal("SELECT 1", payload!["sql"]!.GetValue<string>());

                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        columns = new[] { "1" },
                        rows = new object[] { new Dictionary<string, object?> { ["1"] = 1L } },
                        truncated = false,
                    },
                });
            });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["otel", "query", "SELECT 1"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("1", text);
        Assert.Single(handler.Requests);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task OtelQuery_NonSeekableResponse_RendersServerResult()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Ndjson([
                """{"success":true,"data":{"columns":["total"],"rows":[{"total":1}],"truncated":false}}""",
            ])));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["otel", "query", "SELECT COUNT(*) AS total FROM traces"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("total", output.ToString());
        Assert.Empty(error.ToString());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OtelQuery_DbOption_RejectedAsUnknownOption()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => throw new InvalidOperationException("API must not be called"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["otel", "query", "SELECT 1", "--db", "/tmp/otel.db"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OtelQuery_BareJson_ListsSelectableFieldsWithoutHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => throw new InvalidOperationException("API must not be called"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["otel", "query", "--json"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Empty(handler.Requests);
        var fields = JsonNode.Parse(output.ToString()) as JsonArray;
        Assert.NotNull(fields);
        var names = fields!.Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "columns", "rows", "truncated", "truncate_reason" },
            names);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task OtelQuery_InvalidJsonField_RejectedLocallyWithExitTwo()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => throw new InvalidOperationException("API must not be called"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["otel", "query", "SELECT 1", "--json", "nonexistent"], output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("nonexistent", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task OtelQuery_SelectedJson_ProjectsRequestedFields()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    columns = new[] { "total" },
                    rows = new object[] { new Dictionary<string, object?> { ["total"] = 2L } },
                    truncated = false,
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["otel", "query", "SELECT COUNT(*) AS total FROM traces", "--json", "rows,truncated"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var projected = JsonNode.Parse(output.ToString()) as JsonObject;
        Assert.NotNull(projected);
        Assert.True(projected!.ContainsKey("rows"));
        Assert.True(projected.ContainsKey("truncated"));
        Assert.False(projected.ContainsKey("columns"));
        Assert.False(projected.ContainsKey("truncate_reason"));
        var rows = projected["rows"] as JsonArray;
        Assert.NotNull(rows);
        Assert.Single(rows!);
        var firstRow = rows![0] as JsonObject;
        Assert.NotNull(firstRow);
        Assert.Equal(2L, firstRow!["total"]!.GetValue<long>());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task OtelQuery_TruncatedResult_RendersTruncationNotice()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    columns = new[] { "x" },
                    rows = new object[] { new Dictionary<string, object?> { ["x"] = 1L } },
                    truncated = true,
                    truncate_reason = "row_limit",
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["otel", "query", "SELECT x FROM large"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("(truncated: row_limit)", text);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task OtelQuery_NonTruncatedResult_OmitsTruncationNotice()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    columns = new[] { "service_name" },
                    rows = new object[] { new Dictionary<string, object?> { ["service_name"] = "svc" } },
                    truncated = false,
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["otel", "query", "SELECT service_name FROM traces"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("truncated", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task OtelQuery_EmptyResultSet_RendersHeaderAndZeroRowsMessage()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    columns = new[] { "trace_id", "service_name" },
                    rows = Array.Empty<object>(),
                    truncated = false,
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["otel", "query", "SELECT trace_id, service_name FROM traces WHERE 1=0"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("trace_id", text);
        Assert.Contains("(0 rows)", text);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task OtelQuery_ServerUnreachable_SurfacesStandardServerUnavailableMessage()
    {
        var handler = new ThrowingHttpHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri(CliTestFactory.BaseAddress) };
        var output = new StringWriter();
        var error = new StringWriter();
        var fs = new FakeFileSystem();
        var executor = new FakeCommandExecutor();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["otel", "query", "SELECT 1"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Equal(MohistCliApi.ServerUnavailableMessage + Environment.NewLine, error.ToString());
        Assert.DoesNotContain("ECONNREFUSED", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Connection refused", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task OtelQuery_ServerTimeout_SurfacesStandardServerUnavailableMessage()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => throw new TaskCanceledException("timeout"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["otel", "query", "SELECT 1"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Equal(MohistCliApi.ServerUnavailableMessage + Environment.NewLine, error.ToString());
        Assert.Empty(output.ToString());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OtelQuery_DisposesServerResponse()
    {
        TrackingHttpContent? content = null;
        var (_, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) =>
            {
                content = new TrackingHttpContent(
                    "{\"success\":true,\"data\":{\"columns\":[\"total\"],\"rows\":[{\"total\":1}],\"truncated\":false}}");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["otel", "query", "SELECT 1"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.NotNull(content);
        Assert.True(content!.Disposed);
    }

    [Fact]
    public async Task OtelQuery_ServerRejects_SurfacesErrorAndCode()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(
                new { success = false, error = "Only SELECT queries are allowed.", code = "query_not_select" },
                HttpStatusCode.BadRequest)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["otel", "query", "DELETE FROM traces"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Only SELECT queries are allowed.", error.ToString());
        Assert.Contains("query_not_select", error.ToString());
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task OtelQuery_ServerSqliteError_SurfacesErrorAndCode()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(
                new { success = false, error = "SQLite error: no such table: nonexistent", code = "query_sqlite_error" },
                HttpStatusCode.BadRequest)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["otel", "query", "SELECT * FROM nonexistent"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("SQLite error", error.ToString());
        Assert.Contains("query_sqlite_error", error.ToString());
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task OtelStatus_ServerUpCollectorOnline_RendersOnlineStatus()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = ValidStatus(true, "healthy"),
        })));

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(handler, ["otel", "status"], output, error);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("status: healthy", text);
        Assert.Contains("collector_online: True", text);
        Assert.Contains("usage_bytes: 4096", text);
        Assert.Contains("received_spans: 7", text);

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/otel/api/status", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task OtelStatus_ServerUpCollectorOffline_RendersOfflineStatus()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = ValidStatus(false, "degraded"),
        })));

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(handler, ["otel", "status"], output, error);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("status: degraded", text);
    }

    [Fact]
    public async Task OtelStatus_RendersAllRouteFields()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = ValidStatus(true, "healthy", new object[]
            {
                new
                {
                    route = "/api/items",
                    request_count = 3L,
                    average_duration_ms = 12.5,
                    max_duration_ms = 20.0,
                    database_calls_per_request = 2.0,
                    downstream_calls_per_request = 1.0,
                },
            }),
        })));

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(handler, ["otel", "status"], output, error);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("route: \"/api/items\"", text);
        Assert.Contains("request_count: 3", text);
        Assert.Contains("average_duration_ms: 12.5", text);
        Assert.Contains("max_duration_ms: 20", text);
        Assert.Contains("database_calls_per_request: 2", text);
        Assert.Contains("downstream_calls_per_request: 1", text);
    }

    [Fact]
    public async Task OtelStatus_ServerDown_ShowsStandardMessageWithoutStack()
    {
        var handler = new ThrowingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(handler, ["otel", "status"], output, error);

        Assert.NotEqual(0, exitCode);
        Assert.Equal(MohistCliApi.ServerUnavailableMessage + Environment.NewLine, error.ToString());
        Assert.DoesNotContain("ECONNREFUSED", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Connection refused", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task OtelStatus_ServerReturnsError_RendersError()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.JsonError("boom", "server_error")));

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(handler, ["otel", "status"], output, error);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("boom", error.ToString());
    }

    private int Run(string[] args, out StringWriter output, out StringWriter error)
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        output = new StringWriter();
        error = new StringWriter();
        return MohistCliCommands.RunAsync(
            new HttpClient(handler) { BaseAddress = new Uri(CliTestFactory.BaseAddress) },
            args,
            output,
            error,
            new FakeFileSystem(),
            new FakeCommandExecutor()).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(
        HttpMessageHandler handler,
        string[] args,
        StringWriter output,
        StringWriter error)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri(CliTestFactory.BaseAddress) };
        return await MohistCliCommands.RunAsync(
            http,
            args,
            output,
            error,
            new FakeFileSystem(),
            new FakeCommandExecutor());
    }

    private static object ValidStatus(bool collectorOnline, string status, object[]? routes = null) => new
    {
        status,
        collector_online = collectorOnline,
        since = "2026-07-21T00:00:00+00:00",
        storage = new { usage_bytes = (long?)4096, budget_bytes = 1073741824L, growth_bytes_per_second = (double?)null, growth_window_seconds = (double?)null },
        telemetry = new { received_spans = 7L, saved_spans = 6L, rejected_spans = 1L, dropped_spans = 0L },
        process = new { cpu_utilization = (double?)null, working_set_bytes = (long?)100, gc_heap_bytes = (long?)200 },
        latest_degradation = (object?)null,
        routes = routes ?? Array.Empty<object>(),
    };

    private sealed class ThrowingHttpHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            throw new HttpRequestException("Connection refused (simulated).", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));
        }
    }

    private sealed class TrackingHttpContent(string value) : HttpContent
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(value);

        public bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = _bytes.Length;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = disposing;
            base.Dispose(disposing);
        }
    }
}
