using Microsoft.EntityFrameworkCore.Migrations;
using System;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[Migration("20260718090000_DropAgentSubscriptions")]
public partial class DropAgentSubscriptions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "AgentSubscriptions");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("AgentSubscriptions has no compatibility rollback.");
}
