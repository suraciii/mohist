using System.Net;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliOtelCommandSpecs
{
    [Fact]
    public void OtelRoot_Help_ListsSubcommands()
    {
        var exitCode = Run(["otel", "--help"], out var output, out _);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("query", text);
        Assert.Contains("status", text);
    }

    [Fact]
    public async Task OtelQuery_NoArgs_FailsWithGuidance()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(handler, ["otel", "query"], output, error);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("SQL", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OtelQuery_Help_ListsSubcommands()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(handler, ["otel", "--help"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("query", output.ToString());
        Assert.Contains("status", output.ToString());
    }

    [Fact]
    public async Task OtelQuery_CustomDbPath_RunsAgainstExplicitPath()
    {
        var dbPath = "/tmp/otel-custom.db";
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile(dbPath, "");
        var executor = FakeOtelQueryExecutor.ReturningColumns(
            new[] { "total" },
            new[] { new object?[] { 1L } });

        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["otel", "query", "SELECT COUNT(*) AS total FROM traces", "-d", dbPath],
            output,
            error,
            fileSystem: fileSystem,
            queryExecutor: executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("total", output.ToString());
        Assert.Contains("1", output.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OtelQuery_LocalPath_ReturnsAllRowsWithoutHttpTruncationMetadata()
    {
        var dbPath = "/tmp/otel-unbounded-local.db";
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile(dbPath, "");
        var rows = new List<object?[]>(capacity: 1001);
        for (var i = 1; i <= 1001; i++)
            rows.Add([i]);

        var executor = FakeOtelQueryExecutor.ReturningColumns(["value"], rows);
        var handler = new ThrowingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["otel", "query", "SELECT value FROM traces", "-d", dbPath],
            output,
            error,
            fileSystem: fileSystem,
            queryExecutor: executor);

        Assert.Equal(0, exitCode);
        Assert.Equal(1003, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("1001", output.ToString());
        Assert.DoesNotContain("truncated", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(error.ToString());
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task OtelQuery_DoesNotRequireServer()
    {
        var dbPath = "/tmp/otel-no-server.db";
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile(dbPath, "");
        var executor = FakeOtelQueryExecutor.ReturningColumns(
            new[] { "total" },
            new[] { new object?[] { 1L } });
        var handler = new ThrowingHttpHandler();

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["otel", "query", "SELECT COUNT(*) AS total FROM traces", "-d", dbPath],
            output,
            error,
            fileSystem: fileSystem,
            queryExecutor: executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("total", output.ToString());
        Assert.Equal(0, handler.CallCount);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task OtelQuery_DbMissing_FailsWithClearError()
    {
        var dbPath = "/tmp/otel-does-not-exist-" + Guid.NewGuid().ToString("N") + ".db";
        var fileSystem = new FakeFileSystem();

        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["otel", "query", "SELECT 1", "-d", dbPath],
            output,
            error,
            fileSystem: fileSystem,
            queryExecutor: FakeOtelQueryExecutor.ReturningEmpty());

        Assert.NotEqual(0, exitCode);
        Assert.Contains("otel.db not found", error.ToString());
        Assert.Contains(dbPath, error.ToString());
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task OtelQuery_SqlReferencesMissingTable_FailsWithSqliteError()
    {
        var dbPath = "/tmp/otel-missing-table.db";
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile(dbPath, "");
        var executor = FakeOtelQueryExecutor.Throwing("SQLite error: no such table: nonexistent");

        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["otel", "query", "SELECT * FROM nonexistent", "-d", dbPath],
            output,
            error,
            fileSystem: fileSystem,
            queryExecutor: executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("SQLite error", error.ToString());
        Assert.Contains("no such table", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OtelQuery_OpensConnectionReadOnly_RejectsWrites()
    {
        var dbPath = "/tmp/otel-readonly.db";
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile(dbPath, "");
        var executor = FakeOtelQueryExecutor.Throwing(
            "SQLite error: attempt to write a readonly database",
            isReadOnlyViolation: true);

        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["otel", "query", "INSERT INTO traces (trace_id) VALUES ('abc')", "-d", dbPath],
            output,
            error,
            fileSystem: fileSystem,
            queryExecutor: executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("SQLite error", error.ToString());
        Assert.Contains("readonly", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OtelQuery_SqlSyntaxError_FailsWithSqliteError()
    {
        var dbPath = "/tmp/otel-syntax.db";
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile(dbPath, "");
        var executor = FakeOtelQueryExecutor.Throwing("SQLite error: near \"FROM\": syntax error");

        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["otel", "query", "SELECT FROM WHERE", "-d", dbPath],
            output,
            error,
            fileSystem: fileSystem,
            queryExecutor: executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("SQLite error", error.ToString());
    }

    [Fact]
    public async Task OtelQuery_EmptyResultSet_RendersHeaderAndZeroRowsMessage()
    {
        var dbPath = "/tmp/otel-empty.db";
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile(dbPath, "");
        // The traces table schema is what the production otel.db exposes; the
        // column names drive the rendered header. An empty result (WHERE 1=0)
        // triggers the "(0 rows)" sentinel.
        var executor = FakeOtelQueryExecutor.ReturningColumns(
            new[] { "trace_id", "service_name", "start_time", "end_time", "span_count" },
            Array.Empty<object?[]>());

        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["otel", "query", "SELECT * FROM traces WHERE 1=0", "-d", dbPath],
            output,
            error,
            fileSystem: fileSystem,
            queryExecutor: executor);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("trace_id", text);
        Assert.Contains("(0 rows)", text);
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

    [Fact]
    public void ResolveDatabasePath_NullOrEmpty_ReturnsHomeDirPath()
    {
        var environment = new FakeEnvironment
        {
            ["HOME"] = CliTestFactory.UserHome,
        };
        var resolved = OtelCommands.ResolveDatabasePath(null, environment);
        var expected = Path.Combine(
            CliTestFactory.UserHome,
            ".mohist",
            "otel.db");
        Assert.Equal(expected, resolved);

        var empty = OtelCommands.ResolveDatabasePath("  ", environment);
        Assert.Equal(expected, empty);
    }

    [Fact]
    public void ResolveDatabasePath_DefaultUsesMainDbPathDirectory()
    {
        const string tempDir = "/mohist-tests/otel-main-db";
        var mainDbPath = Path.Combine(tempDir, "mohist.db");
        var environment = new FakeEnvironment();
        environment[OtelCommands.MainDbPathEnvironmentVariable] = mainDbPath;

        var resolved = OtelCommands.ResolveDatabasePath(null, environment);

        Assert.Equal(Path.Combine(tempDir, "otel.db"), resolved);
    }

    [Fact]
    public void ResolveDatabasePath_SuppliedPath_ReturnsFullPath()
    {
        var resolved = OtelCommands.ResolveDatabasePath("./foo/otel.db", new FakeEnvironment());
        Assert.True(Path.IsPathRooted(resolved));
        Assert.EndsWith(Path.Combine("foo", "otel.db"), resolved);
    }

    private int Run(string[] args, out StringWriter output, out StringWriter error)
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        output = new StringWriter();
        error = new StringWriter();
        return MohistCliCommands.RunAsync(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") },
            args,
            output,
            error,
            new FakeFileSystem(),
            new FakeCommandExecutor(),
            queryExecutor: FakeOtelQueryExecutor.ReturningEmpty()).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(
        HttpMessageHandler handler,
        string[] args,
        StringWriter output,
        StringWriter error,
        FakeFileSystem? fileSystem = null,
        IOtelQueryExecutor? queryExecutor = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        return await MohistCliCommands.RunAsync(
            http,
            args,
            output,
            error,
            fileSystem ?? new FakeFileSystem(),
            new FakeCommandExecutor(),
            queryExecutor: queryExecutor);
    }

    private static object ValidStatus(bool collectorOnline, string status) => new
    {
        status,
        collector_online = collectorOnline,
        since = "2026-07-21T00:00:00+00:00",
        storage = new { usage_bytes = (long?)4096, budget_bytes = 1073741824L, growth_bytes_per_second = (double?)null, growth_window_seconds = (double?)null },
        telemetry = new { received_spans = 7L, saved_spans = 6L, rejected_spans = 1L, dropped_spans = 0L },
        process = new { cpu_utilization = (double?)null, working_set_bytes = (long?)100, gc_heap_bytes = (long?)200 },
        latest_degradation = (object?)null,
        routes = Array.Empty<object>(),
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

    private sealed class FakeEnvironment : IEnvironmentVariableProvider
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string? this[string variable]
        {
            get => _values.TryGetValue(variable, out var value) ? value : null;
            set
            {
                if (value is null)
                    _values.Remove(variable);
                else
                    _values[variable] = value;
            }
        }

        public string? GetEnvironmentVariable(string variable) => this[variable];

        public string? GetEnvironmentVariable(string variable, EnvironmentVariableTarget target) => this[variable];

        public IReadOnlyDictionary<string, string> GetEnvironmentVariables() => _values;

        public IReadOnlyDictionary<string, string> GetEnvironmentVariables(EnvironmentVariableTarget target) => _values;

        public string ExpandEnvironmentVariables(string name) => name;

        public void SetEnvironmentVariable(string variable, string? value) => this[variable] = value;

        public void SetEnvironmentVariable(string variable, string? value, EnvironmentVariableTarget target) => this[variable] = value;
    }
}
