using System.Runtime.CompilerServices;
using Mohist.Server.TestSupport;
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.SpecTests.Support;

/// <summary>
/// Process-wide cache of Mohist SQLite schema states. Building a schema
/// (full migration chain, chain up to a historical target, or
/// <c>EnsureCreated</c> from the model) costs seconds per call and used to
/// run once per test; instead each distinct state is built once here and
/// every test clones it via the SQLite online-backup API (milliseconds).
/// Templates are schema-only (zero rows), so a clone is byte-for-byte the
/// state the corresponding EF call would produce — including
/// <c>__EFMigrationsHistory</c> for the migration-based templates, which
/// makes a subsequent <c>Migrate()</c> on the clone purely incremental.
/// Specs whose subject is the chain itself running from an empty database
/// (<c>DatabaseInitializationSpecs</c>) must keep calling <c>Migrate()</c>
/// and must not clone a template.
/// </summary>
public static class MigratedSqliteTemplate
{
    private static readonly Lazy<SqliteConnection> Template = new(
        () => CreateTemplate(null), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly ConcurrentDictionary<string, Lazy<SqliteConnection>> TargetTemplates = new();

    private static readonly Lazy<SqliteConnection> ModelSchemaTemplate = new(
        CreateModelSchemaTemplate, LazyThreadSafetyMode.ExecutionAndPublication);

    // SqliteConnection is not thread-safe and parallel collections clone
    // concurrently; a schema-only backup is sub-millisecond, so a single
    // lock never becomes a bottleneck.
    private static readonly object BackupLock = new();

    // Build the expensive migration template before xUnit starts timing test
    // cases; each case should pay only for cloning and its own seed data.
    internal static void Warm() => _ = Template.Value;

    /// <summary>
    /// Copies the fully migrated schema (including
    /// <c>GrainTestConfig.MigrateWithSchemaFix</c>) into
    /// <paramref name="destination"/>, which must be an already-open
    /// connection to an empty database.
    /// </summary>
    public static void CopyTo(SqliteConnection destination)
    {
        Copy(Template.Value, destination);
    }

    /// <summary>
    /// Copies the schema as of <paramref name="targetMigration"/>
    /// (inclusive) into <paramref name="destination"/> — byte-for-byte
    /// what <c>IMigrator.Migrate(targetMigration)</c> produces on an empty
    /// database. No post-chain schema fix is applied.
    /// </summary>
    public static void CopyTo(SqliteConnection destination, string targetMigration)
    {
        var template = TargetTemplates.GetOrAdd(
            targetMigration,
            static target => new Lazy<SqliteConnection>(
                () => CreateTemplate(target), LazyThreadSafetyMode.ExecutionAndPublication));
        Copy(template.Value, destination);
    }

    /// <summary>
    /// Copies the schema <c>EnsureCreated()</c> builds from the current
    /// model (no <c>__EFMigrationsHistory</c>) into
    /// <paramref name="destination"/>.
    /// </summary>
    public static void CopyModelSchemaTo(SqliteConnection destination)
    {
        Copy(ModelSchemaTemplate.Value, destination);
    }

    private static void Copy(SqliteConnection source, SqliteConnection destination)
    {
        lock (BackupLock)
        {
            source.BackupDatabase(destination);
        }
    }

    private static SqliteConnection CreateTemplate(string? targetMigration)
    {
        var connection = OpenInMemory();
        try
        {
            using var db = CreateContext(connection);
            if (targetMigration is null)
            {
                GrainTestConfig.MigrateWithSchemaFix(db);
            }
            else
            {
                db.GetService<IMigrator>().Migrate(targetMigration);
            }
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static SqliteConnection CreateModelSchemaTemplate()
    {
        var connection = OpenInMemory();
        try
        {
            using var db = CreateContext(connection);
            db.Database.EnsureCreated();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static SqliteConnection OpenInMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static MohistDbContext CreateContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            // Same suppression as GrainTestConfig.CreateDbContext: a raw
            // DbContext outside the silo would otherwise abort Migrate()
            // on a pending-model-changes warning.
            .ConfigureWarnings(w => w.Ignore(
                RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new MohistDbContext(options);
    }
}

internal static class MigratedSqliteTemplateWarmup
{
    [ModuleInitializer]
    internal static void Initialize() => MigratedSqliteTemplate.Warm();
}
