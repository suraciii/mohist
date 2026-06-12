using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    public partial class DropAgentSessionTranscriptSegments : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentSessionTranscriptSegments");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentSessionTranscriptSegments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    WorkflowRunId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SessionName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AgentSessionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    WorkId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    WorkType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RawEventCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_AgentSessionTranscriptSegments", x => x.Id));

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionTranscriptSegments_ProjectId_IssueNumber_Id",
                table: "AgentSessionTranscriptSegments",
                columns: new[] { "ProjectId", "IssueNumber", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionTranscriptSegments_SessionId_Kind_CorrelationId",
                table: "AgentSessionTranscriptSegments",
                columns: new[] { "SessionId", "Kind", "CorrelationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionTranscriptSegments_SessionId_Sequence",
                table: "AgentSessionTranscriptSegments",
                columns: new[] { "SessionId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionTranscriptSegments_WorkflowRunId_SessionName_Sequence",
                table: "AgentSessionTranscriptSegments",
                columns: new[] { "WorkflowRunId", "SessionName", "Sequence" });
        }
    }
}
