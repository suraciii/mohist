using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddWorkflowRunWorkProjection : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ActiveWorkId",
            table: "WorkflowRuns",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ActiveWorkerId",
            table: "WorkflowRuns",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "WorkflowRunTaskMap",
            columns: table => new
            {
                WorkflowRunId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                TaskId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                WorkId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WorkflowRunTaskMap", x => new { x.WorkflowRunId, x.TaskId });
                table.ForeignKey(
                    name: "FK_WorkflowRunTaskMap_WorkflowRuns_WorkflowRunId",
                    column: x => x.WorkflowRunId,
                    principalTable: "WorkflowRuns",
                    principalColumn: "WorkflowRunId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_WorkflowRunTaskMap_WorkflowRunId_TaskId",
            table: "WorkflowRunTaskMap",
            columns: new[] { "WorkflowRunId", "TaskId" });

        migrationBuilder.CreateIndex(
            name: "IX_WorkflowRunTaskMap_WorkflowRunId_WorkId",
            table: "WorkflowRunTaskMap",
            columns: new[] { "WorkflowRunId", "WorkId" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "WorkflowRunTaskMap");

        migrationBuilder.DropColumn(
            name: "ActiveWorkId",
            table: "WorkflowRuns");

        migrationBuilder.DropColumn(
            name: "ActiveWorkerId",
            table: "WorkflowRuns");
    }
}
