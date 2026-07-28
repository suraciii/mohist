using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Storage;

public sealed class TypedWorkflowRunLineageMigrationSpecs
{
    private const string BeforeMigration = "20260726111353_AddAgentSessionStatusActivityProjection";
    private const string Migration = "20260728000000_TypedWorkflowRunLineage";

    [Fact]
    public async Task UpAndDown_TransformsLineageAndPreservesSchemaContracts()
    {
        await using var database = TestSqliteDatabase.CreateEmpty();
        MigratedSqliteTemplate.CopyTo(database.Keeper, BeforeMigration);

        await using (var db = database.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "WorkflowRuns" ("WorkflowRunId", "State", "EpicNumber", "ETag")
                VALUES ('wr_migration', '{{"id":"wr_migration","metadata":{{"createdAt":"2026-01-01T00:00:00Z","annotations":{{"projectId":"proj_1","issueNumber":"42","epicNumber":"3","custom":"kept"}}}},"status":"Running","stages":[]}}', 7, 1);
                """);
            await db.Database.GetService<IMigrator>().MigrateAsync(Migration);
        }

        await using (var db = database.CreateContext())
        {
            var row = await db.WorkflowRuns.SingleAsync(x => x.WorkflowRunId == "wr_migration");
            using var json = JsonDocument.Parse(row.State);
            var metadata = json.RootElement.GetProperty("metadata");
            var annotations = metadata.GetProperty("annotations");
            Assert.Equal("proj_1", metadata.GetProperty("projectId").GetString());
            Assert.Equal(42, metadata.GetProperty("issueNumber").GetInt32());
            Assert.Equal(7, metadata.GetProperty("epicNumber").GetInt32());
            Assert.Equal("kept", annotations.GetProperty("custom").GetString());
            Assert.False(annotations.TryGetProperty("projectId", out _));
            Assert.False(annotations.TryGetProperty("issueNumber", out _));
            Assert.False(annotations.TryGetProperty("epicNumber", out _));
            Assert.Equal("proj_1", row.MetadataProjectId);
            Assert.Equal(42, row.IssueNumber);

            var indexes = await ReadValuesAsync(db, "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'WorkflowRuns'");
            Assert.Contains("IX_WorkflowRuns_ProjectId_IssueNumber", indexes);
            Assert.Contains("IX_WorkflowRuns_ProjectId_EpicNumber", indexes);
            Assert.Contains("IX_WorkflowRuns_MetadataProjectId_WorkflowProfileIdKey", indexes);
            var foreignKeys = await ReadValuesAsync(db, "SELECT \"table\" FROM pragma_foreign_key_list('WorkflowRuns')");
            Assert.Contains(foreignKeys, value => value.Contains("WorkflowProfileRecords", StringComparison.Ordinal));

            await db.Database.GetService<IMigrator>().MigrateAsync(BeforeMigration);
        }

        await using (var db = database.CreateContext())
        {
            var row = await db.WorkflowRuns.SingleAsync(x => x.WorkflowRunId == "wr_migration");
            using var json = JsonDocument.Parse(row.State);
            var annotations = json.RootElement.GetProperty("metadata").GetProperty("annotations");
            Assert.Equal("proj_1", annotations.GetProperty("projectId").GetString());
            Assert.Equal("42", annotations.GetProperty("issueNumber").GetString());
            Assert.Equal("7", annotations.GetProperty("epicNumber").GetString());
            Assert.Equal("kept", annotations.GetProperty("custom").GetString());
        }
    }

    private static async Task<IReadOnlyList<string>> ReadValuesAsync(DbContext db, string sql)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        await db.Database.OpenConnectionAsync();
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));
        return values;
    }
}
