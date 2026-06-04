using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Epic.Storage;
using Mohist.Server.Epics;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Persistence.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Storage;
using Mohist.Server.Project.Storage;
using Mohist.Server.Sessions.Storage;
using Mohist.Server.Infrastructure.Persistence.Db.Entities;
using Mohist.Server.Workflow.Prompts.Storage;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Infrastructure.Persistence.Db;

public class MohistDbContext : DbContext
{
    public DbSet<ProjectRow> Projects { get; set; } = null!;
    public DbSet<ProjectWorkflowProfileRow> ProjectWorkflowProfiles { get; set; } = null!;
    public DbSet<Mohist.Server.Workflow.Storage.ProjectTemplateRow> ProjectTemplates { get; set; } = null!;
    public DbSet<ConfigRow> Configs { get; set; } = null!;
    public DbSet<WorkflowEventRow> WorkflowEvents { get; set; } = null!;
    public DbSet<WorkflowAgentSessionRow> WorkflowAgentSessions { get; set; } = null!;
    public DbSet<WorkflowAgentSessionEventRow> WorkflowAgentSessionEvents { get; set; } = null!;
    public DbSet<IssueCommentRow> IssueComments { get; set; } = null!;
    public DbSet<IssuePrerequisiteRow> IssuePrerequisites { get; set; } = null!;
    public DbSet<EpicRow> Epics { get; set; } = null!;
    public DbSet<EpicIssueRow> EpicIssues { get; set; } = null!;
    public DbSet<IssueStateRow> IssueStates { get; set; } = null!;
    public DbSet<IssueProfileRow> IssueProfiles { get; set; } = null!;
    public DbSet<IssueWorkflowProfileRow> IssueWorkflowProfiles { get; set; } = null!;
    public DbSet<WorkflowRunRow> WorkflowRuns { get; set; } = null!;
    public DbSet<WorkflowLeaseRow> WorkflowLeases { get; set; } = null!;
    public DbSet<WorkflowVariablesRow> WorkflowVariables { get; set; } = null!;
    public DbSet<BacklogStateRow> BacklogStates { get; set; } = null!;
    public DbSet<WorkflowStageLockRow> WorkflowStageLocks { get; set; } = null!;
    public DbSet<IssueCounterRow> IssueCounters { get; set; } = null!;
    public DbSet<Mohist.Server.Workflow.Prompts.Storage.ProjectTemplateRow> ProjectPromptTemplates { get; set; } = null!;
    public DbSet<EpicCounterRow> EpicCounters { get; set; } = null!;

