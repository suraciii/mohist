using Microsoft.Data.Sqlite;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs;

public class MigratedSqliteTemplateTests
{
    [Fact]
    public async Task CopyTo_CreatesSchemaFromAllMigrations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        MigratedSqliteTemplate.CopyTo(connection);

        Assert.True(await HasEntryAsync(connection, "table", "Attachments"));
        Assert.True(await HasEntryAsync(connection, "index", "IX_Attachments_ExpiresAt"));
        Assert.True(await HasEntryAsync(connection, "index", "IX_LabelDefinitions_ProjectId_Key"));

        var columns = await NamesAsync(connection, "SELECT name FROM pragma_table_xinfo('WorkflowRuns')");
        Assert.Contains("Status", columns);
        Assert.Contains("AssignedWorkerId", columns);
        Assert.Contains("ReadySince", columns);

        var indexes = await NamesAsync(connection, "SELECT name FROM pragma_index_list('WorkflowRuns')");
        Assert.Contains("IX_WorkflowRuns_Status", indexes);
        Assert.Contains("IX_WorkflowRuns_Status_ReadySince", indexes);
    }

    private static async Task<bool> HasEntryAsync(SqliteConnection connection, string type, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = $type AND name = $name";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task<IReadOnlyList<string>> NamesAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        return names;
    }
}
