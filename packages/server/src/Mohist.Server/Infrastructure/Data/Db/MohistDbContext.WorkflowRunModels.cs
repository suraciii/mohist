using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Workflow;

namespace Mohist.Server.Infrastructure.Data.Db;

public partial class MohistDbContext
{
    private static void ConfigureWorkflowRunModels(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkflowRunRow>(entity =>
        {
            entity.HasKey(e => e.WorkflowRunId);
            entity.Property(e => e.WorkflowRunId).HasMaxLength(50);
            entity.Property(e => e.State).IsRequired();
            entity.Property<long>("ETag").IsConcurrencyToken();
            entity.Property(e => e.MetadataProjectId)
                .HasComputedColumnSql("COALESCE(json_extract(State, '$.metadata.projectId'), json_extract(State, '$.Metadata.ProjectId'))", stored: true);
            entity.Property(e => e.CreatedAt)
                .HasComputedColumnSql("json_extract(State, '$.metadata.createdAt')", stored: false);
            entity.Property(e => e.AssignedWorkerId)
                .HasComputedColumnSql("COALESCE(json_extract(State, '$.assignment.workerId'), json_extract(State, '$.assignment.runnerId'), json_extract(State, '$.claim.runnerId'))", stored: false);
            // Fairness ordering key: when the run last (re-)entered Ready.
            // VIRTUAL (non-stored) — read only to ORDER Ready runs
            // round-robin (ReadySince ASC), never filtered on. JSON path is
            // camelCase (Orleans JSON serialization). The COALESCE guards a
            // PascalCase historical/projection path.
            entity.Property(e => e.ReadySince)
                .HasComputedColumnSql("COALESCE(json_extract(State, '$.readySince'), json_extract(State, '$.ReadySince'))", stored: false);
            // STORED status computed column. Mirrors the
            // COALESCE path-robustness pattern used by IssueRow.ProjectId /
            // AgentRow.Status; LOWER normalizes the camelCase enum value
            // (e.g. "ready", "pending") so the column is always lowercase
            // regardless of any PascalCase historical state. The matching
            // IX_WorkflowRuns_Status index is created by migration;
            // This declares the model-side projection only.
            entity.Property(e => e.Status)
                .HasComputedColumnSql("LOWER(COALESCE(json_extract(State, '$.status'), json_extract(State, '$.Status')))", stored: true);
            entity.Property(e => e.IssueNumber)
                .HasComputedColumnSql(
                    "CAST(COALESCE(json_extract(State, '$.metadata.issueNumber'), json_extract(State, '$.Metadata.IssueNumber')) AS INTEGER)",
                    stored: true);
            entity.Property(e => e.PullRequestNumber);
            entity.Property(e => e.ActiveWorkId).HasMaxLength(128);
            entity.Property(e => e.ActiveWorkerId).HasMaxLength(128);
            entity.HasIndex(e => e.MetadataProjectId);
            entity.HasIndex(e => e.AssignedWorkerId);
            entity.HasIndex(e => new { e.MetadataProjectId, e.AssignedWorkerId, e.CreatedAt });
            entity.HasIndex(e => new { e.MetadataProjectId, e.IssueNumber })
                .HasDatabaseName("IX_WorkflowRuns_ProjectId_IssueNumber");
            entity.HasIndex(e => new { e.MetadataProjectId, e.PullRequestNumber })
                .HasDatabaseName("IX_WorkflowRuns_ProjectId_PullRequestNumber");
            entity.HasIndex(e => new { e.MetadataProjectId, e.EpicNumber })
                .HasDatabaseName("IX_WorkflowRuns_ProjectId_EpicNumber");
            // Covering index for the two scheduler queries
            // (FindAssignableAsync -> status == pending, FindAssignedToAsync
            // -> status == ready AND assigned == worker). The composite
            // matches the worker-bound filter exactly; the standalone
            // status index is implied by EF through the column projection.
            entity.HasIndex(e => new { e.Status, e.AssignedWorkerId })
                .HasDatabaseName("IX_WorkflowRuns_Status");
            // Fairness: the scheduler serves Ready runs assigned to a worker in
            // ReadySince ASC order. Composite covering index matches the filter
            // (Status, AssignedWorkerId) plus the ordering key (ReadySince) so
            // the round-robin scan is index-only.
            entity.HasIndex(e => new { e.Status, e.AssignedWorkerId, e.ReadySince })
                .HasDatabaseName("IX_WorkflowRuns_Status_ReadySince");
            // Run's nullable custom-Profile backing key.
            // The terminalization transaction clears this column while
            // keeping the public Profile ID in State. Built-in bindings
            // leave it null.
            entity.Property(e => e.WorkflowProfileIdKey).HasMaxLength(256);
            entity.HasIndex(e => new { e.MetadataProjectId, e.WorkflowProfileIdKey })
                .HasDatabaseName("IX_WorkflowRuns_MetadataProjectId_WorkflowProfileIdKey");
            entity.HasOne<WorkflowProfileRecordRow>()
                .WithMany()
                .HasForeignKey(e => new { e.MetadataProjectId, e.WorkflowProfileIdKey })
                .HasPrincipalKey(e => new { e.ProjectId, e.ProfileId })
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
