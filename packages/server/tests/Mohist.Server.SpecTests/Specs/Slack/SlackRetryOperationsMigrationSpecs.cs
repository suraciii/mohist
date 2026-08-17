using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackRetryOperationsMigrationSpecs
{
    private const string Migration = "20260909000000_AddSlackRetryOperations";

    [Fact]
    public async Task Up_creates_retry_operation_table_and_indexes()
    {
        await using var database = CreateDatabase(Migration);
        await using var context = database.CreateDbContext();

        Assert.NotNull(context.SlackRetryOperations);
        Assert.Contains("SlackRetryOperations", await TableNamesAsync(context));
        var indexes = await IndexNamesAsync(context);
        Assert.Contains("UX_SlackRetryOperations_ProjectId_ActionKey", indexes);
        Assert.Contains("IX_SlackRetryOperations_State_RecoveryLeaseExpiresAt", indexes);
    }

    [Fact]
    public async Task Dispatch_claim_is_single_winner_and_release_allows_recovery_takeover()
    {
        await using var database = CreateDatabase(Migration);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero));
        await using (var context = database.CreateDbContext())
        {
            context.SlackRetryOperations.Add(NewRow("one", time.GetUtcNow()));
            await context.SaveChangesAsync();
        }

        var store = new SlackRetryOperationStore(database.Factory, time);
        var first = await store.ClaimDispatchAsync(
            "project-1", "action-1", "claim-one", TimeSpan.FromMinutes(1));
        var concurrent = await store.ClaimDispatchAsync(
            "project-1", "action-1", "claim-two", TimeSpan.FromMinutes(1));

        Assert.NotNull(first);
        Assert.Null(concurrent);

        await store.ReleaseDispatchClaimAsync("project-1", "action-1", "claim-one");
        var recovered = await store.ClaimDispatchAsync(
            "project-1", "action-1", "claim-two", TimeSpan.FromMinutes(1));

        Assert.NotNull(recovered);
        Assert.Equal("claim-two", recovered!.RecoveryLeaseId);
    }

    [Fact]
    public async Task Action_identity_is_unique_per_project()
    {
        await using var database = CreateDatabase(Migration);
        await using var context = database.CreateDbContext();
        var now = new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        context.SlackRetryOperations.Add(NewRow("one", now));
        await context.SaveChangesAsync();

        context.SlackRetryOperations.Add(NewRow("two", now));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    private static SlackRetryOperationRow NewRow(string id, DateTimeOffset now) => new()
    {
        Id = $"slkretry_{id}",
        ProjectId = "project-1",
        ActionKey = "action-1",
        ConnectionId = "connection-1",
        SessionId = "session-1",
        FailedInputId = "input-1",
        FailedTurnId = "turn-1",
        DispatchRef = "dispatch-1",
        WorkspaceTeamId = "T123",
        ConversationId = "C123",
        MessageTs = "100.001",
        ActorSlackUserId = "U123",
        RetryDispatchKey = "slack-retry:project-1:action-1",
        AttemptKind = "root",
        State = SlackRetryOperationStates.DispatchPending,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static TestDatabase CreateDatabase(string migration)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        MigratedSqliteTemplate.CopyTo(connection, migration);
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        return new TestDatabase(connection, new TestDbContextFactory(options));
    }

    private static async Task<string[]> TableNamesAsync(MohistDbContext context)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        return names.ToArray();
    }

    private static async Task<string[]> IndexNamesAsync(MohistDbContext context)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index'";
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        return names.ToArray();
    }

    private sealed class TestDatabase(SqliteConnection connection, TestDbContextFactory factory) : IAsyncDisposable
    {
        public TestDbContextFactory Factory => factory;
        public MohistDbContext CreateDbContext() => factory.CreateDbContext();
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
