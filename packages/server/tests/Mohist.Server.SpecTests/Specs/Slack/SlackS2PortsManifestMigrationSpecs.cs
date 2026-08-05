using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackS2PortsManifestMigrationSpecs
{
    private const string S1Migration = "20260805120000_RenameManagedSlackAgentAppAndGeneralizeSecrets";
    private const string S2Migration = "20260805130000_AddSlackS2PortsManifestRotation";

    [Fact]
    public async Task SQLite_upgrade_converts_S1_https_manager_to_not_ready_socket_and_down_restores_https_target()
    {
        await using var database = CreateS1Database();
        await using (var before = database.CreateDbContext())
        {
            await SeedS1HttpsEnrollmentAsync(before);
            await before.GetService<IMigrator>().MigrateAsync(S2Migration);
        }

        await using (var after = database.CreateDbContext())
        {
            var enrollment = await after.SlackWorkspaceEnrollments.SingleAsync(row => row.Id == "enrollment_https");
            Assert.Equal("T_HTTPS", enrollment.WorkspaceTeamId);
            Assert.Equal("manager-credential", enrollment.ManagerCredentialRef);
            Assert.Equal("A_MANAGER", enrollment.ManagerAppId);
            Assert.Equal("U_MANAGER", enrollment.ManagerBotUserId);
            Assert.Equal("socket", enrollment.ManagerTransportKind);
            Assert.Equal("not_ready", enrollment.ManagerReadiness);
            Assert.Equal("[]", enrollment.AuditJson);

            var app = await after.ManagedSlackAgentApps.SingleAsync(row => row.Id == "agent_app_https");
            Assert.Equal("enrollment_https", app.EnrollmentId);
            Assert.Equal("connection_https", app.AgentConnectionId);
            Assert.Equal("A_AGENT", app.AppId);
            Assert.Equal("U_AGENT", app.BotUserId);

            await Assert.ThrowsAsync<SqliteException>(() => after.Database.ExecuteSqlRawAsync(
                "UPDATE \"SlackWorkspaceEnrollments\" SET \"ManagerTransportKind\" = 'https' WHERE \"Id\" = 'enrollment_https'"));
        }

        await using (var rollback = database.CreateDbContext())
        {
            await rollback.GetService<IMigrator>().MigrateAsync(S1Migration);
        }

        await using var reverted = database.CreateDbContext();
        Assert.Equal("https", await ReadTextAsync(reverted, "SlackWorkspaceEnrollments", "ManagerTransportKind", "enrollment_https"));
        Assert.Equal("not_ready", await ReadTextAsync(reverted, "SlackWorkspaceEnrollments", "ManagerReadiness", "enrollment_https"));
        Assert.Equal("manager-credential", await ReadTextAsync(reverted, "SlackWorkspaceEnrollments", "ManagerCredentialRef", "enrollment_https"));
        Assert.Equal("[]", await ReadTextAsync(reverted, "SlackWorkspaceEnrollments", "AuditJson", "enrollment_https"));
        Assert.Equal("enrollment_https", await ReadTextAsync(reverted, "ManagedSlackAgentApps", "EnrollmentId", "agent_app_https"));
        Assert.Equal("connection_https", await ReadTextAsync(reverted, "ManagedSlackAgentApps", "AgentConnectionId", "agent_app_https"));
        Assert.Equal("socket", await ReadTextAsync(reverted, "ManagedSlackAgentApps", "TransportKind", "agent_app_https"));
    }

    [Fact]
    public async Task Repeated_S2_upgrade_and_downgrade_restores_the_current_transport_not_a_stale_marker()
    {
        await using var database = CreateS1Database();
        await using (var seed = database.CreateDbContext())
        {
            await SeedS1HttpsEnrollmentAsync(seed);
            await seed.GetService<IMigrator>().MigrateAsync(S2Migration);
        }
        await using (var firstDown = database.CreateDbContext())
        {
            await firstDown.GetService<IMigrator>().MigrateAsync(S1Migration);
        }

        await using (var s1 = database.CreateDbContext())
        {
            await s1.Database.ExecuteSqlRawAsync(
                "UPDATE \"SlackWorkspaceEnrollments\" SET \"ManagerTransportKind\" = 'socket' WHERE \"Id\" = 'enrollment_https'");
        }

        await using (var secondUp = database.CreateDbContext())
        {
            await secondUp.GetService<IMigrator>().MigrateAsync(S2Migration);
        }
        await using var secondDown = database.CreateDbContext();
        await secondDown.GetService<IMigrator>().MigrateAsync(S1Migration);

        await using var reverted = database.CreateDbContext();
        Assert.Equal("socket", await ReadTextAsync(reverted, "SlackWorkspaceEnrollments", "ManagerTransportKind", "enrollment_https"));
    }

    private static TestDatabase CreateS1Database()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        MigratedSqliteTemplate.CopyTo(connection, S1Migration);
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new TestDatabase(connection, options);
    }

    private static Task SeedS1HttpsEnrollmentAsync(MohistDbContext context) =>
        context.Database.ExecuteSqlRawAsync("""
            INSERT INTO "SlackWorkspaceEnrollments" (
                "Id", "WorkspaceTeamId", "Lifecycle", "ManagerCapability", "CapabilityReason", "LastVerifiedAt", "PlanCode", "ManagedAppLimit",
                "ManagerCredentialRef", "ManagerAppId", "ManagerBotUserId", "ManagerTransportKind", "ManagerReadiness", "ManagerActorId",
                "ClaimedSlackUserId", "ManagerClaimHash", "ManagerClaimIssuedAt", "ManagerClaimExpiresAt", "ManagerClaimConsumedAt", "AuditJson", "CreatedAt", "UpdatedAt", "DeletedAt")
            VALUES (
                'enrollment_https', 'T_HTTPS', 'active', 'available', NULL, '2026-08-05T12:00:00.0000000+00:00', 'pro', 10,
                'manager-credential', 'A_MANAGER', 'U_MANAGER', 'https', 'ready', 'manager-actor',
                'U_OWNER', 'claim-hash', '2026-08-05T12:01:00.0000000+00:00', '2026-08-05T13:00:00.0000000+00:00', NULL, '[]',
                '2026-08-05T12:00:00.0000000+00:00', '2026-08-05T12:02:00.0000000+00:00', NULL);

            INSERT INTO "AgentConnections" (
                "Id", "ProjectId", "AgentId", "ProviderKind", "WorkspaceTeamId", "AppId", "BotUserId", "BotName",
                "SetupProgress", "DesiredState", "ConnectionHealth", "AgentReadiness", "AccessPolicy", "CreatedAt", "UpdatedAt")
            VALUES (
                'connection_https', 'project_https', 'agent_https', 'slack', 'T_HTTPS', '', '', 'Agent Bot',
                'create_app_credentials', 'enabled', 'healthy', 'unknown', 'owner_only',
                '2026-08-05T12:00:00.0000000+00:00', '2026-08-05T12:02:00.0000000+00:00');

            INSERT INTO "ManagedSlackAgentApps" (
                "Id", "EnrollmentId", "WorkspaceTeamId", "AgentConnectionId", "PublicIngressBaseUrl", "AppId", "BotUserId", "AppLifecycle", "Authorization",
                "TransportKind", "DesiredManifestVersion", "DesiredManifestHash", "AppliedManifestVersion", "AppliedManifestHash", "VerifiedScopesJson", "OperationFence",
                "OperationId", "OperationKind", "OperationStartedAt", "UnknownOutcome", "ErrorClass", "AuthorizationAttemptId", "AuthorizedAt", "AuthorizationExpiresAt",
                "ClientSecretRef", "SigningSecretRef", "AppLevelTokenRef", "BotTokenRef", "BindingState", "BindingErrorClass", "AuditJson", "CreatedAt", "UpdatedAt", "DeletedAt")
            VALUES (
                'agent_app_https', 'enrollment_https', 'T_HTTPS', 'connection_https', NULL, 'A_AGENT', 'U_AGENT', 'created', 'authorized',
                'socket', 2, 'desired-hash', 1, 'applied-hash', '["chat:write"]', 3,
                'operation-1', 'create', '2026-08-05T12:03:00.0000000+00:00', NULL, NULL, 'attempt-1', '2026-08-05T12:04:00.0000000+00:00', NULL,
                'client-ref', 'signing-ref', 'app-token-ref', 'bot-token-ref', 'bound', NULL, '[]',
                '2026-08-05T12:00:00.0000000+00:00', '2026-08-05T12:05:00.0000000+00:00', NULL);
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
