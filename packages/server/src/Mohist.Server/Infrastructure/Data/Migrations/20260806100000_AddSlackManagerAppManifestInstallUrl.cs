using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260806100000_AddSlackManagerAppManifestInstallUrl")]
public partial class AddSlackManagerAppManifestInstallUrl : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ManagerAppManifestHash",
            table: "SlackWorkspaceEnrollments",
            type: "TEXT",
            maxLength: 128,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "ManagerAppInstallUrl",
            table: "SlackWorkspaceEnrollments",
            type: "TEXT",
            maxLength: 2048,
            nullable: false,
            defaultValue: "");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            migrationBuilder.Sql(
                """
                PRAGMA foreign_keys=OFF;
                CREATE TABLE "__SlackWorkspaceEnrollments_ManifestInstallUrl" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_SlackWorkspaceEnrollments" PRIMARY KEY,
                    "WorkspaceTeamId" TEXT NOT NULL,
                    "Lifecycle" TEXT NOT NULL,
                    "ManagerCapability" TEXT NOT NULL,
                    "CapabilityReason" TEXT NULL,
                    "LastVerifiedAt" TEXT NULL,
                    "PlanCode" TEXT NOT NULL,
                    "ManagedAppLimit" INTEGER NOT NULL,
                    "ConfigurationCredentialRef" TEXT NOT NULL,
                    "ConfigurationCredentialGeneration" INTEGER NOT NULL,
                    "ConfigurationCredentialExpiresAt" TEXT NULL,
                    "S2OriginalManagerTransportKind" TEXT NOT NULL DEFAULT 'socket',
                    "ManagerCredentialRef" TEXT NOT NULL,
                    "ManagerAppId" TEXT NOT NULL,
                    "ManagerBotUserId" TEXT NOT NULL,
                    "ManagerTransportKind" TEXT NOT NULL,
                    "ManagerReadiness" TEXT NOT NULL,
                    "ManagerAppLifecycle" TEXT NOT NULL DEFAULT 'not_created',
                    "ManagerAppOperationFence" INTEGER NOT NULL DEFAULT 0,
                    "ManagerAppOperationId" TEXT NULL,
                    "ManagerAppOperationOutcome" TEXT NULL,
                    "RuntimeCredentialValidationState" TEXT NOT NULL DEFAULT 'not_provided',
                    "ManagerActorId" TEXT NOT NULL,
                    "ClaimedSlackUserId" TEXT NULL,
                    "ManagerClaimHash" TEXT NULL,
                    "ManagerClaimIssuedAt" TEXT NULL,
                    "ManagerClaimExpiresAt" TEXT NULL,
                    "ManagerClaimConsumedAt" TEXT NULL,
                    "AuditJson" JSON NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    "DeletedAt" TEXT NULL,
                    CONSTRAINT "CK_SlackWorkspaceEnrollments_Lifecycle" CHECK ("Lifecycle" IN ('active', 'disabled', 'removed')),
                    CONSTRAINT "CK_SlackWorkspaceEnrollments_ManagerCapability" CHECK ("ManagerCapability" IN ('unknown', 'available', 'unauthorized', 'capacity_limited')),
                    CONSTRAINT "CK_SlackWorkspaceEnrollments_ManagerTransportKind" CHECK ("ManagerTransportKind" = 'socket'),
                    CONSTRAINT "CK_SlackWorkspaceEnrollments_ManagerReadiness" CHECK ("ManagerReadiness" IN ('unknown', 'ready', 'not_ready', 'degraded')),
                    CONSTRAINT "CK_SlackWorkspaceEnrollments_ManagerAppLifecycle" CHECK ("ManagerAppLifecycle" IN ('not_created', 'creating', 'created', 'create_unknown')),
                    CONSTRAINT "CK_SlackWorkspaceEnrollments_RuntimeCredentialValidationState" CHECK ("RuntimeCredentialValidationState" IN ('not_provided', 'candidate', 'awaiting_socket', 'verified', 'failed')));
                INSERT INTO "__SlackWorkspaceEnrollments_ManifestInstallUrl" (
                    "Id", "WorkspaceTeamId", "Lifecycle", "ManagerCapability", "CapabilityReason", "LastVerifiedAt", "PlanCode", "ManagedAppLimit",
                    "ConfigurationCredentialRef", "ConfigurationCredentialGeneration", "ConfigurationCredentialExpiresAt", "S2OriginalManagerTransportKind",
                    "ManagerCredentialRef", "ManagerAppId", "ManagerBotUserId", "ManagerTransportKind", "ManagerReadiness",
                    "ManagerAppLifecycle", "ManagerAppOperationFence", "ManagerAppOperationId", "ManagerAppOperationOutcome", "RuntimeCredentialValidationState",
                    "ManagerActorId", "ClaimedSlackUserId", "ManagerClaimHash", "ManagerClaimIssuedAt", "ManagerClaimExpiresAt", "ManagerClaimConsumedAt", "AuditJson",
                    "CreatedAt", "UpdatedAt", "DeletedAt")
                SELECT
                    "Id", "WorkspaceTeamId", "Lifecycle", "ManagerCapability", "CapabilityReason", "LastVerifiedAt", "PlanCode", "ManagedAppLimit",
                    "ConfigurationCredentialRef", "ConfigurationCredentialGeneration", "ConfigurationCredentialExpiresAt", "S2OriginalManagerTransportKind",
                    "ManagerCredentialRef", "ManagerAppId", "ManagerBotUserId", "ManagerTransportKind", "ManagerReadiness",
                    "ManagerAppLifecycle", "ManagerAppOperationFence", "ManagerAppOperationId", "ManagerAppOperationOutcome", "RuntimeCredentialValidationState",
                    "ManagerActorId", "ClaimedSlackUserId", "ManagerClaimHash", "ManagerClaimIssuedAt", "ManagerClaimExpiresAt", "ManagerClaimConsumedAt", "AuditJson",
                    "CreatedAt", "UpdatedAt", "DeletedAt"
                FROM "SlackWorkspaceEnrollments";
                DROP TABLE "SlackWorkspaceEnrollments";
                ALTER TABLE "__SlackWorkspaceEnrollments_ManifestInstallUrl" RENAME TO "SlackWorkspaceEnrollments";
                CREATE UNIQUE INDEX "UX_SlackWorkspaceEnrollments_WorkspaceTeamId" ON "SlackWorkspaceEnrollments" ("WorkspaceTeamId") WHERE "DeletedAt" IS NULL AND "Lifecycle" = 'active';
                CREATE INDEX "IX_SlackWorkspaceEnrollments_Lifecycle_UpdatedAt" ON "SlackWorkspaceEnrollments" ("Lifecycle", "UpdatedAt");
                PRAGMA foreign_keys=ON;
                """,
                suppressTransaction: true);
            return;
        }

        migrationBuilder.DropColumn(
            name: "ManagerAppInstallUrl",
            table: "SlackWorkspaceEnrollments");

        migrationBuilder.DropColumn(
            name: "ManagerAppManifestHash",
            table: "SlackWorkspaceEnrollments");
    }
}
