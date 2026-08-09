using Mohist.Server.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.SpecTests.Support;

public sealed class TestSqliteDatabase : IDisposable, IAsyncDisposable
{
    private TestSqliteDatabase(SqliteConnection keeper)
    {
        Keeper = keeper;
        ConnectionString = keeper.ConnectionString;
        Options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(ConnectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
    }

    public SqliteConnection Keeper { get; }

    public string ConnectionString { get; }

    public DbContextOptions<MohistDbContext> Options { get; }

    internal int DisposeCount { get; private set; }

    public MohistDbContext CreateContext() => new(Options);

    public static TestSqliteDatabase CreateMigrated() => Create(MigratedSqliteTemplate.CopyTo);

    public static TestSqliteDatabase CreateModelSchema() => Create(MigratedSqliteTemplate.CopyModelSchemaTo);

    public static TestSqliteDatabase CreateEmpty() => Create(static _ => { });

    public void Dispose()
    {
        DisposeCount++;
        Keeper.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        DisposeCount++;
        await Keeper.DisposeAsync();
    }

    private static TestSqliteDatabase Create(Action<SqliteConnection> copySchema)
        => Create(
            () => new SqliteConnection($"Data Source=test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared"),
            copySchema);

    internal static TestSqliteDatabase Create(
        Func<SqliteConnection> createKeeper,
        Action<SqliteConnection> copySchema)
        => Create(createKeeper, copySchema, static keeper => new TestSqliteDatabase(keeper));

    internal static TestSqliteDatabase Create(
        Func<SqliteConnection> createKeeper,
        Action<SqliteConnection> copySchema,
        Func<SqliteConnection, TestSqliteDatabase> construct)
    {
        var keeper = createKeeper();
        try
        {
            keeper.Open();
            copySchema(keeper);
            return construct(keeper);
        }
        catch
        {
            keeper.Dispose();
            throw;
        }
    }
}
