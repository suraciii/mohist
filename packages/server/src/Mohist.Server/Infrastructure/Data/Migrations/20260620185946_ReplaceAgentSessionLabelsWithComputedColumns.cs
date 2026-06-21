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

            // SQLite refuses ALTER TABLE ADD COLUMN ... STORED, so add each label
            // column as a plain nullable string, then AlterColumn to the STORED
            // computed definition — AlterColumn triggers EF Core's automatic
            // SQLite table rebuild, which emits CREATE TABLE ... AS (...) STORED.
            var labelColumns = new (string Name, string Expression)[]
            {
                ("LabelIssueNumber", "json_extract(\"State\", '$.metadata.labels.\"mohist.io/issue-number\"')"),
                ("LabelProjectId",   "json_extract(\"State\", '$.metadata.labels.\"mohist.io/project-id\"')"),
                ("LabelSessionName", "json_extract(\"State\", '$.metadata.labels.\"mohist.io/session-name\"')"),
                ("LabelSourceId",    "json_extract(\"State\", '$.metadata.labels.\"mohist.io/source-id\"')"),
                ("LabelSourceKind",  "json_extract(\"State\", '$.metadata.labels.\"mohist.io/source-kind\"')"),
                ("LabelStage",       "json_extract(\"State\", '$.metadata.labels.\"mohist.io/stage\"')"),
                ("LabelWorkId",      "json_extract(\"State\", '$.metadata.labels.\"mohist.io/work-id\"')"),
                ("LabelWorkType",    "json_extract(\"State\", '$.metadata.labels.\"mohist.io/work-type\"')"),
            };

            foreach (var (name, _) in labelColumns)
            {
                migrationBuilder.AddColumn<string>(
                    name: name,
                    table: "AgentSessions",
                    type: "TEXT",
                    nullable: true);
            }

            foreach (var (name, expression) in labelColumns)
            {
                migrationBuilder.AlterColumn<string>(
                    name: name,
                    table: "AgentSessions",
                    type: "TEXT",
                    nullable: true,
                    computedColumnSql: expression,
                    stored: true,
                    oldClrType: typeof(string),
                    oldType: "TEXT",
                    oldNullable: true);
            }

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
