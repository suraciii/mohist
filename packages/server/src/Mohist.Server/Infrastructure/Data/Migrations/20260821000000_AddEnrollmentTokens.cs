using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

/// <summary>
/// One-time runner enrollment tokens (docs/auth.md "Runner：安装即注册").
/// Only the SHA-256 hash is stored; ConsumedAt makes consumption
/// atomic and single-use.
/// </summary>
public partial class AddEnrollmentTokens : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "EnrollmentTokens",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                ConsumedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_EnrollmentTokens", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_EnrollmentTokens_TokenHash",
            table: "EnrollmentTokens",
            column: "TokenHash",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "EnrollmentTokens");
    }
}
