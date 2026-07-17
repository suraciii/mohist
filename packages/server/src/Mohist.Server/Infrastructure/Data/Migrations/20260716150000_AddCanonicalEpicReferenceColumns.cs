using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260716150000_AddCanonicalEpicReferenceColumns")]
public partial class AddCanonicalEpicReferenceColumns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "EpicNumber",
            table: "EpicIssues",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "EpicNumber",
            table: "EpicActiveIssues",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "EpicNumber",
            table: "Issues",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "EpicNumber",
            table: "WorkflowRuns",
            type: "INTEGER",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "EpicNumber", table: "WorkflowRuns");
        migrationBuilder.DropColumn(name: "EpicNumber", table: "Issues");
        migrationBuilder.DropColumn(name: "EpicNumber", table: "EpicActiveIssues");
        migrationBuilder.DropColumn(name: "EpicNumber", table: "EpicIssues");
    }
}
