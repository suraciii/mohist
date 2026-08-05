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
                    "\"Kind\" IN ('appToken', 'botToken', 'webhookSecret', 'clientSecret', 'signingSecret')");
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
            WHERE "OwnerKind" IN ('agent_connection', 'webhook_subscription');
            """);

        migrationBuilder.CreateIndex(
            name: "IX_ConnectionSecrets_ProjectId_ConnectionId",
            table: "ConnectionSecrets",
            columns: new[] { "ProjectId", "ConnectionId" });

        migrationBuilder.DropTable(name: "StoredSecrets");

        migrationBuilder.RenameTable(
            name: "ManagedSlackAgentApps",
            newName: "ManagedSlackChildApps");
    }
}
