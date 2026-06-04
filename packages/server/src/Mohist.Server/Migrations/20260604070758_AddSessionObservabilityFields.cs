using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionObservabilityFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CachedReadTokens",
                table: "WorkflowAgentSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ContextWindowSize",
                table: "WorkflowAgentSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ContextWindowUsed",
                table: "WorkflowAgentSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CostAmount",
                table: "WorkflowAgentSessions",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CostCurrency",
                table: "WorkflowAgentSessions",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureCategory",
                table: "WorkflowAgentSessions",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "InputTokens",
                table: "WorkflowAgentSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OutputTokens",
                table: "WorkflowAgentSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedModel",
                table: "WorkflowAgentSessions",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ThoughtTokens",
                table: "WorkflowAgentSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ToolCallCount",
                table: "WorkflowAgentSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ToolErrorCount",
                table: "WorkflowAgentSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TotalTokens",
                table: "WorkflowAgentSessions",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CachedReadTokens",
                table: "WorkflowAgentSessions");

            migrationBuilder.DropColumn(
                name: "ContextWindowSize",
                table: "WorkflowAgentSessions");

            migrationBuilder.DropColumn(
                name: "ContextWindowUsed",
                table: "WorkflowAgentSessions");

            migrationBuilder.DropColumn(
                name: "CostAmount",
                table: "WorkflowAgentSessions");

            migrationBuilder.DropColumn(
                name: "CostCurrency",
                table: "WorkflowAgentSessions");

            migrationBuilder.DropColumn(
                name: "FailureCategory",
                table: "WorkflowAgentSessions");

            migrationBuilder.DropColumn(
                name: "InputTokens",
                table: "WorkflowAgentSessions");

            migrationBuilder.DropColumn(
                name: "OutputTokens",
                table: "WorkflowAgentSessions");

            migrationBuilder.DropColumn(
                name: "ResolvedModel",
                table: "WorkflowAgentSessions");

            migrationBuilder.DropColumn(
                name: "ThoughtTokens",
                table: "WorkflowAgentSessions");

            migrationBuilder.DropColumn(
                name: "ToolCallCount",
                table: "WorkflowAgentSessions");

            migrationBuilder.DropColumn(
                name: "ToolErrorCount",
                table: "WorkflowAgentSessions");

            migrationBuilder.DropColumn(
                name: "TotalTokens",
                table: "WorkflowAgentSessions");
        }
    }
}
