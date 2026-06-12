using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    public partial class AddAgentSessionTranscriptTurnsParts : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentSessionTranscriptTurns",
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
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    PromptText = table.Column<string>(type: "TEXT", nullable: false),
                    PromptKind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_AgentSessionTranscriptTurns", x => x.Id));

            migrationBuilder.CreateTable(
                name: "AgentSessionTranscriptParts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TurnId = table.Column<long>(type: "INTEGER", nullable: false),
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
                    Type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CorrelationKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RawEventCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_AgentSessionTranscriptParts", x => x.Id));

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionTranscriptTurns_SessionId_Sequence",
                table: "AgentSessionTranscriptTurns",
                columns: new[] { "SessionId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionTranscriptTurns_WorkflowRunId_SessionName_Sequence",
                table: "AgentSessionTranscriptTurns",
                columns: new[] { "WorkflowRunId", "SessionName", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionTranscriptParts_TurnId_Sequence",
                table: "AgentSessionTranscriptParts",
                columns: new[] { "TurnId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionTranscriptParts_TurnId_Type_CorrelationKey",
                table: "AgentSessionTranscriptParts",
                columns: new[] { "TurnId", "Type", "CorrelationKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionTranscriptParts_SessionId_Sequence",
                table: "AgentSessionTranscriptParts",
                columns: new[] { "SessionId", "Sequence" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AgentSessionTranscriptParts");
            migrationBuilder.DropTable(name: "AgentSessionTranscriptTurns");
        }
    }
}
