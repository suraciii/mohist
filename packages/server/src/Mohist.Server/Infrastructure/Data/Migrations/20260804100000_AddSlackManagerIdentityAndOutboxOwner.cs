using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260804100000_AddSlackManagerIdentityAndOutboxOwner")]
public partial class AddSlackManagerIdentityAndOutboxOwner : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ManagerAppId",
            table: "SlackWorkspaceEnrollments",
            type: "TEXT",
            maxLength: 256,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "ManagerBotUserId",
            table: "SlackWorkspaceEnrollments",
            type: "TEXT",
            maxLength: 256,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "ManagerTransportKind",
            table: "SlackWorkspaceEnrollments",
            type: "TEXT",
            maxLength: 32,
            nullable: false,
            defaultValue: "socket");

        migrationBuilder.AddColumn<string>(
            name: "ManagerReadiness",
            table: "SlackWorkspaceEnrollments",
            type: "TEXT",
            maxLength: 32,
            nullable: false,
            defaultValue: "unknown");

        migrationBuilder.AddColumn<string>(
            name: "ManagerActorId",
            table: "SlackWorkspaceEnrollments",
            type: "TEXT",
            maxLength: 256,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "ClaimedSlackUserId",
            table: "SlackWorkspaceEnrollments",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ManagerClaimHash",
            table: "SlackWorkspaceEnrollments",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ManagerClaimIssuedAt",
            table: "SlackWorkspaceEnrollments",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ManagerClaimExpiresAt",
            table: "SlackWorkspaceEnrollments",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ManagerClaimConsumedAt",
            table: "SlackWorkspaceEnrollments",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "OwnerKind",
            table: "SlackOutboxRows",
            type: "TEXT",
            maxLength: 32,
            nullable: false,
            defaultValue: "connection");

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
                    CONSTRAINT "CK_SlackWorkspaceEnrollments_Lifecycle" CHECK ("Lifecycle" IN ('active', 'disabled', 'removed')),
                    CONSTRAINT "CK_SlackWorkspaceEnrollments_ManagerCapability" CHECK ("ManagerCapability" IN ('unknown', 'available', 'unauthorized', 'capacity_limited')),
                    CONSTRAINT "CK_SlackWorkspaceEnrollments_ManagerTransportKind" CHECK ("ManagerTransportKind" IN ('socket', 'https')),
                    CONSTRAINT "CK_SlackWorkspaceEnrollments_ManagerReadiness" CHECK ("ManagerReadiness" IN ('unknown', 'ready', 'not_ready', 'degraded'))
                );
                INSERT INTO "ef_temp_SlackWorkspaceEnrollments" ("Id", "WorkspaceTeamId", "Lifecycle", "ManagerCapability", "CapabilityReason", "LastVerifiedAt", "PlanCode", "ManagedAppLimit", "ManagerCredentialRef", "AuditJson", "CreatedAt", "UpdatedAt", "DeletedAt", "ManagerAppId", "ManagerBotUserId", "ManagerTransportKind", "ManagerReadiness", "ManagerActorId", "ClaimedSlackUserId", "ManagerClaimHash", "ManagerClaimIssuedAt", "ManagerClaimExpiresAt", "ManagerClaimConsumedAt")
                    SELECT "Id", "WorkspaceTeamId", "Lifecycle", "ManagerCapability", "CapabilityReason", "LastVerifiedAt", "PlanCode", "ManagedAppLimit", "ManagerCredentialRef", "AuditJson", "CreatedAt", "UpdatedAt", "DeletedAt", "ManagerAppId", "ManagerBotUserId", "ManagerTransportKind", "ManagerReadiness", "ManagerActorId", "ClaimedSlackUserId", "ManagerClaimHash", "ManagerClaimIssuedAt", "ManagerClaimExpiresAt", "ManagerClaimConsumedAt"
                    FROM "SlackWorkspaceEnrollments";
                DROP TABLE "SlackWorkspaceEnrollments";
                ALTER TABLE "ef_temp_SlackWorkspaceEnrollments" RENAME TO "SlackWorkspaceEnrollments";
                CREATE INDEX "IX_SlackWorkspaceEnrollments_Lifecycle_UpdatedAt" ON "SlackWorkspaceEnrollments" ("Lifecycle", "UpdatedAt");
                CREATE UNIQUE INDEX "UX_SlackWorkspaceEnrollments_WorkspaceTeamId" ON "SlackWorkspaceEnrollments" ("WorkspaceTeamId") WHERE "DeletedAt" IS NULL AND "Lifecycle" = 'active';
                PRAGMA foreign_keys=ON;
                """,
                suppressTransaction: true);
        }
        else
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_SlackWorkspaceEnrollments_ManagerTransportKind",
                table: "SlackWorkspaceEnrollments",
                sql: "\"ManagerTransportKind\" IN ('socket', 'https')");
            migrationBuilder.AddCheckConstraint(
                name: "CK_SlackWorkspaceEnrollments_ManagerReadiness",
                table: "SlackWorkspaceEnrollments",
                sql: "\"ManagerReadiness\" IN ('unknown', 'ready', 'not_ready', 'degraded')");
            migrationBuilder.AddCheckConstraint(
                name: "CK_SlackOutboxRows_OwnerKind",
                table: "SlackOutboxRows",
                sql: "\"OwnerKind\" IN ('connection', 'manager')");
        }

        migrationBuilder.DropIndex(
            name: "UX_SlackOutboxRows_ConnectionId_DispatchRef_Kind",
            table: "SlackOutboxRows");
        migrationBuilder.DropIndex(
            name: "IX_SlackOutboxRows_ConnectionId_State_ClaimedAt",
            table: "SlackOutboxRows");
        migrationBuilder.DropIndex(
            name: "IX_SlackOutboxRows_ConnectionId_State_DeliveryUncertainAt",
            table: "SlackOutboxRows");
        migrationBuilder.DropIndex(
            name: "IX_SlackOutboxRows_ConnectionId_State_NextAttemptAt",
            table: "SlackOutboxRows");
        migrationBuilder.DropIndex(
            name: "IX_SlackOutboxRows_ProjectId_ConnectionId_State",
            table: "SlackOutboxRows");
        migrationBuilder.DropIndex(
            name: "IX_SlackOutboxRows_ConnectionId_DispatchRef_Kind_State",
            table: "SlackOutboxRows");

        if (migrationBuilder.ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            migrationBuilder.Sql(
                """
                PRAGMA foreign_keys=OFF;
                CREATE TABLE "ef_temp_SlackOutboxRows" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_SlackOutboxRows" PRIMARY KEY,
                    "ProjectId" TEXT NOT NULL,
                    "ConnectionId" TEXT NOT NULL,
                    "WorkspaceTeamId" TEXT NOT NULL,
                    "ConversationId" TEXT NOT NULL,
                    "ThreadTs" TEXT NULL,
                    "Kind" TEXT NOT NULL,
                    "State" TEXT NOT NULL,
                    "DispatchRef" TEXT NULL,
                    "PayloadJson" TEXT NOT NULL,
                    "AttemptCount" INTEGER NOT NULL,
                    "NextAttemptAt" TEXT NULL,
                    "ClaimedAt" TEXT NULL,
                    "ClaimedByAdapterId" TEXT NULL,
                    "DeliveredAt" TEXT NULL,
                    "DeliveryUncertainAt" TEXT NULL,
                    "DeadLetteredAt" TEXT NULL,
                    "LastError" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    "OwnerKind" TEXT NOT NULL,
                    CONSTRAINT "CK_SlackOutboxRows_Kind" CHECK ("Kind" IN ('replaceable_progress', 'terminal_result', 'explicit_failure', 'user_action')),
                    CONSTRAINT "CK_SlackOutboxRows_State" CHECK ("State" IN ('pending', 'claimed', 'delivered', 'delivery_uncertain', 'dead_lettered')),
                    CONSTRAINT "CK_SlackOutboxRows_OwnerKind" CHECK ("OwnerKind" IN ('connection', 'manager'))
                );
                INSERT INTO "ef_temp_SlackOutboxRows" ("Id", "ProjectId", "ConnectionId", "WorkspaceTeamId", "ConversationId", "ThreadTs", "Kind", "State", "DispatchRef", "PayloadJson", "AttemptCount", "NextAttemptAt", "ClaimedAt", "ClaimedByAdapterId", "DeliveredAt", "DeliveryUncertainAt", "DeadLetteredAt", "LastError", "CreatedAt", "UpdatedAt", "OwnerKind")
                    SELECT "Id", "ProjectId", "ConnectionId", "WorkspaceTeamId", "ConversationId", "ThreadTs", "Kind", "State", "DispatchRef", "PayloadJson", "AttemptCount", "NextAttemptAt", "ClaimedAt", "ClaimedByAdapterId", "DeliveredAt", "DeliveryUncertainAt", "DeadLetteredAt", "LastError", "CreatedAt", "UpdatedAt", "OwnerKind"
                    FROM "SlackOutboxRows";
                DROP TABLE "SlackOutboxRows";
                ALTER TABLE "ef_temp_SlackOutboxRows" RENAME TO "SlackOutboxRows";
                PRAGMA foreign_keys=ON;
                """,
                suppressTransaction: true);
        }

        migrationBuilder.CreateIndex(
            name: "UX_SlackOutboxRows_OwnerKind_ConnectionId_DispatchRef_Kind",
            table: "SlackOutboxRows",
            columns: new[] { "OwnerKind", "ConnectionId", "DispatchRef", "Kind" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_SlackOutboxRows_ConnectionId_State_ClaimedAt",
            table: "SlackOutboxRows",
            columns: new[] { "OwnerKind", "ConnectionId", "State", "ClaimedAt" });
        migrationBuilder.CreateIndex(
            name: "IX_SlackOutboxRows_ConnectionId_State_DeliveryUncertainAt",
            table: "SlackOutboxRows",
            columns: new[] { "OwnerKind", "ConnectionId", "State", "DeliveryUncertainAt" });
        migrationBuilder.CreateIndex(
            name: "IX_SlackOutboxRows_ConnectionId_State_NextAttemptAt",
            table: "SlackOutboxRows",
            columns: new[] { "OwnerKind", "ConnectionId", "State", "NextAttemptAt" });
        migrationBuilder.CreateIndex(
            name: "IX_SlackOutboxRows_ProjectId_ConnectionId_State",
            table: "SlackOutboxRows",
            columns: new[] { "OwnerKind", "ProjectId", "ConnectionId", "State" });
        migrationBuilder.CreateIndex(
            name: "IX_SlackOutboxRows_ConnectionId_DispatchRef_Kind_State",
            table: "SlackOutboxRows",
            columns: new[] { "OwnerKind", "ConnectionId", "DispatchRef", "Kind", "State" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "UX_SlackOutboxRows_OwnerKind_ConnectionId_DispatchRef_Kind",
            table: "SlackOutboxRows");
        migrationBuilder.DropIndex(
            name: "IX_SlackOutboxRows_ConnectionId_State_ClaimedAt",
            table: "SlackOutboxRows");
        migrationBuilder.DropIndex(
            name: "IX_SlackOutboxRows_ConnectionId_State_DeliveryUncertainAt",
            table: "SlackOutboxRows");
        migrationBuilder.DropIndex(
            name: "IX_SlackOutboxRows_ConnectionId_State_NextAttemptAt",
            table: "SlackOutboxRows");
        migrationBuilder.DropIndex(
            name: "IX_SlackOutboxRows_ProjectId_ConnectionId_State",
            table: "SlackOutboxRows");
        migrationBuilder.DropIndex(
            name: "IX_SlackOutboxRows_ConnectionId_DispatchRef_Kind_State",
            table: "SlackOutboxRows");

        if (migrationBuilder.ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            migrationBuilder.Sql(
                """
                PRAGMA foreign_keys=OFF;
                CREATE TABLE "ef_temp_SlackOutboxRows" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_SlackOutboxRows" PRIMARY KEY,
                    "ProjectId" TEXT NOT NULL,
                    "ConnectionId" TEXT NOT NULL,
                    "WorkspaceTeamId" TEXT NOT NULL,
                    "ConversationId" TEXT NOT NULL,
                    "ThreadTs" TEXT NULL,
                    "Kind" TEXT NOT NULL,
                    "State" TEXT NOT NULL,
                    "DispatchRef" TEXT NULL,
                    "PayloadJson" TEXT NOT NULL,
                    "AttemptCount" INTEGER NOT NULL,
                    "NextAttemptAt" TEXT NULL,
                    "ClaimedAt" TEXT NULL,
                    "ClaimedByAdapterId" TEXT NULL,
                    "DeliveredAt" TEXT NULL,
                    "DeliveryUncertainAt" TEXT NULL,
                    "DeadLetteredAt" TEXT NULL,
                    "LastError" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    CONSTRAINT "CK_SlackOutboxRows_Kind" CHECK ("Kind" IN ('replaceable_progress', 'terminal_result', 'explicit_failure', 'user_action')),
                    CONSTRAINT "CK_SlackOutboxRows_State" CHECK ("State" IN ('pending', 'claimed', 'delivered', 'delivery_uncertain', 'dead_lettered'))
                );
                INSERT INTO "ef_temp_SlackOutboxRows" ("Id", "ProjectId", "ConnectionId", "WorkspaceTeamId", "ConversationId", "ThreadTs", "Kind", "State", "DispatchRef", "PayloadJson", "AttemptCount", "NextAttemptAt", "ClaimedAt", "ClaimedByAdapterId", "DeliveredAt", "DeliveryUncertainAt", "DeadLetteredAt", "LastError", "CreatedAt", "UpdatedAt")
                    SELECT "Id", "ProjectId", "ConnectionId", "WorkspaceTeamId", "ConversationId", "ThreadTs", "Kind", "State", "DispatchRef", "PayloadJson", "AttemptCount", "NextAttemptAt", "ClaimedAt", "ClaimedByAdapterId", "DeliveredAt", "DeliveryUncertainAt", "DeadLetteredAt", "LastError", "CreatedAt", "UpdatedAt"
                    FROM "SlackOutboxRows";
                DROP TABLE "SlackOutboxRows";
                ALTER TABLE "ef_temp_SlackOutboxRows" RENAME TO "SlackOutboxRows";
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
        }
        else
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SlackOutboxRows_OwnerKind",
                table: "SlackOutboxRows");
            migrationBuilder.DropCheckConstraint(
                name: "CK_SlackWorkspaceEnrollments_ManagerReadiness",
                table: "SlackWorkspaceEnrollments");
            migrationBuilder.DropCheckConstraint(
                name: "CK_SlackWorkspaceEnrollments_ManagerTransportKind",
                table: "SlackWorkspaceEnrollments");
            migrationBuilder.DropColumn(name: "OwnerKind", table: "SlackOutboxRows");
            migrationBuilder.DropColumn(name: "ManagerAppId", table: "SlackWorkspaceEnrollments");
            migrationBuilder.DropColumn(name: "ManagerBotUserId", table: "SlackWorkspaceEnrollments");
            migrationBuilder.DropColumn(name: "ManagerTransportKind", table: "SlackWorkspaceEnrollments");
            migrationBuilder.DropColumn(name: "ManagerReadiness", table: "SlackWorkspaceEnrollments");
            migrationBuilder.DropColumn(name: "ManagerActorId", table: "SlackWorkspaceEnrollments");
            migrationBuilder.DropColumn(name: "ClaimedSlackUserId", table: "SlackWorkspaceEnrollments");
            migrationBuilder.DropColumn(name: "ManagerClaimHash", table: "SlackWorkspaceEnrollments");
            migrationBuilder.DropColumn(name: "ManagerClaimIssuedAt", table: "SlackWorkspaceEnrollments");
            migrationBuilder.DropColumn(name: "ManagerClaimExpiresAt", table: "SlackWorkspaceEnrollments");
            migrationBuilder.DropColumn(name: "ManagerClaimConsumedAt", table: "SlackWorkspaceEnrollments");
        }

        migrationBuilder.CreateIndex(
            name: "UX_SlackOutboxRows_ConnectionId_DispatchRef_Kind",
            table: "SlackOutboxRows",
            columns: new[] { "ConnectionId", "DispatchRef", "Kind" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_SlackOutboxRows_ConnectionId_State_ClaimedAt",
            table: "SlackOutboxRows",
            columns: new[] { "ConnectionId", "State", "ClaimedAt" });
        migrationBuilder.CreateIndex(
            name: "IX_SlackOutboxRows_ConnectionId_State_DeliveryUncertainAt",
            table: "SlackOutboxRows",
            columns: new[] { "ConnectionId", "State", "DeliveryUncertainAt" });
        migrationBuilder.CreateIndex(
            name: "IX_SlackOutboxRows_ConnectionId_State_NextAttemptAt",
            table: "SlackOutboxRows",
            columns: new[] { "ConnectionId", "State", "NextAttemptAt" });
        migrationBuilder.CreateIndex(
            name: "IX_SlackOutboxRows_ProjectId_ConnectionId_State",
            table: "SlackOutboxRows",
            columns: new[] { "ProjectId", "ConnectionId", "State" });
        migrationBuilder.CreateIndex(
            name: "IX_SlackOutboxRows_ConnectionId_DispatchRef_Kind_State",
            table: "SlackOutboxRows",
            columns: new[] { "ConnectionId", "DispatchRef", "Kind", "State" });
    }
}
