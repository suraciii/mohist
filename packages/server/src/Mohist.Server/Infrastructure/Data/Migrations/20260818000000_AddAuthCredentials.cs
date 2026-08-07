using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

/// <summary>
/// Issued credentials: the table stores only SHA-256 token hashes; the
/// full value appears once at issuance. Runner/integration binding
/// columns are added by their first consumers (#321 / #324).
/// </summary>
[DbContext(typeof(MohistDbContext))]
[Migration("20260818000000_AddAuthCredentials")]
public partial class AddAuthCredentials : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Credentials",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                PrincipalId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ScopesJson = table.Column<string>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                RevokedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Credentials", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Credentials_TokenHash",
            table: "Credentials",
            column: "TokenHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Credentials_PrincipalId_Name",
            table: "Credentials",
            columns: new[] { "PrincipalId", "Name" },
            filter: "\"RevokedAt\" IS NULL");

        migrationBuilder.CreateIndex(
            name: "IX_Credentials_PrincipalId_Kind_RevokedAt",
            table: "Credentials",
            columns: new[] { "PrincipalId", "Kind", "RevokedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Credentials");
    }
}
