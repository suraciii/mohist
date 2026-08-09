using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using System;

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

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException(
            "RoutingRule idempotency facts cannot be represented after removing IdempotencyKey.");
}
