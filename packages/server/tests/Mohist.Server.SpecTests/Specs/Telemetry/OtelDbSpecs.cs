using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Otel;
using Mohist.Server.SystemInfo;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Telemetry;

public class OtelDbSpecs : IDisposable
{
    private readonly OtelDb _db;
    private readonly SqliteConnection _keeper;

    public OtelDbSpecs()
    {
        (_db, _keeper) = InMemoryOtelDb.Create();
    }

    public void Dispose()
    {
        _keeper.Dispose();
    }

    [Fact]
    public void OpenReadWriteConnection_CreatesTracesAndSpansTables()
    {
        using var connection = _db.OpenReadWriteConnection();

        Assert.True(TableExists(connection, OtelDb.TracesTable));
        Assert.True(TableExists(connection, OtelDb.SpansTable));
    }

    [Fact]
    public void OpenReadWriteConnection_CreatesExpectedColumns()
    {
        using var connection = _db.OpenReadWriteConnection();

        Assert.True(ColumnExists(connection, OtelDb.TracesTable, OtelDb.TracesTraceIdColumn));
        Assert.True(ColumnExists(connection, OtelDb.TracesTable, OtelDb.TracesServiceNameColumn));
        Assert.True(ColumnExists(connection, OtelDb.TracesTable, OtelDb.TracesStartTimeColumn));
        Assert.True(ColumnExists(connection, OtelDb.TracesTable, OtelDb.TracesEndTimeColumn));
        Assert.True(ColumnExists(connection, OtelDb.TracesTable, OtelDb.TracesSpanCountColumn));

        Assert.True(ColumnExists(connection, OtelDb.SpansTable, OtelDb.SpansTraceIdColumn));
        Assert.True(ColumnExists(connection, OtelDb.SpansTable, OtelDb.SpansSpanIdColumn));
        Assert.True(ColumnExists(connection, OtelDb.SpansTable, OtelDb.SpansParentSpanIdColumn));
        Assert.True(ColumnExists(connection, OtelDb.SpansTable, OtelDb.SpansNameColumn));
        Assert.True(ColumnExists(connection, OtelDb.SpansTable, OtelDb.SpansKindColumn));
        Assert.True(ColumnExists(connection, OtelDb.SpansTable, OtelDb.SpansStartTimeColumn));
        Assert.True(ColumnExists(connection, OtelDb.SpansTable, OtelDb.SpansEndTimeColumn));
        Assert.True(ColumnExists(connection, OtelDb.SpansTable, OtelDb.SpansAttributesColumn));
        Assert.True(ColumnExists(connection, OtelDb.SpansTable, OtelDb.SpansStatusCodeColumn));
        Assert.True(ColumnExists(connection, OtelDb.SpansTable, OtelDb.SpansStatusMessageColumn));
        Assert.True(ColumnExists(connection, OtelDb.SpansTable, OtelDb.SpansResourceAttributesColumn));
    }

    [Fact]
    public void OpenReadWriteConnection_CreatesAllExpectedIndices()
    {
        using var connection = _db.OpenReadWriteConnection();

        Assert.True(IndexExists(connection, OtelDb.TracesServiceStartIndex));
        Assert.True(IndexExists(connection, OtelDb.TracesStartIndex));
        Assert.True(IndexExists(connection, OtelDb.TracesEndIndex));
        Assert.True(IndexExists(connection, OtelDb.SpansTraceIndex));
    }

    [Fact]
    public void OpenReadWriteConnection_CalledTwice_IsIdempotent()
    {
        using var first = _db.OpenReadWriteConnection();
        using (var cmd = first.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO traces (trace_id, service_name, start_time, end_time, span_count) VALUES ('t1', 'svc', '2026-01-01T00:00:00Z', '2026-01-01T00:00:01Z', 1);";
            cmd.ExecuteNonQuery();
        }
        first.Dispose();

        using var second = _db.OpenReadWriteConnection();
        using (var cmd = second.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM traces;";
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }
    }

    [Fact]
    public void OpenReadWriteConnection_EnablesIncrementalAutoVacuum()
    {
        using var connection = _db.OpenReadWriteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA auto_vacuum;";
        var value = (long)cmd.ExecuteScalar()!;
        // 2 = INCREMENTAL; 1 = FULL; 0 = NONE.
        Assert.Equal(2L, value);
    }

    [Fact]
    public void OpenReadOnlyConnection_OpensAndExposesSchema()
    {
        using var readOnly = _db.OpenReadOnlyConnection();
        Assert.True(TableExists(readOnly, OtelDb.TracesTable));
        Assert.True(TableExists(readOnly, OtelDb.SpansTable));
    }

