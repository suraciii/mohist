using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.UnitTests.Support;

internal sealed class TestSqliteDatabase : IDisposable
{
    private TestSqliteDatabase(SqliteConnection keeper)
    {
        Keeper = keeper;
        Options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(keeper.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
    }

    public SqliteConnection Keeper { get; }

    public DbContextOptions<MohistDbContext> Options { get; }

    public static TestSqliteDatabase CreateModelSchema()
    {
        var keeper = new SqliteConnection($"Data Source=test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        try
        {
            keeper.Open();
            SqliteSchemaTemplate.CopyModelSchemaTo(keeper);
            return new TestSqliteDatabase(keeper);
        }
        catch
        {
            keeper.Dispose();
            throw;
        }
    }

    public MohistDbContext CreateContext() => new(Options);

    public void Dispose() => Keeper.Dispose();
}
