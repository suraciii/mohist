using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260806000000_AddSlackManagerAppSetupFacts")]
public partial class AddSlackManagerAppSetupFacts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            RebuildSqliteEnrollmentTable(migrationBuilder, includeSetupFacts: true);
            return;
        }

        migrationBuilder.AddColumn<string>(
            name: "ManagerAppLifecycle",
            table: "SlackWorkspaceEnrollments",
            type: "TEXT",
            maxLength: 32,
            nullable: false,
            defaultValue: "not_created");
        migrationBuilder.AddColumn<int>(
            name: "ManagerAppOperationFence",
            table: "SlackWorkspaceEnrollments",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);
        migrationBuilder.AddColumn<string>(
            name: "ManagerAppOperationId",
            table: "SlackWorkspaceEnrollments",
            type: "TEXT",
            maxLength: 256,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "ManagerAppOperationOutcome",
            table: "SlackWorkspaceEnrollments",
            type: "TEXT",
            maxLength: 1024,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "RuntimeCredentialValidationState",
            table: "SlackWorkspaceEnrollments",
            type: "TEXT",
            maxLength: 32,
            nullable: false,
            defaultValue: "not_provided");
        migrationBuilder.AddCheckConstraint(
            name: "CK_SlackWorkspaceEnrollments_ManagerAppLifecycle",
            table: "SlackWorkspaceEnrollments",
            sql: "\"ManagerAppLifecycle\" IN ('not_created', 'creating', 'created', 'create_unknown')");
        migrationBuilder.AddCheckConstraint(
            name: "CK_SlackWorkspaceEnrollments_RuntimeCredentialValidationState",
            table: "SlackWorkspaceEnrollments",
            sql: "\"RuntimeCredentialValidationState\" IN ('not_provided', 'candidate', 'awaiting_socket', 'verified', 'failed')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            RebuildSqliteEnrollmentTable(migrationBuilder, includeSetupFacts: false);
            return;
        }

        migrationBuilder.DropCheckConstraint(
            name: "CK_SlackWorkspaceEnrollments_RuntimeCredentialValidationState",
            table: "SlackWorkspaceEnrollments");
        migrationBuilder.DropCheckConstraint(
            name: "CK_SlackWorkspaceEnrollments_ManagerAppLifecycle",
            table: "SlackWorkspaceEnrollments");
        migrationBuilder.DropColumn(
            name: "RuntimeCredentialValidationState",
            table: "SlackWorkspaceEnrollments");
        migrationBuilder.DropColumn(
            name: "ManagerAppOperationOutcome",
            table: "SlackWorkspaceEnrollments");
        migrationBuilder.DropColumn(
            name: "ManagerAppOperationId",
            table: "SlackWorkspaceEnrollments");
        migrationBuilder.DropColumn(
            name: "ManagerAppOperationFence",
            table: "SlackWorkspaceEnrollments");
        migrationBuilder.DropColumn(
            name: "ManagerAppLifecycle",
            table: "SlackWorkspaceEnrollments");
    }

    private static void RebuildSqliteEnrollmentTable(MigrationBuilder migrationBuilder, bool includeSetupFacts)
    {
        var setupFactsDefinitions = includeSetupFacts
            ? ", \"ManagerAppLifecycle\" TEXT NOT NULL DEFAULT 'not_created', \"ManagerAppOperationFence\" INTEGER NOT NULL DEFAULT 0, \"ManagerAppOperationId\" TEXT NULL, \"ManagerAppOperationOutcome\" TEXT NULL, \"RuntimeCredentialValidationState\" TEXT NOT NULL DEFAULT 'not_provided'"
            : string.Empty;
        var setupFactsChecks = includeSetupFacts
            ? ", CONSTRAINT \"CK_SlackWorkspaceEnrollments_ManagerAppLifecycle\" CHECK (\"ManagerAppLifecycle\" IN ('not_created', 'creating', 'created', 'create_unknown')), CONSTRAINT \"CK_SlackWorkspaceEnrollments_RuntimeCredentialValidationState\" CHECK (\"RuntimeCredentialValidationState\" IN ('not_provided', 'candidate', 'awaiting_socket', 'verified', 'failed'))"
            : string.Empty;
        var setupFactsColumns = includeSetupFacts
            ? ", \"ManagerAppLifecycle\", \"ManagerAppOperationFence\", \"ManagerAppOperationId\", \"ManagerAppOperationOutcome\", \"RuntimeCredentialValidationState\""
            : string.Empty;
        var setupFactsSelect = includeSetupFacts
            ? ", 'not_created', 0, NULL, NULL, 'not_provided'"
            : string.Empty;

        migrationBuilder.Sql($"""
            PRAGMA foreign_keys=OFF;
            CREATE TABLE "__SlackWorkspaceEnrollments_SetupFacts" (
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
                "ManagerActorId" TEXT NOT NULL,
                "ClaimedSlackUserId" TEXT NULL,
                "ManagerClaimHash" TEXT NULL,
                "ManagerClaimIssuedAt" TEXT NULL,
                "ManagerClaimExpiresAt" TEXT NULL,
                "ManagerClaimConsumedAt" TEXT NULL,
                "AuditJson" JSON NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                "DeletedAt" TEXT NULL{setupFactsDefinitions},
                CONSTRAINT "CK_SlackWorkspaceEnrollments_Lifecycle" CHECK ("Lifecycle" IN ('active', 'disabled', 'removed')),
                CONSTRAINT "CK_SlackWorkspaceEnrollments_ManagerCapability" CHECK ("ManagerCapability" IN ('unknown', 'available', 'unauthorized', 'capacity_limited')),
                CONSTRAINT "CK_SlackWorkspaceEnrollments_ManagerTransportKind" CHECK ("ManagerTransportKind" = 'socket'),
                CONSTRAINT "CK_SlackWorkspaceEnrollments_ManagerReadiness" CHECK ("ManagerReadiness" IN ('unknown', 'ready', 'not_ready', 'degraded')){setupFactsChecks});
            INSERT INTO "__SlackWorkspaceEnrollments_SetupFacts" (
                "Id", "WorkspaceTeamId", "Lifecycle", "ManagerCapability", "CapabilityReason", "LastVerifiedAt", "PlanCode", "ManagedAppLimit",
                "ConfigurationCredentialRef", "ConfigurationCredentialGeneration", "ConfigurationCredentialExpiresAt", "S2OriginalManagerTransportKind",
                "ManagerCredentialRef", "ManagerAppId", "ManagerBotUserId", "ManagerTransportKind", "ManagerReadiness", "ManagerActorId",
                "ClaimedSlackUserId", "ManagerClaimHash", "ManagerClaimIssuedAt", "ManagerClaimExpiresAt", "ManagerClaimConsumedAt", "AuditJson",
                "CreatedAt", "UpdatedAt", "DeletedAt"{setupFactsColumns})
            SELECT
                "Id", "WorkspaceTeamId", "Lifecycle", "ManagerCapability", "CapabilityReason", "LastVerifiedAt", "PlanCode", "ManagedAppLimit",
                "ConfigurationCredentialRef", "ConfigurationCredentialGeneration", "ConfigurationCredentialExpiresAt", "S2OriginalManagerTransportKind",
                "ManagerCredentialRef", "ManagerAppId", "ManagerBotUserId", "ManagerTransportKind", "ManagerReadiness", "ManagerActorId",
                "ClaimedSlackUserId", "ManagerClaimHash", "ManagerClaimIssuedAt", "ManagerClaimExpiresAt", "ManagerClaimConsumedAt", "AuditJson",
                "CreatedAt", "UpdatedAt", "DeletedAt"{setupFactsSelect}
            FROM "SlackWorkspaceEnrollments";
            DROP TABLE "SlackWorkspaceEnrollments";
            ALTER TABLE "__SlackWorkspaceEnrollments_SetupFacts" RENAME TO "SlackWorkspaceEnrollments";
            CREATE UNIQUE INDEX "UX_SlackWorkspaceEnrollments_WorkspaceTeamId" ON "SlackWorkspaceEnrollments" ("WorkspaceTeamId") WHERE "DeletedAt" IS NULL AND "Lifecycle" = 'active';
            CREATE INDEX "IX_SlackWorkspaceEnrollments_Lifecycle_UpdatedAt" ON "SlackWorkspaceEnrollments" ("Lifecycle", "UpdatedAt");
            PRAGMA foreign_keys=ON;
            """, suppressTransaction: true);
    }
}
