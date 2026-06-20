using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceAgentSessionLabelsWithComputedColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentSessionLabels");

            migrationBuilder.AddColumn<string>(
                name: "LabelIssueNumber",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "json_extract(\"State\", '$.Metadata.Labels.\"mohist.io/issue-number\"')",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "LabelProjectId",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "json_extract(\"State\", '$.Metadata.Labels.\"mohist.io/project-id\"')",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "LabelSessionName",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "json_extract(\"State\", '$.Metadata.Labels.\"mohist.io/session-name\"')",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "LabelSourceId",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "json_extract(\"State\", '$.Metadata.Labels.\"mohist.io/source-id\"')",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "LabelSourceKind",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "json_extract(\"State\", '$.Metadata.Labels.\"mohist.io/source-kind\"')",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "LabelStage",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "json_extract(\"State\", '$.Metadata.Labels.\"mohist.io/stage\"')",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "LabelWorkId",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "json_extract(\"State\", '$.Metadata.Labels.\"mohist.io/work-id\"')",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "LabelWorkType",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "json_extract(\"State\", '$.Metadata.Labels.\"mohist.io/work-type\"')",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_LabelProjectId_CreatedAt",
                table: "AgentSessions",
                columns: new[] { "LabelProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_LabelSourceId",
                table: "AgentSessions",
                column: "LabelSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_LabelSourceId_LabelSessionName",
                table: "AgentSessions",
                columns: new[] { "LabelSourceId", "LabelSessionName" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentSessions_LabelProjectId_CreatedAt",
                table: "AgentSessions");

            migrationBuilder.DropIndex(
                name: "IX_AgentSessions_LabelSourceId",
                table: "AgentSessions");

            migrationBuilder.DropIndex(
                name: "IX_AgentSessions_LabelSourceId_LabelSessionName",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "LabelIssueNumber",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "LabelProjectId",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "LabelSessionName",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "LabelSourceId",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "LabelSourceKind",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "LabelStage",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "LabelWorkId",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "LabelWorkType",
                table: "AgentSessions");

            migrationBuilder.CreateTable(
                name: "AgentSessionLabels",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSessionLabels", x => new { x.SessionId, x.Key });
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionLabels_Key_Value_SessionId",
                table: "AgentSessionLabels",
                columns: new[] { "Key", "Value", "SessionId" });
        }
    }
}
