using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Mohist.Server.TestSupport;

public static class MigratedSqliteTemplate
{
    private static readonly Lazy<SqliteConnection> Template = new(
        () => CreateTemplate(null), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly ConcurrentDictionary<string, Lazy<SqliteConnection>> TargetTemplates = new();
    private static readonly Lazy<SqliteConnection> ModelSchemaTemplate = new(
        CreateModelSchemaTemplate, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly object BackupLock = new();

    public static void CopyTo(SqliteConnection destination)
    {
        Copy(Template.Value, destination);
    }

    public static void CopyTo(SqliteConnection destination, string targetMigration)
    {
        var template = TargetTemplates.GetOrAdd(
            targetMigration,
            static target => new Lazy<SqliteConnection>(
                () => CreateTemplate(target), LazyThreadSafetyMode.ExecutionAndPublication));
        Copy(template.Value, destination);
    }

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
        using var db = TestDatabaseSchema.CreateDbContext(connection);
        if (targetMigration is null)
        {
            TestDatabaseSchema.Migrate(db);
        }
        else
        {
            db.GetService<IMigrator>().Migrate(targetMigration);
        }
        return connection;
    }

    private static SqliteConnection CreateModelSchemaTemplate()
    {
        var connection = OpenInMemory();
        using var db = TestDatabaseSchema.CreateDbContext(connection);
        db.Database.EnsureCreated();
        return connection;
    }

    private static SqliteConnection OpenInMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }
}
