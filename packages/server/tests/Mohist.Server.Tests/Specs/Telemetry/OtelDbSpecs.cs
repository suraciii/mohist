using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Otel;
using Mohist.Server.SystemInfo;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Telemetry;

[Trait(Traits.Speed.Name, Traits.Speed.Unit)]
[Trait(Traits.Sut.Name, Traits.Sut.Telemetry)]
public class OtelDbSpecs : IDisposable
{
    private readonly string _dataDir;
    private readonly string _databasePath;
    private readonly OtelDb _db;

    public OtelDbSpecs()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"mohist-otel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        _databasePath = Path.Combine(_dataDir, "otel.db");

        var options = new OtelOptions { DbPath = _databasePath };
        _db = new OtelDb(options, new MockEnvironment(), new PassthroughFileSystem());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataDir))
                Directory.Delete(_dataDir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public void Constructor_CreatesDatabaseFileOnFirstConnection()
    {
        Assert.False(File.Exists(_databasePath));

        using var connection = _db.OpenReadWriteConnection();

        Assert.True(File.Exists(_databasePath));
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
        Assert.True(IndexExists(connection, OtelDb.SpansTraceIndex));
    }

    [Fact]
    public void OpenReadWriteConnection_EnablesWalMode()
    {
        using var connection = _db.OpenReadWriteConnection();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode;";
        var mode = (string?)pragma.ExecuteScalar();

        Assert.NotNull(mode);
        Assert.Equal("wal", mode, ignoreCase: true);
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
    public void OpenReadOnlyConnection_OpensAndExposesSchema()
    {
        using var readWrite = _db.OpenReadWriteConnection();
        readWrite.Dispose();

        using var readOnly = _db.OpenReadOnlyConnection();
        Assert.True(TableExists(readOnly, OtelDb.TracesTable));
        Assert.True(TableExists(readOnly, OtelDb.SpansTable));
    }

    [Fact]
    public void OpenReadOnlyConnection_RejectsWriteAttempts()
    {
        using var readWrite = _db.OpenReadWriteConnection();
        readWrite.Dispose();

        using var readOnly = _db.OpenReadOnlyConnection();
        using var cmd = readOnly.CreateCommand();
        cmd.CommandText = "INSERT INTO traces (trace_id, service_name, start_time, end_time, span_count) VALUES ('t2', 'svc', '2026-01-01T00:00:00Z', '2026-01-01T00:00:01Z', 1);";

        Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
    }

    [Fact]
    public void DatabasePath_MatchesOptionsDbPath()
    {
        Assert.Equal(_databasePath, _db.DatabasePath);
    }

    [Fact]
    public void ConnectionStrings_ExposeReadOnlyAndReadWrite()
    {
        Assert.Contains("Mode=ReadOnly", _db.ReadOnlyConnectionString, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Mode=ReadOnly", _db.ReadWriteConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveDatabasePath_UsesConfiguredDbPath()
    {
        var options = new OtelOptions { DbPath = "/tmp/some-otel.db" };
        var path = OtelDb.ResolveDatabasePath(options, new MockEnvironment());

        Assert.Equal(Path.GetFullPath("/tmp/some-otel.db"), path);
    }

    [Fact]
    public void ResolveDatabasePath_FallsBackToEnvVar()
    {
        var options = new OtelOptions();
        var environment = new MockEnvironment();
        environment[OtelOptions.DbPathEnvironmentVariable] = "/tmp/from-env-otel.db";

        var path = OtelDb.ResolveDatabasePath(options, environment);

        Assert.Equal(Path.GetFullPath("/tmp/from-env-otel.db"), path);
    }

    [Fact]
    public void ResolveDatabasePath_DefaultUsesMainDbPathDirectory()
    {
        var mainDbPath = Path.Combine(_dataDir, "custom-main", "mohist.db");
        var options = new OtelOptions();
        var environment = new MockEnvironment();
        environment[OtelOptions.MainDbPathEnvironmentVariable] = mainDbPath;

        var path = OtelDb.ResolveDatabasePath(options, environment);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(mainDbPath)!, OtelDb.DefaultDatabaseFileName)),
            path);
    }

    [Fact]
    public void ServiceRegistration_DefaultOtelDbPathUsesConfiguredMainDbDirectory()
    {
        var mainDbPath = Path.Combine(_dataDir, "configured-main", "mohist.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:DbPath"] = mainDbPath,
                ["Mohist:RunnerRoot"] = _dataDir,
                ["Mohist:SystemUpdate:StatePath"] = Path.Combine(_dataDir, "system-update.json"),
                ["Mohist:ArtifactStorage:Root"] = Path.Combine(_dataDir, "artifacts"),
            })
            .Build();
        var services = new ServiceCollection();
        services.ConfigureMohistServices(config);
        services.AddSingleton<IEnvironmentVariableProvider>(new MockEnvironment());
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<OtelOptions>>().Value;

        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(mainDbPath)!, OtelDb.DefaultDatabaseFileName),
            options.DbPath);
    }

    [Fact]
    public void ResolveDatabasePath_FallsBackToDefaultHomeWhenNothingConfigured()
    {
        var options = new OtelOptions();
        var environment = new MockEnvironment();
        environment["HOME"] = "/home/testuser";

        var path = OtelDb.ResolveDatabasePath(options, environment);

        Assert.Equal(
            Path.GetFullPath(Path.Combine("/home/testuser", OtelDb.DataDirectoryName, OtelDb.DefaultDatabaseFileName)),
            path);
    }

    [Fact]
    public void ResolveDatabasePath_ConfiguredOverridesEnvVar()
    {
        var options = new OtelOptions { DbPath = "/tmp/explicit-otel.db" };
        var environment = new MockEnvironment();
        environment[OtelOptions.DbPathEnvironmentVariable] = "/tmp/from-env-otel.db";

        var path = OtelDb.ResolveDatabasePath(options, environment);

        Assert.Equal(Path.GetFullPath("/tmp/explicit-otel.db"), path);
    }

    [Fact]
    public void DataIsolation_OtelDbAndMainMohistDb_AreSeparateFiles()
    {
        // Simulate the production layout: the main business DB lives at
        // <dataDir>/mohist.db, the otel DB lives at <dataDir>/otel.db.
        // Writing to one must not appear in the other.
        var mainDb = Path.Combine(_dataDir, "mohist.db");

        using (var main = new SqliteConnection($"Data Source={mainDb}"))
        {
            main.Open();
            using var cmd = main.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS issues (id TEXT PRIMARY KEY, title TEXT NOT NULL);";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO issues (id, title) VALUES ('issue-1', 'Hello');";
            cmd.ExecuteNonQuery();
        }

        using (var otel = _db.OpenReadWriteConnection())
        {
            using var cmd = otel.CreateCommand();
            cmd.CommandText = "INSERT INTO traces (trace_id, service_name, start_time, end_time, span_count) VALUES ('trace-1', 'svc', '2026-01-01T00:00:00Z', '2026-01-01T00:00:01Z', 1);";
            cmd.ExecuteNonQuery();
        }

        // otel.db contains trace data but no issues table
        using (var otel = _db.OpenReadOnlyConnection())
        {
            Assert.True(TableExists(otel, OtelDb.TracesTable));
            Assert.False(TableExists(otel, "issues"));

            using var cmd = otel.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM traces;";
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }

        // mohist.db contains issues but no traces table
        using (var main = new SqliteConnection($"Data Source={mainDb}"))
        {
            main.Open();
            Assert.True(TableExists(main, "issues"));
            Assert.False(TableExists(main, OtelDb.TracesTable));

            using var cmd = main.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM issues;";
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }
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
                if (value is null)
                    _values.Remove(variable);
                else
                    _values[variable] = value;
            }
        }

        public string? GetEnvironmentVariable(string variable) => this[variable];

        public string? GetEnvironmentVariable(string variable, EnvironmentVariableTarget target) => this[variable];

        public IReadOnlyDictionary<string, string> GetEnvironmentVariables() =>
            new Dictionary<string, string>(_values, StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> GetEnvironmentVariables(EnvironmentVariableTarget target) =>
            GetEnvironmentVariables();

        public string ExpandEnvironmentVariables(string name) => name;

        public void SetEnvironmentVariable(string variable, string? value) => this[variable] = value;

        public void SetEnvironmentVariable(string variable, string? value, EnvironmentVariableTarget target) => this[variable] = value;
    }

    /// <summary>
    /// Minimal <see cref="IFileSystem"/> that just delegates to the
    /// real filesystem. The <c>OtelDb</c> constructor only calls
    /// <see cref="IFileSystem.Exists"/>, so this fake exists purely to
    /// keep the abstraction in play without mocking out the OS.
    /// </summary>
    private sealed class PassthroughFileSystem : IFileSystem
    {
        public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

        public string ReadAllText(string path) => File.ReadAllText(path);
    }
}
