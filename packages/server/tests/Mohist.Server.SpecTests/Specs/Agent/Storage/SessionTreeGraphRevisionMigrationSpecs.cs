using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Storage;

public sealed class SessionTreeGraphRevisionMigrationSpecs
{
    [Fact]
    public async Task MigratedSchema_HasSessionTreeGraphRevisionsTableWithProjectKeyAndRevisionColumns()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        await using var db = database.CreateContext();

        var columns = await ReadTableColumnsAsync(db, "SessionTreeGraphRevisions");
        Assert.Contains("ProjectId", columns.Keys);
        Assert.Contains("PublishedRevision", columns.Keys);
        Assert.Contains("PublishedAt", columns.Keys);
        Assert.Equal(1, columns["ProjectId"].NotNull);
        Assert.Equal(1, columns["PublishedRevision"].NotNull);
        Assert.Equal(1, columns["PublishedAt"].NotNull);
        Assert.Equal(1, columns["ProjectId"].PrimaryKey);

        var revision = await db.SessionTreeGraphRevisions
            .Where(row => row.ProjectId == "absent-project")
            .Select(row => (long?)row.PublishedRevision)
            .FirstOrDefaultAsync();
        Assert.Null(revision);
    }

    private static async Task<Dictionary<string, (int NotNull, int PrimaryKey, string? DefaultValue)>> ReadTableColumnsAsync(
        MohistDbContext db, string table)
    {
        await db.Database.OpenConnectionAsync();
        var columns = new Dictionary<string, (int NotNull, int PrimaryKey, string? DefaultValue)>(StringComparer.Ordinal);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns[reader.GetString(1)] = (reader.GetInt32(3), reader.GetInt32(5), reader.IsDBNull(4) ? null : reader.GetString(4));
        await db.Database.CloseConnectionAsync();
        return columns;
    }
}
