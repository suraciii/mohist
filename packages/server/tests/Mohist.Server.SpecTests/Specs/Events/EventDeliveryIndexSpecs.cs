using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

public class EventDeliveryIndexSpecs
{
    private const string UndeliveredFilter = "\"DispatchedAt\" IS NULL";

    [Fact]
    public async Task CurrentModelAndMigratedDatabase_KeepAllUndeliveredIndexes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        await using var context = new MohistDbContext(options);
        await context.Database.MigrateAsync();

        var expectations = new[]
        {
            new IndexExpectation(typeof(WorkflowRunEventRow), "WorkflowRunEvents", "IX_WorkflowRunEvents_Source_Id_DispatchedAt"),
            new IndexExpectation(typeof(IssueEventRow), "IssueEvents", "IX_IssueEvents_Source_Id_DispatchedAt"),
            new IndexExpectation(typeof(EpicEventRow), "EpicEvents", "IX_EpicEvents_Source_Id_DispatchedAt"),
        };

        foreach (var expectation in expectations)
        {
            AssertModelIndex(context, expectation);
            await AssertDatabaseIndexAsync(connection, expectation);
        }
    }

    private static void AssertModelIndex(MohistDbContext context, IndexExpectation expectation)
    {
        var entity = context.Model.FindEntityType(expectation.EntityType);
        Assert.NotNull(entity);
        var index = entity!.GetIndexes()
            .SingleOrDefault(candidate => candidate.GetDatabaseName() == expectation.IndexName);
        Assert.NotNull(index);
        Assert.Equal(new[] { "Source", "Id", "DispatchedAt" }, index!.Properties.Select(property => property.Name));
        Assert.Equal(UndeliveredFilter, index.GetFilter());
    }

    private static async Task AssertDatabaseIndexAsync(SqliteConnection connection, IndexExpectation expectation)
    {
        await using var sqlCommand = connection.CreateCommand();
        sqlCommand.CommandText = """
            SELECT sql
            FROM sqlite_master
            WHERE type = 'index' AND tbl_name = $tableName AND name = $indexName;
            """;
        sqlCommand.Parameters.AddWithValue("$tableName", expectation.TableName);
        sqlCommand.Parameters.AddWithValue("$indexName", expectation.IndexName);
        var sql = Assert.IsType<string>(await sqlCommand.ExecuteScalarAsync());
        Assert.Contains($"WHERE {UndeliveredFilter}", sql, StringComparison.Ordinal);

        await using var columnsCommand = connection.CreateCommand();
        columnsCommand.CommandText = """
            SELECT name
            FROM pragma_index_info($indexName)
            ORDER BY seqno;
            """;
        columnsCommand.Parameters.AddWithValue("$indexName", expectation.IndexName);
        var columns = new List<string>();
        await using var reader = await columnsCommand.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(0));
        Assert.Equal(new[] { "Source", "Id", "DispatchedAt" }, columns);
    }

    private sealed record IndexExpectation(Type EntityType, string TableName, string IndexName);
}
