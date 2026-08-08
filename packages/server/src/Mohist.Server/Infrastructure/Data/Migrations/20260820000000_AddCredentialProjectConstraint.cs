using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

/// <summary>
/// Integration credentials carry the canonical id of the project they
/// are narrowed to — the credential-level project constraint the auth
/// layer evaluates per request (P1 records, P2 gates).
/// </summary>
[DbContext(typeof(MohistDbContext))]
[Migration("20260820000000_AddCredentialProjectConstraint")]
public partial class AddCredentialProjectConstraint : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ProjectId",
            table: "Credentials",
            type: "TEXT",
            maxLength: 256,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // EF Core's SQLite provider cannot reverse an AddColumn (its
        // DropColumnOperation throws during down-SQL generation), so the
        // revert drops the column with SQLite's native ALTER TABLE, which
        // the bundled engine (3.35+) supports.
        migrationBuilder.Sql("ALTER TABLE \"Credentials\" DROP COLUMN \"ProjectId\";");
    }
}
