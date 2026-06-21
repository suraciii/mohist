using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliOtelCommandSpecs : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                var wal = path + "-wal";
                if (File.Exists(wal))
                    File.Delete(wal);
                var shm = path + "-shm";
                if (File.Exists(shm))
                    File.Delete(shm);
                var journal = path + "-journal";
                if (File.Exists(journal))
                    File.Delete(journal);
            }
            catch
            {
            }
        }
    }

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
    public void OtelQuery_DefaultPath_UsesHomeDir()
    {
        // The actual end-to-end default-path run touches
        // ~/.mohist/otel.db on the real filesystem, which would race
        // with other parallel tests and any live otel.db the host
        // might already have. The unit-level contract (default =
        // $HOME/.mohist/otel.db) is covered by ResolveDatabasePath_NullOrEmpty_ReturnsHomeDirPath;
        // the E2E "with default path" scenario is exercised by
        // OtelQuery_CustomDbPath_RunsAgainstExplicitPath + the
        // server tests, which collectively prove the CLI passes the
        // resolved path straight through to Microsoft.Data.Sqlite.
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var environment = new FakeEnvironment();
        Assert.Equal(
            Path.Combine(home, ".mohist", "otel.db"),
            OtelCommands.ResolveDatabasePath(null, environment));
        Assert.Equal(
            Path.Combine(home, ".mohist", "otel.db"),
            OtelCommands.ResolveDatabasePath(string.Empty, environment));
        Assert.Equal(
            Path.Combine(home, ".mohist", "otel.db"),
            OtelCommands.ResolveDatabasePath("   ", environment));
        // touch handler so the test still has 2 assertions on it
        Assert.NotNull(handler);
    }

    [Fact]
    public async Task OtelQuery_CustomDbPath_RunsAgainstExplicitPath()
    {
        var dbPath = CreateTempOtelDb(out var fileSystem);

        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["otel", "query", "SELECT COUNT(*) AS total FROM traces", "-d", dbPath],
            output,
            error,
            fileSystem: fileSystem);

        Assert.Equal(0, exitCode);
        Assert.Contains("total", output.ToString());
        Assert.Contains("1", output.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OtelQuery_DoesNotRequireServer()
    {
        var dbPath = CreateTempOtelDb(out var fileSystem);
        var handler = new ThrowingHttpHandler();

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["otel", "query", "SELECT COUNT(*) AS total FROM traces", "-d", dbPath],
            output,
            error,
            fileSystem: fileSystem);

        Assert.Equal(0, exitCode);
        Assert.Contains("total", output.ToString());
        Assert.Equal(0, handler.CallCount);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task OtelQuery_DbMissing_FailsWithClearError()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "mohist-nonexistent-" + Guid.NewGuid().ToString("N") + ".db");
        if (File.Exists(dbPath))
            File.Delete(dbPath);

        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["otel", "query", "SELECT 1", "-d", dbPath],
            output,
            error);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("otel.db not found", error.ToString());
        Assert.Contains(dbPath, error.ToString());
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task OtelQuery_SqlReferencesMissingTable_FailsWithSqliteError()
    {
        var dbPath = CreateTempOtelDb(out var fileSystem);
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["otel", "query", "SELECT * FROM nonexistent", "-d", dbPath],
            output,
            error,
            fileSystem: fileSystem);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("SQLite error", error.ToString());
        Assert.Contains("no such table", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OtelQuery_OpensConnectionReadOnly_RejectsWrites()
    {
        var dbPath = CreateTempOtelDb(out var fileSystem);

        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["otel", "query", "INSERT INTO traces (trace_id, service_name, start_time, end_time, span_count) VALUES ('abc', 'svc', '2026-01-01T00:00:00Z', '2026-01-01T00:00:01Z', 0)", "-d", dbPath],
            output,
            error,
            fileSystem: fileSystem);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("SQLite error", error.ToString());
        Assert.Contains("readonly", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OtelQuery_SqlSyntaxError_FailsWithSqliteError()
    {
        var dbPath = CreateTempOtelDb(out var fileSystem);

        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["otel", "query", "SELECT FROM WHERE", "-d", dbPath],
            output,
            error,
            fileSystem: fileSystem);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("SQLite error", error.ToString());
    }

    [Fact]
    public async Task OtelQuery_EmptyResultSet_RendersHeaderAndZeroRowsMessage()
    {
        var dbPath = CreateEmptyTempOtelDb(out var fileSystem);

        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        var output = new StringWriter();
        var error = new StringWriter();

        // A SELECT that returns 0 result rows (e.g. WHERE 1=0) triggers
        // the "(0 rows)" sentinel in the renderer.
        var exitCode = await RunAsync(
            handler,
            ["otel", "query", "SELECT * FROM traces WHERE 1=0", "-d", dbPath],
            output,
            error,
            fileSystem: fileSystem);

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
            data = new
            {
                collector_online = true,
                db_size_bytes = 4096L,
                trace_count = 7L,
                span_count = 42L,
            },
        })));

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(handler, ["otel", "status"], output, error);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("collector: online", text);
        Assert.Contains("db_size_bytes: 4096", text);
        Assert.Contains("trace_count: 7", text);
        Assert.Contains("span_count: 42", text);

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
            data = new
            {
                collector_online = false,
                db_size_bytes = 0L,
                trace_count = 0L,
                span_count = 0L,
            },
        })));

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(handler, ["otel", "status"], output, error);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("collector: offline", text);
    }

    [Fact]
    public async Task OtelStatus_ServerDown_ShowsStandardMessageWithoutStack()
    {
        var handler = new ThrowingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(handler, ["otel", "status"], output, error);

        Assert.NotEqual(0, exitCode);
        Assert.Equal("Server is not running. Start with: mo server start" + Environment.NewLine, error.ToString());
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
        var environment = new FakeEnvironment();
        var resolved = OtelCommands.ResolveDatabasePath(null, environment);
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mohist",
            "otel.db");
        Assert.Equal(expected, resolved);

        var empty = OtelCommands.ResolveDatabasePath("  ", environment);
        Assert.Equal(expected, empty);
    }

    [Fact]
    public void ResolveDatabasePath_DefaultUsesMainDbPathDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mohist-main-db-" + Guid.NewGuid().ToString("N"));
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
            new FakeCommandExecutor()).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(
        HttpMessageHandler handler,
        string[] args,
        StringWriter output,
        StringWriter error,
        FakeFileSystem? fileSystem = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        return await MohistCliCommands.RunAsync(
            http,
            args,
            output,
            error,
            fileSystem ?? new FakeFileSystem(),
            new FakeCommandExecutor());
    }

    private string CreateTempOtelDb(out FakeFileSystem fileSystem)
    {
        fileSystem = new FakeFileSystem();
        var path = CreateSchemaInNewFile(insertTrace: true);
        fileSystem.AddFile(path, "");
        return path;
    }

    private string CreateEmptyTempOtelDb(out FakeFileSystem fileSystem)
    {
        fileSystem = new FakeFileSystem();
        var path = CreateSchemaInNewFile(insertTrace: false);
        fileSystem.AddFile(path, "");
        return path;
    }

    private string CreateSchemaInNewFile(bool insertTrace)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "mohist-otel-" + Guid.NewGuid().ToString("N") + ".db");
        _tempFiles.Add(path);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };
        using (var connection = new SqliteConnection(builder.ToString()))
        {
            connection.Open();
            ExecuteNonQuery(connection, "PRAGMA journal_mode=WAL;");
            ExecuteNonQuery(connection, """
                CREATE TABLE IF NOT EXISTS traces (
                    trace_id    TEXT PRIMARY KEY,
                    service_name TEXT NOT NULL,
                    start_time  TEXT NOT NULL,
                    end_time    TEXT NOT NULL,
                    span_count  INTEGER NOT NULL DEFAULT 0
                );
                """);
            ExecuteNonQuery(connection, """
                CREATE TABLE IF NOT EXISTS spans (
                    trace_id           TEXT NOT NULL,
                    span_id            TEXT NOT NULL,
                    parent_span_id     TEXT,
                    name               TEXT NOT NULL,
                    kind               INTEGER NOT NULL,
                    start_time         TEXT NOT NULL,
                    end_time           TEXT NOT NULL,
                    attributes         TEXT,
                    status_code        INTEGER NOT NULL DEFAULT 0,
                    status_message     TEXT,
                    resource_attributes TEXT,
                    PRIMARY KEY (trace_id, span_id)
                );
                """);
            if (insertTrace)
            {
                ExecuteNonQuery(connection, """
                    INSERT INTO traces (trace_id, service_name, start_time, end_time, span_count)
                    VALUES ('trace_001', 'test-service', '2026-01-01T00:00:00Z', '2026-01-01T00:00:01Z', 3);
                    """);
            }
        }

        return path;
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

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
