using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Issue-130 T-001: make the direct-Agent (agent-launch) labels
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
    /// On SQLite EF Core cannot <c>ALTER TABLE ADD COLUMN ... STORED</c>,
    /// so each <c>AddColumn</c> with <c>computedColumnSql</c> triggers the
    /// provider's automatic table rebuild (the same effect the
    /// <c>ReplaceAgentSessionLabelsWithComputedColumns</c> migration
    /// achieved by add-then-<c>AlterColumn</c>). All column values
    /// derive from the existing <c>State</c> JSON — no data backfill.
    /// </summary>
    public partial class AddAgentLaunchLabelComputedColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LabelAgentId",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/agent-id\"')",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "LabelAgentLaunchEpicNumber",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/agent-launch/epic-number\"')",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "LabelAgentLaunchIssueNumber",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/agent-launch/issue-number\"')",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "LabelAgentLaunchRepository",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/agent-launch/repository\"')",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "LabelAgentLaunchWorkspacePath",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/agent-launch/workspace-path\"')",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "LabelAgentName",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/agent-name\"')",
                stored: true);

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
