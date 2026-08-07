using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.UnitTests.Support;

internal static class SqliteSchemaTemplate
{
    private static readonly Lazy<SqliteConnection> ModelSchema = new(CreateModelSchema);
    private static readonly object BackupLock = new();

    public static void Warm() => _ = ModelSchema.Value;

    public static void CopyModelSchemaTo(SqliteConnection destination)
    {
        lock (BackupLock)
        {
            ModelSchema.Value.BackupDatabase(destination);
        }
    }

    private static SqliteConnection CreateModelSchema()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        using var db = new MohistDbContext(options);
        db.Database.EnsureCreated();
        return connection;
    }
}

internal static class SqliteSchemaTemplateWarmup
{
    [ModuleInitializer]
    internal static void Initialize() => SqliteSchemaTemplate.Warm();
}
