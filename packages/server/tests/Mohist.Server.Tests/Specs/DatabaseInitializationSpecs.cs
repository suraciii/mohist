using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Storage.Db;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class DatabaseInitializationSpecs
{
    [Fact]
    public async Task Initialize_WhenExistingDatabaseLacksWorkflowSessionTables_CreatesThem()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE "GrainStates" (
                    "Key" TEXT NOT NULL,
                    "Type" TEXT NOT NULL,
                    "JsonState" TEXT NOT NULL,
                    CONSTRAINT "PK_GrainStates" PRIMARY KEY ("Key", "Type")
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new MohistDbContext(options);
        MohistDatabaseInitializer.Initialize(db);

        Assert.True(await TableExistsAsync(connection, "WorkflowSessions"));
        Assert.True(await TableExistsAsync(connection, "WorkflowSessionEvents"));
        Assert.True(await IndexExistsAsync(connection, "IX_WorkflowSessions_WorkflowRunId_SessionName"));
        Assert.True(await IndexExistsAsync(connection, "IX_WorkflowSessionEvents_WorkflowSessionId_Sequence"));
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table' AND name = $name
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$name", name);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task<bool> IndexExistsAsync(SqliteConnection connection, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'index' AND name = $name
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$name", name);
        return await command.ExecuteScalarAsync() is not null;
    }
}
