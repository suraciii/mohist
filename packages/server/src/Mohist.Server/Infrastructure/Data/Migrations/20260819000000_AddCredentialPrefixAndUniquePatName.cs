using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

/// <summary>
/// PAT listing needs a display prefix (kind + first secret characters)
/// because only the SHA-256 hash is stored. Also promotes the
/// (PrincipalId, Name) index to unique on active rows: the database-level
/// backstop for the "one active credential per name per principal" rule.
/// </summary>
[DbContext(typeof(MohistDbContext))]
[Migration("20260819000000_AddCredentialPrefixAndUniquePatName")]
public partial class AddCredentialPrefixAndUniquePatName : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Prefix",
            table: "Credentials",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.DropIndex(
            name: "IX_Credentials_PrincipalId_Name",
            table: "Credentials");

        migrationBuilder.CreateIndex(
            name: "IX_Credentials_PrincipalId_Name",
            table: "Credentials",
            columns: new[] { "PrincipalId", "Name" },
            unique: true,
            filter: "\"RevokedAt\" IS NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Credentials_PrincipalId_Name",
            table: "Credentials");

        migrationBuilder.CreateIndex(
            name: "IX_Credentials_PrincipalId_Name",
            table: "Credentials",
            columns: new[] { "PrincipalId", "Name" },
            filter: "\"RevokedAt\" IS NULL");

        // EF Core's SQLite provider cannot reverse an AddColumn (its
        // DropColumnOperation throws during down-SQL generation), so the
        // revert drops the column with SQLite's native ALTER TABLE, which
        // the bundled engine (3.35+) supports.
        migrationBuilder.Sql("ALTER TABLE \"Credentials\" DROP COLUMN \"Prefix\";");
    }
}
