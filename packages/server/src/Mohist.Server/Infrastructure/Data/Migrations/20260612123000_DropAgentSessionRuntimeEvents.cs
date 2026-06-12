using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(MohistDbContext))]
    [Migration("20260612123000_DropAgentSessionRuntimeEvents")]
    public partial class DropAgentSessionRuntimeEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentSessionRuntimeEvents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentSessionRuntimeEvents",
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
                    Type = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSessionRuntimeEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionRuntimeEvents_ProjectId_IssueNumber_Id",
                table: "AgentSessionRuntimeEvents",
                columns: new[] { "ProjectId", "IssueNumber", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionRuntimeEvents_SessionId_Sequence",
                table: "AgentSessionRuntimeEvents",
                columns: new[] { "SessionId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionRuntimeEvents_WorkflowRunId_SessionName_Sequence",
                table: "AgentSessionRuntimeEvents",
                columns: new[] { "WorkflowRunId", "SessionName", "Sequence" });
        }
    }
}
