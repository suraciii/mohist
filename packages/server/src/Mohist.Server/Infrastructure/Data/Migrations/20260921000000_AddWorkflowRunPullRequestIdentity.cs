using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddWorkflowRunPullRequestIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "PullRequestNumber",
            table: "WorkflowRuns",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_WorkflowRuns_ProjectId_PullRequestNumber",
            table: "WorkflowRuns",
            columns: new[] { "MetadataProjectId", "PullRequestNumber" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_WorkflowRuns_ProjectId_PullRequestNumber",
            table: "WorkflowRuns");

        migrationBuilder.DropColumn(
            name: "PullRequestNumber",
            table: "WorkflowRuns");
    }
}
