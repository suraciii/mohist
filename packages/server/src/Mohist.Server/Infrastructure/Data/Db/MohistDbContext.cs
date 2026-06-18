using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Workflow.Prompts;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Project.Domain;

namespace Mohist.Server.Infrastructure.Data.Db;

public class MohistDbContext : DbContext
{
    private static readonly ValueComparer<Dictionary<string, string>> DictionaryStringComparer = new(
        (left, right) => DictionaryEqual(left, right),
        value => DictionaryHash(value),
        value => value.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal));

    public DbSet<ProjectRow> Projects { get; set; } = null!;
    public DbSet<ProjectWorkflowProfile> ProjectWorkflowProfiles { get; set; } = null!;
    public DbSet<ProjectWorkflowTemplateRow> ProjectWorkflowTemplates { get; set; } = null!;
    public DbSet<WorkflowRunEventRow> WorkflowRunEvents { get; set; } = null!;
    public DbSet<AgentSessionRow> AgentSessions { get; set; } = null!;
    public DbSet<AgentSessionLabelRow> AgentSessionLabels { get; set; } = null!;
    public DbSet<AgentSessionTranscriptTurnRow> AgentSessionTranscriptTurns { get; set; } = null!;
    public DbSet<AgentSessionTranscriptPartRow> AgentSessionTranscriptParts { get; set; } = null!;
    public DbSet<IssueCommentRow> IssueComments { get; set; } = null!;
    public DbSet<AttachmentRow> Attachments { get; set; } = null!;
    public DbSet<IssuePrerequisiteRow> IssuePrerequisites { get; set; } = null!;
    public DbSet<EpicRow> Epics { get; set; } = null!;
    public DbSet<EpicIssueRow> EpicIssues { get; set; } = null!;
    public DbSet<IssueRow> Issues { get; set; } = null!;
    public DbSet<AgentRow> Agents { get; set; } = null!;
    public DbSet<IssueEventRow> IssueEvents { get; set; } = null!;
    public DbSet<IssueWorkflowProfile> IssueWorkflowProfiles { get; set; } = null!;
    public DbSet<WorkflowRunRow> WorkflowRuns { get; set; } = null!;
    public DbSet<WorkflowVariablesRow> WorkflowVariables { get; set; } = null!;
    public DbSet<BacklogStateRow> BacklogStates { get; set; } = null!;
    public DbSet<WorkflowStageLockRow> WorkflowStageLocks { get; set; } = null!;
    public DbSet<IssueCounterRow> IssueCounters { get; set; } = null!;
    public DbSet<ProjectPromptTemplateRow> ProjectPromptTemplates { get; set; } = null!;
    public DbSet<EpicCounterRow> EpicCounters { get; set; } = null!;
    public DbSet<WorkflowArtifactRow> WorkflowArtifacts { get; set; } = null!;
    public DbSet<WorkflowArtifactPendingUploadRow> WorkflowArtifactPendingUploads { get; set; } = null!;

    public MohistDbContext(DbContextOptions<MohistDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProjectRow>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(256);
            entity.Property(e => e.Name).HasMaxLength(ProjectName.MaxLength).IsRequired();
            entity.Property(e => e.RepositoriesJson).IsRequired();
            entity.HasIndex(e => e.Name).IsUnique();
        });

        modelBuilder.Entity<WorkflowRunEventRow>(entity =>
        {
            entity.ToTable("WorkflowRunEvents");
            entity.HasKey(e => new { e.Source, e.Id });
            entity.Property(e => e.Id).IsRequired();
            entity.Property(e => e.Source)
                .HasMaxLength(256)
                .IsRequired();
            entity.Property(e => e.EventId)
                .HasMaxLength(128)
                .IsRequired();
            entity.Property(e => e.Type)
                .HasMaxLength(256)
                .IsRequired();
            entity.Property(e => e.SpecVersion)
                .HasMaxLength(16)
                .IsRequired();
            entity.Property(e => e.Subject)
                .HasMaxLength(256);
            entity.Property(e => e.DataContentType)
                .HasMaxLength(64)
                .IsRequired();
            entity.Property(e => e.Data)
                .IsRequired()
                .HasColumnType("JSON")
                .HasConversion(
                    data => data.GetRawText(),
                    json => JsonDocument.Parse(json).RootElement.Clone());
            entity.Property(e => e.ExtensionsJson)
                .HasColumnType("JSON")
                .HasConversion(
                    json => json,
                    raw => raw);
            entity.Property(e => e.Time)
                .IsRequired();
            entity.HasIndex(nameof(WorkflowRunEventRow.Type), nameof(WorkflowRunEventRow.Source), nameof(WorkflowRunEventRow.Id));
        });

        modelBuilder.Entity<AgentSessionRow>(entity =>
        {
            entity.ToTable("AgentSessions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(512);
            entity.Property(e => e.State).IsRequired();
            entity.Property(e => e.RunnerId).HasMaxLength(256);
            entity.Property(e => e.AgentSessionId).HasMaxLength(256);
            entity.Property(e => e.Status).HasMaxLength(64).IsRequired().HasConversion<string>();
            entity.HasIndex(e => e.AgentSessionId);
            entity.HasIndex(e => new { e.Status, e.CreatedAt });
        });

        modelBuilder.Entity<AgentSessionLabelRow>(entity =>
        {
            entity.ToTable("AgentSessionLabels");
            entity.HasKey(e => new { e.SessionId, e.Key });
            entity.Property(e => e.SessionId).HasMaxLength(512).IsRequired();
            entity.Property(e => e.Key).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Value).HasMaxLength(1024).IsRequired();
            entity.HasIndex(e => new { e.Key, e.Value, e.SessionId });
        });

        modelBuilder.Entity<AgentSessionTranscriptTurnRow>(entity =>
        {
            entity.ToTable("AgentSessionTranscriptTurns");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SessionId).HasMaxLength(512).IsRequired();
            entity.Property(e => e.PromptText).IsRequired();
            entity.Property(e => e.PromptKind).HasMaxLength(64).IsRequired();
            entity.HasIndex(e => new { e.SessionId, e.Sequence }).IsUnique();
        });

        modelBuilder.Entity<AgentSessionTranscriptPartRow>(entity =>
        {
            entity.ToTable("AgentSessionTranscriptParts");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Type).HasMaxLength(64).IsRequired();
            entity.Property(e => e.CorrelationKey).HasMaxLength(512).IsRequired();
            entity.Property(e => e.CorrelationId).HasMaxLength(256);
            entity.Property(e => e.Text).IsRequired();
            entity.Property(e => e.PayloadJson).IsRequired();
            entity.HasIndex(e => new { e.TurnId, e.Sequence }).IsUnique();
            entity.HasIndex(e => new { e.TurnId, e.Type, e.CorrelationKey }).IsUnique();
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

        modelBuilder.Entity<AttachmentRow>(entity =>
        {
            entity.ToTable("Attachments");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.OwnerKind).HasMaxLength(16);
            entity.Property(e => e.OwnerId).HasMaxLength(256);
            entity.Property(e => e.OriginalFileName).HasMaxLength(512).IsRequired();
            entity.Property(e => e.ContentType).HasMaxLength(128);
            entity.Property(e => e.StoragePath).HasMaxLength(1024).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.OwnerKind, e.OwnerId })
                .HasDatabaseName("IX_Attachments_ProjectId_Owner");
            entity.HasIndex(e => e.ExpiresAt)
                .HasDatabaseName("IX_Attachments_ExpiresAt");
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

        modelBuilder.Entity<IssueRow>(entity =>
        {
            entity.ToTable("Issues");
            entity.HasKey(e => e.IssueId);
            entity.Property(e => e.IssueId).HasMaxLength(256);
            entity.Property(e => e.State).IsRequired();
            entity.Property(e => e.Risk).HasMaxLength(16);
            entity.Property(e => e.ProjectId)
                .HasComputedColumnSql("COALESCE(json_extract(State, '$.projectId'), json_extract(State, '$.ProjectId'))", stored: true);
            entity.Property(e => e.Number)
                .HasComputedColumnSql("COALESCE(json_extract(State, '$.number'), json_extract(State, '$.Number'))", stored: true);
            entity.Property(e => e.WorkflowRunId)
                .HasComputedColumnSql("COALESCE(json_extract(State, '$.workflowRunId'), json_extract(State, '$.WorkflowRunId'))", stored: true);
            entity.HasIndex(e => new { e.ProjectId, e.Number }).IsUnique();
            entity.HasIndex(e => e.WorkflowRunId);
        });

        modelBuilder.Entity<AgentRow>(entity =>
        {
            entity.ToTable("Agents");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(256);
            entity.Property(e => e.State).IsRequired();
            entity.Property(e => e.ProjectId)
                .HasComputedColumnSql("COALESCE(json_extract(State, '$.projectId'), json_extract(State, '$.ProjectId'))", stored: true);
            entity.Property(e => e.Name)
                .HasComputedColumnSql("COALESCE(json_extract(State, '$.name'), json_extract(State, '$.Name'))", stored: true);
            entity.Property(e => e.Status)
                .HasComputedColumnSql("COALESCE(json_extract(State, '$.status'), json_extract(State, '$.Status'))", stored: true);
            entity.HasIndex(e => new { e.ProjectId, e.Name }).IsUnique();
            entity.HasIndex(e => new { e.ProjectId, e.Status });
        });

        modelBuilder.Entity<IssueEventRow>(entity =>
        {
            entity.ToTable("IssueEvents");
            entity.HasKey(e => new { e.Source, e.Id });
            entity.Property(e => e.Id).IsRequired();
            entity.Property(e => e.Source)
                .HasMaxLength(256)
                .IsRequired();
            entity.Property(e => e.EventId)
                .HasMaxLength(128)
                .IsRequired();
            entity.Property(e => e.Type)
                .HasMaxLength(256)
                .IsRequired();
            entity.Property(e => e.SpecVersion)
                .HasMaxLength(16)
                .IsRequired();
            entity.Property(e => e.Subject)
                .HasMaxLength(256);
            entity.Property(e => e.DataContentType)
                .HasMaxLength(64)
                .IsRequired();
            entity.Property(e => e.Data)
                .IsRequired()
                .HasColumnType("JSON")
                .HasConversion(
                    data => data.GetRawText(),
                    json => JsonDocument.Parse(json).RootElement.Clone());
            entity.Property(e => e.ExtensionsJson)
                .HasColumnType("JSON")
                .HasConversion(
                    json => json,
                    raw => raw);
            entity.Property(e => e.Time)
                .IsRequired();
            entity.HasIndex(nameof(IssueEventRow.Type), nameof(IssueEventRow.Source), nameof(IssueEventRow.Id));
        });

        modelBuilder.Entity<WorkflowRunRow>(entity =>
        {
            entity.HasKey(e => e.WorkflowRunId);
            entity.Property(e => e.WorkflowRunId).HasMaxLength(50);
            entity.Property(e => e.State).IsRequired();
            entity.Property<long>("ETag").IsConcurrencyToken();
            entity.Property(e => e.MetadataProjectId)
                .HasComputedColumnSql("COALESCE(json_extract(State, '$.metadata.annotations.projectId'), json_extract(State, '$.Metadata.Annotations.projectId'), json_extract(State, '$.Metadata.Annotations.ProjectId'))", stored: true);
            entity.HasIndex(e => e.MetadataProjectId);
        });

        modelBuilder.Entity<WorkflowVariablesRow>(entity =>
        {
            entity.HasKey(e => e.WorkflowRunId);
            entity.Property(e => e.WorkflowRunId).HasMaxLength(256);
            entity.Property(e => e.State).IsRequired();
        });

        modelBuilder.Entity<BacklogStateRow>(entity =>
        {
            entity.HasKey(e => e.ProjectId);
            entity.Property(e => e.ProjectId).HasMaxLength(256);
            entity.Property(e => e.State).IsRequired();
        });

        modelBuilder.Entity<WorkflowStageLockRow>(entity =>
        {
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).HasMaxLength(512);
            entity.Property(e => e.State).IsRequired();
        });

        modelBuilder.Entity<IssueCounterRow>(entity =>
        {
            entity.HasKey(e => e.ProjectId);
            entity.Property(e => e.ProjectId).HasMaxLength(256);
        });

        modelBuilder.Entity<ProjectWorkflowProfile>(entity =>
        {
            entity.ToTable("ProjectWorkflowProfiles");
            entity.HasKey(e => e.ProjectId);
            entity.Property(e => e.ProjectId).HasMaxLength(256);
            entity.Property(e => e.DefaultTemplateId).HasMaxLength(256);
            entity.Property(e => e.Variables).IsRequired();
            entity.Property(e => e.Prompts)
                .HasConversion(
                    v => JSON.Serialize(v),
                    v => JSON.DeserializeDictionary(v))
                .IsRequired()
                .HasDefaultValue(new Dictionary<string, string>());
            entity.Property(e => e.Prompts).Metadata.SetValueComparer(DictionaryStringComparer);
        });

        modelBuilder.Entity<ProjectWorkflowTemplateRow>(entity =>
        {
            entity.ToTable("ProjectWorkflowTemplates");
            entity.HasKey(e => new { e.ProjectId, e.TemplateId });
            entity.Property(e => e.ProjectId).HasMaxLength(256);
            entity.Property(e => e.TemplateId).HasMaxLength(256);
            entity.Property(e => e.Template).IsRequired();
            entity.HasIndex(e => e.ProjectId);
        });

        modelBuilder.Entity<IssueWorkflowProfile>(entity =>
        {
            entity.ToTable("IssueWorkflowProfiles");
            entity.HasKey(e => e.IssueId);
            entity.Property(e => e.IssueId).HasMaxLength(512);
            entity.Property(e => e.SourceTemplateId).HasMaxLength(256);
            entity.Property(e => e.Variables).IsRequired();
            entity.Property(e => e.Prompts)
                .HasConversion(
                    v => JSON.Serialize(v),
                    v => JSON.DeserializeDictionary(v))
                .IsRequired()
                .HasDefaultValue(new Dictionary<string, string>());
            entity.Property(e => e.Prompts).Metadata.SetValueComparer(DictionaryStringComparer);
        });

        modelBuilder.Entity<ProjectPromptTemplateRow>(entity =>
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

        modelBuilder.Entity<WorkflowArtifactRow>(entity =>
        {
            entity.ToTable("WorkflowArtifacts");
            entity.HasKey(e => e.ArtifactId);
            entity.Property(e => e.ArtifactId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.WorkflowRunId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.TaskRunId).HasMaxLength(128).IsRequired();
            entity.Property(e => e.Path).HasMaxLength(1024).IsRequired();
            entity.Property(e => e.RecordedAt).IsRequired();
            entity.Property(e => e.ArtifactStoragePath).HasMaxLength(1024).IsRequired();
            entity.Property(e => e.Kind).HasMaxLength(16).IsRequired().HasDefaultValue("file");
            entity.Property(e => e.ContentType).HasMaxLength(128);
            entity.Property(e => e.ContentHash).HasMaxLength(128);
            entity.Property(e => e.ProjectId).HasMaxLength(256);
            entity.Property(e => e.IssueId).HasMaxLength(256);
            entity.Property(e => e.DisplayName).HasMaxLength(512);

            // Latest per path within a workflow run, plus history scans.
            entity.HasIndex(e => new { e.WorkflowRunId, e.Path, e.RecordedAt })
                .HasDatabaseName("IX_WorkflowArtifacts_WorkflowRunId_Path_RecordedAt");
            // Task-run filter and history ordering.
            entity.HasIndex(e => new { e.WorkflowRunId, e.TaskRunId, e.RecordedAt })
                .HasDatabaseName("IX_WorkflowArtifacts_WorkflowRunId_TaskRunId_RecordedAt");
            // Issue-scoped latest projection support.
            entity.HasIndex(e => new { e.IssueId, e.RecordedAt })
                .HasDatabaseName("IX_WorkflowArtifacts_IssueId_RecordedAt");
        });

        modelBuilder.Entity<WorkflowArtifactPendingUploadRow>(entity =>
        {
            entity.ToTable("WorkflowArtifactPendingUploads");
            entity.HasKey(e => e.UploadId);
            entity.Property(e => e.UploadId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.WorkflowRunId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.WorkId).HasMaxLength(128).IsRequired();
            entity.Property(e => e.TaskRunId).HasMaxLength(128).IsRequired();
            entity.Property(e => e.Path).HasMaxLength(1024).IsRequired();
            entity.Property(e => e.Kind).HasMaxLength(16).IsRequired().HasDefaultValue("file");
            entity.Property(e => e.FileCount);
            entity.Property(e => e.ContentType).HasMaxLength(128);
            entity.Property(e => e.ContentHash).HasMaxLength(128);
            entity.Property(e => e.StoragePath).HasMaxLength(1024).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.ExpiresAt).IsRequired();

            // Idempotency: same workflow run, work item, task run, and
            // path resolves to a single pending upload. Conflicts on
            // content hash reject the retry without mutating the row.
            entity.HasIndex(e => new { e.WorkflowRunId, e.WorkId, e.TaskRunId, e.Path })
                .IsUnique()
                .HasDatabaseName("UX_WorkflowArtifactPendingUploads_IdempotencyKey");
            // TTL cleanup walks by expiry.
            entity.HasIndex(e => e.ExpiresAt)
                .HasDatabaseName("IX_WorkflowArtifactPendingUploads_ExpiresAt");
        });
    }

    private static bool DictionaryEqual(Dictionary<string, string>? left, Dictionary<string, string>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;
        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var rightValue) || !string.Equals(value, rightValue, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static int DictionaryHash(Dictionary<string, string>? value)
    {
        if (value is null) return 0;
        var hash = new HashCode();
        foreach (var entry in value.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            hash.Add(entry.Key, StringComparer.Ordinal);
            hash.Add(entry.Value, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }
}
