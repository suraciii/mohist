using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddRoutingRules : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "RoutingRules",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Position = table.Column<int>(type: "INTEGER", nullable: false),
                Match = table.Column<string>(type: "TEXT", nullable: false),
                AgentId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ResponsePrompt = table.Column<string>(type: "TEXT", nullable: false),
                Continue = table.Column<bool>(type: "INTEGER", nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_RoutingRules", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "UX_RoutingRules_ProjectId_Name",
            table: "RoutingRules",
            columns: new[] { "ProjectId", "Name" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_RoutingRules_ProjectId_Position",
            table: "RoutingRules",
            columns: new[] { "ProjectId", "Position" });
        migrationBuilder.CreateIndex(
            name: "IX_RoutingRules_ProjectId",
            table: "RoutingRules",
            column: "ProjectId");
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "RoutingRules");
}
