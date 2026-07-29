using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.AgentJobs;
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
using Mohist.Server.Infrastructure.Data.Secrets;
using Mohist.Server.Infrastructure.Data.Slack;
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
    public DbSet<WorkflowProfileRecordRow> WorkflowProfileRecords { get; set; } = null!;
    public DbSet<WorkflowRunEventRow> WorkflowRunEvents { get; set; } = null!;
    public DbSet<AgentSessionRow> AgentSessions { get; set; } = null!;
    public DbSet<AgentSessionTranscriptTurnRow> AgentSessionTranscriptTurns { get; set; } = null!;
    public DbSet<AgentSessionTranscriptPartRow> AgentSessionTranscriptParts { get; set; } = null!;
    public DbSet<IssueCommentRow> IssueComments { get; set; } = null!;
    public DbSet<AttachmentRow> Attachments { get; set; } = null!;
    public DbSet<IssuePrerequisiteRow> IssuePrerequisites { get; set; } = null!;
    public DbSet<EpicRow> Epics { get; set; } = null!;
    public DbSet<IssueRow> Issues { get; set; } = null!;
    public DbSet<AgentRow> Agents { get; set; } = null!;
    public DbSet<RoutingRuleRow> RoutingRules { get; set; } = null!;
    public DbSet<WatchEntryRow> WatchEntries { get; set; } = null!;
    public DbSet<AgentConnectionRow> AgentConnections { get; set; } = null!;
    public DbSet<IssueEventRow> IssueEvents { get; set; } = null!;
    public DbSet<EpicEventRow> EpicEvents { get; set; } = null!;
    public DbSet<AgentSessionEventRow> AgentSessionEvents { get; set; } = null!;
    public DbSet<AgentJobEventRow> AgentJobEvents { get; set; } = null!;
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
    public DbSet<AgentJobRow> AgentJobs { get; set; } = null!;
    public DbSet<ConnectionSecretRow> ConnectionSecrets { get; set; } = null!;
    public DbSet<SlackProviderInboxRow> SlackProviderInboxRows { get; set; } = null!;
    public DbSet<SlackOutboxRow> SlackOutboxRows { get; set; } = null!;
    public DbSet<SlackOwnerClaimCodeRow> SlackOwnerClaimCodes { get; set; } = null!;

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
            entity.Property(e => e.AgentSessionId);
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
            entity.Property(e => e.LabelTriggerRuleId)
                .HasComputedColumnSql(JsonExtractLabel(GenericAgentSessionMetadata.TriggerRuleId), stored: false);

            // Virtual projection of status.activity, lowered
            // to match the existing Status column convention. Powers the
            // history-bounded direct-session candidate selection in
            // AgentSessionQuery.ListStatusCandidatesAsync; the matching
            // composite index (LabelProjectId, SourceKind, Activity,
            // CreatedAt) is added below so the candidate predicate uses an
            // index-only path.
            entity.Property(e => e.Activity)
                .HasComputedColumnSql(
                    "LOWER(COALESCE(json_extract(\"State\", '$.status.activity'), json_extract(\"State\", '$.status.Activity')))");

            entity.HasIndex(e => new { e.LabelProjectId, e.CreatedAt }).HasDatabaseName("IX_AgentSessions_LabelProjectId_CreatedAt");
            entity.HasIndex(e => e.LabelSourceId).HasDatabaseName("IX_AgentSessions_LabelSourceId");
            entity.HasIndex(e => new { e.LabelSourceId, e.LabelSessionName }).HasDatabaseName("IX_AgentSessions_LabelSourceId_LabelSessionName");
            entity.HasIndex(e => new { e.LabelProjectId, e.LabelIssueNumber, e.CreatedAt })
                .HasDatabaseName("IX_AgentSessions_LabelProjectId_LabelIssueNumber_CreatedAt");

            // Composite index for the agent-scoped recency
            // list, plus single-column indexes on the two context-ref number
            // labels used by the issue/epic association reads.
            entity.HasIndex(e => new { e.LabelAgentId, e.LabelProjectId, e.CreatedAt })
                .HasDatabaseName("IX_AgentSessions_LabelAgentId_LabelProjectId_CreatedAt");
            entity.HasIndex(e => e.LabelAgentLaunchIssueNumber)
                .HasDatabaseName("IX_AgentSessions_LabelAgentLaunchIssueNumber");
            entity.HasIndex(e => new { e.LabelProjectId, e.LabelAgentLaunchIssueNumber, e.CreatedAt })
                .HasDatabaseName("IX_AgentSessions_LabelProjectId_LabelAgentLaunchIssueNumber_CreatedAt");
            entity.HasIndex(e => e.LabelAgentLaunchEpicNumber)
                .HasDatabaseName("IX_AgentSessions_LabelAgentLaunchEpicNumber");
            entity.HasIndex(e => new { e.LabelProjectId, e.LabelAgentLaunchEpicNumber, e.CreatedAt })
                .HasDatabaseName("IX_AgentSessions_LabelProjectId_LabelAgentLaunchEpicNumber_CreatedAt");

            // Direct status candidate index. Composite on
            // (LabelProjectId, LabelSourceKind, Activity, CreatedAt) so the
            // direct-session branch of AgentSessionQuery.ListStatusCandidatesAsync
            // resolves through a single index scan ordered by CreatedAt
            // DESC, never touching historical inactive / completed rows.
            entity.HasIndex(e => new { e.LabelProjectId, e.LabelSourceKind, e.Activity, e.CreatedAt })
                .HasDatabaseName("IX_AgentSessions_StatusProject_SourceKind_Activity_CreatedAt");
        });

        modelBuilder.Entity<AgentSessionTranscriptTurnRow>(entity =>
        {
            entity.ToTable("AgentSessionTranscriptTurns");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SessionId).HasMaxLength(512).IsRequired();
            entity.Property(e => e.RuntimeSessionId);
            entity.Property(e => e.PromptText).IsRequired();
            entity.Property(e => e.PromptKind).HasMaxLength(64).IsRequired();
            entity.HasIndex(e => new { e.SessionId, e.Sequence }).IsUnique();
            entity.HasIndex(e => new { e.SessionId, e.RuntimeSessionId, e.Sequence });
        });

        modelBuilder.Entity<AgentJobRow>(entity =>
        {
            entity.ToTable("AgentJobs");
            entity.HasKey(e => e.JobKey);
            entity.Property(e => e.JobKey).HasMaxLength(512);
            entity.Property(e => e.State).IsRequired();
            entity.Property(e => e.ProjectId)
                .HasComputedColumnSql("""json_extract("State", '$.input.projectId')""", stored: true);
            entity.Property(e => e.AgentId)
                .HasComputedColumnSql("""json_extract("State", '$.input.agentId')""", stored: true);
            entity.Property(e => e.Status)
                .HasComputedColumnSql("""json_extract("State", '$.status')""", stored: true);
            entity.Property(e => e.SubmittedAt)
                .HasComputedColumnSql("""json_extract("State", '$.submittedAt')""", stored: true);
            entity.Property(e => e.TerminalAt)
                .HasComputedColumnSql("""json_extract("State", '$.terminalAt')""", stored: true);
            entity.HasIndex(e => new { e.AgentId, e.ProjectId, e.SubmittedAt })
                .HasDatabaseName("IX_AgentJobs_AgentId_ProjectId_SubmittedAt");
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
            entity.Property(e => e.Author).HasMaxLength(100);
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
            entity.Property(e => e.OwnerIssueNumber);
            entity.Property(e => e.OriginalFileName).HasMaxLength(512).IsRequired();
            entity.Property(e => e.ContentType).HasMaxLength(128);
            entity.Property(e => e.StoragePath).HasMaxLength(1024).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.OwnerKind, e.OwnerId })
                .HasDatabaseName("IX_Attachments_ProjectId_Owner");
            entity.HasIndex(e => new { e.ProjectId, e.OwnerKind, e.OwnerIssueNumber })
                .HasDatabaseName("IX_Attachments_ProjectId_OwnerIssueNumber");
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
            entity.HasKey(e => new { e.ProjectId, e.Number });
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(512).IsRequired();
            entity.Property(e => e.Priority).HasMaxLength(16).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.PauseReason).HasMaxLength(1024);
            entity.HasIndex(e => new { e.ProjectId, e.Status, e.CreatedAt });
            entity.HasIndex(e => new { e.ProjectId, e.Number }).IsUnique();
        });

        modelBuilder.Entity<IssueRow>(entity =>
        {
            entity.ToTable("Issues");
            entity.HasKey(e => new { e.ProjectId, e.Number });
            entity.Property(e => e.State).IsRequired();
            entity.Property(e => e.Risk).HasMaxLength(16);
            entity.Property(e => e.ProjectId)
                .HasMaxLength(256)
                .IsRequired();
            entity.Property(e => e.Number)
                .IsRequired();
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
            entity.Property(e => e.ParentIssueNumber);
            entity.Property(e => e.IsArchived)
                .HasComputedColumnSql("json_extract(State, '$.archivedAt') IS NOT NULL");
            // Stored generated RepositoryName projected
            // from Issue state JSON. Powers the (ProjectId, RepositoryName,
            // Status) index used by repository-deletion blocker checks and
            // list filtering.
            entity.Property(e => e.RepositoryName)
                .HasComputedColumnSql("COALESCE(json_extract(State, '$.repositoryRef'), json_extract(State, '$.RepositoryRef'))", stored: true);
            // Explicit Issue WorkflowProfile selection is
            // surfaced from State JSON so the public reference stays one
            // place. The coordinator copy + this projection are the only
            // writes; the FK backstop is on WorkflowProfileIdKey.
            entity.Property(e => e.WorkflowProfileIdKey).HasMaxLength(256);
            entity.HasIndex(e => new { e.ProjectId, e.WorkflowProfileIdKey })
                .HasDatabaseName("IX_Issues_ProjectId_WorkflowProfileIdKey");
            entity.HasOne<WorkflowProfileRecordRow>()
                .WithMany()
                .HasForeignKey(e => new { e.ProjectId, e.WorkflowProfileIdKey })
                .HasPrincipalKey(e => new { e.ProjectId, e.ProfileId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.ProjectId, e.Number }).IsUnique();
            entity.HasIndex(e => new { e.ProjectId, e.EpicNumber, e.Number });
            entity.HasIndex(e => new { e.ProjectId, e.ParentIssueNumber, e.Number });
            entity.HasIndex(e => e.WorkflowRunId);
            entity.HasIndex(e => e.Status);
            // Deletion-blocker + list filter index.
            entity.HasIndex(e => new { e.ProjectId, e.RepositoryName, e.Status })
                .HasDatabaseName("IX_Issues_ProjectId_RepositoryName_Status");
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

        modelBuilder.Entity<RoutingRuleRow>(entity =>
        {
            entity.ToTable("RoutingRules");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Position).IsRequired();
            entity.Property(e => e.Match).IsRequired();
            entity.Property(e => e.AgentId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ResponsePrompt).IsRequired();
            entity.Property(e => e.Continue).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.Name })
                .IsUnique()
                .HasDatabaseName("UX_RoutingRules_ProjectId_Name");
            entity.HasIndex(e => new { e.ProjectId, e.Position })
                .HasDatabaseName("IX_RoutingRules_ProjectId_Position");
            entity.HasIndex(e => e.ProjectId)
                .HasDatabaseName("IX_RoutingRules_ProjectId");
        });

        modelBuilder.Entity<WatchEntryRow>(entity =>
        {
            entity.ToTable("WatchEntries");
            entity.HasKey(e => new { e.ProjectId, e.IssueNumber, e.AgentId });
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.IssueNumber).IsRequired();
            entity.Property(e => e.AgentId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.State).HasMaxLength(16).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.IssueNumber, e.AgentId })
                .IsUnique()
                .HasDatabaseName("UX_WatchEntries_ProjectId_IssueNumber_AgentId");
            entity.HasIndex(e => new { e.ProjectId, e.IssueNumber })
                .HasDatabaseName("IX_WatchEntries_ProjectId_IssueNumber");
            entity.HasIndex(e => new { e.ProjectId, e.IssueNumber, e.State })
                .HasDatabaseName("IX_WatchEntries_ProjectId_IssueNumber_State");
        });

        modelBuilder.Entity<AgentConnectionRow>(entity =>
        {
            entity.ToTable("AgentConnections");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.AgentId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ProviderKind).HasMaxLength(32).IsRequired();
            entity.Property(e => e.WorkspaceTeamId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.AppId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.BotUserId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.BotName).HasMaxLength(512).IsRequired();
            entity.Property(e => e.AvatarHash).HasMaxLength(512);
            entity.Property(e => e.SetupProgress).HasMaxLength(64).IsRequired();
            entity.Property(e => e.DesiredState).HasMaxLength(32).IsRequired();
            entity.Property(e => e.ConnectionHealth).HasMaxLength(32).IsRequired();
            entity.Property(e => e.HealthReason).HasMaxLength(1024);
            entity.Property(e => e.AgentReadiness).HasMaxLength(32).IsRequired();
            entity.Property(e => e.OwnerSlackUserId).HasMaxLength(256);
            entity.Property(e => e.LastHeartbeatAt);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.Property(e => e.DeletedAt);
            entity.HasIndex(e => new { e.ProjectId, e.AgentId, e.WorkspaceTeamId })
                .IsUnique()
                .HasFilter("\"DeletedAt\" IS NULL")
                .HasDatabaseName("UX_AgentConnections_ProjectId_AgentId_WorkspaceTeamId");
            entity.HasIndex(e => new { e.ProjectId, e.AgentId })
                .HasDatabaseName("IX_AgentConnections_ProjectId_AgentId");
            entity.HasIndex(e => e.Id)
                .HasDatabaseName("IX_AgentConnections_Id");
        });

        modelBuilder.Entity<SlackOwnerClaimCodeRow>(entity =>
        {
            entity.ToTable("SlackOwnerClaimCodes");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ConnectionId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.CodeHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ExpiresAt).IsRequired();
            entity.Property(e => e.SupersededBy).HasMaxLength(256);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.ConnectionId, e.CodeHash })
                .IsUnique()
                .HasDatabaseName("UX_SlackOwnerClaimCodes_ProjectId_ConnectionId_CodeHash");
            entity.HasIndex(e => new { e.ProjectId, e.ConnectionId, e.UsedAt, e.SupersededBy });
        });

        modelBuilder.Entity<IssueEventRow>(entity =>
        {
            entity.ToTable("IssueEvents");
            entity.HasKey(e => new { e.Source, e.Id });
            entity.Property(e => e.Id).IsRequired();
            entity.Property(e => e.Source)
                .HasMaxLength(256)
                .IsRequired();
            entity.Property(e => e.TimelineSource)
                .HasMaxLength(256)
                .IsRequired()
                .HasDefaultValue("");
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
            entity.HasIndex(e => new { e.TimelineSource, e.Time, e.Source, e.Id })
                .HasDatabaseName("IX_IssueEvents_TimelineSource_Time_Source_Id");
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
            entity.Property(e => e.TimelineSource)
                .HasMaxLength(256)
                .IsRequired()
                .HasDefaultValue("");
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
            entity.HasIndex(e => new { e.TimelineSource, e.Time, e.Source, e.Id })
                .HasDatabaseName("IX_EpicEvents_TimelineSource_Time_Source_Id");
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

        modelBuilder.Entity<AgentJobEventRow>(entity =>
        {
            entity.ToTable("AgentJobEvents");
            entity.HasKey(e => new { e.Source, e.Id });
            entity.Property(e => e.Id).IsRequired();
            entity.Property(e => e.Source)
                .HasMaxLength(256)
                .IsRequired();
            entity.Property(e => e.EventId)
                .HasMaxLength(128)
                .IsRequired();
            entity.HasIndex(e => new { e.Source, e.EventId })
                .IsUnique();
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
            entity.HasIndex(nameof(AgentJobEventRow.Type), nameof(AgentJobEventRow.Source), nameof(AgentJobEventRow.Id));
            entity.HasIndex(e => new { e.Type, e.Time });
            entity.HasIndex(e => new { e.TimeSortKey, e.Source, e.Id })
                .HasDatabaseName("IX_AgentJobEvents_TimeSortKey_Source_Id");
            entity.HasIndex(e => new { e.DataStatus, e.Type, e.TimeSortKey, e.Source, e.Id })
                .HasDatabaseName("IX_AgentJobEvents_DataStatus_Type_TimeSortKey_Source_Id");
            entity.HasIndex(e => new { e.Source, e.Id })
                .HasFilter("\"DispatchedAt\" IS NULL")
                .HasDatabaseName("IX_AgentJobEvents_Undelivered");
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
            entity.HasIndex(e => e.MetadataProjectId);
            entity.HasIndex(e => e.AssignedWorkerId);
            entity.HasIndex(e => new { e.MetadataProjectId, e.AssignedWorkerId, e.CreatedAt });
            entity.HasIndex(e => new { e.MetadataProjectId, e.IssueNumber })
                .HasDatabaseName("IX_WorkflowRuns_ProjectId_IssueNumber");
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
            entity.Property(e => e.DefaultWorkflowProfileId).HasMaxLength(256);
            entity.Property(e => e.DefaultWorkflowProfileIdKey).HasMaxLength(256);
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
            entity.HasOne<WorkflowProfileRecordRow>()
                .WithMany()
                .HasForeignKey(e => new { e.ProjectId, e.DefaultWorkflowProfileIdKey })
                .HasPrincipalKey(e => new { e.ProjectId, e.ProfileId })
                .OnDelete(DeleteBehavior.Restrict);
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

        modelBuilder.Entity<WorkflowProfileRecordRow>(entity =>
        {
            entity.ToTable("WorkflowProfileRecords");
            entity.HasKey(e => new { e.ProjectId, e.ProfileId });
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ProfileId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(512).IsRequired();
            entity.Property(e => e.Description).IsRequired();
            entity.Property(e => e.DefinitionSource).IsRequired();
            entity.Property(e => e.SourceProvenance).HasMaxLength(32).IsRequired();
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
            entity.HasKey(e => new { e.ProjectId, e.IssueNumber });
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.IssueNumber).IsRequired();
            entity.Property(e => e.SourceTemplateId).HasMaxLength(256);
            entity.Property(e => e.Variables).IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.IssueNumber }).IsUnique();
        });

        modelBuilder.Entity<WorkflowRunProfileRow>(entity =>
        {
            entity.ToTable("WorkflowRunProfiles");
            entity.HasKey(e => e.WorkflowRunId);
            entity.Property(e => e.WorkflowRunId).HasMaxLength(256);
            entity.Property(e => e.Variables).IsRequired();
            entity.Property(e => e.DefaultVariables).IsRequired().HasDefaultValue("{}");
            // Optimistic concurrency: SQLite has no native rowversion, so we
            // mirror WorkflowRunRow's manually-incremented long ETag. EF still
            // adds it to every UPDATE's WHERE and raises
            // DbUpdateConcurrencyException on conflict; writers bump it on save.
            entity.Property<long>("ETag").IsConcurrencyToken();
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
            entity.Property(e => e.IssueNumber);
            entity.Property(e => e.DisplayName).HasMaxLength(512);

            // Latest per path within a workflow run, plus history scans.
            entity.HasIndex(e => new { e.WorkflowRunId, e.Path, e.RecordedAt })
                .HasDatabaseName("IX_WorkflowArtifacts_WorkflowRunId_Path_RecordedAt");
            // Task-run filter and history ordering.
            entity.HasIndex(e => new { e.WorkflowRunId, e.TaskRunId, e.RecordedAt })
                .HasDatabaseName("IX_WorkflowArtifacts_WorkflowRunId_TaskRunId_RecordedAt");
            // Issue-scoped latest projection support.
            entity.HasIndex(e => new { e.ProjectId, e.IssueNumber, e.RecordedAt })
                .HasDatabaseName("IX_WorkflowArtifacts_ProjectId_IssueNumber_RecordedAt");
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
            // Composite index supports cursor pagination
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
                    "\"NotificationKind\" IN ('workflow_failed', 'approval_requested', 'issue_started', 'issue_completed', 'agent_response_failed')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
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
            entity.Property(e => e.AgentResponseFailedEnabled).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.HasOne<ProjectRow>()
                .WithOne()
                .HasForeignKey<InboxSubscriptionRow>(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ConnectionSecretRow>(entity =>
        {
            entity.ToTable("ConnectionSecrets");
            entity.HasKey(e => new { e.ProjectId, e.ConnectionId, e.Kind });
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ConnectionId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Kind).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Blob).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.ToTable(t => t.HasCheckConstraint(
                "CK_ConnectionSecrets_Kind",
                "\"Kind\" IN ('appToken', 'botToken')"));
            entity.HasIndex(e => new { e.ProjectId, e.ConnectionId })
                .HasDatabaseName("IX_ConnectionSecrets_ProjectId_ConnectionId");
        });

        modelBuilder.Entity<SlackProviderInboxRow>(entity =>
        {
            entity.ToTable("SlackProviderInboxRows");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ConnectionId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.SlackMessageIdentity).HasMaxLength(512).IsRequired();
            entity.Property(e => e.WorkspaceTeamId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.DmConversationId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.SlackUserId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.AcceptedAt).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasIndex(e => new { e.ConnectionId, e.SlackMessageIdentity })
                .IsUnique()
                .HasDatabaseName("UX_SlackProviderInboxRows_ConnectionId_SlackMessageIdentity");
            entity.HasIndex(e => new { e.ProjectId, e.ConnectionId, e.DispatchedAt })
                .HasDatabaseName("IX_SlackProviderInboxRows_ProjectId_ConnectionId_DispatchedAt");
        });

        modelBuilder.Entity<SlackOutboxRow>(entity =>
        {
            entity.ToTable("SlackOutboxRows", table =>
            {
                table.HasCheckConstraint(
                    "CK_SlackOutboxRows_Kind",
                    "\"Kind\" IN ('replaceable_progress', 'terminal_result', 'explicit_failure', 'user_action')");
                table.HasCheckConstraint(
                    "CK_SlackOutboxRows_State",
                    "\"State\" IN ('pending', 'claimed', 'delivered', 'delivery_uncertain', 'dead_lettered')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ConnectionId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.WorkspaceTeamId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.DmConversationId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Kind).HasMaxLength(32).IsRequired();
            entity.Property(e => e.State).HasMaxLength(32).IsRequired();
            entity.Property(e => e.DispatchRef).HasMaxLength(256);
            entity.Property(e => e.PayloadJson).IsRequired();
            entity.Property(e => e.ClaimedByAdapterId).HasMaxLength(256);
            entity.Property(e => e.LastError).HasMaxLength(1024);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.ConnectionId, e.State })
                .HasDatabaseName("IX_SlackOutboxRows_ProjectId_ConnectionId_State");
            entity.HasIndex(e => new { e.ConnectionId, e.State, e.NextAttemptAt })
                .HasDatabaseName("IX_SlackOutboxRows_ConnectionId_State_NextAttemptAt");
            entity.HasIndex(e => new { e.ConnectionId, e.State, e.ClaimedAt })
                .HasDatabaseName("IX_SlackOutboxRows_ConnectionId_State_ClaimedAt");
            entity.HasIndex(e => new { e.ConnectionId, e.State, e.DeliveryUncertainAt })
                .HasDatabaseName("IX_SlackOutboxRows_ConnectionId_State_DeliveryUncertainAt");
            entity.HasIndex(e => new { e.ConnectionId, e.DispatchRef, e.Kind, e.State })
                .HasDatabaseName("IX_SlackOutboxRows_ConnectionId_DispatchRef_Kind_State");
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

    // Build a json_extract stored-column expression whose
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
