using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.SpecTests.Support;

/// <summary>
/// Process-wide cache of the fully migrated Mohist SQLite schema. Running
/// the full migration chain costs seconds per call and used to run once
/// per test in every spec that news up an in-memory database; instead the
/// chain runs once here and each test clones the result via the SQLite
/// online-backup API (milliseconds). The template is schema-only (zero
/// rows), so a clone is byte-for-byte the state
/// <c>GrainTestConfig.MigrateWithSchemaFix</c> would produce — including
/// <c>__EFMigrationsHistory</c>. Specs that verify migration behavior
/// itself (<c>*MigrationSpecs</c>, <c>DatabaseInitializationSpecs</c>)
/// must keep calling <c>Migrate()</c> and must not use this template.
/// </summary>
public static class MigratedSqliteTemplate
{
    private static readonly Lazy<SqliteConnection> Template = new(
        CreateTemplate, LazyThreadSafetyMode.ExecutionAndPublication);

    // SqliteConnection is not thread-safe and parallel collections clone
    // concurrently; a schema-only backup is sub-millisecond, so a single
    // lock never becomes a bottleneck.
    private static readonly object BackupLock = new();

    /// <summary>
    /// Copies the migrated schema into <paramref name="destination"/>,
    /// which must be an already-open connection to an empty database.
    /// </summary>
    public static void CopyTo(SqliteConnection destination)
    {
        var source = Template.Value;
        lock (BackupLock)
        {
            source.BackupDatabase(destination);
        }
    }

    private static SqliteConnection CreateTemplate()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            // Same suppression as GrainTestConfig.CreateDbContext: a raw
            // DbContext outside the silo would otherwise abort Migrate()
            // on a pending-model-changes warning.
            .ConfigureWarnings(w => w.Ignore(
                RelationalEventId.PendingModelChangesWarning))
            .Options;
        using var db = new MohistDbContext(options);
        GrainTestConfig.MigrateWithSchemaFix(db);
        return connection;
    }
}
