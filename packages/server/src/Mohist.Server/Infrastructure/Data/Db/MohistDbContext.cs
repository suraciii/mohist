using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using System.Text.Json;
using Mohist.Server.Infrastructure.Data.GitHub;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Auth;
using Mohist.Server.Infrastructure.Data.Webhooks;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Workflow.Prompts;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Data.Label;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Infrastructure.Data.Inbox;
using Mohist.Server.Infrastructure.Data.Secrets;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Data.Workspace;
using Mohist.Server.Inbox;
using Mohist.Server.Slack.Domain;
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

    public DbSet<CredentialRow> Credentials { get; set; } = null!;
    public DbSet<EnrollmentTokenRow> EnrollmentTokens { get; set; } = null!;
    public DbSet<PrincipalRow> Principals { get; set; } = null!;
    public DbSet<ProjectRow> Projects { get; set; } = null!;
    public DbSet<ProjectWorkflowProfile> ProjectWorkflowProfiles { get; set; } = null!;
    public DbSet<ProjectWorkflowTemplateRow> ProjectWorkflowTemplates { get; set; } = null!;
    public DbSet<WorkflowProfileRecordRow> WorkflowProfileRecords { get; set; } = null!;
    public DbSet<WorkflowRunEventRow> WorkflowRunEvents { get; set; } = null!;
    public DbSet<AgentSessionRow> AgentSessions { get; set; } = null!;
    public DbSet<SessionTreeGraphRevisionRow> SessionTreeGraphRevisions { get; set; } = null!;
    public DbSet<AgentSessionTranscriptTurnRow> AgentSessionTranscriptTurns { get; set; } = null!;
    public DbSet<AgentSessionTranscriptPartRow> AgentSessionTranscriptParts { get; set; } = null!;
    public DbSet<IssueCommentRow> IssueComments { get; set; } = null!;
    public DbSet<AttachmentRow> Attachments { get; set; } = null!;
    public DbSet<IssuePrerequisiteRow> IssuePrerequisites { get; set; } = null!;
    public DbSet<EpicRow> Epics { get; set; } = null!;
    public DbSet<IssueRow> Issues { get; set; } = null!;
    public DbSet<AgentRow> Agents { get; set; } = null!;
    public DbSet<RoutingRuleRow> RoutingRules { get; set; } = null!;
    public DbSet<WebhookSubscriptionRow> WebhookSubscriptions { get; set; } = null!;
    public DbSet<WebhookDeliveryFailureRow> WebhookDeliveryFailures { get; set; } = null!;
    public DbSet<WatchEntryRow> WatchEntries { get; set; } = null!;
    public DbSet<AgentConnectionRow> AgentConnections { get; set; } = null!;
    public DbSet<IssueEventRow> IssueEvents { get; set; } = null!;
    public DbSet<EpicEventRow> EpicEvents { get; set; } = null!;
    public DbSet<AgentSessionEventRow> AgentSessionEvents { get; set; } = null!;
    public DbSet<AgentJobEventRow> AgentJobEvents { get; set; } = null!;
    public DbSet<IngressEventRow> IngressEvents { get; set; } = null!;
    public DbSet<WorkspaceEventRow> WorkspaceEvents { get; set; } = null!;
    public DbSet<GitHubConnectionRow> GitHubConnections { get; set; } = null!;
    public DbSet<GitHubIssueLinkRow> GitHubIssueLinks { get; set; } = null!;
    public DbSet<GitHubWriteBackFailureRow> GitHubWriteBackFailures { get; set; } = null!;
    public DbSet<DeadLetterRow> DeadLetters { get; set; } = null!;
    public DbSet<IssueWorkflowProfile> IssueWorkflowProfiles { get; set; } = null!;
    public DbSet<WorkflowRunRow> WorkflowRuns { get; set; } = null!;
    public DbSet<WorkflowRunTaskMapRow> WorkflowRunTaskMaps { get; set; } = null!;
    public DbSet<WorkflowVariablesRow> WorkflowVariables { get; set; } = null!;
    public DbSet<WorkflowRunProfileRow> WorkflowRunProfiles { get; set; } = null!;
    public DbSet<WorkflowStageLockRow> WorkflowStageLocks { get; set; } = null!;
    public DbSet<IssueCounterRow> IssueCounters { get; set; } = null!;
    public DbSet<ProjectPromptTemplateRow> ProjectPromptTemplates { get; set; } = null!;
    public DbSet<EpicCounterRow> EpicCounters { get; set; } = null!;
    public DbSet<WorkflowArtifactRow> WorkflowArtifacts { get; set; } = null!;
    public DbSet<WorkflowArtifactPendingUploadRow> WorkflowArtifactPendingUploads { get; set; } = null!;
    public DbSet<WorkflowDispatchSnapshotRow> WorkflowDispatchSnapshots { get; set; } = null!;
    public DbSet<LabelDefinitionRow> LabelDefinitions { get; set; } = null!;
    public DbSet<ProjectIssueTemplateRow> ProjectIssueTemplates { get; set; } = null!;
    public DbSet<RunnerRow> Runners { get; set; } = null!;
    public DbSet<InboxItemRow> InboxItems { get; set; } = null!;
    public DbSet<InboxSubscriptionRow> InboxSubscriptions { get; set; } = null!;
    public DbSet<TaskLogEntryRow> TaskLogEntries { get; set; } = null!;
    public DbSet<TaskLogBatchRow> TaskLogBatches { get; set; } = null!;
    public DbSet<AgentJobRow> AgentJobs { get; set; } = null!;
    public DbSet<WorkspaceRow> Workspaces { get; set; } = null!;
    public DbSet<StoredSecretRow> StoredSecrets { get; set; } = null!;
    public DbSet<SlackProviderInboxRow> SlackProviderInboxRows { get; set; } = null!;
    public DbSet<SlackOutboxRow> SlackOutboxRows { get; set; } = null!;
    public DbSet<SlackManagerToolExecutionFenceRow> SlackManagerToolExecutionFences { get; set; } = null!;
    public DbSet<SlackOwnerClaimCodeRow> SlackOwnerClaimCodes { get; set; } = null!;
    public DbSet<SlackDmSessionMappingRow> SlackDmSessionMappings { get; set; } = null!;
    public DbSet<SlackThreadSessionMappingRow> SlackThreadSessionMappings { get; set; } = null!;
    public DbSet<SlackConnectionAllowedMemberRow> SlackConnectionAllowedMembers { get; set; } = null!;
    public DbSet<SlackWorkspaceEnrollmentRow> SlackWorkspaceEnrollments { get; set; } = null!;
    public DbSet<ManagedSlackAgentAppRow> ManagedSlackAgentApps { get; set; } = null!;
    public DbSet<SlackOAuthStateRow> SlackOAuthStates { get; set; } = null!;
    public DbSet<SlackOAuthAttemptRow> SlackOAuthAttempts { get; set; } = null!;
    public DbSet<SlackAgentAppBindingObligationRow> SlackAgentAppBindingObligations { get; set; } = null!;

    public DbSet<SlackThreadLaunchReservationRow> SlackThreadLaunchReservations { get; set; } = null!;
    public DbSet<SlackAmbiguousPromptRow> SlackAmbiguousPrompts { get; set; } = null!;
    public DbSet<SlackAdapterLeaseRow> SlackAdapterLeases { get; set; } = null!;

    public MohistDbContext(DbContextOptions<MohistDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CredentialRow>(entity =>
        {
            entity.ToTable("Credentials");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(128);
            entity.Property(e => e.PrincipalId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Kind).HasMaxLength(32).IsRequired();
            entity.Property(e => e.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ScopesJson).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(256);
            entity.Property(e => e.Prefix).HasMaxLength(64);
            entity.Property(e => e.ExpiresAt);
            entity.Property(e => e.RevokedAt);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => new { e.PrincipalId, e.Name })
                .IsUnique()
                .HasFilter("\"RevokedAt\" IS NULL");
            entity.HasIndex(e => new { e.PrincipalId, e.Kind, e.RevokedAt });
        });

        modelBuilder.Entity<EnrollmentTokenRow>(entity =>
        {
            entity.ToTable("EnrollmentTokens");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(128);
            entity.Property(e => e.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ExpiresAt).IsRequired();
            entity.Property(e => e.ConsumedAt);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasIndex(e => e.TokenHash).IsUnique();
        });

        modelBuilder.Entity<PrincipalRow>(entity =>
        {
            entity.ToTable("Principals");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(256);
            entity.Property(e => e.Kind).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(256).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
        });

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
            entity.Property(e => e.LabelWorkspaceName)
                .HasComputedColumnSql(JsonExtractLabel(AgentSessionMetadata.WorkspaceNameKey), stored: true);
            entity.HasIndex(e => e.LabelWorkspaceName)
                .HasDatabaseName("IX_AgentSessions_LabelWorkspaceName");
            entity.Property(e => e.LaunchVisibility)
                .IsRequired()
                .HasDefaultValue("visible");

            entity.Property(e => e.LabelTriggerEventId)
                .HasComputedColumnSql(JsonExtractLabel(GenericAgentSessionMetadata.TriggerEventId), stored: false);
            entity.Property(e => e.LabelTriggerRuleId)
                .HasComputedColumnSql(JsonExtractLabel(GenericAgentSessionMetadata.TriggerRuleId), stored: false);

            // Slack DM identity labels stamped by AgentLaunchCoordinatorGrain
            // whenever a connection-launched session is opened. Path is
            // built from AgentSessionQueryMetadataKeys so a rename is a
            // compile-time error rather than a silent drift between SQL
            // and metadata.
            entity.Property(e => e.LabelConnectionId)
                .HasComputedColumnSql(JsonExtractLabel(AgentSessionQueryMetadataKeys.ConnectionId), stored: true);
            entity.Property(e => e.LabelSlackUserId)
                .HasComputedColumnSql(JsonExtractLabel(AgentSessionQueryMetadataKeys.SlackUserId), stored: true);
            entity.Property(e => e.LabelSlackConversationId)
                .HasComputedColumnSql(JsonExtractLabel(AgentSessionQueryMetadataKeys.SlackConversationId), stored: true);
            entity.Property(e => e.LabelSlackThreadTs)
                .HasComputedColumnSql(JsonExtractLabel(AgentSessionQueryMetadataKeys.SlackThreadTs), stored: true);

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

            // Slack DM identity lookups. The mapping store resolves the
            // current session by Connection, but Web/CLI session queries
            // also need to filter by Slack identity; these indexes keep
            // the AgentSessionQuery.QueryRowsByLabels path index-only.
            entity.HasIndex(e => e.LabelConnectionId)
                .HasDatabaseName("IX_AgentSessions_LabelConnectionId");
            entity.HasIndex(e => new { e.LabelProjectId, e.LabelConnectionId, e.CreatedAt })
                .HasDatabaseName("IX_AgentSessions_LabelProjectId_LabelConnectionId_CreatedAt");
            entity.HasIndex(e => e.LabelSlackUserId)
                .HasDatabaseName("IX_AgentSessions_LabelSlackUserId");
            entity.HasIndex(e => new { e.LabelProjectId, e.LabelSlackUserId, e.CreatedAt })
                .HasDatabaseName("IX_AgentSessions_LabelProjectId_LabelSlackUserId_CreatedAt");
            entity.HasIndex(e => e.LabelSlackConversationId)
                .HasDatabaseName("IX_AgentSessions_LabelSlackConversationId");
            entity.HasIndex(e => e.LabelSlackThreadTs)
                .HasDatabaseName("IX_AgentSessions_LabelSlackThreadTs");
            entity.HasIndex(e => new { e.LabelProjectId, e.ParentSessionId, e.ParentLinkState, e.ParentLinkAttachedRevision, e.ParentLinkEdgeId })
                .HasDatabaseName("IX_AgentSessions_TreeParent_AttachedRevision_Edge");
            entity.HasIndex(e => new { e.LabelProjectId, e.LaunchVisibility, e.ParentSessionId, e.ParentLinkAttachedRevision, e.ParentLinkEdgeId })
                .HasDatabaseName("IX_AgentSessions_TreeVisibleParent_AttachedRevision_Edge");
        });

        modelBuilder.Entity<SessionTreeGraphRevisionRow>(entity =>
        {
            entity.ToTable("SessionTreeGraphRevisions");
            entity.HasKey(e => e.ProjectId);
            entity.Property(e => e.ProjectId).HasMaxLength(256);
            entity.Property(e => e.PublishedRevision);
            entity.Property(e => e.PublishedAt).IsRequired();
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
            entity.Property(e => e.Revision).IsRequired();
            entity.Property(e => e.AssignedRunnerId).HasMaxLength(256);
            entity.Property(e => e.WorkId).HasMaxLength(128);
            entity.Property(e => e.WorkType).HasMaxLength(64);
            entity.Property(e => e.Stage).HasMaxLength(128);
            entity.Property(e => e.Title).HasMaxLength(512);
            entity.Property(e => e.IssueProjectId).HasMaxLength(256);
            entity.Property(e => e.AgentSessionId).HasMaxLength(512);
            entity.Property(e => e.InitialInputId).HasMaxLength(128);
            entity.Property(e => e.InitialTurnId).HasMaxLength(128);
            entity.Property(e => e.PinnedRunnerId).HasMaxLength(256);
            entity.HasIndex(e => new { e.AgentId, e.ProjectId, e.SubmittedAt })
                .HasDatabaseName("IX_AgentJobs_AgentId_ProjectId_SubmittedAt");
            // Poll-time queries: assigned running, assigned pending by
            // readiness time, eligible unassigned pending by readiness.
            // Three narrow indexes match the three DispatchService
            // projections; each one is sized to a status-filtered scan.
            entity.HasIndex(e => new { e.AssignedRunnerId, e.Status })
                .HasDatabaseName("IX_AgentJobs_AssignedRunnerId_Status");
            entity.HasIndex(e => new { e.AssignedRunnerId, e.Status, e.ReadySince })
                .HasDatabaseName("IX_AgentJobs_AssignedRunnerId_Status_ReadySince");
            entity.HasIndex(e => new { e.Status, e.ReadySince })
                .HasDatabaseName("IX_AgentJobs_Status_ReadySince");
            entity.HasIndex(e => new { e.PinnedRunnerId, e.Status, e.ReadySince })
                .HasDatabaseName("IX_AgentJobs_PinnedRunner_Status_ReadySince");
            entity.HasIndex(e => new { e.LaunchVisibility, e.Status, e.ReadySince })
                .HasDatabaseName("IX_AgentJobs_LaunchVisibility_Status_ReadySince");
        });

        modelBuilder.Entity<WorkspaceRow>(entity =>
        {
            entity.ToTable("Workspaces");
            entity.HasKey(e => new { e.ProjectId, e.Name });
            entity.Property(e => e.ProjectId).HasMaxLength(256);
            entity.Property(e => e.Name).HasMaxLength(256);
            entity.Property(e => e.OriginKind).HasMaxLength(32).IsRequired();
            entity.Property(e => e.OriginPayloadJson).IsRequired();
            entity.Property(e => e.RepositoriesJson).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(16).IsRequired().HasConversion<string>();
            entity.Property(e => e.HomeRunnerId).HasMaxLength(256);
            // One active workspace per origin: the partial unique index is
            // the hard backstop behind the grain's create-time check.
            entity.HasIndex(e => new { e.ProjectId, e.OriginKind, e.OriginPayloadJson })
                .IsUnique()
                .HasFilter("\"Status\" = 'active'");
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
            entity.Property(e => e.DisplayName).HasMaxLength(100);
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
            entity.Property(e => e.Source).HasMaxLength(32);
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

        modelBuilder.Entity<WebhookSubscriptionRow>(entity =>
        {
            entity.ToTable("WebhookSubscriptions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Match).IsRequired();
            entity.Property(e => e.TargetUrl).HasMaxLength(2048).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.EventSelectionMode).HasMaxLength(16).IsRequired();
            entity.Property(e => e.EventTypes).IsRequired();
            entity.Property(e => e.AuthType).HasMaxLength(16).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.Name })
                .IsUnique()
                .HasDatabaseName("UX_WebhookSubscriptions_ProjectId_Name");
            entity.HasIndex(e => new { e.ProjectId, e.Status })
                .HasDatabaseName("IX_WebhookSubscriptions_ProjectId_Status");
            entity.HasIndex(e => e.ProjectId)
                .HasDatabaseName("IX_WebhookSubscriptions_ProjectId");
        });

        modelBuilder.Entity<WebhookDeliveryFailureRow>(entity =>
        {
            entity.ToTable("WebhookDeliveryFailures");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.SubscriptionId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.EventId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.EventType).HasMaxLength(256).IsRequired();
            entity.Property(e => e.TargetUrl).HasMaxLength(2048).IsRequired();
            entity.Property(e => e.ResponseStatus);
            entity.Property(e => e.DurationMs);
            entity.Property(e => e.ErrorSummary).HasMaxLength(1024).IsRequired();
            entity.Property(e => e.OccurredAt).IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.SubscriptionId })
                .HasDatabaseName("IX_WebhookDeliveryFailures_ProjectId_SubscriptionId");
            entity.HasIndex(e => e.ProjectId)
                .HasDatabaseName("IX_WebhookDeliveryFailures_ProjectId");
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
            entity.Property(e => e.VerifiedBotName).HasMaxLength(512);
            entity.Property(e => e.VerifiedBotIconUrl).HasMaxLength(2048);
            entity.Property(e => e.SetupProgress).HasMaxLength(64).IsRequired();
            entity.Property(e => e.DesiredState).HasMaxLength(32).IsRequired();
            entity.Property(e => e.ConnectionHealth).HasMaxLength(32).IsRequired();
            entity.Property(e => e.HealthReason).HasMaxLength(1024);
            entity.Property(e => e.AgentReadiness).HasMaxLength(32).IsRequired();
            entity.Property(e => e.OwnerSlackUserId).HasMaxLength(256);
            entity.Property(e => e.AccessPolicy)
                .HasMaxLength(32)
                .IsRequired()
                .HasDefaultValue(AccessPolicyKind.OwnerOnly)
                .HasConversion<string>();
            entity.Property(e => e.LastHeartbeatAt);
            entity.Property(e => e.OfflineGapAt);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.Property(e => e.DeletedAt);
            entity.ToTable(t => t.HasCheckConstraint(
                "CK_AgentConnections_StagedSlackBinding",
                "(\"AppId\" = '' AND \"BotUserId\" = '') OR (\"AppId\" <> '' AND \"BotUserId\" <> '')"));
            entity.ToTable(t => t.HasCheckConstraint(
                "CK_AgentConnections_AccessPolicy",
                "\"AccessPolicy\" IN ('owner_only', 'allowlist', 'anyone')"));
            entity.HasIndex(e => new { e.ProjectId, e.AgentId, e.WorkspaceTeamId })
                .IsUnique()
                .HasFilter("\"DeletedAt\" IS NULL")
                .HasDatabaseName("UX_AgentConnections_ProjectId_AgentId_WorkspaceTeamId");
            entity.HasIndex(e => new { e.ProjectId, e.AgentId })
                .HasDatabaseName("IX_AgentConnections_ProjectId_AgentId");
            entity.HasIndex(e => e.Id)
                .HasDatabaseName("IX_AgentConnections_Id");
        });

        modelBuilder.Entity<SlackWorkspaceEnrollmentRow>(entity =>
        {
            entity.ToTable("SlackWorkspaceEnrollments", table =>
            {
                table.HasCheckConstraint(
                    "CK_SlackWorkspaceEnrollments_Lifecycle",
                    "\"Lifecycle\" IN ('active', 'disabled', 'removed')");
                table.HasCheckConstraint(
                    "CK_SlackWorkspaceEnrollments_ManagerCapability",
                    "\"ManagerCapability\" IN ('unknown', 'available', 'unauthorized', 'capacity_limited')");
                table.HasCheckConstraint(
                    "CK_SlackWorkspaceEnrollments_ManagerTransportKind",
                    "\"ManagerTransportKind\" = 'socket'");
                table.HasCheckConstraint(
                    "CK_SlackWorkspaceEnrollments_ManagerReadiness",
                    "\"ManagerReadiness\" IN ('unknown', 'ready', 'not_ready', 'degraded')");
                table.HasCheckConstraint(
                    "CK_SlackWorkspaceEnrollments_ManagerAppLifecycle",
                    "\"ManagerAppLifecycle\" IN ('not_created', 'creating', 'created', 'create_unknown')");
                table.HasCheckConstraint(
                    "CK_SlackWorkspaceEnrollments_RuntimeCredentialValidationState",
                    "\"RuntimeCredentialValidationState\" IN ('not_provided', 'candidate', 'awaiting_socket', 'verified', 'failed')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(256).IsRequired();
            entity.Property(e => e.WorkspaceTeamId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Lifecycle).HasMaxLength(32).IsRequired();
            entity.Property(e => e.ManagerCapability).HasMaxLength(32).IsRequired();
            entity.Property(e => e.CapabilityReason).HasMaxLength(1024);
            entity.Property(e => e.PlanCode).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ManagedAppLimit).IsRequired();
            entity.Property(e => e.ConfigurationCredentialRef).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ConfigurationCredentialGeneration).IsRequired();
            entity.Property(e => e.ConfigurationCredentialExpiresAt);
            entity.Property(e => e.ManagerCredentialRef).HasMaxLength(512).IsRequired();
            entity.Property(e => e.ManagerAppId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ManagerBotUserId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ManagerTransportKind).HasMaxLength(32).IsRequired();
            entity.Property(e => e.ManagerReadiness).HasMaxLength(32).IsRequired();
            entity.Property(e => e.ManagerAppLifecycle).HasMaxLength(32).IsRequired();
            entity.Property(e => e.ManagerAppOperationFence).IsRequired();
            entity.Property(e => e.ManagerAppOperationId).HasMaxLength(256);
            entity.Property(e => e.ManagerAppOperationOutcome).HasMaxLength(1024);
            entity.Property(e => e.ManagerAppManifestHash).HasMaxLength(128).IsRequired();
            entity.Property(e => e.ManagerAppInstallUrl).HasMaxLength(2048).IsRequired();
            entity.Property(e => e.RuntimeCredentialValidationState).HasMaxLength(32).IsRequired();
            entity.Property(e => e.ManagerActorId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ClaimedSlackUserId).HasMaxLength(256);
            entity.Property(e => e.ManagerClaimHash).HasMaxLength(128);
            entity.Property(e => e.AuditJson).HasColumnType("JSON").IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.HasIndex(e => e.WorkspaceTeamId)
                .IsUnique()
                .HasFilter("\"DeletedAt\" IS NULL AND \"Lifecycle\" = 'active'")
                .HasDatabaseName("UX_SlackWorkspaceEnrollments_WorkspaceTeamId");
            entity.HasIndex(e => new { e.Lifecycle, e.UpdatedAt })
                .HasDatabaseName("IX_SlackWorkspaceEnrollments_Lifecycle_UpdatedAt");
        });

        modelBuilder.Entity<ManagedSlackAgentAppRow>(entity =>
        {
            entity.ToTable("ManagedSlackAgentApps", table =>
            {
                table.HasCheckConstraint(
                    "CK_ManagedSlackAgentApps_AppLifecycle",
                    "\"AppLifecycle\" IN ('not_created', 'creating', 'create_unknown', 'created', 'deleting', 'delete_unknown', 'deleted')");
                table.HasCheckConstraint(
                    "CK_ManagedSlackAgentApps_Authorization",
                    "\"Authorization\" IN ('not_started', 'awaiting_user', 'pending_admin', 'authorized', 'expired_or_cancelled', 'revoked')");
                table.HasCheckConstraint(
                    "CK_ManagedSlackAgentApps_BindingState",
                    "\"BindingState\" IN ('pending', 'in_progress', 'bound', 'connection_deleted', 'conflict')");
                table.HasCheckConstraint(
                    "CK_ManagedSlackAgentApps_DesiredManifest",
                    "\"DesiredManifestVersion\" > 0 AND \"DesiredManifestHash\" <> ''");
                table.HasCheckConstraint(
                    "CK_ManagedSlackAgentApps_AppliedManifestPair",
                    "(\"AppliedManifestVersion\" IS NULL AND \"AppliedManifestHash\" IS NULL) OR (\"AppliedManifestVersion\" IS NOT NULL AND \"AppliedManifestHash\" IS NOT NULL AND \"AppliedManifestVersion\" > 0 AND \"AppliedManifestHash\" <> '')");
                table.HasCheckConstraint(
                    "CK_ManagedSlackAgentApps_IdentityPair",
                    "\"BotUserId\" = '' OR \"AppId\" <> ''");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(256).IsRequired();
            entity.Property(e => e.EnrollmentId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.WorkspaceTeamId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.AgentConnectionId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.AppId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.BotUserId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.AppLifecycle).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Authorization).HasMaxLength(32).IsRequired();
            entity.Property(e => e.DesiredManifestHash).HasMaxLength(128).IsRequired();
            entity.Property(e => e.AppliedManifestHash).HasMaxLength(128);
            entity.Property(e => e.VerifiedScopesJson).HasColumnType("JSON").IsRequired();
            entity.Property(e => e.InstallUrl).HasMaxLength(1024).IsRequired();
            entity.Property(e => e.RuntimeCredentialValidationState).HasMaxLength(32).IsRequired();
            entity.Property(e => e.OperationId).HasMaxLength(256);
            entity.Property(e => e.OperationKind).HasMaxLength(32);
            entity.Property(e => e.UnknownOutcome).HasMaxLength(1024);
            entity.Property(e => e.ErrorClass).HasMaxLength(64);
            entity.Property(e => e.AuthorizationAttemptId).HasMaxLength(256);
            entity.Property(e => e.AuthorizationExpiresAt).HasMaxLength(64);
            entity.Property(e => e.ClientSecretRef).HasMaxLength(512).IsRequired();
            entity.Property(e => e.SigningSecretRef).HasMaxLength(512).IsRequired();
            entity.Property(e => e.AppLevelTokenRef).HasMaxLength(512).IsRequired();
            entity.Property(e => e.BotTokenRef).HasMaxLength(512).IsRequired();
            entity.Property(e => e.BindingState).HasMaxLength(32).IsRequired();
            entity.Property(e => e.BindingErrorClass).HasMaxLength(128);
            entity.Property(e => e.AuditJson).HasColumnType("JSON").IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.HasOne<SlackWorkspaceEnrollmentRow>()
                .WithMany()
                .HasForeignKey(e => e.EnrollmentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentConnectionRow>()
                .WithMany()
                .HasForeignKey(e => e.AgentConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => e.AgentConnectionId)
                .IsUnique()
                .HasFilter("\"DeletedAt\" IS NULL")
                .HasDatabaseName("UX_ManagedSlackAgentApps_AgentConnectionId");
            entity.HasIndex(e => new { e.WorkspaceTeamId, e.AppId })
                .IsUnique()
                .HasFilter("\"DeletedAt\" IS NULL AND \"AppId\" <> ''")
                .HasDatabaseName("UX_ManagedSlackAgentApps_WorkspaceTeamId_AppId");
            entity.HasIndex(e => new { e.EnrollmentId, e.UpdatedAt })
                .HasDatabaseName("IX_ManagedSlackAgentApps_EnrollmentId_UpdatedAt");
        });

        modelBuilder.Entity<SlackOAuthAttemptRow>(entity =>
        {
            entity.ToTable("SlackOAuthAttempts", table =>
            {
                table.HasCheckConstraint(
                    "CK_SlackOAuthAttempts_Status",
                    "\"Status\" IN ('issued', 'consumed', 'secret_stored', 'applied', 'expired', 'recovery_required')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(256).IsRequired();
            entity.Property(e => e.AgentAppId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.WorkspaceTeamId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.AppId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.StateHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.BotUserId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.BotTokenRef).HasMaxLength(512);
            entity.Property(e => e.FailureClass).HasMaxLength(128);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.HasOne<ManagedSlackAgentAppRow>()
                .WithMany()
                .HasForeignKey(e => e.AgentAppId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => e.StateHash)
                .IsUnique()
                .HasDatabaseName("UX_SlackOAuthAttempts_StateHash");
            entity.HasIndex(e => new { e.AgentAppId, e.Status, e.UpdatedAt })
                .HasDatabaseName("IX_SlackOAuthAttempts_AgentAppId_Status_UpdatedAt");
        });

        modelBuilder.Entity<SlackAgentAppBindingObligationRow>(entity =>
        {
            entity.ToTable("SlackAgentAppBindingObligations", table =>
            {
                table.HasCheckConstraint(
                    "CK_SlackAgentAppBindingObligations_Status",
                    "\"Status\" IN ('pending', 'in_progress', 'bound', 'connection_deleted', 'conflict')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(256).IsRequired();
            entity.Property(e => e.AgentAppId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.AgentConnectionId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.ClaimToken).HasMaxLength(64).IsRequired();
            entity.Property(e => e.FailureClass).HasMaxLength(128);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.HasOne<ManagedSlackAgentAppRow>()
                .WithMany()
                .HasForeignKey(e => e.AgentAppId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentConnectionRow>()
                .WithMany()
                .HasForeignKey(e => e.AgentConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => e.AgentConnectionId)
                .HasDatabaseName("IX_SlackAgentAppBindingObligations_AgentConnectionId");
            entity.HasIndex(e => e.AgentAppId)
                .IsUnique()
                .HasDatabaseName("UX_SlackAgentAppBindingObligations_AgentAppId");
            entity.HasIndex(e => new { e.Status, e.UpdatedAt })
                .HasDatabaseName("IX_SlackAgentAppBindingObligations_Status_UpdatedAt");
        });

        modelBuilder.Entity<SlackOAuthStateRow>(entity =>
        {
            entity.ToTable("SlackOAuthStates", table =>
            {
                table.HasCheckConstraint(
                    "CK_SlackOAuthStates_Outcome",
                    "\"Outcome\" IS NULL OR \"Outcome\" IN ('accepted', 'expired')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(256).IsRequired();
            entity.Property(e => e.AgentAppId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.WorkspaceTeamId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.AppId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.StateHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.AuthorizationAttemptId).HasMaxLength(256);
            entity.Property(e => e.ExpiresAt).IsRequired();
            entity.Property(e => e.Outcome).HasMaxLength(64);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasOne<ManagedSlackAgentAppRow>()
                .WithMany()
                .HasForeignKey(e => e.AgentAppId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SlackOAuthAttemptRow>()
                .WithMany()
                .HasForeignKey(e => e.AuthorizationAttemptId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => e.AuthorizationAttemptId)
                .HasDatabaseName("IX_SlackOAuthStates_AuthorizationAttemptId");
            entity.HasIndex(e => e.StateHash)
                .IsUnique()
                .HasDatabaseName("UX_SlackOAuthStates_StateHash");
            entity.HasIndex(e => new { e.AgentAppId, e.ConsumedAt, e.ExpiresAt })
                .HasDatabaseName("IX_SlackOAuthStates_AgentAppId_ConsumedAt_ExpiresAt");
        });

        modelBuilder.Entity<SlackOwnerClaimCodeRow>(entity =>
        {
            entity.ToTable("SlackOwnerClaimCodes");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ConnectionId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.CodeHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Kind).HasMaxLength(32).IsRequired().HasDefaultValue(SlackOwnerClaimCodeKinds.Initial);
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

        modelBuilder.Entity<WorkspaceEventRow>(entity =>
        {
            entity.ToTable("WorkspaceEvents");
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
            entity.HasIndex(nameof(WorkspaceEventRow.Type), nameof(WorkspaceEventRow.Source), nameof(WorkspaceEventRow.Id));
            entity.HasIndex(e => new { e.TimelineSource, e.Time, e.Source, e.Id })
                .HasDatabaseName("IX_WorkspaceEvents_TimelineSource_Time_Source_Id");
            entity.HasIndex(e => new { e.TimeSortKey, e.Source, e.Id })
                .HasDatabaseName("IX_WorkspaceEvents_TimeSortKey_Source_Id");
            entity.HasIndex(e => new { e.Source, e.Id, e.DispatchedAt })
                .HasFilter("\"DispatchedAt\" IS NULL")
                .HasDatabaseName("IX_WorkspaceEvents_Source_Id_DispatchedAt");
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

        modelBuilder.Entity<IngressEventRow>(entity =>
        {
            entity.ToTable("IngressEvents");
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
            entity.HasIndex(e => new { e.Source, e.Id })
                .HasFilter("\"DispatchedAt\" IS NULL")
                .HasDatabaseName("IX_IngressEvents_Undelivered");
        });

        modelBuilder.Entity<GitHubConnectionRow>(entity =>
        {
            entity.ToTable("GitHubConnections");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id)
                .HasMaxLength(256)
                .IsRequired();
            entity.Property(e => e.ProjectId)
                .HasMaxLength(256)
                .IsRequired();
            entity.Property(e => e.Owner)
                .HasMaxLength(256)
                .IsRequired();
            entity.Property(e => e.Repo)
                .HasMaxLength(256)
                .IsRequired();
            entity.HasIndex(e => new { e.Owner, e.Repo })
                .IsUnique();
            entity.Property(e => e.RepositoryName)
                .HasMaxLength(256)
                .IsRequired();
            entity.Property(e => e.IntakeLabel)
                .HasMaxLength(256)
                .IsRequired();
            entity.Property(e => e.FeedMode)
                .HasMaxLength(32)
                .IsRequired();
            entity.Property(e => e.ApproversJson)
                .HasColumnType("JSON")
                .IsRequired();
            entity.Property(e => e.Status)
                .HasMaxLength(32)
                .IsRequired();
            entity.Property(e => e.IdentityKind)
                .HasMaxLength(32)
                .IsRequired();
            entity.Property(e => e.InstallationId)
                .HasMaxLength(256);
            entity.Property(e => e.NeedsAttention)
                .IsRequired();
            entity.Property(e => e.CreatedAt)
                .IsRequired();
            entity.Property(e => e.UpdatedAt)
                .IsRequired();
        });

        modelBuilder.Entity<GitHubIssueLinkRow>(entity =>
        {
            entity.ToTable("GitHubIssueLinks");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id)
                .HasMaxLength(256)
                .IsRequired();
            entity.Property(e => e.ProjectId)
                .HasMaxLength(256)
                .IsRequired();
            entity.Property(e => e.RepositoryName)
                .HasMaxLength(256)
                .IsRequired();
            entity.Property(e => e.GithubIssueNumber)
                .IsRequired();
            entity.Property(e => e.IssueNumber)
                .IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.RepositoryName, e.GithubIssueNumber })
                .IsUnique();
            entity.Property(e => e.PostedCommentsJson)
                .HasColumnType("JSON")
                .IsRequired();
            entity.Property(e => e.StateLabel)
                .HasMaxLength(256);
            entity.Property(e => e.CreatedAt)
                .IsRequired();
            entity.Property(e => e.UpdatedAt)
                .IsRequired();
        });

        modelBuilder.Entity<GitHubWriteBackFailureRow>(entity =>
        {
            entity.ToTable("GitHubWriteBackFailures");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id)
                .HasMaxLength(256)
                .IsRequired();
            entity.Property(e => e.ProjectId)
                .HasMaxLength(256)
                .IsRequired();
            entity.Property(e => e.ConnectionId)
                .HasMaxLength(256)
                .IsRequired();
            entity.Property(e => e.RepositoryName)
                .HasMaxLength(256)
                .IsRequired();
            entity.Property(e => e.GithubIssueNumber)
                .IsRequired();
            entity.Property(e => e.IssueNumber)
                .IsRequired();
            entity.Property(e => e.EventType)
                .HasMaxLength(256)
                .IsRequired();
            entity.Property(e => e.Operation)
                .HasMaxLength(32)
                .IsRequired();
            entity.Property(e => e.ErrorCode)
                .HasMaxLength(64)
                .IsRequired();
            entity.Property(e => e.ErrorDetail)
                .IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.CreatedAt });
            entity.Property(e => e.CreatedAt)
                .IsRequired();
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
            entity.Property(e => e.ActiveWorkId).HasMaxLength(128);
            entity.Property(e => e.ActiveWorkerId).HasMaxLength(128);
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

        modelBuilder.Entity<WorkflowRunTaskMapRow>(entity =>
        {
            entity.ToTable("WorkflowRunTaskMap");
            entity.HasKey(e => new { e.WorkflowRunId, e.TaskId });
            entity.Property(e => e.WorkflowRunId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.TaskId).HasMaxLength(128).IsRequired();
            entity.Property(e => e.WorkId).HasMaxLength(128).IsRequired();
            entity.HasIndex(e => new { e.WorkflowRunId, e.TaskId })
                .HasDatabaseName("IX_WorkflowRunTaskMap_WorkflowRunId_TaskId");
            entity.HasIndex(e => new { e.WorkflowRunId, e.WorkId })
                .HasDatabaseName("IX_WorkflowRunTaskMap_WorkflowRunId_WorkId");
            entity.HasOne<WorkflowRunRow>()
                .WithMany()
                .HasForeignKey(e => e.WorkflowRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkflowDispatchSnapshotRow>(entity =>
        {
            entity.ToTable("WorkflowDispatchSnapshots");
            entity.HasKey(e => new { e.WorkflowRunId, e.WorkId });
            entity.Property(e => e.WorkflowRunId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.WorkId).HasMaxLength(128).IsRequired();
            entity.Property(e => e.SnapshotJson).IsRequired();
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

        modelBuilder.Entity<StoredSecretRow>(entity =>
        {
            entity.ToTable("StoredSecrets");
            entity.HasKey(e => new { e.OwnerKind, e.OwnerScope, e.OwnerId, e.Kind });
            entity.Property(e => e.OwnerKind).HasMaxLength(64).IsRequired();
            entity.Property(e => e.OwnerScope).HasMaxLength(256).IsRequired();
            entity.Property(e => e.OwnerId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Kind).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Blob).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.ToTable(t => t.HasCheckConstraint(
                "CK_StoredSecrets_OwnerKind",
                "\"OwnerKind\" IN ('agent_connection', 'webhook_subscription', 'slack_workspace_enrollment', 'managed_slack_agent_app')"));
            entity.ToTable(t => t.HasCheckConstraint(
                "CK_StoredSecrets_Kind",
                "\"Kind\" IN ('appToken', 'botToken', 'webhookSecret', 'clientSecret', 'signingSecret', 'configurationAccessToken', 'configurationRefreshToken', 'previousBotToken', 'previousAppToken', 'candidateBotToken', 'candidateAppToken')"));
            entity.ToTable(t => t.HasCheckConstraint(
                "CK_StoredSecrets_OwnerKindKind",
                "(\"OwnerKind\" = 'agent_connection' AND \"Kind\" IN ('appToken', 'botToken')) OR " +
                "(\"OwnerKind\" = 'webhook_subscription' AND \"Kind\" = 'webhookSecret') OR " +
                "(\"OwnerKind\" = 'slack_workspace_enrollment' AND \"Kind\" IN ('configurationAccessToken', 'configurationRefreshToken', 'appToken', 'botToken', 'clientSecret', 'signingSecret', 'previousBotToken', 'previousAppToken', 'candidateBotToken', 'candidateAppToken')) OR " +
                "(\"OwnerKind\" = 'managed_slack_agent_app' AND \"Kind\" IN ('appToken', 'botToken', 'clientSecret', 'signingSecret', 'previousBotToken', 'previousAppToken', 'candidateBotToken', 'candidateAppToken'))"));
            entity.HasIndex(e => new { e.OwnerKind, e.OwnerScope, e.OwnerId })
                .HasDatabaseName("IX_StoredSecrets_Owner");
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
            entity.Property(e => e.ConversationId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ThreadTs).HasMaxLength(64);
            entity.Property(e => e.SlackUserId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.RouteKind).HasMaxLength(32);
            entity.Property(e => e.RouteSessionId).HasMaxLength(512);
            entity.Property(e => e.RouteTurnId).HasMaxLength(512);
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
                table.HasCheckConstraint(
                    "CK_SlackOutboxRows_OwnerKind",
                    "\"OwnerKind\" IN ('connection', 'manager')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ConnectionId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.OwnerKind).HasMaxLength(32).IsRequired();
            entity.Property(e => e.WorkspaceTeamId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ConversationId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ThreadTs).HasMaxLength(64);
            entity.Property(e => e.Kind).HasMaxLength(32).IsRequired();
            entity.Property(e => e.State).HasMaxLength(32).IsRequired();
            entity.Property(e => e.DispatchRef).HasMaxLength(256);
            entity.Property(e => e.PayloadJson).IsRequired();
            entity.Property(e => e.ClaimedByAdapterId).HasMaxLength(256);
            entity.Property(e => e.LastError).HasMaxLength(1024);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.HasIndex(e => new { e.OwnerKind, e.ProjectId, e.ConnectionId, e.State })
                .HasDatabaseName("IX_SlackOutboxRows_ProjectId_ConnectionId_State");
            entity.HasIndex(e => new { e.OwnerKind, e.ConnectionId, e.State, e.NextAttemptAt })
                .HasDatabaseName("IX_SlackOutboxRows_ConnectionId_State_NextAttemptAt");
            entity.HasIndex(e => new { e.OwnerKind, e.ConnectionId, e.State, e.ClaimedAt })
                .HasDatabaseName("IX_SlackOutboxRows_ConnectionId_State_ClaimedAt");
            entity.HasIndex(e => new { e.OwnerKind, e.ConnectionId, e.State, e.DeliveryUncertainAt })
                .HasDatabaseName("IX_SlackOutboxRows_ConnectionId_State_DeliveryUncertainAt");
            entity.HasIndex(e => new { e.OwnerKind, e.ConnectionId, e.DispatchRef, e.Kind, e.State })
                .HasDatabaseName("IX_SlackOutboxRows_ConnectionId_DispatchRef_Kind_State");
            entity.HasIndex(e => new { e.OwnerKind, e.ConnectionId, e.DispatchRef, e.Kind })
                .IsUnique()
                .HasDatabaseName("UX_SlackOutboxRows_OwnerKind_ConnectionId_DispatchRef_Kind");
        });

        modelBuilder.Entity<SlackManagerToolExecutionFenceRow>(entity =>
        {
            entity.ToTable("SlackManagerToolExecutionFences", table =>
                table.HasCheckConstraint(
                    "CK_SlackManagerToolExecutionFences_State",
                    "\"State\" IN ('started', 'completed')"));
            entity.HasKey(e => e.JobKey);
            entity.Property(e => e.JobKey).HasMaxLength(512).IsRequired();
            entity.Property(e => e.SessionId).HasMaxLength(512).IsRequired();
            entity.Property(e => e.State).HasMaxLength(32).IsRequired();
            entity.Property(e => e.StartedAt).IsRequired();
            entity.HasIndex(e => e.SessionId)
                .HasDatabaseName("IX_SlackManagerToolExecutionFences_SessionId");
        });

        modelBuilder.Entity<SlackDmSessionMappingRow>(entity =>
        {
            entity.ToTable("SlackDmSessionMappings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ConnectionId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.WorkspaceTeamId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.SlackUserId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.DmConversationId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.CurrentSessionId).HasMaxLength(512).IsRequired();
            entity.Property(e => e.CurrentMessageTs).HasMaxLength(64);
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.HasIndex(e => new { e.ConnectionId, e.DmConversationId })
                .IsUnique()
                .HasDatabaseName("UX_SlackDmSessionMappings_ConnectionId_DmConversationId");
            entity.HasIndex(e => new { e.ProjectId, e.ConnectionId, e.UpdatedAt })
                .HasDatabaseName("IX_SlackDmSessionMappings_ProjectId_ConnectionId_UpdatedAt");
        });

        modelBuilder.Entity<SlackThreadSessionMappingRow>(entity =>
        {
            entity.ToTable("SlackThreadSessionMappings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ConnectionId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.WorkspaceTeamId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ConversationId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ThreadTs).HasMaxLength(64).IsRequired();
            entity.Property(e => e.SlackUserId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.SessionId).HasMaxLength(512).IsRequired();
            entity.Property(e => e.RootMessageTs).HasMaxLength(64).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.HasIndex(e => new { e.ConnectionId, e.WorkspaceTeamId, e.ConversationId, e.ThreadTs })
                .IsUnique()
                .HasDatabaseName("UX_SlackThreadSessionMappings_ConnectionId_WorkspaceTeamId_ConversationId_ThreadTs");
            entity.HasIndex(e => new { e.ProjectId, e.WorkspaceTeamId, e.ConversationId, e.ThreadTs })
                .HasDatabaseName("IX_SlackThreadSessionMappings_ProjectId_WorkspaceTeamId_ConversationId_ThreadTs");
            entity.HasIndex(e => new { e.WorkspaceTeamId, e.ConversationId, e.ThreadTs })
                .HasDatabaseName("IX_SlackThreadSessionMappings_WorkspaceTeamId_ConversationId_ThreadTs");
            entity.HasIndex(e => new { e.ProjectId, e.ConnectionId, e.UpdatedAt })
                .HasDatabaseName("IX_SlackThreadSessionMappings_ProjectId_ConnectionId_UpdatedAt");
        });

        modelBuilder.Entity<SlackThreadLaunchReservationRow>(entity =>
        {
            entity.ToTable("SlackThreadLaunchReservations");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ConnectionId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.WorkspaceTeamId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ConversationId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ThreadTs).HasMaxLength(64).IsRequired();
            entity.Property(e => e.LaunchMessageTs).HasMaxLength(64).IsRequired();
            entity.Property(e => e.SlackUserId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.SessionId).HasMaxLength(512);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.HasIndex(e => new { e.ConnectionId, e.WorkspaceTeamId, e.ConversationId, e.ThreadTs })
                .IsUnique()
                .HasDatabaseName("UX_SlackThreadLaunchReservations_ConnectionId_WorkspaceTeamId_ConversationId_ThreadTs");
            entity.HasIndex(e => new { e.ProjectId, e.ConnectionId, e.UpdatedAt })
                .HasDatabaseName("IX_SlackThreadLaunchReservations_ProjectId_ConnectionId_UpdatedAt");
        });

        modelBuilder.Entity<SlackAmbiguousPromptRow>(entity =>
        {
            entity.ToTable("SlackAmbiguousPrompts");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.WorkspaceTeamId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ConversationId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.MessageTs).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ThreadTs).HasMaxLength(64);
            entity.Property(e => e.WinningConnectionId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.MentionedConnectionIdsJson).IsRequired();
            entity.Property(e => e.PromptedAt).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.HasIndex(e => new { e.WorkspaceTeamId, e.ConversationId, e.MessageTs })
                .IsUnique()
                .HasDatabaseName("UX_SlackAmbiguousPrompts_WorkspaceTeamId_ConversationId_MessageTs");
            entity.HasIndex(e => new { e.ProjectId, e.UpdatedAt })
                .HasDatabaseName("IX_SlackAmbiguousPrompts_ProjectId_UpdatedAt");
        });

        modelBuilder.Entity<SlackConnectionAllowedMemberRow>(entity =>
        {
            entity.ToTable("SlackConnectionAllowedMembers");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ConnectionId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.SlackUserId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.WorkspaceTeamId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasIndex(e => new { e.ProjectId, e.ConnectionId, e.SlackUserId })
                .IsUnique()
                .HasDatabaseName("UX_SlackConnectionAllowedMembers_ProjectId_ConnectionId_SlackUserId");
            entity.HasIndex(e => new { e.ProjectId, e.ConnectionId })
                .HasDatabaseName("IX_SlackConnectionAllowedMembers_ProjectId_ConnectionId");
        });

        modelBuilder.Entity<SlackAdapterLeaseRow>(entity =>
        {
            entity.ToTable("SlackAdapterLeases", table =>
            {
                table.HasCheckConstraint(
                    "CK_SlackAdapterLeases_LeaseKind",
                    "\"LeaseKind\" IS NULL OR \"LeaseKind\" IN ('validation', 'runtime')");
                table.HasCheckConstraint(
                    "CK_SlackAdapterLeases_ActiveLeaseCoherent",
                    "(\"LeaseId\" IS NULL) = (\"LeaseKind\" IS NULL) AND " +
                    "(\"LeaseId\" IS NULL) = (\"AdapterId\" IS NULL) AND " +
                    "(\"LeaseId\" IS NULL) = (\"IssuedAt\" IS NULL) AND " +
                    "(\"LeaseId\" IS NULL) = (\"ExpiresAt\" IS NULL)");
            });
            entity.HasKey(e => e.TargetKey);
            entity.Property(e => e.TargetKey).HasMaxLength(320).IsRequired();
            entity.Property(e => e.Generation).IsRequired();
            entity.Property(e => e.LeaseId).HasMaxLength(64);
            entity.Property(e => e.LeaseKind).HasMaxLength(32);
            entity.Property(e => e.AdapterId).HasMaxLength(256);
            entity.Property(e => e.CredentialFingerprint).HasMaxLength(64);
            entity.Property(e => e.UpdatedAt).IsRequired();
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
