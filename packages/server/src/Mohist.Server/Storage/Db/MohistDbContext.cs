using Microsoft.EntityFrameworkCore;
using Mohist.Server.Config;
using Mohist.Server.Epics;
using Mohist.Server.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Storage;
using Mohist.Server.Project.Storage;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Storage;
using Mohist.Server.Storage.Db.Entities;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Storage.Db;

public class MohistDbContext : DbContext
{
    public DbSet<ProjectEntry> Projects { get; set; } = null!;
    public DbSet<ConfigEntry> Configs { get; set; } = null!;
    public DbSet<WorkflowEventEntry> WorkflowEvents { get; set; } = null!;
    public DbSet<WorkflowAgentSession> WorkflowAgentSessions { get; set; } = null!;
    public DbSet<WorkflowAgentSessionEvent> WorkflowAgentSessionEvents { get; set; } = null!;
    public DbSet<IssueCommentEntry> IssueComments { get; set; } = null!;
    public DbSet<IssuePrerequisiteEntry> IssuePrerequisites { get; set; } = null!;
    public DbSet<EpicEntry> Epics { get; set; } = null!;
    public DbSet<EpicIssueEntry> EpicIssues { get; set; } = null!;
    public DbSet<IssueStateRow> IssueStates { get; set; } = null!;
    public DbSet<IssueProfileRow> IssueProfiles { get; set; } = null!;
    public DbSet<WorkflowRunEntity> WorkflowRuns { get; set; } = null!;
    public DbSet<WorkflowRunProfileRow> WorkflowRunProfiles { get; set; } = null!;
    public DbSet<WorkflowLeaseRow> WorkflowLeases { get; set; } = null!;
    public DbSet<WorkflowVariablesRow> WorkflowVariables { get; set; } = null!;
    public DbSet<BacklogStateRow> BacklogStates { get; set; } = null!;
    public DbSet<IssueCounterRow> IssueCounters { get; set; } = null!;

    public MohistDbContext(DbContextOptions<MohistDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProjectEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(256);
            entity.Property(e => e.Name).HasMaxLength(256).IsRequired();
            entity.Property(e => e.RepositoriesJson).IsRequired();
            entity.HasIndex(e => e.Name).IsUnique();
        });

        modelBuilder.Entity<ConfigEntry>(entity =>
        {
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).HasMaxLength(256);
            entity.Property(e => e.Value).IsRequired();
        });

        modelBuilder.Entity<WorkflowEventEntry>(entity =>
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

        modelBuilder.Entity<WorkflowAgentSession>(entity =>
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
            entity.HasIndex(e => new { e.ProjectId, e.IssueNumber, e.CreatedAt });
            entity.HasIndex(e => new { e.WorkflowRunId, e.WorkId }).IsUnique();
            entity.HasIndex(e => new { e.WorkflowRunId, e.SessionName }).IsUnique();
            entity.HasIndex(e => e.AgentSessionId);
            entity.HasIndex(e => new { e.ProjectId, e.Status, e.CreatedAt });
        });

        modelBuilder.Entity<WorkflowAgentSessionEvent>(entity =>
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

        modelBuilder.Entity<IssueCommentEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.IssueId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Body).IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.IssueNumber, e.CreatedAt });
        });

        modelBuilder.Entity<IssuePrerequisiteEntry>(entity =>
        {
            entity.HasKey(e => new { e.ProjectId, e.IssueNumber, e.PrerequisiteNumber });
            entity.Property(e => e.ProjectId).HasMaxLength(256);
        });

        modelBuilder.Entity<EpicEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(512).IsRequired();
            entity.Property(e => e.Priority).HasMaxLength(16).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.Status, e.CreatedAt });
        });

        modelBuilder.Entity<EpicIssueEntry>(entity =>
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

        modelBuilder.Entity<WorkflowRunEntity>(entity =>
        {
            entity.HasKey(e => e.WorkflowRunId);
            entity.Property(e => e.WorkflowRunId).HasMaxLength(50);
            entity.Property(e => e.State).IsRequired();
            entity.Property(e => e.MetadataProjectId)
                .HasComputedColumnSql("json_extract(State, '$.Metadata.Annotations.projectId')", stored: true);
            entity.HasIndex(e => e.MetadataProjectId);
        });

        modelBuilder.Entity<WorkflowRunProfileRow>(entity =>
        {
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).HasMaxLength(256);
            entity.Property(e => e.StateJson).IsRequired();
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

        modelBuilder.Entity<IssueCounterRow>(entity =>
        {
            entity.HasKey(e => e.ProjectId);
            entity.Property(e => e.ProjectId).HasMaxLength(256);
        });
    }
}