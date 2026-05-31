using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Persistence.Db;

#nullable disable

namespace Mohist.Server.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(MohistDbContext))]
    [Migration("20260531180600_RelaxWorkflowAgentSessionWorkIdIndex")]
    public partial class RelaxWorkflowAgentSessionWorkIdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkflowAgentSessions_WorkflowRunId_WorkId",
                table: "WorkflowAgentSessions");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowAgentSessions_WorkflowRunId_WorkId",
                table: "WorkflowAgentSessions",
                columns: new[] { "WorkflowRunId", "WorkId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkflowAgentSessions_WorkflowRunId_WorkId",
                table: "WorkflowAgentSessions");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowAgentSessions_WorkflowRunId_WorkId",
                table: "WorkflowAgentSessions",
                columns: new[] { "WorkflowRunId", "WorkId" },
                unique: true);
        }
    }
}
