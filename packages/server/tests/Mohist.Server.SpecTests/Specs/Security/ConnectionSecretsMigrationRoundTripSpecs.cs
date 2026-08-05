using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Security;

public partial class ConnectionSecretsMigrationSpecs
{
    private static void AssertRebuiltDependentIndexes(IReadOnlyModel model)
    {
        AssertIndex(
            model,
            typeof(SlackChildAppBindingObligationRow),
            "IX_SlackChildAppBindingObligations_AgentConnectionId",
            "AgentConnectionId");
        AssertIndex(
            model,
            typeof(SlackOAuthStateRow),
            "IX_SlackOAuthStates_AuthorizationAttemptId",
            "AuthorizationAttemptId");
    }

    private static void AssertIndex(IReadOnlyModel model, Type entityType, string indexName, string propertyName)
    {
        var entity = model.FindEntityType(entityType);
        Assert.NotNull(entity);
        var index = entity!.GetIndexes().SingleOrDefault(candidate => candidate.GetDatabaseName() == indexName);
        Assert.NotNull(index);
        Assert.Equal([propertyName], index!.Properties.Select(property => property.Name));
    }

    private static async Task SeedRebuildRowsAsync(MohistDbContext context)
    {
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO "SlackWorkspaceEnrollments" (
                "Id", "WorkspaceTeamId", "Lifecycle", "ManagerCapability", "PlanCode", "ManagedAppLimit",
                "ManagerCredentialRef", "ManagerAppId", "ManagerBotUserId", "ManagerTransportKind", "ManagerReadiness",
                "ManagerActorId", "AuditJson", "CreatedAt", "UpdatedAt")
            VALUES (
                'enrollment_copy', 'T_COPY', 'active', 'available', 'pro', 3,
                'manager-credential-copy', 'A_MANAGER_COPY', 'U_MANAGER_COPY', 'socket', 'ready',
                'manager-copy', '[]', '2026-08-05T00:00:00.0000000+00:00', '2026-08-05T00:01:00.0000000+00:00');

            INSERT INTO "AgentConnections" (
                "Id", "ProjectId", "AgentId", "ProviderKind", "WorkspaceTeamId", "AppId", "BotUserId", "BotName",
                "SetupProgress", "DesiredState", "ConnectionHealth", "AgentReadiness", "AccessPolicy", "CreatedAt", "UpdatedAt")
            VALUES (
                'connection_copy', 'project_copy', 'agent_copy', 'slack', 'T_COPY', '', '', 'Copy Bot',
                'create_app_credentials', 'enabled', 'healthy', 'unknown', 'owner_only',
                '2026-08-05T00:00:00.0000000+00:00', '2026-08-05T00:01:00.0000000+00:00');

            INSERT INTO "ManagedSlackChildApps" (
                "Id", "EnrollmentId", "WorkspaceTeamId", "AgentConnectionId", "AppId", "BotUserId", "AppLifecycle",
                "Authorization", "TransportKind", "DesiredManifestVersion", "DesiredManifestHash", "AppliedManifestVersion",
                "AppliedManifestHash", "VerifiedScopesJson", "OperationFence", "OperationId", "OperationKind", "OperationStartedAt",
                "ErrorClass", "AuthorizationAttemptId", "AuthorizedAt", "ClientSecretRef", "SigningSecretRef", "AppLevelTokenRef",
                "BotTokenRef", "BindingState", "AuditJson", "CreatedAt", "UpdatedAt")
            VALUES (
                'child_copy', 'enrollment_copy', 'T_COPY', 'connection_copy', 'A_COPY', 'U_COPY', 'created',
                'authorized', 'socket', 3, 'desired-copy', 2, 'applied-copy', '["chat:write"]', 7, 'operation-copy',
                'create', '2026-08-05T00:02:00.0000000+00:00', 'copy_error', 'attempt_copy',
                '2026-08-05T00:03:00.0000000+00:00', 'client-copy', 'signing-copy', 'app-token-copy', 'bot-token-copy',
                'bound', '["copy"]', '2026-08-05T00:00:00.0000000+00:00', '2026-08-05T00:04:00.0000000+00:00');

            INSERT INTO "SlackChildAppBindingObligations" (
                "Id", "ChildAppId", "AgentConnectionId", "Status", "AttemptCount", "LastAttemptAt", "ClaimToken",
                "CreatedAt", "UpdatedAt")
            VALUES (
                'binding_copy', 'child_copy', 'connection_copy', 'bound', 2, '2026-08-05T00:05:00.0000000+00:00', 'claim-copy',
                '2026-08-05T00:00:00.0000000+00:00', '2026-08-05T00:06:00.0000000+00:00');

            INSERT INTO "SlackOAuthAttempts" (
                "Id", "ChildAppId", "WorkspaceTeamId", "AppId", "StateHash", "BotUserId", "Status", "BotTokenRef",
                "CreatedAt", "UpdatedAt", "ConsumedAt", "SecretStoredAt", "AppliedAt")
            VALUES (
                'attempt_copy', 'child_copy', 'T_COPY', 'A_COPY', 'state-hash-copy', 'U_COPY', 'applied', 'oauth-token-copy',
                '2026-08-05T00:00:00.0000000+00:00', '2026-08-05T00:07:00.0000000+00:00',
                '2026-08-05T00:08:00.0000000+00:00', '2026-08-05T00:09:00.0000000+00:00', '2026-08-05T00:10:00.0000000+00:00');

            INSERT INTO "SlackOAuthStates" (
                "Id", "ChildAppId", "WorkspaceTeamId", "AppId", "StateHash", "AuthorizationAttemptId", "ExpiresAt",
                "Outcome", "CreatedAt")
            VALUES (
                'state_copy', 'child_copy', 'T_COPY', 'A_COPY', 'oauth-state-copy', 'attempt_copy',
                '2026-08-05T01:00:00.0000000+00:00', 'accepted', '2026-08-05T00:00:00.0000000+00:00');
            """);
    }

    private static async Task AssertRebuiltRowsAsync(MohistDbContext context, string appsTable)
    {
        Assert.Equal(1, await CountRowsAsync(context, appsTable));
        Assert.Equal(1, await CountRowsAsync(context, "SlackChildAppBindingObligations"));
        Assert.Equal(1, await CountRowsAsync(context, "SlackOAuthAttempts"));
        Assert.Equal(1, await CountRowsAsync(context, "SlackOAuthStates"));
        Assert.Equal("enrollment_copy", await ReadTextAsync(context, appsTable, "EnrollmentId", "child_copy"));
        Assert.Equal("connection_copy", await ReadTextAsync(context, appsTable, "AgentConnectionId", "child_copy"));
        Assert.Null(await ReadTextAsync(context, appsTable, "PublicIngressBaseUrl", "child_copy"));
        Assert.Equal("A_COPY", await ReadTextAsync(context, appsTable, "AppId", "child_copy"));
        Assert.Equal("U_COPY", await ReadTextAsync(context, appsTable, "BotUserId", "child_copy"));
        Assert.Equal("applied-copy", await ReadTextAsync(context, appsTable, "AppliedManifestHash", "child_copy"));
        Assert.Equal("operation-copy", await ReadTextAsync(context, appsTable, "OperationId", "child_copy"));
        Assert.Null(await ReadTextAsync(context, appsTable, "UnknownOutcome", "child_copy"));
        Assert.Null(await ReadTextAsync(context, appsTable, "AuthorizationExpiresAt", "child_copy"));

        var binding = Assert.Single(await context.SlackChildAppBindingObligations.AsNoTracking().ToListAsync());
        Assert.Equal("child_copy", binding.ChildAppId);
        Assert.Equal("connection_copy", binding.AgentConnectionId);
        Assert.Equal(2, binding.AttemptCount);
        Assert.NotNull(binding.LastAttemptAt);
        Assert.Null(binding.FailureClass);

        var attempt = Assert.Single(await context.SlackOAuthAttempts.AsNoTracking().ToListAsync());
        Assert.Equal("child_copy", attempt.ChildAppId);
        Assert.Equal("oauth-token-copy", attempt.BotTokenRef);
        Assert.Null(attempt.FailureClass);
        Assert.NotNull(attempt.AppliedAt);

        var state = Assert.Single(await context.SlackOAuthStates.AsNoTracking().ToListAsync());
        Assert.Equal("child_copy", state.ChildAppId);
        Assert.Equal("attempt_copy", state.AuthorizationAttemptId);
        Assert.Null(state.ConsumedAt);
        Assert.Equal("accepted", state.Outcome);
        await AssertNoForeignKeyViolationsAsync(context);
    }

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

    private static async Task AssertNoForeignKeyViolationsAsync(MohistDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.False(await reader.ReadAsync());
    }
}
