using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration(MigrationId)]
public partial class AddWorkflowProfileRevision : Migration
{
    public const string MigrationId = "20260926000000_AddWorkflowProfileRevision";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Revision",
            table: "WorkflowProfileRecords",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        // Existing rows get a random token per row: callers can then read a
        // revision and use it as an edit precondition, and no later write can
        // revive one of these tokens. Deterministic backfill (row content or
        // identity) would let a change-away-and-back edit revalidate a stale
        // precondition, so the value must be non-repeating.
        migrationBuilder.Sql(
            "UPDATE WorkflowProfileRecords SET Revision = lower(hex(randomblob(16))) WHERE Revision IS NULL OR Revision = ''");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "Revision", table: "WorkflowProfileRecords");
    }
}
