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
using Mohist.Server.Infrastructure.Data.Label;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Infrastructure.Data.Inbox;
using Mohist.Server.Inbox;
using Mohist.Server.Project.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Infrastructure.Data.Db;

public class MohistDbContext : DbContext
{
    private static readonly ValueComparer<Dictionary<string, string>> DictionaryStringComparer = new(
        (left, right) => DictionaryEqual(left, right),
        value => DictionaryHash(value),
        value => value.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal));

    private static readonly ValueComparer<List<string>> ListStringComparer = new(
        (left, right) => (left == null && right == null) || (left != null && right != null && left.SequenceEqual(right)),
        value => value == null ? 0 : value.Aggregate(0, (hash, s) => hash ^ (s == null ? 0 : StringComparer.Ordinal.GetHashCode(s))),
        value => value == null ? new List<string>() : new List<string>(value));

    public DbSet<ProjectRow> Projects { get; set; } = null!;
    public DbSet<ProjectWorkflowProfile> ProjectWorkflowProfiles { get; set; } = null!;
    public DbSet<ProjectWorkflowTemplateRow> ProjectWorkflowTemplates { get; set; } = null!;
    public DbSet<WorkflowRunEventRow> WorkflowRunEvents { get; set; } = null!;
    public DbSet<AgentSessionRow> AgentSessions { get; set; } = null!;
    public DbSet<AgentSessionTranscriptTurnRow> AgentSessionTranscriptTurns { get; set; } = null!;
    public DbSet<AgentSessionTranscriptPartRow> AgentSessionTranscriptParts { get; set; } = null!;
    public DbSet<IssueCommentRow> IssueComments { get; set; } = null!;
    public DbSet<AttachmentRow> Attachments { get; set; } = null!;
    public DbSet<IssuePrerequisiteRow> IssuePrerequisites { get; set; } = null!;
    public DbSet<EpicRow> Epics { get; set; } = null!;
    public DbSet<EpicIssueRow> EpicIssues { get; set; } = null!;
    public DbSet<EpicActiveIssueRow> EpicActiveIssues { get; set; } = null!;
    public DbSet<IssueRow> Issues { get; set; } = null!;
    public DbSet<AgentRow> Agents { get; set; } = null!;
    public DbSet<AgentSubscriptionRow> AgentSubscriptions { get; set; } = null!;
    public DbSet<IssueEventRow> IssueEvents { get; set; } = null!;
    public DbSet<EpicEventRow> EpicEvents { get; set; } = null!;
    public DbSet<AgentSessionEventRow> AgentSessionEvents { get; set; } = null!;
    public DbSet<DeadLetterRow> DeadLetters { get; set; } = null!;
    public DbSet<IssueWorkflowProfile> IssueWorkflowProfiles { get; set; } = null!;
    public DbSet<WorkflowRunRow> WorkflowRuns { get; set; } = null!;
    public DbSet<WorkflowVariablesRow> WorkflowVariables { get; set; } = null!;
    public DbSet<WorkflowRunProfileRow> WorkflowRunProfiles { get; set; } = null!;
    public DbSet<WorkflowStageLockRow> WorkflowStageLocks { get; set; } = null!;
    public DbSet<IssueCounterRow> IssueCounters { get; set; } = null!;
    public DbSet<ProjectPromptTemplateRow> ProjectPromptTemplates { get; set; } = null!;
    public DbSet<EpicCounterRow> EpicCounters { get; set; } = null!;
    public DbSet<WorkflowArtifactRow> WorkflowArtifacts { get; set; } = null!;
    public DbSet<WorkflowArtifactPendingUploadRow> WorkflowArtifactPendingUploads { get; set; } = null!;
    public DbSet<LabelDefinitionRow> LabelDefinitions { get; set; } = null!;
    public DbSet<ProjectIssueTemplateRow> ProjectIssueTemplates { get; set; } = null!;
    public DbSet<RunnerRow> Runners { get; set; } = null!;
    public DbSet<RunnerWorkRow> RunnerWorks { get; set; } = null!;
    public DbSet<InboxItemRow> InboxItems { get; set; } = null!;
    public DbSet<InboxSubscriptionRow> InboxSubscriptions { get; set; } = null!;
    public DbSet<TaskLogEntryRow> TaskLogEntries { get; set; } = null!;
    public DbSet<TaskLogBatchRow> TaskLogBatches { get; set; } = null!;

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
            entity.Property(e => e.TimeSortKey)
                .HasComputedColumnSql(EventReadKeys.TimeSortKeySql, stored: true);
            entity.Property(e => e.DispatchedAt);
            entity.HasIndex(nameof(WorkflowRunEventRow.Type), nameof(WorkflowRunEventRow.Source), nameof(WorkflowRunEventRow.Id));
            entity.HasIndex(e => new { e.TimeSortKey, e.Source, e.Id })
                .HasDatabaseName("IX_WorkflowRunEvents_TimeSortKey_Source_Id");
            entity.HasIndex(e => new { e.Source, e.Id, e.DispatchedAt })
                .HasFilter("\"DispatchedAt\" IS NULL")
                .HasDatabaseName("IX_WorkflowRunEvents_Source_Id_DispatchedAt");
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

            entity.Property(e => e.LabelProjectId)
                .HasComputedColumnSql("""json_extract("State", '$.metadata.labels."mohist.io/project-id"')""", stored: true);
            entity.Property(e => e.LabelSourceId)
                .HasComputedColumnSql("""json_extract("State", '$.metadata.labels."mohist.io/source-id"')""", stored: true);
            entity.Property(e => e.LabelSessionName)
                .HasComputedColumnSql("""json_extract("State", '$.metadata.labels."mohist.io/session-name"')""", stored: true);
            entity.Property(e => e.LabelIssueNumber)
                .HasComputedColumnSql("""json_extract("State", '$.metadata.labels."mohist.io/issue-number"')""", stored: true);
            entity.Property(e => e.LabelWorkId)
                .HasComputedColumnSql("""json_extract("State", '$.metadata.labels."mohist.io/work-id"')""", stored: true);
            entity.Property(e => e.LabelWorkType)
                .HasComputedColumnSql("""json_extract("State", '$.metadata.labels."mohist.io/work-type"')""", stored: true);
            entity.Property(e => e.LabelStage)
                .HasComputedColumnSql("""json_extract("State", '$.metadata.labels."mohist.io/stage"')""", stored: true);
            entity.Property(e => e.LabelSourceKind)
                .HasComputedColumnSql("""json_extract("State", '$.metadata.labels."mohist.io/source-kind"')""", stored: true);

            // Direct Agent (agent-launch) labels. SQL paths are built from
            // GenericAgentSessionMetadata constants so a rename is a compile
            // error instead of a silent drift between metadata and DB.
            entity.Property(e => e.LabelAgentId)
                .HasComputedColumnSql(JsonExtractLabel(GenericAgentSessionMetadata.AgentId), stored: true);
            entity.Property(e => e.LabelAgentName)
                .HasComputedColumnSql(JsonExtractLabel(GenericAgentSessionMetadata.AgentName), stored: true);
            entity.Property(e => e.LabelAgentLaunchIssueNumber)
                .HasComputedColumnSql(JsonExtractLabel(GenericAgentSessionMetadata.IssueNumber), stored: true);
            entity.Property(e => e.LabelAgentLaunchEpicNumber)
                .HasComputedColumnSql(JsonExtractLabel(GenericAgentSessionMetadata.EpicNumber), stored: true);
            entity.Property(e => e.LabelAgentLaunchRepository)
                .HasComputedColumnSql(JsonExtractLabel(GenericAgentSessionMetadata.Repository), stored: true);
            entity.Property(e => e.LabelAgentLaunchWorkspacePath)
                .HasComputedColumnSql(JsonExtractLabel(GenericAgentSessionMetadata.WorkspacePath), stored: true);

            entity.Property(e => e.LabelTriggerEventId)
                .HasComputedColumnSql(JsonExtractLabel(GenericAgentSessionMetadata.TriggerEventId), stored: false);
            entity.Property(e => e.LabelTriggerSubscriptionId)
                .HasComputedColumnSql(JsonExtractLabel(GenericAgentSessionMetadata.TriggerSubscriptionId), stored: false);

            entity.HasIndex(e => new { e.LabelProjectId, e.CreatedAt }).HasDatabaseName("IX_AgentSessions_LabelProjectId_CreatedAt");
            entity.HasIndex(e => e.LabelSourceId).HasDatabaseName("IX_AgentSessions_LabelSourceId");
            entity.HasIndex(e => new { e.LabelSourceId, e.LabelSessionName }).HasDatabaseName("IX_AgentSessions_LabelSourceId_LabelSessionName");

            // issued-130 T-001: composite index for the agent-scoped recency
            // list, plus single-column indexes on the two context-ref number
            // labels used by the issue/epic association reads.
            entity.HasIndex(e => new { e.LabelAgentId, e.LabelProjectId, e.CreatedAt })
                .HasDatabaseName("IX_AgentSessions_LabelAgentId_LabelProjectId_CreatedAt");
            entity.HasIndex(e => e.LabelAgentLaunchIssueNumber)
                .HasDatabaseName("IX_AgentSessions_LabelAgentLaunchIssueNumber");
            entity.HasIndex(e => e.LabelAgentLaunchEpicNumber)
                .HasDatabaseName("IX_AgentSessions_LabelAgentLaunchEpicNumber");
        });

        modelBuilder.Entity<AgentSessionTranscriptTurnRow>(entity =>
        {
            entity.ToTable("AgentSessionTranscriptTurns");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SessionId).HasMaxLength(512).IsRequired();
            entity.Property(e => e.RuntimeSessionId).HasMaxLength(256);
            entity.Property(e => e.PromptText).IsRequired();
            entity.Property(e => e.PromptKind).HasMaxLength(64).IsRequired();
            entity.HasIndex(e => new { e.SessionId, e.Sequence }).IsUnique();
            entity.HasIndex(e => new { e.SessionId, e.RuntimeSessionId, e.Sequence });
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
            entity.Property(e => e.PayloadStatus)
                .HasComputedColumnSql(EventReadKeys.PayloadStatusSql, stored: true);
            entity.HasIndex(e => new { e.TurnId, e.Sequence }).IsUnique();
            entity.HasIndex(e => new { e.TurnId, e.Type, e.CorrelationKey }).IsUnique();
            entity.HasIndex(e => new { e.Type, e.PayloadStatus, e.LastSeenAt, e.Id })
                .HasDatabaseName("IX_AgentSessionTranscriptParts_Type_PayloadStatus_LastSeenAt_Id");
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
            entity.Property(e => e.PauseReason).HasMaxLength(1024);
            entity.HasIndex(e => new { e.ProjectId, e.Status, e.CreatedAt });
            entity.HasIndex(e => new { e.ProjectId, e.Number });
        });

        modelBuilder.Entity<EpicIssueRow>(entity =>
        {
            entity.HasKey(e => new { e.EpicId, e.IssueId });
            entity.Property(e => e.EpicId).HasMaxLength(64);
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.IssueId).HasMaxLength(256).IsRequired();
            // Issue-179: relax uniqueness - an issue may hold a terminal-epic
            // membership (done/closed) AND a non-terminal membership
            // (idle/running/paused) concurrently so it can be re-homed from a
            // finished epic into a new active one. The active-membership slot
            // table below enforces the "at most one non-terminal epic per issue"
            // invariant at the database boundary.
            entity.HasIndex(e => new { e.ProjectId, e.IssueId });
            entity.HasIndex(e => new { e.ProjectId, e.IssueNumber });
        });

        modelBuilder.Entity<EpicActiveIssueRow>(entity =>
        {
            entity.HasKey(e => new { e.ProjectId, e.IssueId });
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.IssueId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.EpicId).HasMaxLength(64).IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.EpicId });
        });

        modelBuilder.Entity<IssueRow>(entity =>
        {
            entity.ToTable("Issues");
            entity.HasKey(e => e.IssueId);
            entity.Property(e => e.IssueId).HasMaxLength(256);
            entity.Property(e => e.State).IsRequired();
            entity.Property(e => e.Risk).HasMaxLength(16);
            entity.Property(e => e.EpicId).HasMaxLength(64);
            entity.Property(e => e.ProjectId)
                .HasComputedColumnSql("COALESCE(json_extract(State, '$.projectId'), json_extract(State, '$.ProjectId'))", stored: true);
            entity.Property(e => e.Number)
                .HasComputedColumnSql("COALESCE(json_extract(State, '$.number'), json_extract(State, '$.Number'))", stored: true);
            entity.Property(e => e.Status)
                .HasComputedColumnSql("COALESCE(json_extract(State, '$.status'), json_extract(State, '$.Status'))");
            entity.Property(e => e.WorkflowRunId)
                .HasComputedColumnSql("COALESCE(json_extract(State, '$.workflowRunId'), json_extract(State, '$.WorkflowRunId'))", stored: true)
                .IsConcurrencyToken();
            entity.Property(e => e.Title)
                .HasComputedColumnSql("COALESCE(json_extract(State, '$.title'), json_extract(State, '$.Title'))");
            entity.Property(e => e.Priority)
                .HasComputedColumnSql("COALESCE(json_extract(State, '$.priority'), json_extract(State, '$.Priority'))");
            entity.Property(e => e.IsDraft)
                .HasComputedColumnSql("COALESCE(json_extract(State, '$.isDraft'), json_extract(State, '$.IsDraft'))");
            entity.Property(e => e.PrerequisiteNumbersJson)
                .HasComputedColumnSql("COALESCE(json_extract(State, '$.prerequisiteNumbers'), json_extract(State, '$.PrerequisiteNumbers'))");
            entity.Property(e => e.IsArchived)
                .HasComputedColumnSql("json_extract(State, '$.archivedAt') IS NOT NULL");
            entity.HasIndex(e => new { e.ProjectId, e.Number }).IsUnique();
            entity.HasIndex(e => e.WorkflowRunId);
            entity.HasIndex(e => e.Status);
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

        modelBuilder.Entity<AgentSubscriptionRow>(entity =>
        {
            entity.ToTable("AgentSubscriptions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(256);
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.AgentId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(256).IsRequired();
            entity.Property(e => e.FilterType).HasMaxLength(256).IsRequired();
            entity.Property(e => e.FilterSource).HasMaxLength(512);
            entity.Property(e => e.FilterSubject).HasMaxLength(256);
            entity.Property(e => e.ResponsePrompt).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.AgentId })
                .HasDatabaseName("IX_AgentSubscriptions_ProjectId_AgentId");
            entity.HasIndex(e => e.ProjectId)
                .HasDatabaseName("IX_AgentSubscriptions_ProjectId");
            entity.HasIndex(e => new { e.AgentId, e.Name })
                .IsUnique()
                .HasDatabaseName("UX_AgentSubscriptions_AgentId_Name");
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
            entity.Property(e => e.TimeSortKey)
                .HasComputedColumnSql(EventReadKeys.TimeSortKeySql, stored: true);
            entity.Property(e => e.DispatchedAt);
            entity.HasIndex(nameof(IssueEventRow.Type), nameof(IssueEventRow.Source), nameof(IssueEventRow.Id));
            entity.HasIndex(e => new { e.TimeSortKey, e.Source, e.Id })
                .HasDatabaseName("IX_IssueEvents_TimeSortKey_Source_Id");
            entity.HasIndex(e => new { e.Source, e.Id, e.DispatchedAt })
                .HasFilter("\"DispatchedAt\" IS NULL")
                .HasDatabaseName("IX_IssueEvents_Source_Id_DispatchedAt");
        });

        modelBuilder.Entity<EpicEventRow>(entity =>
        {
            entity.ToTable("EpicEvents");
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
            entity.Property(e => e.DispatchedAt);
            entity.HasIndex(nameof(EpicEventRow.Type), nameof(EpicEventRow.Source), nameof(EpicEventRow.Id));
            entity.HasIndex(e => new { e.Source, e.Id, e.DispatchedAt })
                .HasFilter("\"DispatchedAt\" IS NULL")
                .HasDatabaseName("IX_EpicEvents_Source_Id_DispatchedAt");
        });

        modelBuilder.Entity<AgentSessionEventRow>(entity =>
        {
            entity.ToTable("AgentSessionEvents");
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
            entity.Property(e => e.TimeSortKey)
                .HasComputedColumnSql(EventReadKeys.TimeSortKeySql, stored: true);
            entity.Property(e => e.DataStatus)
                .HasComputedColumnSql(EventReadKeys.DataStatusSql, stored: true);
            entity.Property(e => e.DispatchedAt);
            entity.HasIndex(nameof(AgentSessionEventRow.Type), nameof(AgentSessionEventRow.Source), nameof(AgentSessionEventRow.Id));
            entity.HasIndex(e => new { e.Type, e.Time });
            entity.HasIndex(e => new { e.TimeSortKey, e.Source, e.Id })
                .HasDatabaseName("IX_AgentSessionEvents_TimeSortKey_Source_Id");
            entity.HasIndex(e => new { e.DataStatus, e.Type, e.TimeSortKey, e.Source, e.Id })
                .HasDatabaseName("IX_AgentSessionEvents_DataStatus_Type_TimeSortKey_Source_Id");
            entity.HasIndex(e => new { e.Source, e.Id })
                .HasFilter("\"DispatchedAt\" IS NULL")
                .HasDatabaseName("IX_AgentSessionEvents_Undelivered");
        });

        modelBuilder.Entity<DeadLetterRow>(entity =>
        {
            entity.ToTable("DeadLetters");
            entity.HasKey(e => e.DeadLetterId);
            entity.Property(e => e.DeadLetterId).ValueGeneratedOnAdd();
            entity.Property(e => e.Origin)
                .HasMaxLength(32)
                .IsRequired();
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
            entity.Property(e => e.FailingHandler)
                .HasMaxLength(512)
                .IsRequired();
            entity.Property(e => e.ErrorMessage)
                .IsRequired();
            entity.Property(e => e.AttemptCount)
                .IsRequired();
            entity.Property(e => e.DeadLetteredAt)
                .IsRequired();
            entity.Property(e => e.Status)
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();
            entity.Property(e => e.RedeliveryAttemptedAt);
            entity.Property(e => e.ResolvedAt);
            entity.HasIndex(e => e.DeadLetteredAt);
            entity.HasIndex(e => new { e.FailingHandler, e.DeadLetteredAt });
            entity.HasIndex(e => new { e.Source, e.Id, e.FailingHandler })
                .IsUnique();
        });

        modelBuilder.Entity<WorkflowRunRow>(entity =>
        {
            entity.HasKey(e => e.WorkflowRunId);
            entity.Property(e => e.WorkflowRunId).HasMaxLength(50);
            entity.Property(e => e.State).IsRequired();
            entity.Property(e => e.EpicId).HasMaxLength(64);
            entity.Property<long>("ETag").IsConcurrencyToken();
            entity.Property(e => e.MetadataProjectId)
                .HasComputedColumnSql("COALESCE(json_extract(State, '$.metadata.annotations.projectId'), json_extract(State, '$.Metadata.Annotations.projectId'), json_extract(State, '$.Metadata.Annotations.ProjectId'))", stored: true);
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
            // Issue-318 D3: STORED status computed column. Mirrors the
            // COALESCE path-robustness pattern used by IssueRow.ProjectId /
            // AgentRow.Status; LOWER normalizes the camelCase enum value
            // (e.g. "ready", "pending") so the column is always lowercase
            // regardless of any PascalCase historical state. The matching
            // IX_WorkflowRuns_Status index is created in T-004 migration;
            // T-002 declares the model-side projection only.
            entity.Property(e => e.Status)
                .HasComputedColumnSql("LOWER(COALESCE(json_extract(State, '$.status'), json_extract(State, '$.Status')))", stored: true);
            entity.HasIndex(e => e.MetadataProjectId);
            entity.HasIndex(e => e.AssignedWorkerId);
            entity.HasIndex(e => new { e.MetadataProjectId, e.AssignedWorkerId, e.CreatedAt });
            // Issue-318 D3: covering index for the two scheduler queries
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
        });

        modelBuilder.Entity<WorkflowVariablesRow>(entity =>
        {
            entity.HasKey(e => e.WorkflowRunId);
            entity.Property(e => e.WorkflowRunId).HasMaxLength(256);
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
            entity.Property(e => e.DisableDefaultIssueTemplate).IsRequired().HasDefaultValue(false);
            entity.Property(e => e.Prompts)
                .HasConversion(
                    v => JSON.Serialize(v),
                    v => JSON.DeserializeDictionary(v))
                .IsRequired()
                .HasDefaultValue(new Dictionary<string, string>());
            entity.Property(e => e.Prompts).Metadata.SetValueComparer(DictionaryStringComparer);

            entity.Property(e => e.DisabledWorkflowProfileIds)
                .HasConversion(
                    v => JSON.Serialize(v),
                    v => JSON.Deserialize<List<string>>(v) ?? new List<string>())
                .IsRequired()
                .HasDefaultValue(new List<string>());
            entity.Property(e => e.DisabledWorkflowProfileIds).Metadata.SetValueComparer(ListStringComparer);
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

        modelBuilder.Entity<ProjectIssueTemplateRow>(entity =>
        {
            entity.ToTable("ProjectIssueTemplates");
            entity.HasKey(e => new { e.ProjectId, e.Name });
            entity.Property(e => e.ProjectId).HasMaxLength(256);
            entity.Property(e => e.Name).HasMaxLength(256);
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

        modelBuilder.Entity<WorkflowRunProfileRow>(entity =>
        {
            entity.ToTable("WorkflowRunProfiles");
            entity.HasKey(e => e.WorkflowRunId);
            entity.Property(e => e.WorkflowRunId).HasMaxLength(256);
            entity.Property(e => e.Variables).IsRequired();
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

        modelBuilder.Entity<LabelDefinitionRow>(entity =>
        {
            entity.ToTable("LabelDefinitions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Key).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Description).IsRequired();
            entity.Property(e => e.SupportedValuesJson).IsRequired().HasDefaultValue("[]");
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.Key }).IsUnique();
        });

        modelBuilder.Entity<RunnerRow>(entity =>
        {
            entity.ToTable("Runners");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Slots).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.ToTable(t => t.HasCheckConstraint("CK_Runners_Slots_Positive", "\"Slots\" > 0"));
        });

        modelBuilder.Entity<RunnerWorkRow>(entity =>
        {
            entity.ToTable("RunnerWorks");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.RunnerId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.OwnerKind).HasMaxLength(16).IsRequired();
            entity.Property(e => e.OwnerId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.WorkId).HasMaxLength(128).IsRequired();
            entity.Property(e => e.TakenAt).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(16).IsRequired();
            entity.Property(e => e.Reason).HasMaxLength(256);
            entity.HasIndex(e => new { e.RunnerId, e.Status });
            entity.HasIndex(e => new { e.RunnerId, e.OwnerKind, e.OwnerId, e.WorkId });
        });

        modelBuilder.Entity<TaskLogEntryRow>(entity =>
        {
            entity.ToTable("TaskLogEntries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.OwnerKind).HasMaxLength(16).IsRequired();
            entity.Property(e => e.OwnerId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.WorkId).HasMaxLength(128).IsRequired();
            entity.Property(e => e.Seq).IsRequired();
            entity.Property(e => e.Timestamp).IsRequired();
            entity.Property(e => e.Source).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Text).IsRequired();
            // issue-336 T-001: composite index supports cursor pagination
            // over (OwnerKind, OwnerId, WorkId, Seq) and also enforces
            // owner-kind routing isolation between workflow and agent-job
            // entries (the two owner kinds share no key space).
            entity.HasIndex(e => new { e.OwnerKind, e.OwnerId, e.WorkId, e.Seq })
                .IsUnique()
                .HasDatabaseName("IX_TaskLogEntries_Owner_WorkId_Seq");
        });

        modelBuilder.Entity<TaskLogBatchRow>(entity =>
        {
            entity.ToTable("TaskLogBatches");
            entity.HasKey(e => new { e.OwnerKind, e.OwnerId, e.WorkId });
            entity.Property(e => e.OwnerKind).HasMaxLength(16).IsRequired();
            entity.Property(e => e.OwnerId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.WorkId).HasMaxLength(128).IsRequired();
            entity.Property(e => e.UploadedAt).IsRequired();
        });

        modelBuilder.Entity<InboxItemRow>(entity =>
        {
            entity.ToTable("InboxItems", table =>
            {
                table.HasCheckConstraint(
                    "CK_InboxItems_NotificationKind",
                    "\"NotificationKind\" IN ('workflow_failed', 'approval_requested', 'issue_started', 'issue_completed')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.IssueId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.IssueTitle).HasMaxLength(512);
            entity.Property(e => e.NotificationKind).HasMaxLength(32).IsRequired();
            entity.Property(e => e.SourceEventSource).HasMaxLength(512).IsRequired();
            entity.Property(e => e.SourceEventId).HasMaxLength(128).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.ReadAt);
            entity.Property(e => e.ArchivedAt);
            // Most-recent-first list query scoped to one project.
            entity.HasIndex(e => new { e.ProjectId, e.CreatedAt })
                .HasDatabaseName("IX_InboxItems_ProjectId_CreatedAt")
                .IsDescending(false, true);
            // Idempotency: CloudEvents are uniquely identified by source + id.
            entity.HasIndex(e => new { e.SourceEventSource, e.SourceEventId })
                .IsUnique()
                .HasDatabaseName("UQ_InboxItems_SourceEvent");
            // Project-scoped lookups for mark-read / archive mutations.
            entity.HasIndex(e => new { e.ProjectId, e.Id })
                .HasDatabaseName("IX_InboxItems_ProjectId_Id");
        });

        modelBuilder.Entity<InboxSubscriptionRow>(entity =>
        {
            entity.ToTable("InboxSubscriptions");
            entity.HasKey(e => e.ProjectId);
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.WorkflowFailedEnabled).IsRequired();
            entity.Property(e => e.ApprovalRequestedEnabled).IsRequired();
            entity.Property(e => e.IssueStartedEnabled).IsRequired();
            entity.Property(e => e.IssueCompletedEnabled).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.HasOne<ProjectRow>()
                .WithOne()
                .HasForeignKey<InboxSubscriptionRow>(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
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

    // issued-130 T-001: build a json_extract stored-column expression whose
    // path is keyed by a label-name constant. Returning the expression from
    // one helper means a rename in GenericAgentSessionMetadata is a
    // compile-time error rather than a silent SQL/metadata drift.
    private static string JsonExtractLabel(string key) =>
        $$"""json_extract("State", '$.metadata.labels."{{key}}"')""";

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
