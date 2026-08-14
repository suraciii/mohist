using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Inbox;
using Mohist.Server.Infrastructure.Data.Workflow;

namespace Mohist.Server.Infrastructure.Data.Db;

internal static class WorkflowAttentionModelConfiguration
{
    public static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkflowRunRow>(entity =>
        {
            entity.Property(e => e.AttentionStatus).HasMaxLength(32);
            entity.HasIndex(e => new { e.MetadataProjectId, e.AttentionStatus, e.CreatedAt })
                .HasDatabaseName("IX_WorkflowRuns_ProjectId_AttentionStatus_CreatedAt");
        });

        modelBuilder.Entity<InboxItemRow>(entity =>
        {
            entity.ToTable("InboxItems", table =>
            {
                table.HasCheckConstraint(
                    "CK_InboxItems_NotificationKind",
                    "\"NotificationKind\" IN ('workflow_failed', 'agent_result_unconfirmed', 'approval_requested', 'issue_started', 'issue_completed', 'agent_response_failed')");
            });
        });

        modelBuilder.Entity<InboxSubscriptionRow>(entity =>
        {
            entity.Property(e => e.AgentResultUnconfirmedEnabled).IsRequired();
        });
    }
}