    public MohistDbContext(DbContextOptions<MohistDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProjectRow>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(256);
            entity.Property(e => e.Name).HasMaxLength(256).IsRequired();
            entity.Property(e => e.RepositoriesJson).IsRequired();
            entity.Property(e => e.VariablesJson).IsRequired();
            entity.HasIndex(e => e.Name).IsUnique();
        });

        modelBuilder.Entity<ConfigRow>(entity =>
        {
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).HasMaxLength(256);
            entity.Property(e => e.Value).IsRequired();
        });

        modelBuilder.Entity<WorkflowEventRow>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.IssueId).HasMaxLength(256);
            entity.Property(e => e.WorkflowRunId).HasMaxLength(256);
            entity.Property(e => e.Category).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Type).HasMaxLength(128).IsRequired();
            entity.Property(e => e.Stage).HasMaxLength(64);
            entity.Property(e => e.TaskId).HasMaxLength(128);
            entity.Property(e => e.CheckName).HasMaxLength(128);
            entity.Property(e => e.RunnerId).HasMaxLength(256);
            entity.Property(e => e.Status).HasMaxLength(64);
            entity.HasIndex(e => new { e.ProjectId, e.IssueNumber, e.Id });
            entity.HasIndex(e => new { e.WorkflowRunId, e.Id });
            entity.HasIndex(e => new { e.ProjectId, e.Id });
            entity.HasIndex(e => new { e.Type, e.CreatedAt });
        });

        modelBuilder.Entity<WorkflowAgentSessionRow>(entity =>
        {
            entity.ToTable("WorkflowAgentSessions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(512);
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.WorkflowRunId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.SessionName).HasMaxLength(256).IsRequired();
            entity.Property(e => e.WorkId).HasMaxLength(256);
            entity.Property(e => e.WorkType).HasMaxLength(64);
            entity.Property(e => e.Stage).HasMaxLength(64);
            entity.Property(e => e.Title).HasMaxLength(512);
            entity.Property(e => e.RunnerId).HasMaxLength(256);
            entity.Property(e => e.AgentSessionId).HasMaxLength(256);
            entity.Property(e => e.Status).HasMaxLength(64).IsRequired().HasConversion<string>();
            entity.Property(e => e.Model).HasMaxLength(256);
            entity.Property(e => e.ResolvedModel).HasMaxLength(256);
            entity.Property(e => e.CostCurrency).HasMaxLength(16);
            entity.Property(e => e.FailureCategory).HasMaxLength(64);
            entity.HasIndex(e => new { e.ProjectId, e.IssueNumber, e.CreatedAt });
            entity.HasIndex(e => new { e.WorkflowRunId, e.WorkId });
            entity.HasIndex(e => new { e.WorkflowRunId, e.SessionName }).IsUnique();
            entity.HasIndex(e => e.AgentSessionId);
            entity.HasIndex(e => new { e.ProjectId, e.Status, e.CreatedAt });
        });

        modelBuilder.Entity<WorkflowAgentSessionEventRow>(entity =>
        {
            entity.ToTable("WorkflowAgentSessionEvents");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SessionId).HasMaxLength(512).IsRequired();
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.WorkflowRunId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.SessionName).HasMaxLength(256).IsRequired();
            entity.Property(e => e.AgentSessionId).HasMaxLength(256);
            entity.Property(e => e.WorkId).HasMaxLength(256);
            entity.Property(e => e.WorkType).HasMaxLength(64);
            entity.Property(e => e.Stage).HasMaxLength(64);
            entity.Property(e => e.Type).HasMaxLength(128).IsRequired();
            entity.Property(e => e.PayloadJson).IsRequired();
            entity.HasIndex(e => new { e.SessionId, e.Sequence }).IsUnique();
            entity.HasIndex(e => new { e.ProjectId, e.IssueNumber, e.Id });
            entity.HasIndex(e => new { e.WorkflowRunId, e.SessionName, e.Sequence });
        });

        modelBuilder.Entity<IssueCommentRow>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.IssueId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Body).IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.IssueNumber, e.CreatedAt });
        });

        modelBuilder.Entity<IssuePrerequisiteRow>(entity =>
        {
            entity.HasKey(e => new { e.ProjectId, e.IssueNumber, e.PrerequisiteNumber });
            entity.Property(e => e.ProjectId).HasMaxLength(256);
        });

        modelBuilder.Entity<EpicRow>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(512).IsRequired();
            entity.Property(e => e.Priority).HasMaxLength(16).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.Status, e.CreatedAt });
            entity.HasIndex(e => new { e.ProjectId, e.Number });
        });

        modelBuilder.Entity<EpicIssueRow>(entity =>
        {
            entity.HasKey(e => new { e.EpicId, e.IssueId });
            entity.Property(e => e.EpicId).HasMaxLength(64);
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.IssueId).HasMaxLength(256).IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.IssueId }).IsUnique();
            entity.HasIndex(e => new { e.ProjectId, e.IssueNumber });
        });

        modelBuilder.Entity<IssueStateRow>(entity =>
        {
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).HasMaxLength(512);
            entity.Property(e => e.StateJson).IsRequired();
        });

        modelBuilder.Entity<IssueProfileRow>(entity =>
        {
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).HasMaxLength(512);
            entity.Property(e => e.StateJson).IsRequired();
        });

        modelBuilder.Entity<WorkflowRunRow>(entity =>
        {
            entity.HasKey(e => e.WorkflowRunId);
            entity.Property(e => e.WorkflowRunId).HasMaxLength(50);
            entity.Property(e => e.State).IsRequired();
            entity.Property<long>("ETag").IsConcurrencyToken();
            entity.Property(e => e.MetadataProjectId)
                .HasComputedColumnSql("json_extract(State, '$.Metadata.Annotations.projectId')", stored: true);
            entity.HasIndex(e => e.MetadataProjectId);
        });

        modelBuilder.Entity<WorkflowLeaseRow>(entity =>
        {
            entity.HasKey(e => e.WorkflowRunId);
            entity.Property(e => e.WorkflowRunId).HasMaxLength(256);
            entity.Property(e => e.StateJson).IsRequired();
        });

        modelBuilder.Entity<WorkflowVariablesRow>(entity =>
        {
            entity.HasKey(e => e.WorkflowRunId);
            entity.Property(e => e.WorkflowRunId).HasMaxLength(256);
            entity.Property(e => e.StateJson).IsRequired();
        });

        modelBuilder.Entity<BacklogStateRow>(entity =>
        {
            entity.HasKey(e => e.ProjectId);
            entity.Property(e => e.ProjectId).HasMaxLength(256);
            entity.Property(e => e.StateJson).IsRequired();
        });

        modelBuilder.Entity<WorkflowStageLockRow>(entity =>
        {
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).HasMaxLength(512);
            entity.Property(e => e.StateJson).IsRequired();
        });

        modelBuilder.Entity<IssueCounterRow>(entity =>
        {
            entity.HasKey(e => e.ProjectId);
            entity.Property(e => e.ProjectId).HasMaxLength(256);
        });

        modelBuilder.Entity<ProjectWorkflowProfileRow>(entity =>
        {
            entity.ToTable("ProjectWorkflowProfiles");
            entity.HasKey(e => e.ProjectId);
            entity.Property(e => e.ProjectId).HasMaxLength(256);
            entity.Property(e => e.DefaultTemplateId).HasMaxLength(256);
            entity.Property(e => e.VariablesJson).IsRequired();
        });

        modelBuilder.Entity<Mohist.Server.Workflow.Storage.ProjectTemplateRow>(entity =>
        {
            entity.ToTable("ProjectTemplates");
            entity.HasKey(e => new { e.ProjectId, e.TemplateId });
            entity.Property(e => e.ProjectId).HasMaxLength(256);
            entity.Property(e => e.TemplateId).HasMaxLength(256);
            entity.Property(e => e.TemplateJson).IsRequired();
            entity.HasIndex(e => e.ProjectId);
        });

        modelBuilder.Entity<IssueWorkflowProfileRow>(entity =>
        {
            entity.ToTable("IssueWorkflowProfiles");
            entity.HasKey(e => e.IssueKey);
            entity.Property(e => e.IssueKey).HasMaxLength(512);
            entity.Property(e => e.SourceTemplateId).HasMaxLength(256);
            entity.Property(e => e.VariablesJson).IsRequired();
        });

        modelBuilder.Entity<Mohist.Server.Workflow.Prompts.Storage.ProjectTemplateRow>(entity =>
        {
            entity.ToTable("ProjectPromptTemplates");
            entity.HasKey(e => new { e.ProjectId, e.Key });
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Key).HasMaxLength(256).IsRequired();
            entity.Property(e => e.DisplayName).HasMaxLength(512).IsRequired();
            entity.Property(e => e.Description).IsRequired();
            entity.Property(e => e.TagsJson).IsRequired().HasDefaultValue("[]");
            entity.Property(e => e.Body).IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.UpdatedAt });
        });

        modelBuilder.Entity<EpicCounterRow>(entity =>
        {
            entity.HasKey(e => e.ProjectId);
            entity.Property(e => e.ProjectId).HasMaxLength(256);
        });
    }
}
