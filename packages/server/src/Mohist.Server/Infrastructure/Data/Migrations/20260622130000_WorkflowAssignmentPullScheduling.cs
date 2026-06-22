using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    [DbContext(typeof(Db.MohistDbContext))]
    [Migration("20260622130000_WorkflowAssignmentPullScheduling")]
    public partial class WorkflowAssignmentPullScheduling : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BacklogStates");

            migrationBuilder.AddColumn<string>(
                name: "AssignedRunnerId",
                table: "WorkflowRuns",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "COALESCE(json_extract(State, '$.assignment.runnerId'), json_extract(State, '$.claim.runnerId'))",
                stored: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "WorkflowRuns",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "json_extract(State, '$.metadata.createdAt')",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_AssignedRunnerId",
                table: "WorkflowRuns",
                column: "AssignedRunnerId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_MetadataProjectId_AssignedRunnerId_CreatedAt",
                table: "WorkflowRuns",
                columns: new[] { "MetadataProjectId", "AssignedRunnerId", "CreatedAt" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_AssignedRunnerId",
                table: "WorkflowRuns");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_MetadataProjectId_AssignedRunnerId_CreatedAt",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "AssignedRunnerId",
                table: "WorkflowRuns");

            migrationBuilder.CreateTable(
                name: "BacklogStates",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BacklogStates", x => x.ProjectId);
                });
        }
    }
}
