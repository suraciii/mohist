using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Slack.Domain;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackManagerAppSetupFactsMigrationSpecs
{
    private const string S2Migration = "20260805130000_AddSlackS2PortsManifestRotation";
    private const string SetupFactsMigration = "20260806000000_AddSlackManagerAppSetupFacts";

    [Fact]
    public async Task SQLite_upgrade_preserves_enrollments_and_applies_setup_fact_defaults()
    {
        await using var database = CreateS2Database();
        await using (var seed = database.CreateDbContext())
        {
            await SeedS2EnrollmentAsync(seed);
            await seed.GetService<IMigrator>().MigrateAsync(SetupFactsMigration);
        }

        await using (var after = database.CreateDbContext())
        {
            var enrollment = await after.SlackWorkspaceEnrollments
                .Where(row => row.Id == "enrollment-setup")
                .Select(row => new
                {
                    row.WorkspaceTeamId,
                    row.ManagerCredentialRef,
                    row.ManagerAppLifecycle,
                    row.ManagerAppOperationFence,
                    row.ManagerAppOperationId,
                    row.ManagerAppOperationOutcome,
                    row.RuntimeCredentialValidationState,
                })
                .SingleAsync();
            Assert.Equal("T_SETUP", enrollment.WorkspaceTeamId);
            Assert.Equal("manager-credential", enrollment.ManagerCredentialRef);
            Assert.Equal(SlackManagerAppLifecycle.NotCreated, enrollment.ManagerAppLifecycle);
            Assert.Equal(0, enrollment.ManagerAppOperationFence);
            Assert.Null(enrollment.ManagerAppOperationId);
            Assert.Null(enrollment.ManagerAppOperationOutcome);
            Assert.Equal(SlackRuntimeCredentialValidationState.NotProvided, enrollment.RuntimeCredentialValidationState);
        }

        await using (var constraints = database.CreateDbContext())
        {
            await Assert.ThrowsAsync<SqliteException>(() => constraints.Database.ExecuteSqlRawAsync(
                "UPDATE \"SlackWorkspaceEnrollments\" SET \"ManagerAppLifecycle\" = 'bogus' WHERE \"Id\" = 'enrollment-setup'"));
            await Assert.ThrowsAsync<SqliteException>(() => constraints.Database.ExecuteSqlRawAsync(
                "UPDATE \"SlackWorkspaceEnrollments\" SET \"RuntimeCredentialValidationState\" = 'bogus' WHERE \"Id\" = 'enrollment-setup'"));
        }
    }

    [Fact]
    public async Task SQLite_upgrade_fresh_insert_gets_setup_fact_defaults()
    {
        await using var database = CreateS2Database();
        await using (var seed = database.CreateDbContext())
        {
            await seed.GetService<IMigrator>().MigrateAsync(SetupFactsMigration);
            await seed.Database.ExecuteSqlRawAsync("""
                INSERT INTO "SlackWorkspaceEnrollments" (
                    "Id", "WorkspaceTeamId", "Lifecycle", "ManagerCapability", "CapabilityReason", "LastVerifiedAt", "PlanCode", "ManagedAppLimit",
                    "ConfigurationCredentialRef", "ConfigurationCredentialGeneration", "ConfigurationCredentialExpiresAt", "S2OriginalManagerTransportKind",
                    "ManagerCredentialRef", "ManagerAppId", "ManagerBotUserId", "ManagerTransportKind", "ManagerReadiness", "ManagerActorId",
                    "ClaimedSlackUserId", "ManagerClaimHash", "ManagerClaimIssuedAt", "ManagerClaimExpiresAt", "ManagerClaimConsumedAt", "AuditJson",
                    "CreatedAt", "UpdatedAt", "DeletedAt")
                VALUES (
                    'enrollment-fresh', 'T_FRESH', 'active', 'available', NULL, NULL, 'pro', 10,
                    '', 0, NULL, 'socket',
                    'manager-credential', 'A_MANAGER', 'U_MANAGER', 'socket', 'ready', 'manager-actor',
                    NULL, NULL, NULL, NULL, NULL, '[]',
                    '2026-08-06T00:00:00.0000000+00:00', '2026-08-06T00:01:00.0000000+00:00', NULL);
                """);
        }

        await using var after = database.CreateDbContext();
        var row = await after.SlackWorkspaceEnrollments
            .Where(item => item.Id == "enrollment-fresh")
            .Select(item => new
            {
                item.ManagerAppLifecycle,
                item.ManagerAppOperationFence,
                item.ManagerAppOperationId,
                item.ManagerAppOperationOutcome,
                item.RuntimeCredentialValidationState,
            })
            .SingleAsync();
        Assert.Equal(SlackManagerAppLifecycle.NotCreated, row.ManagerAppLifecycle);
        Assert.Equal(0, row.ManagerAppOperationFence);
        Assert.Null(row.ManagerAppOperationId);
        Assert.Null(row.ManagerAppOperationOutcome);
        Assert.Equal(SlackRuntimeCredentialValidationState.NotProvided, row.RuntimeCredentialValidationState);
    }

    [Fact]
    public async Task SQLite_downgrade_restores_the_S2_schema_and_reupgrade_keeps_rows()
    {
        await using var database = CreateS2Database();
        await using (var seed = database.CreateDbContext())
        {
            await SeedS2EnrollmentAsync(seed);
            await seed.GetService<IMigrator>().MigrateAsync(SetupFactsMigration);
        }
        await using (var down = database.CreateDbContext())
        {
            await down.GetService<IMigrator>().MigrateAsync(S2Migration);
        }

        await using (var reverted = database.CreateDbContext())
        {
            Assert.Equal("manager-credential", await ReadTextAsync(reverted, "SlackWorkspaceEnrollments", "ManagerCredentialRef", "enrollment-setup"));
            await Assert.ThrowsAsync<SqliteException>(() =>
                ReadTextAsync(reverted, "SlackWorkspaceEnrollments", "ManagerAppLifecycle", "enrollment-setup"));
        }

        await using (var again = database.CreateDbContext())
        {
            await again.GetService<IMigrator>().MigrateAsync(SetupFactsMigration);
        }

        await using var final = database.CreateDbContext();
        var enrollment = await final.SlackWorkspaceEnrollments
            .Where(row => row.Id == "enrollment-setup")
            .Select(row => new { row.WorkspaceTeamId, row.ManagerCredentialRef, row.ManagerAppLifecycle })
            .SingleAsync();
        Assert.Equal("T_SETUP", enrollment.WorkspaceTeamId);
        Assert.Equal("manager-credential", enrollment.ManagerCredentialRef);
        Assert.Equal(SlackManagerAppLifecycle.NotCreated, enrollment.ManagerAppLifecycle);
    }

    private static TestDatabase CreateS2Database()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        MigratedSqliteTemplate.CopyTo(connection, S2Migration);
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new TestDatabase(connection, options);
    }

    private static Task SeedS2EnrollmentAsync(MohistDbContext context) =>
        context.Database.ExecuteSqlRawAsync("""
            INSERT INTO "SlackWorkspaceEnrollments" (
                "Id", "WorkspaceTeamId", "Lifecycle", "ManagerCapability", "CapabilityReason", "LastVerifiedAt", "PlanCode", "ManagedAppLimit",
                "ConfigurationCredentialRef", "ConfigurationCredentialGeneration", "ConfigurationCredentialExpiresAt", "S2OriginalManagerTransportKind",
                "ManagerCredentialRef", "ManagerAppId", "ManagerBotUserId", "ManagerTransportKind", "ManagerReadiness", "ManagerActorId",
                "ClaimedSlackUserId", "ManagerClaimHash", "ManagerClaimIssuedAt", "ManagerClaimExpiresAt", "ManagerClaimConsumedAt", "AuditJson",
                "CreatedAt", "UpdatedAt", "DeletedAt")
            VALUES (
                'enrollment-setup', 'T_SETUP', 'active', 'available', NULL, NULL, 'pro', 10,
                '', 0, NULL, 'socket',
                'manager-credential', 'A_MANAGER', 'U_MANAGER', 'socket', 'ready', 'manager-actor',
                NULL, NULL, NULL, NULL, NULL, '[]',
                '2026-08-06T00:00:00.0000000+00:00', '2026-08-06T00:01:00.0000000+00:00', NULL);
            """);

    private static async Task<string?> ReadTextAsync(MohistDbContext context, string tableName, string columnName, string id)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"{columnName}\" FROM \"{tableName}\" WHERE \"Id\" = $id";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$id";
        parameter.Value = id;
        command.Parameters.Add(parameter);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private sealed class TestDatabase(SqliteConnection connection, DbContextOptions<MohistDbContext> options) : IAsyncDisposable
    {
        public MohistDbContext CreateDbContext() => new(options);

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
