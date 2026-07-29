using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddSlackOwnerClaimCodes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SlackOwnerClaimCodes",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                CodeHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UsedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                SupersededBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_SlackOwnerClaimCodes", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "UX_SlackOwnerClaimCodes_ProjectId_ConnectionId_CodeHash",
            table: "SlackOwnerClaimCodes",
            columns: new[] { "ProjectId", "ConnectionId", "CodeHash" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_SlackOwnerClaimCodes_ProjectId_ConnectionId_UsedAt_SupersededBy",
            table: "SlackOwnerClaimCodes",
            columns: new[] { "ProjectId", "ConnectionId", "UsedAt", "SupersededBy" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("SlackOwnerClaimCodes");
}
