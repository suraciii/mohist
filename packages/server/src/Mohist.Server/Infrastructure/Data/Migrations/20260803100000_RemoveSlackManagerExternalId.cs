using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260803100000_RemoveSlackManagerExternalId")]
public partial class RemoveSlackManagerExternalId : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            migrationBuilder.Sql(
                """
                PRAGMA foreign_keys=OFF;
                CREATE TABLE "ef_temp_SlackWorkspaceEnrollments" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_SlackWorkspaceEnrollments" PRIMARY KEY,
                    "WorkspaceTeamId" TEXT NOT NULL,
                    "Lifecycle" TEXT NOT NULL,
                    "ManagerCapability" TEXT NOT NULL,
                    "CapabilityReason" TEXT NULL,
                    "LastVerifiedAt" TEXT NULL,
                    "PlanCode" TEXT NOT NULL,
                    "ManagedAppLimit" INTEGER NOT NULL,
                    "ManagerCredentialRef" TEXT NOT NULL,
                    "AuditJson" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    "DeletedAt" TEXT NULL,
                    CONSTRAINT "CK_SlackWorkspaceEnrollments_Lifecycle" CHECK ("Lifecycle" IN ('active', 'disabled', 'removed')),
                    CONSTRAINT "CK_SlackWorkspaceEnrollments_ManagerCapability" CHECK ("ManagerCapability" IN ('unknown', 'available', 'unauthorized', 'capacity_limited'))
                );
                INSERT INTO "ef_temp_SlackWorkspaceEnrollments" ("Id", "WorkspaceTeamId", "Lifecycle", "ManagerCapability", "CapabilityReason", "LastVerifiedAt", "PlanCode", "ManagedAppLimit", "ManagerCredentialRef", "AuditJson", "CreatedAt", "UpdatedAt", "DeletedAt")
                    SELECT "Id", "WorkspaceTeamId", "Lifecycle", "ManagerCapability", "CapabilityReason", "LastVerifiedAt", "PlanCode", "ManagedAppLimit", "ManagerCredentialRef", "AuditJson", "CreatedAt", "UpdatedAt", "DeletedAt"
                    FROM "SlackWorkspaceEnrollments";
                DROP TABLE "SlackWorkspaceEnrollments";
                ALTER TABLE "ef_temp_SlackWorkspaceEnrollments" RENAME TO "SlackWorkspaceEnrollments";
                CREATE INDEX "IX_SlackWorkspaceEnrollments_Lifecycle_UpdatedAt" ON "SlackWorkspaceEnrollments" ("Lifecycle", "UpdatedAt");
                CREATE UNIQUE INDEX "UX_SlackWorkspaceEnrollments_WorkspaceTeamId" ON "SlackWorkspaceEnrollments" ("WorkspaceTeamId") WHERE "DeletedAt" IS NULL AND "Lifecycle" = 'active';
                PRAGMA foreign_keys=ON;
                """,
                suppressTransaction: true);
            return;
        }

        migrationBuilder.DropColumn(
            name: "ManagerExternalId",
            table: "SlackWorkspaceEnrollments");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            migrationBuilder.Sql(
                """
                PRAGMA foreign_keys=OFF;
                CREATE TABLE "ef_temp_SlackWorkspaceEnrollments" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_SlackWorkspaceEnrollments" PRIMARY KEY,
                    "WorkspaceTeamId" TEXT NOT NULL,
                    "ManagerExternalId" TEXT NOT NULL,
                    "Lifecycle" TEXT NOT NULL,
                    "ManagerCapability" TEXT NOT NULL,
                    "CapabilityReason" TEXT NULL,
                    "LastVerifiedAt" TEXT NULL,
                    "PlanCode" TEXT NOT NULL,
                    "ManagedAppLimit" INTEGER NOT NULL,
                    "ManagerCredentialRef" TEXT NOT NULL,
                    "AuditJson" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    "DeletedAt" TEXT NULL,
                    CONSTRAINT "CK_SlackWorkspaceEnrollments_Lifecycle" CHECK ("Lifecycle" IN ('active', 'disabled', 'removed')),
                    CONSTRAINT "CK_SlackWorkspaceEnrollments_ManagerCapability" CHECK ("ManagerCapability" IN ('unknown', 'available', 'unauthorized', 'capacity_limited'))
                );
                INSERT INTO "ef_temp_SlackWorkspaceEnrollments" ("Id", "WorkspaceTeamId", "ManagerExternalId", "Lifecycle", "ManagerCapability", "CapabilityReason", "LastVerifiedAt", "PlanCode", "ManagedAppLimit", "ManagerCredentialRef", "AuditJson", "CreatedAt", "UpdatedAt", "DeletedAt")
                    SELECT "Id", "WorkspaceTeamId", '', "Lifecycle", "ManagerCapability", "CapabilityReason", "LastVerifiedAt", "PlanCode", "ManagedAppLimit", "ManagerCredentialRef", "AuditJson", "CreatedAt", "UpdatedAt", "DeletedAt"
                    FROM "SlackWorkspaceEnrollments";
                DROP TABLE "SlackWorkspaceEnrollments";
                ALTER TABLE "ef_temp_SlackWorkspaceEnrollments" RENAME TO "SlackWorkspaceEnrollments";
                CREATE INDEX "IX_SlackWorkspaceEnrollments_Lifecycle_UpdatedAt" ON "SlackWorkspaceEnrollments" ("Lifecycle", "UpdatedAt");
                CREATE UNIQUE INDEX "UX_SlackWorkspaceEnrollments_WorkspaceTeamId" ON "SlackWorkspaceEnrollments" ("WorkspaceTeamId") WHERE "DeletedAt" IS NULL AND "Lifecycle" = 'active';
                PRAGMA foreign_keys=ON;
                """,
                suppressTransaction: true);
            return;
        }

        migrationBuilder.AddColumn<string>(
            name: "ManagerExternalId",
            table: "SlackWorkspaceEnrollments",
            type: "TEXT",
            maxLength: 256,
            nullable: false,
            defaultValue: "");
    }
}
