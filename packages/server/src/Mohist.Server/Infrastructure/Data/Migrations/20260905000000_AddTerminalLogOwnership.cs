using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddTerminalLogOwnership : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TerminalLogOwnerships",
            columns: table => new
            {
                OwnerKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                OwnerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                WorkId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                RunnerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TerminalLogOwnerships", x => new { x.OwnerKind, x.OwnerId, x.WorkId });
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "TerminalLogOwnerships");
    }
}
