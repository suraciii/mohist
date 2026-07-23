using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.SystemInfo;

namespace Mohist.Server.Otel;

/// <summary>
/// Owns the <c>otel.db</c> SQLite file: path resolution, schema
/// initialization, WAL mode, and connection factories. The DDL and
/// indices are the <strong>stable contract</strong> between the server
/// (writer) and the <c>mo otel</c> CLI (direct reader) — any change to
/// table names, column names, or index names is a breaking change for
/// downstream queries.
/// </summary>
/// <remarks>
/// Schema reference: <c>openspec/changes/issue-219/design.md</c> Decision 4
/// and <c>openspec/changes/issue-219/specs/otel-trace-collection/spec.md</c>.
/// Times are stored as ISO 8601 UTC text so SQLite's lexicographic
/// ordering matches chronological ordering and the values are
/// human-readable for CLI consumers.
/// </remarks>
public sealed class OtelDb
{
    public const string DefaultDatabaseFileName = "otel.db";
    public const string DataDirectoryName = ".mohist";

    public const string TracesTable = "traces";
    public const string SpansTable = "spans";

    public const string TracesTraceIdColumn = "trace_id";
    public const string TracesServiceNameColumn = "service_name";
    public const string TracesStartTimeColumn = "start_time";
    public const string TracesEndTimeColumn = "end_time";
    public const string TracesSpanCountColumn = "span_count";

    public const string SpansTraceIdColumn = "trace_id";
    public const string SpansSpanIdColumn = "span_id";
    public const string SpansParentSpanIdColumn = "parent_span_id";
    public const string SpansNameColumn = "name";
    public const string SpansKindColumn = "kind";
    public const string SpansStartTimeColumn = "start_time";
    public const string SpansEndTimeColumn = "end_time";
    public const string SpansAttributesColumn = "attributes";
    public const string SpansStatusCodeColumn = "status_code";
    public const string SpansStatusMessageColumn = "status_message";
    public const string SpansResourceAttributesColumn = "resource_attributes";

    public const string TracesServiceStartIndex = "idx_traces_service_start";
    public const string TracesStartIndex = "idx_traces_start";
    public const string SpansTraceIndex = "idx_spans_trace";

    private const string CreateTracesTable = """
        CREATE TABLE IF NOT EXISTS traces (
            trace_id    TEXT PRIMARY KEY,
            service_name TEXT NOT NULL,
            start_time  TEXT NOT NULL,
            end_time    TEXT NOT NULL,
            span_count  INTEGER NOT NULL DEFAULT 0
        );
        """;

    private const string CreateSpansTable = """
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
        """;

    private const string CreateTracesServiceStartIndex =
        "CREATE INDEX IF NOT EXISTS idx_traces_service_start ON traces(service_name, start_time DESC);";

    private const string CreateTracesStartIndex =
        "CREATE INDEX IF NOT EXISTS idx_traces_start ON traces(start_time DESC);";

    private const string CreateSpansTraceIndex =
        "CREATE INDEX IF NOT EXISTS idx_spans_trace ON spans(trace_id);";

    private readonly object _initGate = new();
    private bool _initialized;

    /// <summary>
    /// Absolute path of the <c>otel.db</c> file. Resolved once at
    /// construction time.
    /// </summary>
    public string DatabasePath { get; }

    /// <summary>
    /// SQLite connection string for a read-write connection that opens
    /// (or creates) <see cref="DatabasePath"/>. Includes pooling and
    /// foreign keys but is intentionally <em>not</em> locked to
    /// <c>Mode=ReadOnly</c> — see <see cref="OpenReadOnlyConnection"/>
    /// for that.
    /// </summary>
    public string ReadWriteConnectionString { get; }

    /// <summary>
    /// SQLite connection string for a read-only connection. Writing
    /// through this connection fails at the SQLite engine regardless of
    /// what the caller attempts.
    /// </summary>
    public string ReadOnlyConnectionString { get; }

    /// <summary>
    /// DI-friendly constructor. The <see cref="IOptions{TOptions}"/> wrapper
    /// is unwrapped so the runtime dependencies are all explicit; the
    /// <see cref="IEnvironmentVariableProvider"/> and
    /// <see cref="IFileSystem"/> come from the service container's
    /// existing singletons.
    /// </summary>
    public OtelDb(IOptions<OtelOptions> options, IEnvironmentVariableProvider environment, IFileSystem fileSystem)
        : this(options.Value, environment, fileSystem)
    {
    }

    /// <summary>Test-friendly constructor that takes explicit dependencies.</summary>
    public OtelDb(OtelOptions options, IEnvironmentVariableProvider environment, IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(fileSystem);

        DatabasePath = ResolveDatabasePath(options, environment);
        EnsureDirectoryExists(DatabasePath, fileSystem);

        ReadWriteConnectionString = BuildConnectionString(DatabasePath, readOnly: false);
        ReadOnlyConnectionString = BuildConnectionString(DatabasePath, readOnly: true);
    }

