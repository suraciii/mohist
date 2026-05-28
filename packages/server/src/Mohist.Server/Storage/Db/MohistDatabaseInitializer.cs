using Microsoft.EntityFrameworkCore;

namespace Mohist.Server.Storage.Db;

public static class MohistDatabaseInitializer
{
    public static void Initialize(MohistDbContext db)
    {
        db.Database.EnsureCreated();
    }
}
