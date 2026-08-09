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
        migrationBuilder.DropIndex(
            name: "UX_RoutingRules_ProjectId_IdempotencyKey",
            table: "RoutingRules");

        migrationBuilder.DropColumn(
            name: "IdempotencyKey",
            table: "RoutingRules");
    }
}
