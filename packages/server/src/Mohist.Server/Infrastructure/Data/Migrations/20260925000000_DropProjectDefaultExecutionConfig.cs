using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

/// <summary>
/// Drops the Project default execution configuration. Execution resolves
/// only from an accepted caller hint and the Agent definition, so the
/// persisted Project selection has no remaining reader or writer.
/// </summary>
[DbContext(typeof(MohistDbContext))]
[Migration(MigrationId)]
public partial class DropProjectDefaultExecutionConfig : Migration
{
    public const string MigrationId = "20260925000000_DropProjectDefaultExecutionConfig";

    protected override void Up(MigrationBuilder migrationBuilder) =>
        // SQLite's EF migration generator rejects DropColumnOperation and
        // demands a table rebuild; the provider's SQLite build supports
        // ALTER TABLE ... DROP COLUMN directly, so the one statement works on
        // both SQLite and Postgres.
        migrationBuilder.Sql("ALTER TABLE \"Projects\" DROP COLUMN \"DefaultExecutionConfigJson\";");

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "DefaultExecutionConfigJson",
            table: "Projects",
            type: "TEXT",
            nullable: true);
    }
}