    [Fact]
    public void OpenReadinessConnection_DoesNotInitializeSchema()
    {
        var name = $"readiness-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={name};Mode=Memory;Cache=Shared";
        using var keeper = new SqliteConnection(connectionString);
        keeper.Open();
        var db = new OtelDb(connectionString, connectionString);
        using var connection = db.OpenReadinessConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';";

        Assert.Equal(0L, (long)command.ExecuteScalar()!);
        Assert.Contains("Default Timeout=1", connection.ConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OtelStorageProbe_ReadsSchemaAndEachSidecarOnce()
    {
        var fileSystem = new LengthFileSystem
        {
            ["<in-memory>"] = 11,
            ["<in-memory>-wal"] = 13,
            ["<in-memory>-shm"] = 17,
        };
        var probe = new OtelStorageProbe(_db, fileSystem);

        var result = probe.Probe();

        Assert.Equal(41, result.UsageBytes);
        Assert.Equal(1, fileSystem.Reads["<in-memory>"]);
        Assert.Equal(1, fileSystem.Reads["<in-memory>-wal"]);
        Assert.Equal(1, fileSystem.Reads["<in-memory>-shm"]);
    }

    // ---- Path resolution (pure static functions — no filesystem I/O) ----

    [Fact]
    public void ResolveDatabasePath_UsesConfiguredDbPath()
    {
        var options = new OtelOptions { DbPath = "/tmp/some-otel.db" };
        var path = OtelDb.ResolveDatabasePath(options, new MockEnvironment());

        Assert.Equal("/tmp/some-otel.db", path);
    }

    [Fact]
    public void ResolveDatabasePath_FallsBackToEnvVar()
    {
        var options = new OtelOptions();
        var environment = new MockEnvironment();
        environment[OtelOptions.DbPathEnvironmentVariable] = "/tmp/from-env-otel.db";

        var path = OtelDb.ResolveDatabasePath(options, environment);

        Assert.Equal("/tmp/from-env-otel.db", path);
    }

    [Fact]
    public void ResolveDatabasePath_DefaultUsesMainDbPathDirectory()
    {
        var mainDbPath = "/data/custom-main/mohist.db";
        var options = new OtelOptions();
        var environment = new MockEnvironment();
        environment[OtelOptions.MainDbPathEnvironmentVariable] = mainDbPath;

        var path = OtelDb.ResolveDatabasePath(options, environment);

        Assert.Equal("/data/custom-main/otel.db", path);
    }

    [Fact]
    public void ResolveDatabasePath_FallsBackToDefaultHomeWhenNothingConfigured()
    {
        var options = new OtelOptions();
        var environment = new MockEnvironment();
        environment["HOME"] = "/home/testuser";

        var path = OtelDb.ResolveDatabasePath(options, environment);

        Assert.Equal("/home/testuser/.mohist/otel.db", path);
    }

    [Fact]
    public void ResolveDatabasePath_ConfiguredOverridesEnvVar()
    {
        var options = new OtelOptions { DbPath = "/tmp/explicit-otel.db" };
        var environment = new MockEnvironment();
        environment[OtelOptions.DbPathEnvironmentVariable] = "/tmp/from-env-otel.db";

        var path = OtelDb.ResolveDatabasePath(options, environment);

        Assert.Equal("/tmp/explicit-otel.db", path);
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", tableName);
        var result = cmd.ExecuteScalar();
        return result != null;
    }

    private static bool ColumnExists(SqliteConnection connection, string tableName, string columnName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IndexExists(SqliteConnection connection, string indexName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='index' AND name=$name LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", indexName);
        var result = cmd.ExecuteScalar();
        return result != null;
    }

    private sealed class MockEnvironment : IEnvironmentVariableProvider
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string? this[string variable]
        {
            get => _values.TryGetValue(variable, out var v) ? v : null;
            set
            {
                if (value is null) _values.Remove(variable);
                else _values[variable] = value;
            }
        }

        public string? GetEnvironmentVariable(string variable) => this[variable];
        public string? GetEnvironmentVariable(string variable, EnvironmentVariableTarget target) => this[variable];
        public IReadOnlyDictionary<string, string> GetEnvironmentVariables() => new Dictionary<string, string>(_values, StringComparer.Ordinal);
        public IReadOnlyDictionary<string, string> GetEnvironmentVariables(EnvironmentVariableTarget target) => GetEnvironmentVariables();
        public string ExpandEnvironmentVariables(string name) => name;
        public void SetEnvironmentVariable(string variable, string? value) => this[variable] = value;
        public void SetEnvironmentVariable(string variable, string? value, EnvironmentVariableTarget target) => this[variable] = value;
    }

    private sealed class LengthFileSystem : IFileSystem
    {
        private readonly Dictionary<string, long> _lengths = new(StringComparer.Ordinal);

        public Dictionary<string, int> Reads { get; } = new(StringComparer.Ordinal);

        public long this[string path]
        {
            set => _lengths[path] = value;
        }

        public bool Exists(string path) => _lengths.ContainsKey(path);

        public string ReadAllText(string path) => throw new NotSupportedException();

        public void CreateDirectory(string path) { }

        public long? GetFileLength(string path)
        {
            Reads[path] = Reads.TryGetValue(path, out var count) ? count + 1 : 1;
            return _lengths.TryGetValue(path, out var length) ? length : null;
        }

        public void WriteAllText(string path, string contents) => throw new NotSupportedException();

        public void Delete(string path) => _lengths.Remove(path);
    }
}
