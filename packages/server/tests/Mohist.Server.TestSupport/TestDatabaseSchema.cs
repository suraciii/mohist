using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.TestSupport;

public static class TestDatabaseSchema
{
    public static MohistDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new MohistDbContext(options);
    }

    public static MohistDbContext CreateDbContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new MohistDbContext(options);
    }

    public static void Migrate(MohistDbContext db) => db.Database.Migrate();
}
