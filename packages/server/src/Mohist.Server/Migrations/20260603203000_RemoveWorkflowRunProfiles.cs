using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Persistence.Db;

#nullable disable

namespace Mohist.Server.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260603203000_RemoveWorkflowRunProfiles")]
public partial class RemoveWorkflowRunProfiles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""DROP TABLE IF EXISTS "WorkflowRunProfiles";""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "WorkflowRunProfiles",
            columns: table => new
            {
                Key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                StateJson = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WorkflowRunProfiles", x => x.Key);
            });
    }
}
