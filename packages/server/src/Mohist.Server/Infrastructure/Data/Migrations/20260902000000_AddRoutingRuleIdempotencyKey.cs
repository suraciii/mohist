using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260902000000_AddRoutingRuleIdempotencyKey")]
public partial class AddRoutingRuleIdempotencyKey : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "UX_RoutingRules_ProjectId_Name",
            table: "RoutingRules");

        migrationBuilder.CreateIndex(
            name: "UX_RoutingRules_ProjectId_Name",
            table: "RoutingRules",
            columns: new[] { "ProjectId", "Name" },
            unique: true,
            filter: "\"Status\" <> 'deleted'");

        migrationBuilder.AddColumn<string>(
            name: "IdempotencyKey",
            table: "RoutingRules",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "UX_RoutingRules_ProjectId_IdempotencyKey",
            table: "RoutingRules",
            columns: new[] { "ProjectId", "IdempotencyKey" },
            unique: true,
            filter: "\"IdempotencyKey\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
            // SQLite only permits RAISE() inside a trigger. Keep the guard and its
            // no-op update in one migration operation so it runs before destructive
            // rollback SQL executes.
        migrationBuilder.Sql(
            """
            CREATE TEMP TRIGGER "__Mohist_RoutingRuleIdempotencyRollbackGuard"
            BEFORE UPDATE OF "IdempotencyKey" ON main."RoutingRules"
            BEGIN
                SELECT RAISE(ABORT, 'RoutingRule idempotency facts cannot be represented after removing IdempotencyKey.')
                WHERE OLD."IdempotencyKey" IS NOT NULL;
                SELECT RAISE(ABORT, 'RoutingRule names cannot be represented by the restored unique index.')
                WHERE EXISTS (
                    SELECT 1
                    FROM main."RoutingRules" AS duplicate
                    WHERE duplicate."ProjectId" = OLD."ProjectId"
                      AND duplicate."Name" = OLD."Name"
                      AND duplicate."Id" <> OLD."Id"
                );
            END;
            UPDATE main."RoutingRules"
            SET "IdempotencyKey" = "IdempotencyKey";
            DROP TRIGGER "__Mohist_RoutingRuleIdempotencyRollbackGuard";
            """);

        migrationBuilder.DropIndex(
            name: "UX_RoutingRules_ProjectId_IdempotencyKey",
            table: "RoutingRules");

        migrationBuilder.DropIndex(
            name: "UX_RoutingRules_ProjectId_Name",
            table: "RoutingRules");

        migrationBuilder.Sql(
            "ALTER TABLE \"RoutingRules\" DROP COLUMN \"IdempotencyKey\";");

        migrationBuilder.CreateIndex(
            name: "UX_RoutingRules_ProjectId_Name",
            table: "RoutingRules",
            columns: new[] { "ProjectId", "Name" },
            unique: true);
    }
}
