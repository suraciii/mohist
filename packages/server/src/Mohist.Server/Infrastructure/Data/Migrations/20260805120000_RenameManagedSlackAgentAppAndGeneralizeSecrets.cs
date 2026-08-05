using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260805120000_RenameManagedSlackAgentAppAndGeneralizeSecrets")]
public partial class RenameManagedSlackAgentAppAndGeneralizeSecrets : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameTable(
            name: "ManagedSlackChildApps",
            newName: "ManagedSlackAgentApps");
        migrationBuilder.DropIndex(
            name: "UX_ManagedSlackChildApps_AgentConnectionId",
            table: "ManagedSlackAgentApps");
        migrationBuilder.DropIndex(
            name: "UX_ManagedSlackChildApps_WorkspaceTeamId_AppId",
            table: "ManagedSlackAgentApps");
        migrationBuilder.DropIndex(
            name: "IX_ManagedSlackChildApps_EnrollmentId_UpdatedAt",
            table: "ManagedSlackAgentApps");
        RebuildManagedSlackAgentAppConstraints(
            migrationBuilder,
            tableName: "ManagedSlackAgentApps",
            oldConstraintTableName: "ManagedSlackChildApps",
            newConstraintTableName: "ManagedSlackAgentApps");
        migrationBuilder.CreateIndex(
            name: "UX_ManagedSlackAgentApps_AgentConnectionId",
            table: "ManagedSlackAgentApps",
            column: "AgentConnectionId",
            unique: true,
            filter: "\"DeletedAt\" IS NULL");
        migrationBuilder.CreateIndex(
            name: "UX_ManagedSlackAgentApps_WorkspaceTeamId_AppId",
            table: "ManagedSlackAgentApps",
            columns: new[] { "WorkspaceTeamId", "AppId" },
            unique: true,
            filter: "\"DeletedAt\" IS NULL AND \"AppId\" <> ''");
        migrationBuilder.CreateIndex(
            name: "IX_ManagedSlackAgentApps_EnrollmentId_UpdatedAt",
            table: "ManagedSlackAgentApps",
            columns: new[] { "EnrollmentId", "UpdatedAt" });

        migrationBuilder.CreateTable(
            name: "StoredSecrets",
            columns: table => new
            {
                OwnerKind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                OwnerScope = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                OwnerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                Blob = table.Column<byte[]>(type: "BLOB", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StoredSecrets", x => new { x.OwnerKind, x.OwnerScope, x.OwnerId, x.Kind });
                table.CheckConstraint(
                    "CK_StoredSecrets_OwnerKind",
                    "\"OwnerKind\" IN ('agent_connection', 'webhook_subscription', 'slack_workspace_enrollment', 'managed_slack_agent_app')");
                table.CheckConstraint(
                    "CK_StoredSecrets_Kind",
                    "\"Kind\" IN ('appToken', 'botToken', 'webhookSecret', 'clientSecret', 'signingSecret', 'configurationAccessToken', 'configurationRefreshToken', 'previousBotToken', 'previousAppToken')");
                table.CheckConstraint(
                    "CK_StoredSecrets_OwnerKindKind",
                    "(\"OwnerKind\" = 'agent_connection' AND \"Kind\" IN ('appToken', 'botToken')) OR " +
                    "(\"OwnerKind\" = 'webhook_subscription' AND \"Kind\" = 'webhookSecret') OR " +
                    "(\"OwnerKind\" = 'slack_workspace_enrollment' AND \"Kind\" IN ('configurationAccessToken', 'configurationRefreshToken', 'appToken', 'botToken', 'clientSecret', 'signingSecret', 'previousBotToken', 'previousAppToken')) OR " +
                    "(\"OwnerKind\" = 'managed_slack_agent_app' AND \"Kind\" IN ('appToken', 'botToken', 'clientSecret', 'signingSecret', 'previousBotToken', 'previousAppToken'))");
            });

        migrationBuilder.Sql(
            """
            INSERT INTO "StoredSecrets" ("OwnerKind", "OwnerScope", "OwnerId", "Kind", "Blob", "UpdatedAt")
            SELECT CASE WHEN "Kind" = 'webhookSecret' THEN 'webhook_subscription' ELSE 'agent_connection' END,
                   "ProjectId",
                   "ConnectionId",
                   "Kind",
                   "Blob",
                   "UpdatedAt"
            FROM "ConnectionSecrets";
            """);

        migrationBuilder.DropTable(name: "ConnectionSecrets");

        migrationBuilder.CreateIndex(
            name: "IX_StoredSecrets_Owner",
            table: "StoredSecrets",
            columns: new[] { "OwnerKind", "OwnerScope", "OwnerId" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE "__StoredSecretsDownCompatibilityGuard" (
                "Value" INTEGER NOT NULL CONSTRAINT "CK_StoredSecrets_DownCompatible" CHECK ("Value" = 0)
            );
            """);
        migrationBuilder.Sql(
            """
            INSERT INTO "__StoredSecretsDownCompatibilityGuard" ("Value")
            SELECT 1
            WHERE EXISTS (
                SELECT 1
                FROM "StoredSecrets"
                WHERE NOT (
                    ("OwnerKind" = 'agent_connection' AND "Kind" IN ('appToken', 'botToken'))
                    OR ("OwnerKind" = 'webhook_subscription' AND "Kind" = 'webhookSecret')
                )
            );
            """);
        migrationBuilder.Sql("DROP TABLE \"__StoredSecretsDownCompatibilityGuard\";");

        migrationBuilder.CreateTable(
            name: "ConnectionSecrets",
            columns: table => new
            {
                ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                Blob = table.Column<byte[]>(type: "BLOB", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ConnectionSecrets", x => new { x.ProjectId, x.ConnectionId, x.Kind });
                table.CheckConstraint(
                    "CK_ConnectionSecrets_Kind",
                    "\"Kind\" IN ('appToken', 'botToken', 'webhookSecret')");
            });

        migrationBuilder.Sql(
            """
            INSERT INTO "ConnectionSecrets" ("ProjectId", "ConnectionId", "Kind", "Blob", "UpdatedAt")
            SELECT "OwnerScope", "OwnerId", "Kind", "Blob", "UpdatedAt"
            FROM "StoredSecrets"
            WHERE ("OwnerKind" = 'agent_connection' AND "Kind" IN ('appToken', 'botToken'))
               OR ("OwnerKind" = 'webhook_subscription' AND "Kind" = 'webhookSecret');
            """);

        migrationBuilder.CreateIndex(
            name: "IX_ConnectionSecrets_ProjectId_ConnectionId",
            table: "ConnectionSecrets",
            columns: new[] { "ProjectId", "ConnectionId" });

        migrationBuilder.DropTable(name: "StoredSecrets");

        migrationBuilder.DropIndex(
            name: "UX_ManagedSlackAgentApps_AgentConnectionId",
            table: "ManagedSlackAgentApps");
        migrationBuilder.DropIndex(
            name: "UX_ManagedSlackAgentApps_WorkspaceTeamId_AppId",
            table: "ManagedSlackAgentApps");
        migrationBuilder.DropIndex(
            name: "IX_ManagedSlackAgentApps_EnrollmentId_UpdatedAt",
            table: "ManagedSlackAgentApps");
        RebuildManagedSlackAgentAppConstraints(
            migrationBuilder,
            tableName: "ManagedSlackAgentApps",
            oldConstraintTableName: "ManagedSlackAgentApps",
            newConstraintTableName: "ManagedSlackChildApps");
        migrationBuilder.CreateIndex(
            name: "UX_ManagedSlackChildApps_AgentConnectionId",
            table: "ManagedSlackAgentApps",
            column: "AgentConnectionId",
            unique: true,
            filter: "\"DeletedAt\" IS NULL");
        migrationBuilder.CreateIndex(
            name: "UX_ManagedSlackChildApps_WorkspaceTeamId_AppId",
            table: "ManagedSlackAgentApps",
            columns: new[] { "WorkspaceTeamId", "AppId" },
            unique: true,
            filter: "\"DeletedAt\" IS NULL AND \"AppId\" <> ''");
        migrationBuilder.CreateIndex(
            name: "IX_ManagedSlackChildApps_EnrollmentId_UpdatedAt",
            table: "ManagedSlackAgentApps",
            columns: new[] { "EnrollmentId", "UpdatedAt" });

        migrationBuilder.RenameTable(
            name: "ManagedSlackAgentApps",
            newName: "ManagedSlackChildApps");
    }

    private static void RebuildManagedSlackAgentAppConstraints(
        MigrationBuilder migrationBuilder,
        string tableName,
        string oldConstraintTableName,
        string newConstraintTableName)
    {
        if (migrationBuilder.ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            RebuildManagedSlackAgentAppTablesForSqlite(
                migrationBuilder,
                tableName,
                newConstraintTableName);
            return;
        }

        migrationBuilder.DropForeignKey(
            name: $"FK_SlackChildAppBindingObligations_{oldConstraintTableName}_ChildAppId",
            table: "SlackChildAppBindingObligations");
        migrationBuilder.DropForeignKey(
            name: $"FK_SlackOAuthAttempts_{oldConstraintTableName}_ChildAppId",
            table: "SlackOAuthAttempts");
        migrationBuilder.DropForeignKey(
            name: $"FK_SlackOAuthStates_{oldConstraintTableName}_ChildAppId",
            table: "SlackOAuthStates");
        migrationBuilder.DropForeignKey(
            name: $"FK_{oldConstraintTableName}_AgentConnections_AgentConnectionId",
            table: tableName);
        migrationBuilder.DropForeignKey(
            name: $"FK_{oldConstraintTableName}_SlackWorkspaceEnrollments_EnrollmentId",
            table: tableName);

        foreach (var (suffix, _) in ManagedSlackAgentAppChecks)
        {
            migrationBuilder.DropCheckConstraint(
                name: $"CK_{oldConstraintTableName}_{suffix}",
                table: tableName);
        }

        foreach (var (suffix, sql) in ManagedSlackAgentAppChecks)
        {
            migrationBuilder.AddCheckConstraint(
                name: $"CK_{newConstraintTableName}_{suffix}",
                table: tableName,
                sql: sql);
        }

        migrationBuilder.AddForeignKey(
            name: $"FK_{newConstraintTableName}_AgentConnections_AgentConnectionId",
            table: tableName,
            column: "AgentConnectionId",
            principalTable: "AgentConnections",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddForeignKey(
            name: $"FK_{newConstraintTableName}_SlackWorkspaceEnrollments_EnrollmentId",
            table: tableName,
            column: "EnrollmentId",
            principalTable: "SlackWorkspaceEnrollments",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddForeignKey(
            name: $"FK_SlackChildAppBindingObligations_{newConstraintTableName}_ChildAppId",
            table: "SlackChildAppBindingObligations",
            column: "ChildAppId",
            principalTable: tableName,
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddForeignKey(
            name: $"FK_SlackOAuthAttempts_{newConstraintTableName}_ChildAppId",
            table: "SlackOAuthAttempts",
            column: "ChildAppId",
            principalTable: tableName,
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddForeignKey(
            name: $"FK_SlackOAuthStates_{newConstraintTableName}_ChildAppId",
            table: "SlackOAuthStates",
            column: "ChildAppId",
            principalTable: tableName,
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    private static void RebuildManagedSlackAgentAppTablesForSqlite(
        MigrationBuilder migrationBuilder,
        string tableName,
        string constraintTableName)
    {
        const string rebuiltAppsTable = "__ManagedSlackAgentApps_constraint_rebuild";
        const string rebuiltBindingObligationsTable = "__SlackChildAppBindingObligations_constraint_rebuild";
        const string rebuiltOAuthAttemptsTable = "__SlackOAuthAttempts_constraint_rebuild";
        const string rebuiltOAuthStatesTable = "__SlackOAuthStates_constraint_rebuild";

        migrationBuilder.CreateTable(
            name: rebuiltAppsTable,
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                EnrollmentId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                AgentConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                PublicIngressBaseUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                AppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                BotUserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                AppLifecycle = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                Authorization = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                TransportKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                DesiredManifestVersion = table.Column<int>(type: "INTEGER", nullable: false),
                DesiredManifestHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                AppliedManifestVersion = table.Column<int>(type: "INTEGER", nullable: true),
                AppliedManifestHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                VerifiedScopesJson = table.Column<string>(type: "JSON", nullable: false),
                OperationFence = table.Column<int>(type: "INTEGER", nullable: false),
                OperationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                OperationKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                OperationStartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                UnknownOutcome = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                ErrorClass = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                AuthorizationAttemptId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                AuthorizedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                AuthorizationExpiresAt = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                ClientSecretRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                SigningSecretRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                AppLevelTokenRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                BotTokenRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                BindingState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                BindingErrorClass = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                AuditJson = table.Column<string>(type: "JSON", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                DeletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey($"PK_{constraintTableName}", x => x.Id);
                foreach (var (suffix, sql) in ManagedSlackAgentAppChecks)
                {
                    table.CheckConstraint($"CK_{constraintTableName}_{suffix}", sql);
                }
                table.ForeignKey(
                    name: $"FK_{constraintTableName}_AgentConnections_AgentConnectionId",
                    column: x => x.AgentConnectionId,
                    principalTable: "AgentConnections",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: $"FK_{constraintTableName}_SlackWorkspaceEnrollments_EnrollmentId",
                    column: x => x.EnrollmentId,
                    principalTable: "SlackWorkspaceEnrollments",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: rebuiltBindingObligationsTable,
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ChildAppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                AgentConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                LastAttemptAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                ClaimToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                FailureClass = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SlackChildAppBindingObligations", x => x.Id);
                table.CheckConstraint(
                    "CK_SlackChildAppBindingObligations_Status",
                    "\"Status\" IN ('pending', 'in_progress', 'bound', 'connection_deleted', 'conflict')");
                table.ForeignKey(
                    name: "FK_SlackChildAppBindingObligations_AgentConnections_AgentConnectionId",
                    column: x => x.AgentConnectionId,
                    principalTable: "AgentConnections",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: $"FK_SlackChildAppBindingObligations_{constraintTableName}_ChildAppId",
                    column: x => x.ChildAppId,
                    principalTable: rebuiltAppsTable,
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: rebuiltOAuthAttemptsTable,
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ChildAppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                AppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                StateHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                BotUserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                BotTokenRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                FailureClass = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                ConsumedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                SecretStoredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                AppliedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SlackOAuthAttempts", x => x.Id);
                table.CheckConstraint(
                    "CK_SlackOAuthAttempts_Status",
                    "\"Status\" IN ('issued', 'consumed', 'secret_stored', 'applied', 'expired', 'recovery_required')");
                table.ForeignKey(
                    name: $"FK_SlackOAuthAttempts_{constraintTableName}_ChildAppId",
                    column: x => x.ChildAppId,
                    principalTable: rebuiltAppsTable,
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: rebuiltOAuthStatesTable,
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ChildAppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                AppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                StateHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                AuthorizationAttemptId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                ConsumedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                Outcome = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SlackOAuthStates", x => x.Id);
                table.CheckConstraint(
                    "CK_SlackOAuthStates_Outcome",
                    "\"Outcome\" IS NULL OR \"Outcome\" IN ('accepted', 'expired')");
                table.ForeignKey(
                    name: $"FK_SlackOAuthStates_{constraintTableName}_ChildAppId",
                    column: x => x.ChildAppId,
                    principalTable: rebuiltAppsTable,
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_SlackOAuthStates_SlackOAuthAttempts_AuthorizationAttemptId",
                    column: x => x.AuthorizationAttemptId,
                    principalTable: rebuiltOAuthAttemptsTable,
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        CopyRows(migrationBuilder, rebuiltAppsTable, tableName, ManagedSlackAgentAppColumns);
        CopyRows(migrationBuilder, rebuiltBindingObligationsTable, "SlackChildAppBindingObligations", SlackChildAppBindingObligationColumns);
        CopyRows(migrationBuilder, rebuiltOAuthAttemptsTable, "SlackOAuthAttempts", SlackOAuthAttemptColumns);
        CopyRows(migrationBuilder, rebuiltOAuthStatesTable, "SlackOAuthStates", SlackOAuthStateColumns);

        migrationBuilder.DropTable(name: "SlackOAuthStates");
        migrationBuilder.DropTable(name: "SlackChildAppBindingObligations");
        migrationBuilder.DropTable(name: "SlackOAuthAttempts");
        migrationBuilder.DropTable(name: tableName);
        migrationBuilder.RenameTable(name: rebuiltAppsTable, newName: tableName);
        migrationBuilder.RenameTable(name: rebuiltBindingObligationsTable, newName: "SlackChildAppBindingObligations");
        migrationBuilder.RenameTable(name: rebuiltOAuthAttemptsTable, newName: "SlackOAuthAttempts");
        migrationBuilder.RenameTable(name: rebuiltOAuthStatesTable, newName: "SlackOAuthStates");

        migrationBuilder.CreateIndex(
            name: "UX_SlackChildAppBindingObligations_ChildAppId",
            table: "SlackChildAppBindingObligations",
            column: "ChildAppId",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_SlackChildAppBindingObligations_AgentConnectionId",
            table: "SlackChildAppBindingObligations",
            column: "AgentConnectionId");
        migrationBuilder.CreateIndex(
            name: "IX_SlackChildAppBindingObligations_Status_UpdatedAt",
            table: "SlackChildAppBindingObligations",
            columns: new[] { "Status", "UpdatedAt" });
        migrationBuilder.CreateIndex(
            name: "UX_SlackOAuthAttempts_StateHash",
            table: "SlackOAuthAttempts",
            column: "StateHash",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_SlackOAuthAttempts_ChildAppId_Status_UpdatedAt",
            table: "SlackOAuthAttempts",
            columns: new[] { "ChildAppId", "Status", "UpdatedAt" });
        migrationBuilder.CreateIndex(
            name: "UX_SlackOAuthStates_StateHash",
            table: "SlackOAuthStates",
            column: "StateHash",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_SlackOAuthStates_AuthorizationAttemptId",
            table: "SlackOAuthStates",
            column: "AuthorizationAttemptId");
        migrationBuilder.CreateIndex(
            name: "IX_SlackOAuthStates_ChildAppId_ConsumedAt_ExpiresAt",
            table: "SlackOAuthStates",
            columns: new[] { "ChildAppId", "ConsumedAt", "ExpiresAt" });
    }

    private static void CopyRows(
        MigrationBuilder migrationBuilder,
        string destinationTable,
        string sourceTable,
        string[] columns)
    {
        var quotedColumns = string.Join(", ", columns.Select(column => $"\"{column}\""));
        migrationBuilder.Sql(
            $"INSERT INTO \"{destinationTable}\" ({quotedColumns}) SELECT {quotedColumns} FROM \"{sourceTable}\";");
    }

    private static readonly string[] ManagedSlackAgentAppColumns =
    [
        "Id", "EnrollmentId", "WorkspaceTeamId", "AgentConnectionId", "PublicIngressBaseUrl", "AppId", "BotUserId",
        "AppLifecycle", "Authorization", "TransportKind", "DesiredManifestVersion", "DesiredManifestHash",
        "AppliedManifestVersion", "AppliedManifestHash", "VerifiedScopesJson", "OperationFence", "OperationId",
        "OperationKind", "OperationStartedAt", "UnknownOutcome", "ErrorClass", "AuthorizationAttemptId", "AuthorizedAt",
        "AuthorizationExpiresAt", "ClientSecretRef", "SigningSecretRef", "AppLevelTokenRef", "BotTokenRef", "BindingState",
        "BindingErrorClass", "AuditJson", "CreatedAt", "UpdatedAt", "DeletedAt",
    ];

    private static readonly string[] SlackChildAppBindingObligationColumns =
    [
        "Id", "ChildAppId", "AgentConnectionId", "Status", "AttemptCount", "LastAttemptAt", "ClaimToken", "FailureClass",
        "CreatedAt", "UpdatedAt",
    ];

    private static readonly string[] SlackOAuthAttemptColumns =
    [
        "Id", "ChildAppId", "WorkspaceTeamId", "AppId", "StateHash", "BotUserId", "Status", "BotTokenRef", "FailureClass",
        "CreatedAt", "UpdatedAt", "ConsumedAt", "SecretStoredAt", "AppliedAt",
    ];

    private static readonly string[] SlackOAuthStateColumns =
    [
        "Id", "ChildAppId", "WorkspaceTeamId", "AppId", "StateHash", "AuthorizationAttemptId", "ExpiresAt", "ConsumedAt",
        "Outcome", "CreatedAt",
    ];

    private static readonly (string Suffix, string Sql)[] ManagedSlackAgentAppChecks =
    [
        ("AppliedManifestPair", "(\"AppliedManifestVersion\" IS NULL AND \"AppliedManifestHash\" IS NULL) OR (\"AppliedManifestVersion\" IS NOT NULL AND \"AppliedManifestHash\" IS NOT NULL AND \"AppliedManifestVersion\" > 0 AND \"AppliedManifestHash\" <> '')"),
        ("AppLifecycle", "\"AppLifecycle\" IN ('not_created', 'creating', 'create_unknown', 'created', 'deleting', 'delete_unknown', 'deleted')"),
        ("Authorization", "\"Authorization\" IN ('not_started', 'awaiting_user', 'pending_admin', 'authorized', 'expired_or_cancelled', 'revoked')"),
        ("BindingState", "\"BindingState\" IN ('pending', 'in_progress', 'bound', 'connection_deleted', 'conflict')"),
        ("DesiredManifest", "\"DesiredManifestVersion\" > 0 AND \"DesiredManifestHash\" <> ''"),
        ("IdentityPair", "\"BotUserId\" = '' OR \"AppId\" <> ''"),
        ("TransportKind", "\"TransportKind\" IN ('socket', 'https')"),
    ];
}
