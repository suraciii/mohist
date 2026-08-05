using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260805130000_AddSlackS2PortsManifestRotation")]
public partial class AddSlackS2PortsManifestRotation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            RebuildSqliteTables(migrationBuilder, includeHttpsColumns: false);
            return;
        }

        migrationBuilder.DropCheckConstraint("CK_ManagedSlackAgentApps_TransportKind", "ManagedSlackAgentApps");
        migrationBuilder.DropColumn("PublicIngressBaseUrl", "ManagedSlackAgentApps");
        migrationBuilder.DropColumn("TransportKind", "ManagedSlackAgentApps");
        migrationBuilder.AddColumn<string>("ConfigurationCredentialRef", "SlackWorkspaceEnrollments", "TEXT", maxLength: 256, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<int>("ConfigurationCredentialGeneration", "SlackWorkspaceEnrollments", "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<DateTimeOffset>("ConfigurationCredentialExpiresAt", "SlackWorkspaceEnrollments", "TEXT", nullable: true);
        migrationBuilder.DropCheckConstraint("CK_SlackWorkspaceEnrollments_ManagerTransportKind", "SlackWorkspaceEnrollments");
        migrationBuilder.AddCheckConstraint("CK_SlackWorkspaceEnrollments_ManagerTransportKind", "SlackWorkspaceEnrollments", "\"ManagerTransportKind\" = 'socket'");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            RebuildSqliteTables(migrationBuilder, includeHttpsColumns: true);
            return;
        }

        migrationBuilder.DropCheckConstraint("CK_SlackWorkspaceEnrollments_ManagerTransportKind", "SlackWorkspaceEnrollments");
        migrationBuilder.AddCheckConstraint("CK_SlackWorkspaceEnrollments_ManagerTransportKind", "SlackWorkspaceEnrollments", "\"ManagerTransportKind\" IN ('socket', 'https')");
        migrationBuilder.DropColumn("ConfigurationCredentialExpiresAt", "SlackWorkspaceEnrollments");
        migrationBuilder.DropColumn("ConfigurationCredentialGeneration", "SlackWorkspaceEnrollments");
        migrationBuilder.DropColumn("ConfigurationCredentialRef", "SlackWorkspaceEnrollments");
        migrationBuilder.AddColumn<string>("PublicIngressBaseUrl", "ManagedSlackAgentApps", "TEXT", maxLength: 2048, nullable: true);
        migrationBuilder.AddColumn<string>("TransportKind", "ManagedSlackAgentApps", "TEXT", maxLength: 32, nullable: false, defaultValue: "socket");
        migrationBuilder.AddCheckConstraint("CK_ManagedSlackAgentApps_TransportKind", "ManagedSlackAgentApps", "\"TransportKind\" IN ('socket', 'https')");
    }

    private static void RebuildSqliteTables(MigrationBuilder migrationBuilder, bool includeHttpsColumns)
    {
        const string migratedHttpsAuditAction = "manager_transport_migrated_https_to_socket_not_ready";
        var appColumns = includeHttpsColumns
            ? "\"Id\", \"EnrollmentId\", \"WorkspaceTeamId\", \"AgentConnectionId\", \"PublicIngressBaseUrl\", \"AppId\", \"BotUserId\", \"AppLifecycle\", \"Authorization\", \"TransportKind\", \"DesiredManifestVersion\", \"DesiredManifestHash\", \"AppliedManifestVersion\", \"AppliedManifestHash\", \"VerifiedScopesJson\", \"OperationFence\", \"OperationId\", \"OperationKind\", \"OperationStartedAt\", \"UnknownOutcome\", \"ErrorClass\", \"AuthorizationAttemptId\", \"AuthorizedAt\", \"AuthorizationExpiresAt\", \"ClientSecretRef\", \"SigningSecretRef\", \"AppLevelTokenRef\", \"BotTokenRef\", \"BindingState\", \"BindingErrorClass\", \"AuditJson\", \"CreatedAt\", \"UpdatedAt\", \"DeletedAt\""
            : "\"Id\", \"EnrollmentId\", \"WorkspaceTeamId\", \"AgentConnectionId\", \"AppId\", \"BotUserId\", \"AppLifecycle\", \"Authorization\", \"DesiredManifestVersion\", \"DesiredManifestHash\", \"AppliedManifestVersion\", \"AppliedManifestHash\", \"VerifiedScopesJson\", \"OperationFence\", \"OperationId\", \"OperationKind\", \"OperationStartedAt\", \"UnknownOutcome\", \"ErrorClass\", \"AuthorizationAttemptId\", \"AuthorizedAt\", \"AuthorizationExpiresAt\", \"ClientSecretRef\", \"SigningSecretRef\", \"AppLevelTokenRef\", \"BotTokenRef\", \"BindingState\", \"BindingErrorClass\", \"AuditJson\", \"CreatedAt\", \"UpdatedAt\", \"DeletedAt\"";
        var appSelect = includeHttpsColumns
            ? "\"Id\", \"EnrollmentId\", \"WorkspaceTeamId\", \"AgentConnectionId\", NULL, \"AppId\", \"BotUserId\", \"AppLifecycle\", \"Authorization\", 'socket', \"DesiredManifestVersion\", \"DesiredManifestHash\", \"AppliedManifestVersion\", \"AppliedManifestHash\", \"VerifiedScopesJson\", \"OperationFence\", \"OperationId\", \"OperationKind\", \"OperationStartedAt\", \"UnknownOutcome\", \"ErrorClass\", \"AuthorizationAttemptId\", \"AuthorizedAt\", \"AuthorizationExpiresAt\", \"ClientSecretRef\", \"SigningSecretRef\", \"AppLevelTokenRef\", \"BotTokenRef\", \"BindingState\", \"BindingErrorClass\", \"AuditJson\", \"CreatedAt\", \"UpdatedAt\", \"DeletedAt\""
            : "\"Id\", \"EnrollmentId\", \"WorkspaceTeamId\", \"AgentConnectionId\", \"AppId\", \"BotUserId\", \"AppLifecycle\", \"Authorization\", \"DesiredManifestVersion\", \"DesiredManifestHash\", \"AppliedManifestVersion\", \"AppliedManifestHash\", \"VerifiedScopesJson\", \"OperationFence\", \"OperationId\", \"OperationKind\", \"OperationStartedAt\", \"UnknownOutcome\", \"ErrorClass\", \"AuthorizationAttemptId\", \"AuthorizedAt\", \"AuthorizationExpiresAt\", \"ClientSecretRef\", \"SigningSecretRef\", \"AppLevelTokenRef\", \"BotTokenRef\", \"BindingState\", \"BindingErrorClass\", \"AuditJson\", \"CreatedAt\", \"UpdatedAt\", \"DeletedAt\"";
        var appDefinitions = includeHttpsColumns
            ? "\"Id\" TEXT NOT NULL CONSTRAINT \"PK_ManagedSlackAgentApps\" PRIMARY KEY, \"EnrollmentId\" TEXT NOT NULL, \"WorkspaceTeamId\" TEXT NOT NULL, \"AgentConnectionId\" TEXT NOT NULL, \"PublicIngressBaseUrl\" TEXT NULL, \"AppId\" TEXT NOT NULL, \"BotUserId\" TEXT NOT NULL, \"AppLifecycle\" TEXT NOT NULL, \"Authorization\" TEXT NOT NULL, \"TransportKind\" TEXT NOT NULL, \"DesiredManifestVersion\" INTEGER NOT NULL, \"DesiredManifestHash\" TEXT NOT NULL, \"AppliedManifestVersion\" INTEGER NULL, \"AppliedManifestHash\" TEXT NULL, \"VerifiedScopesJson\" JSON NOT NULL, \"OperationFence\" INTEGER NOT NULL, \"OperationId\" TEXT NULL, \"OperationKind\" TEXT NULL, \"OperationStartedAt\" TEXT NULL, \"UnknownOutcome\" TEXT NULL, \"ErrorClass\" TEXT NULL, \"AuthorizationAttemptId\" TEXT NULL, \"AuthorizedAt\" TEXT NULL, \"AuthorizationExpiresAt\" TEXT NULL, \"ClientSecretRef\" TEXT NOT NULL, \"SigningSecretRef\" TEXT NOT NULL, \"AppLevelTokenRef\" TEXT NOT NULL, \"BotTokenRef\" TEXT NOT NULL, \"BindingState\" TEXT NOT NULL, \"BindingErrorClass\" TEXT NULL, \"AuditJson\" JSON NOT NULL, \"CreatedAt\" TEXT NOT NULL, \"UpdatedAt\" TEXT NOT NULL, \"DeletedAt\" TEXT NULL, CONSTRAINT \"CK_ManagedSlackAgentApps_AppLifecycle\" CHECK (\"AppLifecycle\" IN ('not_created', 'creating', 'create_unknown', 'created', 'deleting', 'delete_unknown', 'deleted')), CONSTRAINT \"CK_ManagedSlackAgentApps_Authorization\" CHECK (\"Authorization\" IN ('not_started', 'awaiting_user', 'pending_admin', 'authorized', 'expired_or_cancelled', 'revoked')), CONSTRAINT \"CK_ManagedSlackAgentApps_TransportKind\" CHECK (\"TransportKind\" IN ('socket', 'https')), CONSTRAINT \"CK_ManagedSlackAgentApps_BindingState\" CHECK (\"BindingState\" IN ('pending', 'in_progress', 'bound', 'connection_deleted', 'conflict')), CONSTRAINT \"CK_ManagedSlackAgentApps_DesiredManifest\" CHECK (\"DesiredManifestVersion\" > 0 AND \"DesiredManifestHash\" <> ''), CONSTRAINT \"CK_ManagedSlackAgentApps_AppliedManifestPair\" CHECK ((\"AppliedManifestVersion\" IS NULL AND \"AppliedManifestHash\" IS NULL) OR (\"AppliedManifestVersion\" IS NOT NULL AND \"AppliedManifestHash\" IS NOT NULL AND \"AppliedManifestVersion\" > 0 AND \"AppliedManifestHash\" <> '')), CONSTRAINT \"CK_ManagedSlackAgentApps_IdentityPair\" CHECK (\"BotUserId\" = '' OR \"AppId\" <> ''), FOREIGN KEY (\"AgentConnectionId\") REFERENCES \"AgentConnections\" (\"Id\") ON DELETE RESTRICT, FOREIGN KEY (\"EnrollmentId\") REFERENCES \"SlackWorkspaceEnrollments\" (\"Id\") ON DELETE RESTRICT"
            : "\"Id\" TEXT NOT NULL CONSTRAINT \"PK_ManagedSlackAgentApps\" PRIMARY KEY, \"EnrollmentId\" TEXT NOT NULL, \"WorkspaceTeamId\" TEXT NOT NULL, \"AgentConnectionId\" TEXT NOT NULL, \"AppId\" TEXT NOT NULL, \"BotUserId\" TEXT NOT NULL, \"AppLifecycle\" TEXT NOT NULL, \"Authorization\" TEXT NOT NULL, \"DesiredManifestVersion\" INTEGER NOT NULL, \"DesiredManifestHash\" TEXT NOT NULL, \"AppliedManifestVersion\" INTEGER NULL, \"AppliedManifestHash\" TEXT NULL, \"VerifiedScopesJson\" JSON NOT NULL, \"OperationFence\" INTEGER NOT NULL, \"OperationId\" TEXT NULL, \"OperationKind\" TEXT NULL, \"OperationStartedAt\" TEXT NULL, \"UnknownOutcome\" TEXT NULL, \"ErrorClass\" TEXT NULL, \"AuthorizationAttemptId\" TEXT NULL, \"AuthorizedAt\" TEXT NULL, \"AuthorizationExpiresAt\" TEXT NULL, \"ClientSecretRef\" TEXT NOT NULL, \"SigningSecretRef\" TEXT NOT NULL, \"AppLevelTokenRef\" TEXT NOT NULL, \"BotTokenRef\" TEXT NOT NULL, \"BindingState\" TEXT NOT NULL, \"BindingErrorClass\" TEXT NULL, \"AuditJson\" JSON NOT NULL, \"CreatedAt\" TEXT NOT NULL, \"UpdatedAt\" TEXT NOT NULL, \"DeletedAt\" TEXT NULL, CONSTRAINT \"CK_ManagedSlackAgentApps_AppLifecycle\" CHECK (\"AppLifecycle\" IN ('not_created', 'creating', 'create_unknown', 'created', 'deleting', 'delete_unknown', 'deleted')), CONSTRAINT \"CK_ManagedSlackAgentApps_Authorization\" CHECK (\"Authorization\" IN ('not_started', 'awaiting_user', 'pending_admin', 'authorized', 'expired_or_cancelled', 'revoked')), CONSTRAINT \"CK_ManagedSlackAgentApps_BindingState\" CHECK (\"BindingState\" IN ('pending', 'in_progress', 'bound', 'connection_deleted', 'conflict')), CONSTRAINT \"CK_ManagedSlackAgentApps_DesiredManifest\" CHECK (\"DesiredManifestVersion\" > 0 AND \"DesiredManifestHash\" <> ''), CONSTRAINT \"CK_ManagedSlackAgentApps_AppliedManifestPair\" CHECK ((\"AppliedManifestVersion\" IS NULL AND \"AppliedManifestHash\" IS NULL) OR (\"AppliedManifestVersion\" IS NOT NULL AND \"AppliedManifestHash\" IS NOT NULL AND \"AppliedManifestVersion\" > 0 AND \"AppliedManifestHash\" <> '')), CONSTRAINT \"CK_ManagedSlackAgentApps_IdentityPair\" CHECK (\"BotUserId\" = '' OR \"AppId\" <> ''), FOREIGN KEY (\"AgentConnectionId\") REFERENCES \"AgentConnections\" (\"Id\") ON DELETE RESTRICT, FOREIGN KEY (\"EnrollmentId\") REFERENCES \"SlackWorkspaceEnrollments\" (\"Id\") ON DELETE RESTRICT";
        var enrollmentColumns = includeHttpsColumns
            ? "\"Id\", \"WorkspaceTeamId\", \"Lifecycle\", \"ManagerCapability\", \"CapabilityReason\", \"LastVerifiedAt\", \"PlanCode\", \"ManagedAppLimit\", \"ManagerCredentialRef\", \"ManagerAppId\", \"ManagerBotUserId\", \"ManagerTransportKind\", \"ManagerReadiness\", \"ManagerActorId\", \"ClaimedSlackUserId\", \"ManagerClaimHash\", \"ManagerClaimIssuedAt\", \"ManagerClaimExpiresAt\", \"ManagerClaimConsumedAt\", \"AuditJson\", \"CreatedAt\", \"UpdatedAt\", \"DeletedAt\""
            : "\"Id\", \"WorkspaceTeamId\", \"Lifecycle\", \"ManagerCapability\", \"CapabilityReason\", \"LastVerifiedAt\", \"PlanCode\", \"ManagedAppLimit\", \"ConfigurationCredentialRef\", \"ConfigurationCredentialGeneration\", \"ConfigurationCredentialExpiresAt\", \"ManagerCredentialRef\", \"ManagerAppId\", \"ManagerBotUserId\", \"ManagerTransportKind\", \"ManagerReadiness\", \"ManagerActorId\", \"ClaimedSlackUserId\", \"ManagerClaimHash\", \"ManagerClaimIssuedAt\", \"ManagerClaimExpiresAt\", \"ManagerClaimConsumedAt\", \"AuditJson\", \"CreatedAt\", \"UpdatedAt\", \"DeletedAt\"";
        var enrollmentSelect = includeHttpsColumns
            ? $"\"Id\", \"WorkspaceTeamId\", \"Lifecycle\", \"ManagerCapability\", \"CapabilityReason\", \"LastVerifiedAt\", \"PlanCode\", \"ManagedAppLimit\", \"ManagerCredentialRef\", \"ManagerAppId\", \"ManagerBotUserId\", CASE WHEN instr(\"AuditJson\", '\"Action\":\"{migratedHttpsAuditAction}\"') > 0 THEN 'https' ELSE 'socket' END, \"ManagerReadiness\", \"ManagerActorId\", \"ClaimedSlackUserId\", \"ManagerClaimHash\", \"ManagerClaimIssuedAt\", \"ManagerClaimExpiresAt\", \"ManagerClaimConsumedAt\", \"AuditJson\", \"CreatedAt\", \"UpdatedAt\", \"DeletedAt\""
            : $"\"Id\", \"WorkspaceTeamId\", \"Lifecycle\", \"ManagerCapability\", \"CapabilityReason\", \"LastVerifiedAt\", \"PlanCode\", \"ManagedAppLimit\", '', 0, NULL, \"ManagerCredentialRef\", \"ManagerAppId\", \"ManagerBotUserId\", CASE WHEN \"ManagerTransportKind\" = 'https' THEN 'socket' ELSE \"ManagerTransportKind\" END, CASE WHEN \"ManagerTransportKind\" = 'https' THEN 'not_ready' ELSE \"ManagerReadiness\" END, \"ManagerActorId\", \"ClaimedSlackUserId\", \"ManagerClaimHash\", \"ManagerClaimIssuedAt\", \"ManagerClaimExpiresAt\", \"ManagerClaimConsumedAt\", CASE WHEN \"ManagerTransportKind\" = 'https' THEN json_insert(\"AuditJson\", '$[#]', json_object('Action', '{migratedHttpsAuditAction}', 'SlackUserId', NULL, 'At', \"UpdatedAt\")) ELSE \"AuditJson\" END, \"CreatedAt\", \"UpdatedAt\", \"DeletedAt\"";
        var configurationCredentialDefinitions = includeHttpsColumns
            ? string.Empty
            : ", \"ConfigurationCredentialRef\" TEXT NOT NULL, \"ConfigurationCredentialGeneration\" INTEGER NOT NULL, \"ConfigurationCredentialExpiresAt\" TEXT NULL";
        migrationBuilder.Sql($"""
            PRAGMA foreign_keys=OFF;
            CREATE TABLE "__ManagedSlackAgentApps_S2" ({appDefinitions});
            INSERT INTO "__ManagedSlackAgentApps_S2" ({appColumns}) SELECT {appSelect} FROM "ManagedSlackAgentApps";
            DROP TABLE "ManagedSlackAgentApps";
            ALTER TABLE "__ManagedSlackAgentApps_S2" RENAME TO "ManagedSlackAgentApps";
            CREATE UNIQUE INDEX "UX_ManagedSlackAgentApps_AgentConnectionId" ON "ManagedSlackAgentApps" ("AgentConnectionId") WHERE "DeletedAt" IS NULL;
            CREATE UNIQUE INDEX "UX_ManagedSlackAgentApps_WorkspaceTeamId_AppId" ON "ManagedSlackAgentApps" ("WorkspaceTeamId", "AppId") WHERE "DeletedAt" IS NULL AND "AppId" <> '';
            CREATE INDEX "IX_ManagedSlackAgentApps_EnrollmentId_UpdatedAt" ON "ManagedSlackAgentApps" ("EnrollmentId", "UpdatedAt");
            CREATE TABLE "__SlackWorkspaceEnrollments_S2" ("Id" TEXT NOT NULL CONSTRAINT "PK_SlackWorkspaceEnrollments" PRIMARY KEY, "WorkspaceTeamId" TEXT NOT NULL, "Lifecycle" TEXT NOT NULL, "ManagerCapability" TEXT NOT NULL, "CapabilityReason" TEXT NULL, "LastVerifiedAt" TEXT NULL, "PlanCode" TEXT NOT NULL, "ManagedAppLimit" INTEGER NOT NULL{configurationCredentialDefinitions}, "ManagerCredentialRef" TEXT NOT NULL, "ManagerAppId" TEXT NOT NULL, "ManagerBotUserId" TEXT NOT NULL, "ManagerTransportKind" TEXT NOT NULL, "ManagerReadiness" TEXT NOT NULL, "ManagerActorId" TEXT NOT NULL, "ClaimedSlackUserId" TEXT NULL, "ManagerClaimHash" TEXT NULL, "ManagerClaimIssuedAt" TEXT NULL, "ManagerClaimExpiresAt" TEXT NULL, "ManagerClaimConsumedAt" TEXT NULL, "AuditJson" JSON NOT NULL, "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL, "DeletedAt" TEXT NULL, CONSTRAINT "CK_SlackWorkspaceEnrollments_Lifecycle" CHECK ("Lifecycle" IN ('active', 'disabled', 'removed')), CONSTRAINT "CK_SlackWorkspaceEnrollments_ManagerCapability" CHECK ("ManagerCapability" IN ('unknown', 'available', 'unauthorized', 'capacity_limited')), CONSTRAINT "CK_SlackWorkspaceEnrollments_ManagerTransportKind" CHECK ({(includeHttpsColumns ? "\"ManagerTransportKind\" IN ('socket', 'https')" : "\"ManagerTransportKind\" = 'socket'")}), CONSTRAINT "CK_SlackWorkspaceEnrollments_ManagerReadiness" CHECK ("ManagerReadiness" IN ('unknown', 'ready', 'not_ready', 'degraded')));
            INSERT INTO "__SlackWorkspaceEnrollments_S2" ({enrollmentColumns}) SELECT {enrollmentSelect} FROM "SlackWorkspaceEnrollments";
            DROP TABLE "SlackWorkspaceEnrollments";
            ALTER TABLE "__SlackWorkspaceEnrollments_S2" RENAME TO "SlackWorkspaceEnrollments";
            CREATE UNIQUE INDEX "UX_SlackWorkspaceEnrollments_WorkspaceTeamId" ON "SlackWorkspaceEnrollments" ("WorkspaceTeamId") WHERE "DeletedAt" IS NULL AND "Lifecycle" = 'active';
            CREATE INDEX "IX_SlackWorkspaceEnrollments_Lifecycle_UpdatedAt" ON "SlackWorkspaceEnrollments" ("Lifecycle", "UpdatedAt");
            PRAGMA foreign_keys=ON;
            """, suppressTransaction: true);
    }
}
