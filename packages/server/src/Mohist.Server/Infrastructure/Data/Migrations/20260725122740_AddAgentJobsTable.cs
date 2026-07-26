using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentJobsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentJobs",
                columns: table => new
                {
                    JobKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.input.projectId')", stored: true),
                    AgentId = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.input.agentId')", stored: true),
                    Status = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.status')", stored: true),
                    SubmittedAt = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.submittedAt')", stored: true),
                    TerminalAt = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.terminalAt')", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentJobs", x => x.JobKey);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentJobs_AgentId_ProjectId_SubmittedAt",
                table: "AgentJobs",
                columns: new[] { "AgentId", "ProjectId", "SubmittedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentJobs");
        }
    }
}
