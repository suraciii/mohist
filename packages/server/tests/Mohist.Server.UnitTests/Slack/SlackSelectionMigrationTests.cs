using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Slack;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackSelectionMigrationTests
{
    private const string SelectionMigration = "20260913000000_AddSlackSelectionFacts";
    private const string PreviousMigration = "20260912000000_AddAgentRetryOperations";
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AddSlackSelectionFacts_migrates_existing_prompt_to_inert_legacy_row_and_cleanup_reaps_it()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        await using (var before = new MohistDbContext(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            await before.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "SlackAmbiguousPrompts" (
                    "Id", "ProjectId", "WorkspaceTeamId", "ConversationId", "MessageTs",
                    "ThreadTs", "WinningConnectionId", "MentionedConnectionIdsJson",
                    "PromptedAt", "CreatedAt", "UpdatedAt")
                VALUES (
                    {"legacy-prompt"}, {"legacy-project"}, {"legacy-team"}, {"legacy-channel"},
                    {"1710000000.000001"}, {null}, {"legacy-connection"}, {"[\"legacy-connection\",\"other\"]"},
                    {Now.AddHours(-1)}, {Now.AddHours(-1)}, {Now.AddHours(-1)});
                """);
            await before.GetService<IMigrator>().MigrateAsync(SelectionMigration);
        }

        var store = new SlackAmbiguousPromptStore(
            new MigrationDbContextFactory(options),
            new FakeTimeProvider(Now));
        var migrated = await store.FindAsync("legacy-team", "legacy-channel", "1710000000.000001");
        Assert.NotNull(migrated);
        Assert.Equal(SlackAmbiguityKinds.Legacy, migrated!.AmbiguityKind);
        Assert.Equal("", migrated.SenderSlackUserId);
        Assert.Equal("", migrated.TaskText);
        Assert.Equal("[]", migrated.FilesJson);
        Assert.Equal("[]", migrated.CandidateReferencesJson);
        Assert.Equal(SlackSelectionStates.Pending, migrated.SelectionState);
        Assert.Null(migrated.DispatchKind);
        Assert.Null(migrated.SelectionSessionId);

        var redelivery = await store.TryClaimAsync(
            migrated.ProjectId,
            migrated.WorkspaceTeamId,
            migrated.ConversationId,
            migrated.MessageTs,
            migrated.ThreadTs,
            migrated.WinningConnectionId,
            [
                new SlackSelectionCandidateReference(
                    migrated.ProjectId,
                    migrated.WinningConnectionId,
                    "U_LEGACY_WINNER"),
                new SlackSelectionCandidateReference(
                    migrated.ProjectId,
                    "other",
                    "U_LEGACY_OTHER"),
            ],
            "U_LEGACY_ACTOR",
            "current redelivery text",
            "[]",
            SlackAmbiguityKinds.RootMultiMention);
        Assert.False(redelivery.Claimed);
        Assert.False(redelivery.HasCompleteSelectionFacts);
        Assert.Equal(SlackAmbiguityKinds.Legacy, redelivery.Snapshot.AmbiguityKind);
        Assert.Equal("[]", redelivery.Snapshot.CandidateReferencesJson);

        await Assert.ThrowsAsync<ArgumentException>(() => store.TryDecideAsync(
            migrated.WorkspaceTeamId,
            migrated.ConversationId,
            migrated.MessageTs,
            "legacy-project",
            "legacy-connection",
            SlackSelectionDispatchKinds.RootLaunch,
            "",
            "",
            ""));
        Assert.True(await store.TrySettleAsync(
            migrated.Id,
            SlackSelectionStates.Pending,
            "legacy_missing_selection_facts"));

        await using (var age = new MohistDbContext(options))
        {
            var row = await age.SlackAmbiguousPrompts.SingleAsync(candidate => candidate.Id == migrated.Id);
            row.FinishedAt = Now.AddMinutes(-31);
            row.UpdatedAt = Now.AddMinutes(-31);
            await age.SaveChangesAsync();
        }

        Assert.Equal(1, await store.DeleteFinishedBeforeAsync(Now.AddMinutes(-30)));
        Assert.Null(await store.FindAsync("legacy-team", "legacy-channel", "1710000000.000001"));
    }

    private sealed class MigrationDbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
        public Task<MohistDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
