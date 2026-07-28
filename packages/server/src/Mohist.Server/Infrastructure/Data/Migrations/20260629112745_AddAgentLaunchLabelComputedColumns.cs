using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Make the direct-Agent (agent-launch) labels
    /// queryable. Adds six STORED computed columns to
    /// <c>AgentSessions</c> mirroring the established
    /// <c>json_extract("State", '$.metadata.labels."…')'</c> pattern,
    /// plus three indexes:
    /// <list type="bullet">
    ///   <item>composite <c>(LabelAgentId, LabelProjectId, CreatedAt)</c>
    ///     for the agent-scoped recency list,</item>
    ///   <item>single-column <c>LabelAgentLaunchIssueNumber</c> and
    ///     <c>LabelAgentLaunchEpicNumber</c> for the issue/epic
    ///     association reads.</item>
    /// </list>
    /// On SQLite EF Core cannot <c>ALTER TABLE ADD COLUMN ... STORED</c>.
    /// Add each label column as a plain nullable string first, then
    /// <c>AlterColumn</c> it to the STORED computed definition so the
    /// provider emits its automatic table rebuild. This mirrors
    /// <c>ReplaceAgentSessionLabelsWithComputedColumns</c>. All column
    /// values derive from the existing <c>State</c> JSON — no data backfill.
    /// </summary>
    public partial class AddAgentLaunchLabelComputedColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var labelColumns = new (string Name, string Expression)[]
            {
                ("LabelAgentId", "json_extract(\"State\", '$.metadata.labels.\"mohist.io/agent-id\"')"),
                ("LabelAgentLaunchEpicNumber", "json_extract(\"State\", '$.metadata.labels.\"mohist.io/agent-launch/epic-number\"')"),
                ("LabelAgentLaunchIssueNumber", "json_extract(\"State\", '$.metadata.labels.\"mohist.io/agent-launch/issue-number\"')"),
                ("LabelAgentLaunchRepository", "json_extract(\"State\", '$.metadata.labels.\"mohist.io/agent-launch/repository\"')"),
                ("LabelAgentLaunchWorkspacePath", "json_extract(\"State\", '$.metadata.labels.\"mohist.io/agent-launch/workspace-path\"')"),
                ("LabelAgentName", "json_extract(\"State\", '$.metadata.labels.\"mohist.io/agent-name\"')"),
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
                name: "IX_AgentSessions_LabelAgentId_LabelProjectId_CreatedAt",
                table: "AgentSessions",
                columns: new[] { "LabelAgentId", "LabelProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_LabelAgentLaunchEpicNumber",
                table: "AgentSessions",
                column: "LabelAgentLaunchEpicNumber");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_LabelAgentLaunchIssueNumber",
                table: "AgentSessions",
                column: "LabelAgentLaunchIssueNumber");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentSessions_LabelAgentId_LabelProjectId_CreatedAt",
                table: "AgentSessions");

            migrationBuilder.DropIndex(
                name: "IX_AgentSessions_LabelAgentLaunchEpicNumber",
                table: "AgentSessions");

            migrationBuilder.DropIndex(
                name: "IX_AgentSessions_LabelAgentLaunchIssueNumber",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "LabelAgentId",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "LabelAgentLaunchEpicNumber",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "LabelAgentLaunchIssueNumber",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "LabelAgentLaunchRepository",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "LabelAgentLaunchWorkspacePath",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "LabelAgentName",
                table: "AgentSessions");
        }
    }
}