    /// <summary>
    /// Test-only constructor that injects explicit connection strings instead
    /// of deriving them from a file path. Used to back <c>OtelDb</c> with an
    /// in-memory shared-cache SQLite database so OTel specs never touch a real
    /// <c>otel.db</c> file (design/testing.md hard-constraint 1). The caller
    /// owns a keeper <see cref="SqliteConnection"/> that keeps the in-memory
    /// database alive for the lifetime of this instance.
    /// </summary>
    /// <remarks>
    /// When the connection strings target an in-memory database, the read-only
    /// contract is <strong>not</strong> physically enforced by SQLite (the
    /// <c>Mode=ReadOnly</c> open flag is a no-op against an in-memory
    /// shared-cache database). That is acceptable in tests because the
    /// read-only contract is a production CLI safety guard, not a behavior the
    /// specs assert on; <see cref="TraceQuerier"/> only ever issues
    /// <c>SELECT</c> statements.
    /// </remarks>
    internal OtelDb(string readWriteConnectionString, string readOnlyConnectionString)
    {
        ReadWriteConnectionString = readWriteConnectionString;
        ReadOnlyConnectionString = readOnlyConnectionString;
        DatabasePath = "<in-memory>";
    }

    /// <summary>
    /// Resolves the absolute path of the <c>otel.db</c> file from the
    /// provided options + environment. Pure function — does not touch
    /// the filesystem.
    /// </summary>
    public static string ResolveDatabasePath(OtelOptions options, IEnvironmentVariableProvider environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        var configured = options.DbPath;
        if (string.IsNullOrWhiteSpace(configured))
            configured = environment.GetEnvironmentVariable(OtelOptions.DbPathEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(Path.Combine(ResolveDefaultDataDirectory(environment), DefaultDatabaseFileName));

        return Path.GetFullPath(configured);
    }

    private static string ResolveDefaultDataDirectory(IEnvironmentVariableProvider environment)
    {
        var mainDbPath = environment.GetEnvironmentVariable(OtelOptions.MainDbPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(mainDbPath))
        {
            var fullMainDbPath = Path.GetFullPath(mainDbPath);
            var mainDbDirectory = Path.GetDirectoryName(fullMainDbPath);
            if (!string.IsNullOrWhiteSpace(mainDbDirectory))
                return mainDbDirectory;
        }

        var home = environment.GetEnvironmentVariable(MohistServiceRegistration.HomeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, DataDirectoryName);
    }

    /// <summary>
    /// Opens a read-write connection and, on first use, initializes the
    /// schema and enables WAL mode. Safe to call from multiple threads:
    /// initialization is guarded by an internal lock and the
    /// <c>CREATE TABLE IF NOT EXISTS</c> DDL is idempotent.
    /// </summary>
    public SqliteConnection OpenReadWriteConnection()
    {
        var connection = new SqliteConnection(ReadWriteConnectionString);
        connection.Open();
        EnsureInitialized(connection);
        return connection;
    }

    public SqliteConnection OpenReadinessConnection()
    {
        var builder = new SqliteConnectionStringBuilder(ReadWriteConnectionString)
        {
            DefaultTimeout = 1,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Opens a read-only connection to <c>otel.db</c>. Used by the
    /// <c>POST /otel/api/query</c> handler and by <c>mo otel query</c> so
    /// the SQLite engine physically rejects write attempts regardless of
    /// any keyword-level checks at higher layers.
    /// </summary>
    /// <remarks>
    /// The call lazily materializes <c>otel.db</c> (and its schema) on
    /// first use if no ingester has touched the file yet. The
    /// initialization is the same DDL the read-write path runs, so the
    /// file is queryable end-to-end the moment either factory runs —
    /// which matters for <c>mo otel status</c> and the query endpoints
    /// being meaningful before the first batch of traces arrives.
    /// </remarks>
    public SqliteConnection OpenReadOnlyConnection()
    {
        // SQLite's read-only open mode refuses to create a missing file
        // (it would have to take a write lock). Bootstrap the file with
        // a transient read-write connection so the read-only open
        // immediately afterwards sees a real database. This is the
        // cheapest way to keep the read-only contract without forcing
        // every consumer to pre-write a row.
        if (!_initialized)
        {
            using var bootstrap = OpenReadWriteConnection();
        }

        var connection = new SqliteConnection(ReadOnlyConnectionString);
        connection.Open();
        return connection;
    }

    private void EnsureInitialized(SqliteConnection connection)
    {
        if (_initialized)
            return;

        lock (_initGate)
        {
            if (_initialized)
                return;

            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL;";
                _ = pragma.ExecuteScalar();
            }

            ExecuteNonQuery(connection, CreateTracesTable);
            ExecuteNonQuery(connection, CreateSpansTable);
            ExecuteNonQuery(connection, CreateTracesServiceStartIndex);
            ExecuteNonQuery(connection, CreateTracesStartIndex);
            ExecuteNonQuery(connection, CreateSpansTraceIndex);

            _initialized = true;
        }
    }

    private static void EnsureDirectoryExists(string databasePath, IFileSystem fileSystem)
    {
        var dir = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrWhiteSpace(dir))
            return;
        if (fileSystem.Exists(dir))
            return;
        Directory.CreateDirectory(dir);
    }

    private static string BuildConnectionString(string databasePath, bool readOnly)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        };
        if (!readOnly)
        {
            // Shared cache lets concurrent writers see each other's
            // changes immediately. Read-only connections cannot
            // participate in a shared cache, so the option is
            // intentionally omitted when readOnly is true.
            builder.Cache = SqliteCacheMode.Shared;
        }
        return builder.ToString();
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
