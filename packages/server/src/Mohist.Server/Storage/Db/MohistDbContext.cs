using Microsoft.EntityFrameworkCore;
using Mohist.Server.Config.Domain;
using Mohist.Server.Epics;
using Mohist.Server.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Sessions;
using Mohist.Server.Storage.Db.Entities;

namespace Mohist.Server.Storage.Db;

public class MohistDbContext : DbContext
{
    public DbSet<GrainState> GrainStates { get; set; } = null!;
    public DbSet<ConfigEntry> Configs { get; set; } = null!;
    public DbSet<WorkflowEventEntry> WorkflowEvents { get; set; } = null!;
    public DbSet<AgentSession> AgentSessions { get; set; } = null!;
    public DbSet<AgentSessionEvent> AgentSessionEvents { get; set; } = null!;
    public DbSet<IssueCommentEntry> IssueComments { get; set; } = null!;
    public DbSet<IssuePrerequisiteEntry> IssuePrerequisites { get; set; } = null!;
    public DbSet<EpicEntry> Epics { get; set; } = null!;
    public DbSet<EpicIssueEntry> EpicIssues { get; set; } = null!;

    public MohistDbContext(DbContextOptions<MohistDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GrainState>(entity =>
        {
            entity.HasKey(e => new { e.Key, e.Type });
            entity.Property(e => e.Key).HasMaxLength(256);
            entity.Property(e => e.Type).HasMaxLength(256);
            entity.Property(e => e.JsonState).IsRequired();
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

        modelBuilder.Entity<AgentSession>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.WorkflowRunId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.WorkId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.WorkType).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Stage).HasMaxLength(64);
            entity.Property(e => e.Title).HasMaxLength(512);
            entity.Property(e => e.RunnerId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ExternalSessionId).HasMaxLength(256);
            entity.Property(e => e.Status).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Model).HasMaxLength(256);
            entity.HasIndex(e => new { e.ProjectId, e.IssueNumber, e.CreatedAt });
            entity.HasIndex(e => new { e.WorkflowRunId, e.WorkId }).IsUnique();
            entity.HasIndex(e => new { e.ProjectId, e.Status, e.CreatedAt });
        });

        modelBuilder.Entity<AgentSessionEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SessionId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.WorkflowRunId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.WorkId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Type).HasMaxLength(128).IsRequired();
            entity.Property(e => e.PayloadJson).IsRequired();
            entity.HasIndex(e => new { e.SessionId, e.Sequence }).IsUnique();
            entity.HasIndex(e => new { e.ProjectId, e.IssueNumber, e.Id });
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
    }
}
