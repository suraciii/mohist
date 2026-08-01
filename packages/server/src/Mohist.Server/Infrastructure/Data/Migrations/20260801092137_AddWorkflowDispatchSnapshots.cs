using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddWorkflowDispatchSnapshots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "WorkflowDispatchSnapshots",
            columns: table => new
            {
                WorkflowRunId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                WorkId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                SnapshotJson = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WorkflowDispatchSnapshots", x => new { x.WorkflowRunId, x.WorkId });
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "WorkflowDispatchSnapshots");
    }
}
