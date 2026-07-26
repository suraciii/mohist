using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentResponseFailedNotification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_InboxItems_NotificationKind",
                table: "InboxItems");

            migrationBuilder.AddColumn<bool>(
                name: "AgentResponseFailedEnabled",
                table: "InboxSubscriptions",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_InboxItems_NotificationKind",
                table: "InboxItems",
                sql: "\"NotificationKind\" IN ('workflow_failed', 'approval_requested', 'issue_started', 'issue_completed', 'agent_response_failed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_InboxItems_NotificationKind",
                table: "InboxItems");

            migrationBuilder.DropColumn(
                name: "AgentResponseFailedEnabled",
                table: "InboxSubscriptions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InboxItems_NotificationKind",
                table: "InboxItems",
                sql: "\"NotificationKind\" IN ('workflow_failed', 'approval_requested', 'issue_started', 'issue_completed')");
        }
    }
}
