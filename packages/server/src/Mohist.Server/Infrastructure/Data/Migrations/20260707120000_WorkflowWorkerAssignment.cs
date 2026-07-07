using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    [DbContext(typeof(Db.MohistDbContext))]
    [Migration("20260707120000_WorkflowWorkerAssignment")]
    public partial class WorkflowWorkerAssignment : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_Status_ReadySince",
                table: "WorkflowRuns");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_Status",
                table: "WorkflowRuns");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_MetadataProjectId_AssignedRunnerId_CreatedAt",
                table: "WorkflowRuns");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_AssignedRunnerId",
                table: "WorkflowRuns");

            migrationBuilder.AddColumn<string>(
                name: "AssignedWorkerId",
                table: "WorkflowRuns",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "COALESCE(json_extract(State, '$.assignment.workerId'), json_extract(State, '$.assignment.runnerId'), json_extract(State, '$.claim.runnerId'))",
                stored: false);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_AssignedWorkerId",
                table: "WorkflowRuns",
                column: "AssignedWorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_MetadataProjectId_AssignedWorkerId_CreatedAt",
                table: "WorkflowRuns",
                columns: new[] { "MetadataProjectId", "AssignedWorkerId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_Status",
                table: "WorkflowRuns",
                columns: new[] { "Status", "AssignedWorkerId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_Status_ReadySince",
                table: "WorkflowRuns",
                columns: new[] { "Status", "AssignedWorkerId", "ReadySince" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_Status_ReadySince",
                table: "WorkflowRuns");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_Status",
                table: "WorkflowRuns");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_MetadataProjectId_AssignedWorkerId_CreatedAt",
                table: "WorkflowRuns");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_AssignedWorkerId",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "AssignedWorkerId",
                table: "WorkflowRuns");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_AssignedRunnerId",
                table: "WorkflowRuns",
                column: "AssignedRunnerId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_MetadataProjectId_AssignedRunnerId_CreatedAt",
                table: "WorkflowRuns",
                columns: new[] { "MetadataProjectId", "AssignedRunnerId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_Status",
                table: "WorkflowRuns",
                columns: new[] { "Status", "AssignedRunnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_Status_ReadySince",
                table: "WorkflowRuns",
                columns: new[] { "Status", "AssignedRunnerId", "ReadySince" });
        }
    }
}
