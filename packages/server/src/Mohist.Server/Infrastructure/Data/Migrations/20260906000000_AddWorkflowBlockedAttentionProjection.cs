using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddWorkflowBlockedAttentionProjection : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AttentionStatus",
            table: "WorkflowRuns",
            type: "TEXT",
            maxLength: 32,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_WorkflowRuns_ProjectId_AttentionStatus_CreatedAt",
            table: "WorkflowRuns",
            columns: new[] { "MetadataProjectId", "AttentionStatus", "CreatedAt" });

        migrationBuilder.AddColumn<bool>(
            name: "AgentResultUnconfirmedEnabled",
            table: "InboxSubscriptions",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.DropCheckConstraint(
            name: "CK_InboxItems_NotificationKind",
            table: "InboxItems");

        migrationBuilder.AddCheckConstraint(
            name: "CK_InboxItems_NotificationKind",
            table: "InboxItems",
            sql: "\"NotificationKind\" IN ('workflow_failed', 'agent_result_unconfirmed', 'approval_requested', 'issue_started', 'issue_completed', 'agent_response_failed')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_InboxItems_NotificationKind",
            table: "InboxItems");

        migrationBuilder.AddCheckConstraint(
            name: "CK_InboxItems_NotificationKind",
            table: "InboxItems",
            sql: "\"NotificationKind\" IN ('workflow_failed', 'approval_requested', 'issue_started', 'issue_completed', 'agent_response_failed')");

        migrationBuilder.DropIndex(
            name: "IX_WorkflowRuns_ProjectId_AttentionStatus_CreatedAt",
            table: "WorkflowRuns");

        migrationBuilder.DropColumn(
            name: "AttentionStatus",
            table: "WorkflowRuns");

        migrationBuilder.DropColumn(
            name: "AgentResultUnconfirmedEnabled",
            table: "InboxSubscriptions");
    }
}
