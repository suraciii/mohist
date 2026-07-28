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

    [Theory]
    [InlineData("42junk", "numeric suffix")]
    [InlineData("1e2", "scientific notation")]
    [InlineData("1.5", "decimal")]
    public async Task Up_PreservesMalformedLegacyIssueNumberInsteadOfFabricatingIdentity(string issueNumber, string label)
    {
        await using var database = TestSqliteDatabase.CreateEmpty();
        MigratedSqliteTemplate.CopyTo(database.Keeper, BeforeMigration);

        await using (var db = database.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "WorkflowRuns" ("WorkflowRunId", "State", "EpicNumber", "ETag")
                VALUES ('wr_malformed', {LegacyAnnotationState(issueNumber)}, NULL, 1);
                """);
            await db.Database.GetService<IMigrator>().MigrateAsync(Migration);
        }

        await using (var db = database.CreateContext())
        {
            var row = await db.WorkflowRuns.SingleAsync(x => x.WorkflowRunId == "wr_malformed");
            using var json = JsonDocument.Parse(row.State);
            var metadata = json.RootElement.GetProperty("metadata");
            var annotations = metadata.GetProperty("annotations");
            Assert.True(annotations.TryGetProperty("issueNumber", out var preservedIssue), $"{label}: legacy issueNumber annotation must be preserved");
            Assert.Equal(issueNumber, preservedIssue.GetString());
            Assert.True(annotations.TryGetProperty("projectId", out _), $"{label}: legacy projectId annotation must be preserved");
            Assert.False(metadata.TryGetProperty("issueNumber", out _), $"{label}: no typed issueNumber must be fabricated");
            Assert.False(metadata.TryGetProperty("projectId", out _), $"{label}: no typed projectId must be fabricated");
        }
    }

    [Theory]
    [InlineData("+5", 5)]
    [InlineData("042", 42)]
    [InlineData(" 42 ", 42)]
    [InlineData("\t42\t", 42)]
    [InlineData("\r\n42\r\n", 42)]
    public async Task Up_MigratesLegacyIssueNumberAcceptedByPreviousReader(string issueNumber, int expectedIssueNumber)
    {
        await using var database = TestSqliteDatabase.CreateEmpty();
        MigratedSqliteTemplate.CopyTo(database.Keeper, BeforeMigration);

        await using (var db = database.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "WorkflowRuns" ("WorkflowRunId", "State", "EpicNumber", "ETag")
                VALUES ('wr_legacy_integer', {LegacyAnnotationState(issueNumber)}, NULL, 1);
                """);
            await db.Database.GetService<IMigrator>().MigrateAsync(Migration);
        }

        await using (var db = database.CreateContext())
        {
            var row = await db.WorkflowRuns.SingleAsync(x => x.WorkflowRunId == "wr_legacy_integer");
            using var json = JsonDocument.Parse(row.State);
            var metadata = json.RootElement.GetProperty("metadata");
            var annotations = metadata.GetProperty("annotations");
            Assert.Equal(expectedIssueNumber, metadata.GetProperty("issueNumber").GetInt32());
            Assert.False(annotations.TryGetProperty("projectId", out _));
            Assert.False(annotations.TryGetProperty("issueNumber", out _));
        }
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\t \n\r")]
    public async Task Up_PreservesWhitespaceOnlyLegacyProjectIdInsteadOfMigratingIt(string projectId)
    {
        await using var database = TestSqliteDatabase.CreateEmpty();
        MigratedSqliteTemplate.CopyTo(database.Keeper, BeforeMigration);

        await using (var db = database.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "WorkflowRuns" ("WorkflowRunId", "State", "EpicNumber", "ETag")
                VALUES ('wr_blank_project', {LegacyBlankProjectState(projectId)}, NULL, 1);
                """);
            await db.Database.GetService<IMigrator>().MigrateAsync(Migration);
        }

        await using (var db = database.CreateContext())
        {
            var row = await db.WorkflowRuns.SingleAsync(x => x.WorkflowRunId == "wr_blank_project");
            using var json = JsonDocument.Parse(row.State);
            var metadata = json.RootElement.GetProperty("metadata");
            var annotations = metadata.GetProperty("annotations");
            Assert.True(annotations.TryGetProperty("projectId", out var preservedProject), "whitespace-only legacy projectId annotation must be preserved");
            Assert.Equal(projectId, preservedProject.GetString());
            Assert.False(metadata.TryGetProperty("projectId", out _), "no typed projectId must be fabricated from whitespace");
            Assert.False(metadata.TryGetProperty("issueNumber", out _), "no typed issueNumber must be fabricated");
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

    private static string LegacyAnnotationState(string issueNumber) =>
        "{\"id\":\"wr_malformed\",\"metadata\":{\"createdAt\":\"2026-01-01T00:00:00Z\",\"annotations\":{\"projectId\":\"proj_1\",\"issueNumber\":" + JsonSerializer.Serialize(issueNumber) + "}},\"status\":\"Running\",\"stages\":[]}";

    private static string LegacyBlankProjectState(string projectId) =>
        "{\"id\":\"wr_blank_project\",\"metadata\":{\"createdAt\":\"2026-01-01T00:00:00Z\",\"annotations\":{\"projectId\":" + JsonSerializer.Serialize(projectId) + ",\"issueNumber\":\"42\"}},\"status\":\"Running\",\"stages\":[]}";
}
