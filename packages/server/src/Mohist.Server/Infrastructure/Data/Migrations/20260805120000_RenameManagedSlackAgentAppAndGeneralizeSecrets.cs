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
        migrationBuilder.CreateIndex(
            name: "IX_ManagedSlackAgentApps_AgentConnectionId",
            table: "ManagedSlackAgentApps",
            column: "AgentConnectionId");
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
                    "\"Kind\" IN ('appToken', 'botToken', 'webhookSecret', 'clientSecret', 'signingSecret', 'configurationAccessToken', 'configurationRefreshToken')");
                table.CheckConstraint(
                    "CK_StoredSecrets_OwnerKindKind",
                    "(\"OwnerKind\" = 'agent_connection' AND \"Kind\" IN ('appToken', 'botToken')) OR " +
                    "(\"OwnerKind\" = 'webhook_subscription' AND \"Kind\" = 'webhookSecret') OR " +
                    "(\"OwnerKind\" = 'slack_workspace_enrollment' AND \"Kind\" IN ('configurationAccessToken', 'configurationRefreshToken', 'appToken', 'botToken', 'clientSecret', 'signingSecret')) OR " +
                    "(\"OwnerKind\" = 'managed_slack_agent_app' AND \"Kind\" IN ('appToken', 'botToken', 'clientSecret', 'signingSecret'))");
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
            name: "IX_ManagedSlackAgentApps_AgentConnectionId",
            table: "ManagedSlackAgentApps");
        migrationBuilder.DropIndex(
            name: "UX_ManagedSlackAgentApps_AgentConnectionId",
            table: "ManagedSlackAgentApps");
        migrationBuilder.DropIndex(
            name: "UX_ManagedSlackAgentApps_WorkspaceTeamId_AppId",
            table: "ManagedSlackAgentApps");
        migrationBuilder.DropIndex(
            name: "IX_ManagedSlackAgentApps_EnrollmentId_UpdatedAt",
            table: "ManagedSlackAgentApps");
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
}
