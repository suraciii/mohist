using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Migrations;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

[Trait(Traits.Speed.Name, Traits.Speed.Service)]
[Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
public class AgentSessionTranscriptRuntimeMigrationSpecs
{
    private const string PreviousMigrationId = "20260714120000_AddProjectEventReadKeys";
    private const string MigrationId = "20260716120000_AddRuntimeSessionIdToTranscriptTurns";

    [Fact]
    public async Task Migration_AddsRuntimeColumnAndDownDropsItsIndexAndColumn()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(PreviousMigrationId);
        await migrator.MigrateAsync(MigrationId);
        await InsertTurnAsync(database.Connection);
        Assert.True(await ColumnExistsAsync(database.Connection, "RuntimeSessionId"));
        Assert.True(await IndexExistsAsync(database.Connection));
        Assert.Contains(MigrationId, await context.Database.GetAppliedMigrationsAsync());

        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        new TestMigration().BuildDown(builder);

        Assert.Contains(builder.Operations, operation => operation is DropIndexOperation
        {
            Name: "IX_AgentSessionTranscriptTurns_SessionId_RuntimeSessionId_Sequence",
            Table: "AgentSessionTranscriptTurns",
        });
        Assert.Contains(builder.Operations, operation => operation is DropColumnOperation
        {
            Name: "RuntimeSessionId",
            Table: "AgentSessionTranscriptTurns",
        });
    }

    private static async Task InsertTurnAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AgentSessionTranscriptTurns (SessionId, Sequence, PromptText, PromptKind, StartedAt, UpdatedAt)
            VALUES ('migration-session', 1, 'prompt', 'task', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM pragma_table_info('AgentSessionTranscriptTurns') WHERE name = $column LIMIT 1;";
        command.Parameters.AddWithValue("$column", column);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task<bool> IndexExistsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = 'IX_AgentSessionTranscriptTurns_SessionId_RuntimeSessionId_Sequence' LIMIT 1;";
        return await command.ExecuteScalarAsync() is not null;
    }

    private static TestDatabase CreateDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new TestDatabase(connection, options);
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly DbContextOptions<MohistDbContext> _options;

        public TestDatabase(SqliteConnection connection, DbContextOptions<MohistDbContext> options)
        {
            Connection = connection;
            _options = options;
        }

        public SqliteConnection Connection { get; }

        public MohistDbContext CreateDbContext() => new(_options);

        public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
    }

    private sealed class TestMigration : AddRuntimeSessionIdToTranscriptTurns
    {
        public void BuildDown(MigrationBuilder builder) => Down(builder);
    }
}
