using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentSessionTriggerLabelColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LabelTriggerEventId",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/trigger/event-id\"')",
                stored: false);

            migrationBuilder.AddColumn<string>(
                name: "LabelTriggerSubscriptionId",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/trigger/subscription-id\"')",
                stored: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LabelTriggerEventId",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "LabelTriggerSubscriptionId",
                table: "AgentSessions");
        }
    }
}
