using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SquashedBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentConnections",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AgentId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ProviderKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    BotUserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    BotName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    AvatarHash = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    VerifiedBotName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    VerifiedBotIconUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    SetupProgress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DesiredState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ConnectionHealth = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    HealthReason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    AgentReadiness = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OwnerSlackUserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AccessPolicy = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "owner_only"),
                    LastHeartbeatAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    OfflineGapAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentConnections", x => x.Id);
                    table.CheckConstraint("CK_AgentConnections_AccessPolicy", "\"AccessPolicy\" IN ('owner_only', 'allowlist', 'anyone')");
                    table.CheckConstraint("CK_AgentConnections_StagedSlackBinding", "(\"AppId\" = '' AND \"BotUserId\" = '') OR (\"AppId\" <> '' AND \"BotUserId\" <> '')");
                });

            migrationBuilder.CreateTable(
                name: "AgentJobEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    EventId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    TimeSortKey = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "strftime('%Y-%m-%dT%H:%M:%S', \"Time\") ||\nsubstr(\n    CASE\n        WHEN instr(substr(\"Time\", 20), '+') > 0 THEN substr(\"Time\", 20, instr(substr(\"Time\", 20), '+') - 1)\n        WHEN instr(substr(\"Time\", 20), '-') > 0 THEN substr(\"Time\", 20, instr(substr(\"Time\", 20), '-') - 1)\n        ELSE ''\n    END || '.0000000',\n    1,\n    8\n) || 'Z'", stored: true),
                    DataStatus = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "LOWER(COALESCE(json_extract(\"Data\", '$.status'), json_extract(\"Data\", '$.Status')))", stored: true),
                    SpecVersion = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DataContentType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Data = table.Column<string>(type: "JSON", nullable: false),
                    ExtensionsJson = table.Column<string>(type: "JSON", nullable: false),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentJobEvents", x => new { x.Source, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "AgentJobs",
                columns: table => new
                {
                    JobKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                    ProjectId = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.input.projectId')", stored: true),
                    AgentId = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.input.agentId')", stored: true),
                    Status = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.status')", stored: true),
                    SubmittedAt = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.submittedAt')", stored: true),
                    TerminalAt = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.terminalAt')", stored: true),
                    AssignedRunnerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    WorkId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ReadySince = table.Column<string>(type: "TEXT", nullable: true),
                    RunningSince = table.Column<string>(type: "TEXT", nullable: true),
                    DispatchJson = table.Column<string>(type: "TEXT", nullable: true),
                    WorkType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    IssueProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    IssueNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    AgentSessionId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    InitialInputId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    InitialTurnId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PinnedRunnerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    LaunchVisibility = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "visible")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentJobs", x => x.JobKey);
                });

            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "COALESCE(json_extract(State, '$.projectId'), json_extract(State, '$.ProjectId'))", stored: true),
                    Name = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "COALESCE(json_extract(State, '$.name'), json_extract(State, '$.Name'))", stored: true),
                    Status = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "COALESCE(json_extract(State, '$.status'), json_extract(State, '$.Status'))", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentSessionEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    EventId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    TimeSortKey = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "strftime('%Y-%m-%dT%H:%M:%S', \"Time\") ||\nsubstr(\n    CASE\n        WHEN instr(substr(\"Time\", 20), '+') > 0 THEN substr(\"Time\", 20, instr(substr(\"Time\", 20), '+') - 1)\n        WHEN instr(substr(\"Time\", 20), '-') > 0 THEN substr(\"Time\", 20, instr(substr(\"Time\", 20), '-') - 1)\n        ELSE ''\n    END || '.0000000',\n    1,\n    8\n) || 'Z'", stored: true),
                    DataStatus = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "LOWER(COALESCE(json_extract(\"Data\", '$.status'), json_extract(\"Data\", '$.Status')))", stored: true),
                    SpecVersion = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DataContentType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Data = table.Column<string>(type: "JSON", nullable: false),
                    ExtensionsJson = table.Column<string>(type: "JSON", nullable: false),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSessionEvents", x => new { x.Source, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "AgentSessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    RunnerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AgentSessionId = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastDataAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LabelProjectId = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/project-id\"')", stored: true),
                    LabelSourceId = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/source-id\"')", stored: true),
                    LabelSessionName = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/session-name\"')", stored: true),
                    LabelIssueNumber = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/issue-number\"')", stored: true),
                    LabelWorkId = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/work-id\"')", stored: true),
                    LabelWorkType = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/work-type\"')", stored: true),
                    LabelStage = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/stage\"')", stored: true),
                    LabelSourceKind = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/source-kind\"')", stored: true),
                    LabelAgentId = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/agent-id\"')", stored: true),
                    LabelAgentName = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/agent-name\"')", stored: true),
                    LabelAgentLaunchIssueNumber = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/agent-launch/issue-number\"')", stored: true),
                    LabelAgentLaunchEpicNumber = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/agent-launch/epic-number\"')", stored: true),
                    LabelAgentLaunchRepository = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/agent-launch/repository\"')", stored: true),
                    LabelAgentLaunchWorkspacePath = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/agent-launch/workspace-path\"')", stored: true),
                    LabelWorkspaceName = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/workspace-name\"')", stored: true),
                    LabelTriggerEventId = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/trigger/event-id\"')", stored: false),
                    LabelTriggerRuleId = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/trigger/rule-id\"')", stored: false),
                    LabelConnectionId = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/connection-id\"')", stored: true),
                    LabelSlackUserId = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/slack-user-id\"')", stored: true),
                    LabelSlackConversationId = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/slack-conversation-id\"')", stored: true),
                    LabelSlackThreadTs = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/slack-thread-ts\"')", stored: true),
                    Activity = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "LOWER(COALESCE(json_extract(\"State\", '$.status.activity'), json_extract(\"State\", '$.status.Activity')))"),
                    ParentLinkEdgeId = table.Column<string>(type: "TEXT", nullable: true),
                    ParentSessionId = table.Column<string>(type: "TEXT", nullable: true),
                    ParentAgentId = table.Column<string>(type: "TEXT", nullable: true),
                    ChildLaunchJobId = table.Column<string>(type: "TEXT", nullable: true),
                    ParentLinkState = table.Column<string>(type: "TEXT", nullable: true),
                    ParentLinkAttachedRevision = table.Column<long>(type: "INTEGER", nullable: true),
                    ParentLinkAttachedAt = table.Column<string>(type: "TEXT", nullable: true),
                    ParentLinkDetachedRevision = table.Column<long>(type: "INTEGER", nullable: true),
                    ParentLinkDetachedAt = table.Column<string>(type: "TEXT", nullable: true),
                    LaunchVisibility = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "visible")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentSessionTranscriptParts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TurnId = table.Column<long>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CorrelationKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadStatus = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "LOWER(COALESCE(json_extract(\"PayloadJson\", '$.status'), json_extract(\"PayloadJson\", '$.Status')))", stored: true),
                    FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RawEventCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSessionTranscriptParts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentSessionTranscriptTurns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    RuntimeSessionId = table.Column<string>(type: "TEXT", nullable: true),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    PromptText = table.Column<string>(type: "TEXT", nullable: false),
                    PromptKind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSessionTranscriptTurns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Attachments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    OwnerKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    OwnerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    OriginalFileName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    StoragePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    OwnerIssueNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuthAuditEvents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SubjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TargetKind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TargetId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthAuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Credentials",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PrincipalId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ScopesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Prefix = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    FamilyId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Credentials", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeadLetters",
                columns: table => new
                {
                    DeadLetterId = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Origin = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    EventId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SpecVersion = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DataContentType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Data = table.Column<string>(type: "JSON", nullable: false),
                    ExtensionsJson = table.Column<string>(type: "JSON", nullable: false),
                    FailingHandler = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorStack = table.Column<string>(type: "TEXT", nullable: true),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    DeadLetteredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "Pending"),
                    RedeliveryAttemptedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeadLetters", x => x.DeadLetterId);
                });

            migrationBuilder.CreateTable(
                name: "DeviceAuthorizations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DeviceCodeHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UserCodeHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ClientName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PrincipalId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceAuthorizations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EnrollmentTokens",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnrollmentTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EpicCounters",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Next = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EpicCounters", x => x.ProjectId);
                });

            migrationBuilder.CreateTable(
                name: "EpicEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    TimelineSource = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, defaultValue: ""),
                    EventId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SpecVersion = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DataContentType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Data = table.Column<string>(type: "JSON", nullable: false),
                    ExtensionsJson = table.Column<string>(type: "JSON", nullable: false),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EpicEvents", x => new { x.Source, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "Epics",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Priority = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    PauseReason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Epics", x => new { x.ProjectId, x.Number });
                });

            migrationBuilder.CreateTable(
                name: "GitHubConnections",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Owner = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Repo = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RepositoryName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IntakeLabel = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    FeedMode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ApproversJson = table.Column<string>(type: "JSON", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    IdentityKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    InstallationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NeedsAttention = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GitHubIssueLinks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RepositoryName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    GithubIssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    IssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    PostedCommentsJson = table.Column<string>(type: "JSON", nullable: false),
                    StateLabel = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubIssueLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GitHubWriteBackFailures",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RepositoryName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    GithubIssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    IssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ErrorDetail = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubWriteBackFailures", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InboxItems",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    IssueTitle = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    NotificationKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SourceEventSource = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SourceEventId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxItems", x => x.Id);
                    table.CheckConstraint("CK_InboxItems_NotificationKind", "\"NotificationKind\" IN ('workflow_failed', 'agent_result_unconfirmed', 'approval_requested', 'issue_started', 'issue_completed', 'agent_response_failed')");
                });

            migrationBuilder.CreateTable(
                name: "IngressEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    EventId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SpecVersion = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DataContentType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Data = table.Column<string>(type: "JSON", nullable: false),
                    ExtensionsJson = table.Column<string>(type: "JSON", nullable: false),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngressEvents", x => new { x.Source, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "IssueComments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Author = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueComments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IssueCounters",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Next = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueCounters", x => x.ProjectId);
                });

            migrationBuilder.CreateTable(
                name: "IssueEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    TimelineSource = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, defaultValue: ""),
                    EventId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    TimeSortKey = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "strftime('%Y-%m-%dT%H:%M:%S', \"Time\") ||\nsubstr(\n    CASE\n        WHEN instr(substr(\"Time\", 20), '+') > 0 THEN substr(\"Time\", 20, instr(substr(\"Time\", 20), '+') - 1)\n        WHEN instr(substr(\"Time\", 20), '-') > 0 THEN substr(\"Time\", 20, instr(substr(\"Time\", 20), '-') - 1)\n        ELSE ''\n    END || '.0000000',\n    1,\n    8\n) || 'Z'", stored: true),
                    SpecVersion = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DataContentType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Data = table.Column<string>(type: "JSON", nullable: false),
                    ExtensionsJson = table.Column<string>(type: "JSON", nullable: false),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueEvents", x => new { x.Source, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "IssuePrerequisites",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    PrerequisiteNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssuePrerequisites", x => new { x.ProjectId, x.IssueNumber, x.PrerequisiteNumber });
                });

            migrationBuilder.CreateTable(
                name: "IssueWorkflowProfiles",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceTemplateId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Template = table.Column<string>(type: "TEXT", nullable: true),
                    Variables = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueWorkflowProfiles", x => new { x.ProjectId, x.IssueNumber });
                });

            migrationBuilder.CreateTable(
                name: "LabelDefinitions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    SupportedValuesJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LabelDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Principals",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Principals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProjectIssueTemplates",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Template = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectIssueTemplates", x => new { x.ProjectId, x.Name });
                });

            migrationBuilder.CreateTable(
                name: "ProjectPromptTemplates",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    TagsJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    Stage = table.Column<string>(type: "TEXT", nullable: true),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectPromptTemplates", x => new { x.ProjectId, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    RepositoriesJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RepositoryRevision = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                    LastRepositoryCommandJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProjectWorkflowTemplates",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    TemplateId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Template = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectWorkflowTemplates", x => new { x.ProjectId, x.TemplateId });
                });

            migrationBuilder.CreateTable(
                name: "RoutingRules",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Match = table.Column<string>(type: "TEXT", nullable: false),
                    AgentId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ResponsePrompt = table.Column<string>(type: "TEXT", nullable: false),
                    Continue = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoutingRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Runners",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Slots = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runners", x => x.Id);
                    table.CheckConstraint("CK_Runners_Slots_Positive", "\"Slots\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "SessionTreeGraphRevisions",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PublishedRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    PublishedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionTreeGraphRevisions", x => x.ProjectId);
                });

            migrationBuilder.CreateTable(
                name: "SlackAdapterLeases",
                columns: table => new
                {
                    TargetKey = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Generation = table.Column<int>(type: "INTEGER", nullable: false),
                    LeaseId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LeaseKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    AdapterId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CredentialFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    IssuedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlackAdapterLeases", x => x.TargetKey);
                    table.CheckConstraint("CK_SlackAdapterLeases_ActiveLeaseCoherent", "(\"LeaseId\" IS NULL) = (\"LeaseKind\" IS NULL) AND (\"LeaseId\" IS NULL) = (\"AdapterId\" IS NULL) AND (\"LeaseId\" IS NULL) = (\"IssuedAt\" IS NULL) AND (\"LeaseId\" IS NULL) = (\"ExpiresAt\" IS NULL)");
                    table.CheckConstraint("CK_SlackAdapterLeases_LeaseKind", "\"LeaseKind\" IS NULL OR \"LeaseKind\" IN ('validation', 'runtime')");
                });

            migrationBuilder.CreateTable(
                name: "SlackAmbiguousPrompts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    MessageTs = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ThreadTs = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    WinningConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    MentionedConnectionIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    PromptedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlackAmbiguousPrompts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SlackConnectionAllowedMembers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SlackUserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlackConnectionAllowedMembers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SlackDmSessionMappings",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SlackUserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DmConversationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CurrentSessionId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    CurrentMessageTs = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlackDmSessionMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SlackManagerToolExecutionFences",
                columns: table => new
                {
                    JobKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlackManagerToolExecutionFences", x => x.JobKey);
                    table.CheckConstraint("CK_SlackManagerToolExecutionFences_State", "\"State\" IN ('started', 'completed')");
                });

            migrationBuilder.CreateTable(
                name: "SlackOutboxRows",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    OwnerKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ThreadTs = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DispatchRef = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ClaimedByAdapterId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeliveryUncertainAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeadLetteredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlackOutboxRows", x => x.Id);
                    table.CheckConstraint("CK_SlackOutboxRows_Kind", "\"Kind\" IN ('replaceable_progress', 'terminal_result', 'explicit_failure', 'user_action')");
                    table.CheckConstraint("CK_SlackOutboxRows_OwnerKind", "\"OwnerKind\" IN ('connection', 'manager')");
                    table.CheckConstraint("CK_SlackOutboxRows_State", "\"State\" IN ('pending', 'claimed', 'delivered', 'delivery_uncertain', 'dead_lettered')");
                });

            migrationBuilder.CreateTable(
                name: "SlackOwnerClaimCodes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CodeHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "initial"),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SupersededBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlackOwnerClaimCodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SlackProviderInboxRows",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SlackMessageIdentity = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ThreadTs = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SlackUserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RouteKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    RouteSessionId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    RouteTurnId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlackProviderInboxRows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SlackThreadLaunchReservations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ThreadTs = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LaunchMessageTs = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SlackUserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlackThreadLaunchReservations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SlackThreadSessionMappings",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ThreadTs = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SlackUserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    RootMessageTs = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlackThreadSessionMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SlackWorkspaceEnrollments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Lifecycle = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ManagerCapability = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CapabilityReason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    LastVerifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    PlanCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ManagedAppLimit = table.Column<int>(type: "INTEGER", nullable: false),
                    ConfigurationCredentialRef = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ConfigurationCredentialGeneration = table.Column<int>(type: "INTEGER", nullable: false),
                    ConfigurationCredentialExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ManagerCredentialRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ManagerAppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ManagerBotUserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ManagerTransportKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ManagerReadiness = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ManagerAppLifecycle = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "not_created"),
                    ManagerAppOperationFence = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    ManagerAppOperationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ManagerAppOperationOutcome = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    ManagerAppManifestHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, defaultValue: ""),
                    ManagerAppInstallUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false, defaultValue: ""),
                    RuntimeCredentialValidationState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "not_provided"),
                    ManagerActorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ClaimedSlackUserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ManagerClaimHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ManagerClaimIssuedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ManagerClaimExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ManagerClaimConsumedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AuditJson = table.Column<string>(type: "JSON", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlackWorkspaceEnrollments", x => x.Id);
                    table.CheckConstraint("CK_SlackWorkspaceEnrollments_Lifecycle", "\"Lifecycle\" IN ('active', 'disabled', 'removed')");
                    table.CheckConstraint("CK_SlackWorkspaceEnrollments_ManagerAppLifecycle", "\"ManagerAppLifecycle\" IN ('not_created', 'creating', 'created', 'create_unknown')");
                    table.CheckConstraint("CK_SlackWorkspaceEnrollments_ManagerCapability", "\"ManagerCapability\" IN ('unknown', 'available', 'unauthorized', 'capacity_limited')");
                    table.CheckConstraint("CK_SlackWorkspaceEnrollments_ManagerReadiness", "\"ManagerReadiness\" IN ('unknown', 'ready', 'not_ready', 'degraded')");
                    table.CheckConstraint("CK_SlackWorkspaceEnrollments_ManagerTransportKind", "\"ManagerTransportKind\" = 'socket'");
                    table.CheckConstraint("CK_SlackWorkspaceEnrollments_RuntimeCredentialValidationState", "\"RuntimeCredentialValidationState\" IN ('not_provided', 'candidate', 'awaiting_socket', 'verified', 'failed')");
                });

            migrationBuilder.CreateTable(
                name: "StoredSecrets",
                columns: table => new
                {
                    OwnerKind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OwnerScope = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Blob = table.Column<byte[]>(type: "BLOB", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredSecrets", x => new { x.OwnerKind, x.OwnerScope, x.OwnerId, x.Kind });
                    table.CheckConstraint("CK_StoredSecrets_Kind", "\"Kind\" IN ('appToken', 'botToken', 'webhookSecret', 'clientSecret', 'signingSecret', 'configurationAccessToken', 'configurationRefreshToken', 'previousBotToken', 'previousAppToken', 'candidateBotToken', 'candidateAppToken')");
                    table.CheckConstraint("CK_StoredSecrets_OwnerKind", "\"OwnerKind\" IN ('agent_connection', 'webhook_subscription', 'slack_workspace_enrollment', 'managed_slack_agent_app')");
                    table.CheckConstraint("CK_StoredSecrets_OwnerKindKind", "(\"OwnerKind\" = 'agent_connection' AND \"Kind\" IN ('appToken', 'botToken')) OR (\"OwnerKind\" = 'webhook_subscription' AND \"Kind\" = 'webhookSecret') OR (\"OwnerKind\" = 'slack_workspace_enrollment' AND \"Kind\" IN ('configurationAccessToken', 'configurationRefreshToken', 'appToken', 'botToken', 'clientSecret', 'signingSecret', 'previousBotToken', 'previousAppToken', 'candidateBotToken', 'candidateAppToken')) OR (\"OwnerKind\" = 'managed_slack_agent_app' AND \"Kind\" IN ('appToken', 'botToken', 'clientSecret', 'signingSecret', 'previousBotToken', 'previousAppToken', 'candidateBotToken', 'candidateAppToken'))");
                });

            migrationBuilder.CreateTable(
                name: "TaskLogBatches",
                columns: table => new
                {
                    OwnerKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    WorkId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Truncated = table.Column<bool>(type: "INTEGER", nullable: false),
                    Terminal = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    TerminalDigest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    UploadedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskLogBatches", x => new { x.OwnerKind, x.OwnerId, x.WorkId });
                });

            migrationBuilder.CreateTable(
                name: "TaskLogEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    WorkId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Seq = table.Column<long>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskLogEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TerminalLogOwnerships",
                columns: table => new
                {
                    OwnerKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    WorkId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RunnerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TerminalLogOwnerships", x => new { x.OwnerKind, x.OwnerId, x.WorkId });
                });

            migrationBuilder.CreateTable(
                name: "WatchEntries",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchEntries", x => new { x.ProjectId, x.IssueNumber, x.AgentId });
                });

            migrationBuilder.CreateTable(
                name: "WebhookDeliveryFailures",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SubscriptionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    EventId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    TargetUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    ResponseStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: true),
                    ErrorSummary = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookDeliveryFailures", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebhookSubscriptions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Match = table.Column<string>(type: "TEXT", nullable: false),
                    TargetUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    EventSelectionMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, defaultValue: "all"),
                    EventTypes = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    AuthType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, defaultValue: "none"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowArtifactPendingUploads",
                columns: table => new
                {
                    UploadId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    WorkflowRunId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    WorkId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TaskRunId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, defaultValue: "file"),
                    FileCount = table.Column<int>(type: "INTEGER", nullable: true),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Size = table.Column<long>(type: "INTEGER", nullable: true),
                    StoragePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowArtifactPendingUploads", x => x.UploadId);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowArtifacts",
                columns: table => new
                {
                    ArtifactId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    WorkflowRunId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    TaskRunId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SourceUploadId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ArtifactStoragePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, defaultValue: "file"),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Size = table.Column<long>(type: "INTEGER", nullable: true),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    IssueNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowArtifacts", x => x.ArtifactId);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowDispatchSnapshots",
                columns: table => new
                {
                    WorkflowRunId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    WorkId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowDispatchSnapshots", x => new { x.WorkflowRunId, x.WorkId });
                });

            migrationBuilder.CreateTable(
                name: "WorkflowProfileRecords",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    DefinitionSource = table.Column<string>(type: "TEXT", nullable: false),
                    SourceProvenance = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowProfileRecords", x => new { x.ProjectId, x.ProfileId });
                });

            migrationBuilder.CreateTable(
                name: "WorkflowRunEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    EventId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    TimeSortKey = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "strftime('%Y-%m-%dT%H:%M:%S', \"Time\") ||\nsubstr(\n    CASE\n        WHEN instr(substr(\"Time\", 20), '+') > 0 THEN substr(\"Time\", 20, instr(substr(\"Time\", 20), '+') - 1)\n        WHEN instr(substr(\"Time\", 20), '-') > 0 THEN substr(\"Time\", 20, instr(substr(\"Time\", 20), '-') - 1)\n        ELSE ''\n    END || '.0000000',\n    1,\n    8\n) || 'Z'", stored: true),
                    SpecVersion = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DataContentType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Data = table.Column<string>(type: "JSON", nullable: false),
                    ExtensionsJson = table.Column<string>(type: "JSON", nullable: false),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowRunEvents", x => new { x.Source, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "WorkflowRunProfiles",
                columns: table => new
                {
                    WorkflowRunId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Variables = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ETag = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowRunProfiles", x => x.WorkflowRunId);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowStageLocks",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowStageLocks", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowVariables",
                columns: table => new
                {
                    WorkflowRunId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowVariables", x => x.WorkflowRunId);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    TimelineSource = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, defaultValue: ""),
                    EventId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    TimeSortKey = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "strftime('%Y-%m-%dT%H:%M:%S', \"Time\") ||\nsubstr(\n    CASE\n        WHEN instr(substr(\"Time\", 20), '+') > 0 THEN substr(\"Time\", 20, instr(substr(\"Time\", 20), '+') - 1)\n        WHEN instr(substr(\"Time\", 20), '-') > 0 THEN substr(\"Time\", 20, instr(substr(\"Time\", 20), '-') - 1)\n        ELSE ''\n    END || '.0000000',\n    1,\n    8\n) || 'Z'", stored: true),
                    SpecVersion = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DataContentType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Data = table.Column<string>(type: "JSON", nullable: false),
                    ExtensionsJson = table.Column<string>(type: "JSON", nullable: false),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceEvents", x => new { x.Source, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "Workspaces",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    OriginKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OriginPayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    RepositoriesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    HomeRunnerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    HomePath = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workspaces", x => new { x.ProjectId, x.Name });
                });

            migrationBuilder.CreateTable(
                name: "InboxSubscriptions",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    WorkflowFailedEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AgentResultUnconfirmedEnabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    ApprovalRequestedEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IssueStartedEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IssueCompletedEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AgentResponseFailedEnabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxSubscriptions", x => x.ProjectId);
                    table.ForeignKey(
                        name: "FK_InboxSubscriptions_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ManagedSlackAgentApps",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    EnrollmentId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AgentConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    BotUserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AppLifecycle = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Authorization = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DesiredManifestVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    DesiredManifestHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AppliedManifestVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    AppliedManifestHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    VerifiedScopesJson = table.Column<string>(type: "JSON", nullable: false),
                    InstallUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false, defaultValue: ""),
                    RuntimeCredentialValidationState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "not_provided"),
                    OperationFence = table.Column<int>(type: "INTEGER", nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    OperationKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    OperationStartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UnknownOutcome = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    ErrorClass = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AuthorizationAttemptId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AuthorizedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AuthorizationExpiresAt = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ClientSecretRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SigningSecretRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    AppLevelTokenRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    BotTokenRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    BindingState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BindingErrorClass = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    AuditJson = table.Column<string>(type: "JSON", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagedSlackAgentApps", x => x.Id);
                    table.CheckConstraint("CK_ManagedSlackAgentApps_AppliedManifestPair", "(\"AppliedManifestVersion\" IS NULL AND \"AppliedManifestHash\" IS NULL) OR (\"AppliedManifestVersion\" IS NOT NULL AND \"AppliedManifestHash\" IS NOT NULL AND \"AppliedManifestVersion\" > 0 AND \"AppliedManifestHash\" <> '')");
                    table.CheckConstraint("CK_ManagedSlackAgentApps_AppLifecycle", "\"AppLifecycle\" IN ('not_created', 'creating', 'create_unknown', 'created', 'deleting', 'delete_unknown', 'deleted')");
                    table.CheckConstraint("CK_ManagedSlackAgentApps_Authorization", "\"Authorization\" IN ('not_started', 'awaiting_user', 'pending_admin', 'authorized', 'expired_or_cancelled', 'revoked')");
                    table.CheckConstraint("CK_ManagedSlackAgentApps_BindingState", "\"BindingState\" IN ('pending', 'in_progress', 'bound', 'connection_deleted', 'conflict')");
                    table.CheckConstraint("CK_ManagedSlackAgentApps_DesiredManifest", "\"DesiredManifestVersion\" > 0 AND \"DesiredManifestHash\" <> ''");
                    table.CheckConstraint("CK_ManagedSlackAgentApps_IdentityPair", "\"BotUserId\" = '' OR \"AppId\" <> ''");
                    table.ForeignKey(
                        name: "FK_ManagedSlackAgentApps_AgentConnections_AgentConnectionId",
                        column: x => x.AgentConnectionId,
                        principalTable: "AgentConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ManagedSlackAgentApps_SlackWorkspaceEnrollments_EnrollmentId",
                        column: x => x.EnrollmentId,
                        principalTable: "SlackWorkspaceEnrollments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Issues",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "COALESCE(json_extract(State, '$.status'), json_extract(State, '$.Status'))"),
                    WorkflowRunId = table.Column<string>(type: "TEXT", rowVersion: true, nullable: true, computedColumnSql: "COALESCE(json_extract(State, '$.workflowRunId'), json_extract(State, '$.WorkflowRunId'))", stored: true),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: true, computedColumnSql: "json_extract(State, '$.archivedAt') IS NOT NULL"),
                    Title = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "COALESCE(json_extract(State, '$.title'), json_extract(State, '$.Title'))"),
                    Priority = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "COALESCE(json_extract(State, '$.priority'), json_extract(State, '$.Priority'))"),
                    IsDraft = table.Column<bool>(type: "INTEGER", nullable: true, computedColumnSql: "COALESCE(json_extract(State, '$.isDraft'), json_extract(State, '$.IsDraft'))"),
                    PrerequisiteNumbersJson = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "COALESCE(json_extract(State, '$.prerequisiteNumbers'), json_extract(State, '$.PrerequisiteNumbers'))"),
                    Risk = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    EpicNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    ParentIssueNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    RepositoryName = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "COALESCE(json_extract(State, '$.repositoryRef'), json_extract(State, '$.RepositoryRef'))", stored: true),
                    WorkflowProfileIdKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Issues", x => new { x.ProjectId, x.Number });
                    table.ForeignKey(
                        name: "FK_Issues_WorkflowProfileRecords_ProjectId_WorkflowProfileIdKey",
                        columns: x => new { x.ProjectId, x.WorkflowProfileIdKey },
                        principalTable: "WorkflowProfileRecords",
                        principalColumns: new[] { "ProjectId", "ProfileId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProjectWorkflowProfiles",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DefaultTemplateId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DefaultWorkflowProfileId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DefaultWorkflowProfileIdKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Variables = table.Column<string>(type: "TEXT", nullable: false),
                    Prompts = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "{}"),
                    AgentActionOverrides = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "{}"),
                    DisableDefaultIssueTemplate = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    DisabledWorkflowProfileIds = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectWorkflowProfiles", x => x.ProjectId);
                    table.ForeignKey(
                        name: "FK_ProjectWorkflowProfiles_WorkflowProfileRecords_ProjectId_DefaultWorkflowProfileIdKey",
                        columns: x => new { x.ProjectId, x.DefaultWorkflowProfileIdKey },
                        principalTable: "WorkflowProfileRecords",
                        principalColumns: new[] { "ProjectId", "ProfileId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowRuns",
                columns: table => new
                {
                    WorkflowRunId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    EpicNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    MetadataProjectId = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "COALESCE(json_extract(State, '$.metadata.projectId'), json_extract(State, '$.Metadata.ProjectId'))", stored: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(State, '$.metadata.createdAt')", stored: false),
                    AssignedWorkerId = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "COALESCE(json_extract(State, '$.assignment.workerId'), json_extract(State, '$.assignment.runnerId'), json_extract(State, '$.claim.runnerId'))", stored: false),
                    ReadySince = table.Column<DateTime>(type: "TEXT", nullable: true, computedColumnSql: "COALESCE(json_extract(State, '$.readySince'), json_extract(State, '$.ReadySince'))", stored: false),
                    Status = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "LOWER(COALESCE(json_extract(State, '$.status'), json_extract(State, '$.Status')))", stored: true),
                    AttentionStatus = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    IssueNumber = table.Column<int>(type: "INTEGER", nullable: true, computedColumnSql: "CAST(COALESCE(json_extract(State, '$.metadata.issueNumber'), json_extract(State, '$.Metadata.IssueNumber')) AS INTEGER)", stored: true),
                    ActiveWorkId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ActiveWorkerId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    WorkflowProfileIdKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ETag = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowRuns", x => x.WorkflowRunId);
                    table.ForeignKey(
                        name: "FK_WorkflowRuns_WorkflowProfileRecords_MetadataProjectId_WorkflowProfileIdKey",
                        columns: x => new { x.MetadataProjectId, x.WorkflowProfileIdKey },
                        principalTable: "WorkflowProfileRecords",
                        principalColumns: new[] { "ProjectId", "ProfileId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SlackAgentAppBindingObligations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AgentAppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AgentConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ClaimToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FailureClass = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlackAgentAppBindingObligations", x => x.Id);
                    table.CheckConstraint("CK_SlackAgentAppBindingObligations_Status", "\"Status\" IN ('pending', 'in_progress', 'bound', 'connection_deleted', 'conflict')");
                    table.ForeignKey(
                        name: "FK_SlackAgentAppBindingObligations_AgentConnections_AgentConnectionId",
                        column: x => x.AgentConnectionId,
                        principalTable: "AgentConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SlackAgentAppBindingObligations_ManagedSlackAgentApps_AgentAppId",
                        column: x => x.AgentAppId,
                        principalTable: "ManagedSlackAgentApps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SlackOAuthAttempts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AgentAppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    StateHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    BotUserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BotTokenRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    FailureClass = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SecretStoredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AppliedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlackOAuthAttempts", x => x.Id);
                    table.CheckConstraint("CK_SlackOAuthAttempts_Status", "\"Status\" IN ('issued', 'consumed', 'secret_stored', 'applied', 'expired', 'recovery_required')");
                    table.ForeignKey(
                        name: "FK_SlackOAuthAttempts_ManagedSlackAgentApps_AgentAppId",
                        column: x => x.AgentAppId,
                        principalTable: "ManagedSlackAgentApps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowRunTaskMap",
                columns: table => new
                {
                    WorkflowRunId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    TaskId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    WorkId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowRunTaskMap", x => new { x.WorkflowRunId, x.TaskId });
                    table.ForeignKey(
                        name: "FK_WorkflowRunTaskMap_WorkflowRuns_WorkflowRunId",
                        column: x => x.WorkflowRunId,
                        principalTable: "WorkflowRuns",
                        principalColumn: "WorkflowRunId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SlackOAuthStates",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AgentAppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    StateHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AuthorizationAttemptId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlackOAuthStates", x => x.Id);
                    table.CheckConstraint("CK_SlackOAuthStates_Outcome", "\"Outcome\" IS NULL OR \"Outcome\" IN ('accepted', 'expired')");
                    table.ForeignKey(
                        name: "FK_SlackOAuthStates_ManagedSlackAgentApps_AgentAppId",
                        column: x => x.AgentAppId,
                        principalTable: "ManagedSlackAgentApps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SlackOAuthStates_SlackOAuthAttempts_AuthorizationAttemptId",
                        column: x => x.AuthorizationAttemptId,
                        principalTable: "SlackOAuthAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentConnections_Id",
                table: "AgentConnections",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_AgentConnections_ProjectId_AgentId",
                table: "AgentConnections",
                columns: new[] { "ProjectId", "AgentId" });

            migrationBuilder.CreateIndex(
                name: "UX_AgentConnections_ProjectId_AgentId_WorkspaceTeamId",
                table: "AgentConnections",
                columns: new[] { "ProjectId", "AgentId", "WorkspaceTeamId" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AgentJobEvents_DataStatus_Type_TimeSortKey_Source_Id",
                table: "AgentJobEvents",
                columns: new[] { "DataStatus", "Type", "TimeSortKey", "Source", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentJobEvents_Source_EventId",
                table: "AgentJobEvents",
                columns: new[] { "Source", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentJobEvents_TimeSortKey_Source_Id",
                table: "AgentJobEvents",
                columns: new[] { "TimeSortKey", "Source", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentJobEvents_Type_Source_Id",
                table: "AgentJobEvents",
                columns: new[] { "Type", "Source", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentJobEvents_Type_Time",
                table: "AgentJobEvents",
                columns: new[] { "Type", "Time" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentJobEvents_Undelivered",
                table: "AgentJobEvents",
                columns: new[] { "Source", "Id" },
                filter: "\"DispatchedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AgentJobs_AgentId_ProjectId_SubmittedAt",
                table: "AgentJobs",
                columns: new[] { "AgentId", "ProjectId", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentJobs_AssignedRunnerId_Status",
                table: "AgentJobs",
                columns: new[] { "AssignedRunnerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentJobs_AssignedRunnerId_Status_ReadySince",
                table: "AgentJobs",
                columns: new[] { "AssignedRunnerId", "Status", "ReadySince" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentJobs_LaunchVisibility_Status_ReadySince",
                table: "AgentJobs",
                columns: new[] { "LaunchVisibility", "Status", "ReadySince" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentJobs_PinnedRunner_Status_ReadySince",
                table: "AgentJobs",
                columns: new[] { "PinnedRunnerId", "Status", "ReadySince" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentJobs_Status_ReadySince",
                table: "AgentJobs",
                columns: new[] { "Status", "ReadySince" });

            migrationBuilder.CreateIndex(
                name: "IX_Agents_ProjectId_Name",
                table: "Agents",
                columns: new[] { "ProjectId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Agents_ProjectId_Status",
                table: "Agents",
                columns: new[] { "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionEvents_DataStatus_Type_TimeSortKey_Source_Id",
                table: "AgentSessionEvents",
                columns: new[] { "DataStatus", "Type", "TimeSortKey", "Source", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionEvents_TimeSortKey_Source_Id",
                table: "AgentSessionEvents",
                columns: new[] { "TimeSortKey", "Source", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionEvents_Type_Source_Id",
                table: "AgentSessionEvents",
                columns: new[] { "Type", "Source", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionEvents_Type_Time",
                table: "AgentSessionEvents",
                columns: new[] { "Type", "Time" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionEvents_Undelivered",
                table: "AgentSessionEvents",
                columns: new[] { "Source", "Id" },
                filter: "\"DispatchedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_AgentSessionId",
                table: "AgentSessions",
                column: "AgentSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_LabelAgentId_LabelProjectId_CreatedAt",
                table: "AgentSessions",
                columns: new[] { "LabelAgentId", "LabelProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_LabelAgentLaunchEpicNumber",
                table: "AgentSessions",
                column: "LabelAgentLaunchEpicNumber");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_LabelAgentLaunchIssueNumber",
                table: "AgentSessions",
                column: "LabelAgentLaunchIssueNumber");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_LabelConnectionId",
                table: "AgentSessions",
                column: "LabelConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_LabelProjectId_CreatedAt",
                table: "AgentSessions",
                columns: new[] { "LabelProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_LabelProjectId_LabelAgentLaunchEpicNumber_CreatedAt",
                table: "AgentSessions",
                columns: new[] { "LabelProjectId", "LabelAgentLaunchEpicNumber", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_LabelProjectId_LabelAgentLaunchIssueNumber_CreatedAt",
                table: "AgentSessions",
                columns: new[] { "LabelProjectId", "LabelAgentLaunchIssueNumber", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_LabelProjectId_LabelConnectionId_CreatedAt",
                table: "AgentSessions",
                columns: new[] { "LabelProjectId", "LabelConnectionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_LabelProjectId_LabelIssueNumber_CreatedAt",
                table: "AgentSessions",
                columns: new[] { "LabelProjectId", "LabelIssueNumber", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_LabelProjectId_LabelSlackUserId_CreatedAt",
                table: "AgentSessions",
                columns: new[] { "LabelProjectId", "LabelSlackUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_LabelSlackConversationId",
                table: "AgentSessions",
                column: "LabelSlackConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_LabelSlackThreadTs",
                table: "AgentSessions",
                column: "LabelSlackThreadTs");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_LabelSlackUserId",
                table: "AgentSessions",
                column: "LabelSlackUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_LabelSourceId",
                table: "AgentSessions",
                column: "LabelSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_LabelSourceId_LabelSessionName",
                table: "AgentSessions",
                columns: new[] { "LabelSourceId", "LabelSessionName" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_LabelWorkspaceName",
                table: "AgentSessions",
                column: "LabelWorkspaceName");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_Status_CreatedAt",
                table: "AgentSessions",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_StatusProject_SourceKind_Activity_CreatedAt",
                table: "AgentSessions",
                columns: new[] { "LabelProjectId", "LabelSourceKind", "Activity", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_TreeParent_AttachedRevision_Edge",
                table: "AgentSessions",
                columns: new[] { "LabelProjectId", "ParentSessionId", "ParentLinkState", "ParentLinkAttachedRevision", "ParentLinkEdgeId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_TreeVisibleParent_AttachedRevision_Edge",
                table: "AgentSessions",
                columns: new[] { "LabelProjectId", "LaunchVisibility", "ParentSessionId", "ParentLinkAttachedRevision", "ParentLinkEdgeId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionTranscriptParts_TurnId_Sequence",
                table: "AgentSessionTranscriptParts",
                columns: new[] { "TurnId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionTranscriptParts_TurnId_Type_CorrelationKey",
                table: "AgentSessionTranscriptParts",
                columns: new[] { "TurnId", "Type", "CorrelationKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionTranscriptParts_Type_PayloadStatus_LastSeenAt_Id",
                table: "AgentSessionTranscriptParts",
                columns: new[] { "Type", "PayloadStatus", "LastSeenAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionTranscriptTurns_SessionId_RuntimeSessionId_Sequence",
                table: "AgentSessionTranscriptTurns",
                columns: new[] { "SessionId", "RuntimeSessionId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionTranscriptTurns_SessionId_Sequence",
                table: "AgentSessionTranscriptTurns",
                columns: new[] { "SessionId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_ExpiresAt",
                table: "Attachments",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_ProjectId_Owner",
                table: "Attachments",
                columns: new[] { "ProjectId", "OwnerKind", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_ProjectId_OwnerIssueNumber",
                table: "Attachments",
                columns: new[] { "ProjectId", "OwnerKind", "OwnerIssueNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthAuditEvents_EventType_OccurredAt",
                table: "AuthAuditEvents",
                columns: new[] { "EventType", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Credentials_FamilyId",
                table: "Credentials",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_Credentials_PrincipalId_Kind_RevokedAt",
                table: "Credentials",
                columns: new[] { "PrincipalId", "Kind", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Credentials_PrincipalId_Name",
                table: "Credentials",
                columns: new[] { "PrincipalId", "Name" },
                unique: true,
                filter: "\"RevokedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Credentials_TokenHash",
                table: "Credentials",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeadLetters_DeadLetteredAt",
                table: "DeadLetters",
                column: "DeadLetteredAt");

            migrationBuilder.CreateIndex(
                name: "IX_DeadLetters_FailingHandler_DeadLetteredAt",
                table: "DeadLetters",
                columns: new[] { "FailingHandler", "DeadLetteredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DeadLetters_Source_Id_FailingHandler",
                table: "DeadLetters",
                columns: new[] { "Source", "Id", "FailingHandler" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceAuthorizations_DeviceCodeHash",
                table: "DeviceAuthorizations",
                column: "DeviceCodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceAuthorizations_UserCodeHash",
                table: "DeviceAuthorizations",
                column: "UserCodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EnrollmentTokens_TokenHash",
                table: "EnrollmentTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EpicEvents_Source_Id_DispatchedAt",
                table: "EpicEvents",
                columns: new[] { "Source", "Id", "DispatchedAt" },
                filter: "\"DispatchedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EpicEvents_TimelineSource_Time_Source_Id",
                table: "EpicEvents",
                columns: new[] { "TimelineSource", "Time", "Source", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_EpicEvents_Type_Source_Id",
                table: "EpicEvents",
                columns: new[] { "Type", "Source", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Epics_ProjectId_Number",
                table: "Epics",
                columns: new[] { "ProjectId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Epics_ProjectId_Status_CreatedAt",
                table: "Epics",
                columns: new[] { "ProjectId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GitHubConnections_Owner_Repo",
                table: "GitHubConnections",
                columns: new[] { "Owner", "Repo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitHubIssueLinks_ProjectId_RepositoryName_GithubIssueNumber",
                table: "GitHubIssueLinks",
                columns: new[] { "ProjectId", "RepositoryName", "GithubIssueNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitHubWriteBackFailures_ProjectId_CreatedAt",
                table: "GitHubWriteBackFailures",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InboxItems_ProjectId_CreatedAt",
                table: "InboxItems",
                columns: new[] { "ProjectId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_InboxItems_ProjectId_Id",
                table: "InboxItems",
                columns: new[] { "ProjectId", "Id" });

            migrationBuilder.CreateIndex(
                name: "UQ_InboxItems_SourceEvent",
                table: "InboxItems",
                columns: new[] { "SourceEventSource", "SourceEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IngressEvents_Undelivered",
                table: "IngressEvents",
                columns: new[] { "Source", "Id" },
                filter: "\"DispatchedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_IssueComments_ProjectId_IssueNumber_CreatedAt",
                table: "IssueComments",
                columns: new[] { "ProjectId", "IssueNumber", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IssueEvents_Source_Id_DispatchedAt",
                table: "IssueEvents",
                columns: new[] { "Source", "Id", "DispatchedAt" },
                filter: "\"DispatchedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_IssueEvents_TimelineSource_Time_Source_Id",
                table: "IssueEvents",
                columns: new[] { "TimelineSource", "Time", "Source", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_IssueEvents_TimeSortKey_Source_Id",
                table: "IssueEvents",
                columns: new[] { "TimeSortKey", "Source", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_IssueEvents_Type_Source_Id",
                table: "IssueEvents",
                columns: new[] { "Type", "Source", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Issues_ProjectId_EpicNumber_Number",
                table: "Issues",
                columns: new[] { "ProjectId", "EpicNumber", "Number" });

            migrationBuilder.CreateIndex(
                name: "IX_Issues_ProjectId_Number",
                table: "Issues",
                columns: new[] { "ProjectId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Issues_ProjectId_ParentIssueNumber_Number",
                table: "Issues",
                columns: new[] { "ProjectId", "ParentIssueNumber", "Number" });

            migrationBuilder.CreateIndex(
                name: "IX_Issues_ProjectId_RepositoryName_Status",
                table: "Issues",
                columns: new[] { "ProjectId", "RepositoryName", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Issues_ProjectId_WorkflowProfileIdKey",
                table: "Issues",
                columns: new[] { "ProjectId", "WorkflowProfileIdKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Issues_Status",
                table: "Issues",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Issues_WorkflowRunId",
                table: "Issues",
                column: "WorkflowRunId");

            migrationBuilder.CreateIndex(
                name: "IX_IssueWorkflowProfiles_ProjectId_IssueNumber",
                table: "IssueWorkflowProfiles",
                columns: new[] { "ProjectId", "IssueNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LabelDefinitions_ProjectId_Key",
                table: "LabelDefinitions",
                columns: new[] { "ProjectId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManagedSlackAgentApps_EnrollmentId_UpdatedAt",
                table: "ManagedSlackAgentApps",
                columns: new[] { "EnrollmentId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_ManagedSlackAgentApps_AgentConnectionId",
                table: "ManagedSlackAgentApps",
                column: "AgentConnectionId",
                unique: true,
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_ManagedSlackAgentApps_WorkspaceTeamId_AppId",
                table: "ManagedSlackAgentApps",
                columns: new[] { "WorkspaceTeamId", "AppId" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL AND \"AppId\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectIssueTemplates_ProjectId",
                table: "ProjectIssueTemplates",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectPromptTemplates_ProjectId_UpdatedAt",
                table: "ProjectPromptTemplates",
                columns: new[] { "ProjectId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_Name",
                table: "Projects",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectWorkflowProfiles_ProjectId_DefaultWorkflowProfileIdKey",
                table: "ProjectWorkflowProfiles",
                columns: new[] { "ProjectId", "DefaultWorkflowProfileIdKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectWorkflowTemplates_ProjectId",
                table: "ProjectWorkflowTemplates",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_RoutingRules_ProjectId",
                table: "RoutingRules",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_RoutingRules_ProjectId_Position",
                table: "RoutingRules",
                columns: new[] { "ProjectId", "Position" });

            migrationBuilder.CreateIndex(
                name: "UX_RoutingRules_ProjectId_IdempotencyKey",
                table: "RoutingRules",
                columns: new[] { "ProjectId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_RoutingRules_ProjectId_Name",
                table: "RoutingRules",
                columns: new[] { "ProjectId", "Name" },
                unique: true,
                filter: "\"Status\" <> 'deleted'");

            migrationBuilder.CreateIndex(
                name: "IX_SlackAgentAppBindingObligations_AgentConnectionId",
                table: "SlackAgentAppBindingObligations",
                column: "AgentConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_SlackAgentAppBindingObligations_Status_UpdatedAt",
                table: "SlackAgentAppBindingObligations",
                columns: new[] { "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_SlackAgentAppBindingObligations_AgentAppId",
                table: "SlackAgentAppBindingObligations",
                column: "AgentAppId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlackAmbiguousPrompts_ProjectId_UpdatedAt",
                table: "SlackAmbiguousPrompts",
                columns: new[] { "ProjectId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_SlackAmbiguousPrompts_WorkspaceTeamId_ConversationId_MessageTs",
                table: "SlackAmbiguousPrompts",
                columns: new[] { "WorkspaceTeamId", "ConversationId", "MessageTs" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlackConnectionAllowedMembers_ProjectId_ConnectionId",
                table: "SlackConnectionAllowedMembers",
                columns: new[] { "ProjectId", "ConnectionId" });

            migrationBuilder.CreateIndex(
                name: "UX_SlackConnectionAllowedMembers_ProjectId_ConnectionId_SlackUserId",
                table: "SlackConnectionAllowedMembers",
                columns: new[] { "ProjectId", "ConnectionId", "SlackUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlackDmSessionMappings_ProjectId_ConnectionId_UpdatedAt",
                table: "SlackDmSessionMappings",
                columns: new[] { "ProjectId", "ConnectionId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_SlackDmSessionMappings_ConnectionId_DmConversationId",
                table: "SlackDmSessionMappings",
                columns: new[] { "ConnectionId", "DmConversationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlackManagerToolExecutionFences_SessionId",
                table: "SlackManagerToolExecutionFences",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_SlackOAuthAttempts_AgentAppId_Status_UpdatedAt",
                table: "SlackOAuthAttempts",
                columns: new[] { "AgentAppId", "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_SlackOAuthAttempts_StateHash",
                table: "SlackOAuthAttempts",
                column: "StateHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlackOAuthStates_AgentAppId_ConsumedAt_ExpiresAt",
                table: "SlackOAuthStates",
                columns: new[] { "AgentAppId", "ConsumedAt", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SlackOAuthStates_AuthorizationAttemptId",
                table: "SlackOAuthStates",
                column: "AuthorizationAttemptId");

            migrationBuilder.CreateIndex(
                name: "UX_SlackOAuthStates_StateHash",
                table: "SlackOAuthStates",
                column: "StateHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlackOutboxRows_ConnectionId_DispatchRef_Kind_State",
                table: "SlackOutboxRows",
                columns: new[] { "OwnerKind", "ConnectionId", "DispatchRef", "Kind", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_SlackOutboxRows_ConnectionId_State_ClaimedAt",
                table: "SlackOutboxRows",
                columns: new[] { "OwnerKind", "ConnectionId", "State", "ClaimedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SlackOutboxRows_ConnectionId_State_DeliveryUncertainAt",
                table: "SlackOutboxRows",
                columns: new[] { "OwnerKind", "ConnectionId", "State", "DeliveryUncertainAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SlackOutboxRows_ConnectionId_State_NextAttemptAt",
                table: "SlackOutboxRows",
                columns: new[] { "OwnerKind", "ConnectionId", "State", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SlackOutboxRows_ProjectId_ConnectionId_State",
                table: "SlackOutboxRows",
                columns: new[] { "OwnerKind", "ProjectId", "ConnectionId", "State" });

            migrationBuilder.CreateIndex(
                name: "UX_SlackOutboxRows_OwnerKind_ConnectionId_DispatchRef_Kind",
                table: "SlackOutboxRows",
                columns: new[] { "OwnerKind", "ConnectionId", "DispatchRef", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlackOwnerClaimCodes_ProjectId_ConnectionId_UsedAt_SupersededBy",
                table: "SlackOwnerClaimCodes",
                columns: new[] { "ProjectId", "ConnectionId", "UsedAt", "SupersededBy" });

            migrationBuilder.CreateIndex(
                name: "UX_SlackOwnerClaimCodes_ProjectId_ConnectionId_CodeHash",
                table: "SlackOwnerClaimCodes",
                columns: new[] { "ProjectId", "ConnectionId", "CodeHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlackProviderInboxRows_ProjectId_ConnectionId_DispatchedAt",
                table: "SlackProviderInboxRows",
                columns: new[] { "ProjectId", "ConnectionId", "DispatchedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_SlackProviderInboxRows_ConnectionId_SlackMessageIdentity",
                table: "SlackProviderInboxRows",
                columns: new[] { "ConnectionId", "SlackMessageIdentity" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlackThreadLaunchReservations_ProjectId_ConnectionId_UpdatedAt",
                table: "SlackThreadLaunchReservations",
                columns: new[] { "ProjectId", "ConnectionId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_SlackThreadLaunchReservations_ConnectionId_WorkspaceTeamId_ConversationId_ThreadTs",
                table: "SlackThreadLaunchReservations",
                columns: new[] { "ConnectionId", "WorkspaceTeamId", "ConversationId", "ThreadTs" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlackThreadSessionMappings_ProjectId_ConnectionId_UpdatedAt",
                table: "SlackThreadSessionMappings",
                columns: new[] { "ProjectId", "ConnectionId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SlackThreadSessionMappings_ProjectId_WorkspaceTeamId_ConversationId_ThreadTs",
                table: "SlackThreadSessionMappings",
                columns: new[] { "ProjectId", "WorkspaceTeamId", "ConversationId", "ThreadTs" });

            migrationBuilder.CreateIndex(
                name: "IX_SlackThreadSessionMappings_WorkspaceTeamId_ConversationId_ThreadTs",
                table: "SlackThreadSessionMappings",
                columns: new[] { "WorkspaceTeamId", "ConversationId", "ThreadTs" });

            migrationBuilder.CreateIndex(
                name: "UX_SlackThreadSessionMappings_ConnectionId_WorkspaceTeamId_ConversationId_ThreadTs",
                table: "SlackThreadSessionMappings",
                columns: new[] { "ConnectionId", "WorkspaceTeamId", "ConversationId", "ThreadTs" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlackWorkspaceEnrollments_Lifecycle_UpdatedAt",
                table: "SlackWorkspaceEnrollments",
                columns: new[] { "Lifecycle", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_SlackWorkspaceEnrollments_WorkspaceTeamId",
                table: "SlackWorkspaceEnrollments",
                column: "WorkspaceTeamId",
                unique: true,
                filter: "\"DeletedAt\" IS NULL AND \"Lifecycle\" = 'active'");

            migrationBuilder.CreateIndex(
                name: "IX_StoredSecrets_Owner",
                table: "StoredSecrets",
                columns: new[] { "OwnerKind", "OwnerScope", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskLogEntries_Owner_WorkId_Seq",
                table: "TaskLogEntries",
                columns: new[] { "OwnerKind", "OwnerId", "WorkId", "Seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WatchEntries_ProjectId_IssueNumber",
                table: "WatchEntries",
                columns: new[] { "ProjectId", "IssueNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_WatchEntries_ProjectId_IssueNumber_State",
                table: "WatchEntries",
                columns: new[] { "ProjectId", "IssueNumber", "State" });

            migrationBuilder.CreateIndex(
                name: "UX_WatchEntries_ProjectId_IssueNumber_AgentId",
                table: "WatchEntries",
                columns: new[] { "ProjectId", "IssueNumber", "AgentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveryFailures_ProjectId",
                table: "WebhookDeliveryFailures",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveryFailures_ProjectId_SubscriptionId",
                table: "WebhookDeliveryFailures",
                columns: new[] { "ProjectId", "SubscriptionId" });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookSubscriptions_ProjectId",
                table: "WebhookSubscriptions",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookSubscriptions_ProjectId_Status",
                table: "WebhookSubscriptions",
                columns: new[] { "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_WebhookSubscriptions_ProjectId_Name",
                table: "WebhookSubscriptions",
                columns: new[] { "ProjectId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowArtifactPendingUploads_ExpiresAt",
                table: "WorkflowArtifactPendingUploads",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "UX_WorkflowArtifactPendingUploads_IdempotencyKey",
                table: "WorkflowArtifactPendingUploads",
                columns: new[] { "WorkflowRunId", "WorkId", "TaskRunId", "Path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowArtifacts_ProjectId_IssueNumber_RecordedAt",
                table: "WorkflowArtifacts",
                columns: new[] { "ProjectId", "IssueNumber", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowArtifacts_WorkflowRunId_Path_RecordedAt",
                table: "WorkflowArtifacts",
                columns: new[] { "WorkflowRunId", "Path", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowArtifacts_WorkflowRunId_TaskRunId_RecordedAt",
                table: "WorkflowArtifacts",
                columns: new[] { "WorkflowRunId", "TaskRunId", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_WorkflowArtifacts_SourceUploadId",
                table: "WorkflowArtifacts",
                column: "SourceUploadId",
                unique: true,
                filter: "\"SourceUploadId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowProfileRecords_ProjectId",
                table: "WorkflowProfileRecords",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRunEvents_Source_Id_DispatchedAt",
                table: "WorkflowRunEvents",
                columns: new[] { "Source", "Id", "DispatchedAt" },
                filter: "\"DispatchedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRunEvents_TimeSortKey_Source_Id",
                table: "WorkflowRunEvents",
                columns: new[] { "TimeSortKey", "Source", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRunEvents_Type_Source_Id",
                table: "WorkflowRunEvents",
                columns: new[] { "Type", "Source", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_AssignedWorkerId",
                table: "WorkflowRuns",
                column: "AssignedWorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_MetadataProjectId",
                table: "WorkflowRuns",
                column: "MetadataProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_MetadataProjectId_AssignedWorkerId_CreatedAt",
                table: "WorkflowRuns",
                columns: new[] { "MetadataProjectId", "AssignedWorkerId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_MetadataProjectId_WorkflowProfileIdKey",
                table: "WorkflowRuns",
                columns: new[] { "MetadataProjectId", "WorkflowProfileIdKey" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_ProjectId_AttentionStatus_CreatedAt",
                table: "WorkflowRuns",
                columns: new[] { "MetadataProjectId", "AttentionStatus", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_ProjectId_EpicNumber",
                table: "WorkflowRuns",
                columns: new[] { "MetadataProjectId", "EpicNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_ProjectId_IssueNumber",
                table: "WorkflowRuns",
                columns: new[] { "MetadataProjectId", "IssueNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_Status",
                table: "WorkflowRuns",
                columns: new[] { "Status", "AssignedWorkerId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_Status_ReadySince",
                table: "WorkflowRuns",
                columns: new[] { "Status", "AssignedWorkerId", "ReadySince" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRunTaskMap_WorkflowRunId_TaskId",
                table: "WorkflowRunTaskMap",
                columns: new[] { "WorkflowRunId", "TaskId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRunTaskMap_WorkflowRunId_WorkId",
                table: "WorkflowRunTaskMap",
                columns: new[] { "WorkflowRunId", "WorkId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceEvents_Source_Id_DispatchedAt",
                table: "WorkspaceEvents",
                columns: new[] { "Source", "Id", "DispatchedAt" },
                filter: "\"DispatchedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceEvents_TimelineSource_Time_Source_Id",
                table: "WorkspaceEvents",
                columns: new[] { "TimelineSource", "Time", "Source", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceEvents_TimeSortKey_Source_Id",
                table: "WorkspaceEvents",
                columns: new[] { "TimeSortKey", "Source", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceEvents_Type_Source_Id",
                table: "WorkspaceEvents",
                columns: new[] { "Type", "Source", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_ProjectId_OriginKind_OriginPayloadJson",
                table: "Workspaces",
                columns: new[] { "ProjectId", "OriginKind", "OriginPayloadJson" },
                unique: true,
                filter: "\"Status\" = 'active'");

            CreateOrleansStorage(migrationBuilder);
        }

        // Orleans ADO.NET persistence/reminder tables are runtime
        // infrastructure outside the EF model; the squashed chain created
        // them in 20260605025642_InitialSchema and
        // 20260610085006_AddOrleansStorage. Kept verbatim so a freshly
        // created database matches one upgraded through the old chain.
        private static void CreateOrleansStorage(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrleansQuery",
                columns: table => new
                {
                    QueryKey = table.Column<string>(type: "TEXT", nullable: false),
                    QueryText = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrleansQuery", x => x.QueryKey);
                });

            migrationBuilder.CreateTable(
                name: "OrleansRemindersTable",
                columns: table => new
                {
                    ServiceId = table.Column<string>(type: "TEXT", nullable: false),
                    GrainId = table.Column<string>(type: "TEXT", nullable: false),
                    ReminderName = table.Column<string>(type: "TEXT", nullable: false),
                    StartTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Period = table.Column<long>(type: "INTEGER", nullable: false),
                    GrainHash = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrleansRemindersTable", x => new { x.ServiceId, x.GrainId, x.ReminderName });
                });

            migrationBuilder.CreateTable(
                name: "OrleansStorage",
                columns: table => new
                {
                    GrainIdHash = table.Column<int>(type: "INTEGER", nullable: false),
                    GrainIdN0 = table.Column<long>(type: "INTEGER", nullable: false),
                    GrainIdN1 = table.Column<long>(type: "INTEGER", nullable: false),
                    GrainTypeHash = table.Column<int>(type: "INTEGER", nullable: false),
                    GrainTypeString = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    GrainIdExtensionString = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ServiceId = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    PayloadBinary = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ModifiedOn = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: true)
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrleansStorage",
                table: "OrleansStorage",
                columns: new[] { "GrainIdHash", "GrainTypeHash" });

            migrationBuilder.Sql(
                """
                INSERT INTO OrleansQuery (QueryKey, QueryText) VALUES
                ('DeleteReminderRowKey', 'DELETE FROM OrleansRemindersTable
                WHERE
                    ServiceId = @ServiceId AND @ServiceId IS NOT NULL
                    AND GrainId = @GrainId AND @GrainId IS NOT NULL
                    AND ReminderName = @ReminderName AND @ReminderName IS NOT NULL
                    AND Version = @Version AND @Version IS NOT NULL
                RETURNING 1;'),
                ('DeleteReminderRowsKey', 'DELETE FROM OrleansRemindersTable
                WHERE
                    ServiceId = @ServiceId AND @ServiceId IS NOT NULL;'),
                ('ReadRangeRows1Key', 'SELECT
                    GrainId,
                    ReminderName,
                    StartTime,
                    Period,
                    Version
                FROM OrleansRemindersTable
                WHERE
                    ServiceId = @ServiceId AND @ServiceId IS NOT NULL
                    AND GrainHash > @BeginHash AND @BeginHash IS NOT NULL
                    AND GrainHash <= @EndHash AND @EndHash IS NOT NULL;'),
                ('ReadRangeRows2Key', 'SELECT
                    GrainId,
                    ReminderName,
                    StartTime,
                    Period,
                    Version
                FROM OrleansRemindersTable
                WHERE
                    ServiceId = @ServiceId AND @ServiceId IS NOT NULL
                    AND ((GrainHash > @BeginHash AND @BeginHash IS NOT NULL)
                    OR (GrainHash <= @EndHash AND @EndHash IS NOT NULL));'),
                ('ReadReminderRowKey', 'SELECT
                    GrainId,
                    ReminderName,
                    StartTime,
                    Period,
                    Version
                FROM OrleansRemindersTable
                WHERE
                    ServiceId = @ServiceId AND @ServiceId IS NOT NULL
                    AND GrainId = @GrainId AND @GrainId IS NOT NULL
                    AND ReminderName = @ReminderName AND @ReminderName IS NOT NULL;'),
                ('ReadReminderRowsKey', 'SELECT
                    GrainId,
                    ReminderName,
                    StartTime,
                    Period,
                    Version
                FROM OrleansRemindersTable
                WHERE
                    ServiceId = @ServiceId AND @ServiceId IS NOT NULL
                    AND GrainId = @GrainId AND @GrainId IS NOT NULL;'),
                ('UpsertReminderRowKey', 'INSERT INTO OrleansRemindersTable
                (
                    ServiceId,
                    GrainId,
                    ReminderName,
                    StartTime,
                    Period,
                    GrainHash,
                    Version
                )
                VALUES
                (
                    @ServiceId,
                    @GrainId,
                    @ReminderName,
                    @StartTime,
                    @Period,
                    @GrainHash,
                    0
                )
                ON CONFLICT(ServiceId, GrainId, ReminderName) DO UPDATE SET
                    StartTime = excluded.StartTime,
                    Period = excluded.Period,
                    GrainHash = excluded.GrainHash,
                    Version = OrleansRemindersTable.Version + 1
                RETURNING Version;');
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO OrleansQuery (QueryKey, QueryText) VALUES
                ('WriteToStorageKey', '
                    BEGIN TRANSACTION;

                    CREATE TEMP TABLE IF NOT EXISTS OrleansStorageWriteState
                    (
                        TotalChangesBefore INT NOT NULL
                    );
                    DELETE FROM OrleansStorageWriteState;
                    INSERT INTO OrleansStorageWriteState (TotalChangesBefore) VALUES (total_changes() + 1);

                    UPDATE OrleansStorage
                    SET
                        PayloadBinary = @PayloadBinary,
                        ModifiedOn = datetime(''now''),
                        Version = Version + 1
                    WHERE
                        GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
                        AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
                        AND GrainTypeString = @GrainTypeString
                        AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
                        AND ServiceId = @ServiceId
                        AND Version = @GrainStateVersion;

                    INSERT INTO OrleansStorage (GrainIdHash, GrainIdN0, GrainIdN1, GrainTypeHash, GrainTypeString, GrainIdExtensionString, ServiceId, PayloadBinary, ModifiedOn, Version)
                    SELECT @GrainIdHash, @GrainIdN0, @GrainIdN1, @GrainTypeHash, @GrainTypeString, @GrainIdExtensionString, @ServiceId, @PayloadBinary, datetime(''now''), 1
                    WHERE changes() = 0
                      AND @GrainStateVersion IS NULL
                      AND NOT EXISTS (
                        SELECT 1 FROM OrleansStorage
                        WHERE GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
                        AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
                        AND GrainTypeString = @GrainTypeString
                        AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
                        AND ServiceId = @ServiceId
                    );

                    SELECT Version AS NewGrainStateVersion FROM OrleansStorage
                    WHERE total_changes() > (SELECT TotalChangesBefore FROM OrleansStorageWriteState LIMIT 1)
                        AND GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
                        AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
                        AND GrainTypeString = @GrainTypeString
                        AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
                        AND ServiceId = @ServiceId;

                    SELECT @GrainStateVersion AS NewGrainStateVersion
                    WHERE total_changes() = (SELECT TotalChangesBefore FROM OrleansStorageWriteState LIMIT 1)
                        AND @GrainStateVersion IS NOT NULL;

                    COMMIT;
                '),
                ('ReadFromStorageKey', '
                    SELECT
                        PayloadBinary,
                        Version AS Version
                    FROM
                        OrleansStorage
                    WHERE
                        GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
                        AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
                        AND GrainTypeString = @GrainTypeString
                        AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
                        AND ServiceId = @ServiceId
                    LIMIT 1;
                '),
                ('ClearStorageKey', '
                    UPDATE OrleansStorage
                    SET
                        PayloadBinary = NULL,
                        ModifiedOn = datetime(''now''),
                        Version = Version + 1
                    WHERE
                        GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
                        AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
                        AND GrainTypeString = @GrainTypeString
                        AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
                        AND ServiceId = @ServiceId
                        AND Version = @GrainStateVersion;

                    SELECT Version AS NewGrainStateVersion FROM OrleansStorage
                    WHERE changes() > 0
                        AND GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
                        AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
                        AND GrainTypeString = @GrainTypeString
                        AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
                        AND ServiceId = @ServiceId;

                    SELECT @GrainStateVersion AS NewGrainStateVersion
                    WHERE changes() = 0
                        AND @GrainStateVersion IS NOT NULL;
                ');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentJobEvents");

            migrationBuilder.DropTable(
                name: "AgentJobs");

            migrationBuilder.DropTable(
                name: "Agents");

            migrationBuilder.DropTable(
                name: "AgentSessionEvents");

            migrationBuilder.DropTable(
                name: "AgentSessions");

            migrationBuilder.DropTable(
                name: "AgentSessionTranscriptParts");

            migrationBuilder.DropTable(
                name: "AgentSessionTranscriptTurns");

            migrationBuilder.DropTable(
                name: "Attachments");

            migrationBuilder.DropTable(
                name: "AuthAuditEvents");

            migrationBuilder.DropTable(
                name: "Credentials");

            migrationBuilder.DropTable(
                name: "DeadLetters");

            migrationBuilder.DropTable(
                name: "DeviceAuthorizations");

            migrationBuilder.DropTable(
                name: "EnrollmentTokens");

            migrationBuilder.DropTable(
                name: "EpicCounters");

            migrationBuilder.DropTable(
                name: "EpicEvents");

            migrationBuilder.DropTable(
                name: "Epics");

            migrationBuilder.DropTable(
                name: "GitHubConnections");

            migrationBuilder.DropTable(
                name: "GitHubIssueLinks");

            migrationBuilder.DropTable(
                name: "GitHubWriteBackFailures");

            migrationBuilder.DropTable(
                name: "InboxItems");

            migrationBuilder.DropTable(
                name: "InboxSubscriptions");

            migrationBuilder.DropTable(
                name: "IngressEvents");

            migrationBuilder.DropTable(
                name: "IssueComments");

            migrationBuilder.DropTable(
                name: "IssueCounters");

            migrationBuilder.DropTable(
                name: "IssueEvents");

            migrationBuilder.DropTable(
                name: "IssuePrerequisites");

            migrationBuilder.DropTable(
                name: "Issues");

            migrationBuilder.DropTable(
                name: "IssueWorkflowProfiles");

            migrationBuilder.DropTable(
                name: "LabelDefinitions");

            migrationBuilder.DropTable(
                name: "Principals");

            migrationBuilder.DropTable(
                name: "ProjectIssueTemplates");

            migrationBuilder.DropTable(
                name: "ProjectPromptTemplates");

            migrationBuilder.DropTable(
                name: "ProjectWorkflowProfiles");

            migrationBuilder.DropTable(
                name: "ProjectWorkflowTemplates");

            migrationBuilder.DropTable(
                name: "RoutingRules");

            migrationBuilder.DropTable(
                name: "Runners");

            migrationBuilder.DropTable(
                name: "SessionTreeGraphRevisions");

            migrationBuilder.DropTable(
                name: "SlackAdapterLeases");

            migrationBuilder.DropTable(
                name: "SlackAgentAppBindingObligations");

            migrationBuilder.DropTable(
                name: "SlackAmbiguousPrompts");

            migrationBuilder.DropTable(
                name: "SlackConnectionAllowedMembers");

            migrationBuilder.DropTable(
                name: "SlackDmSessionMappings");

            migrationBuilder.DropTable(
                name: "SlackManagerToolExecutionFences");

            migrationBuilder.DropTable(
                name: "SlackOAuthStates");

            migrationBuilder.DropTable(
                name: "SlackOutboxRows");

            migrationBuilder.DropTable(
                name: "SlackOwnerClaimCodes");

            migrationBuilder.DropTable(
                name: "SlackProviderInboxRows");

            migrationBuilder.DropTable(
                name: "SlackThreadLaunchReservations");

            migrationBuilder.DropTable(
                name: "SlackThreadSessionMappings");

            migrationBuilder.DropTable(
                name: "StoredSecrets");

            migrationBuilder.DropTable(
                name: "TaskLogBatches");

            migrationBuilder.DropTable(
                name: "TaskLogEntries");

            migrationBuilder.DropTable(
                name: "TerminalLogOwnerships");

            migrationBuilder.DropTable(
                name: "WatchEntries");

            migrationBuilder.DropTable(
                name: "WebhookDeliveryFailures");

            migrationBuilder.DropTable(
                name: "WebhookSubscriptions");

            migrationBuilder.DropTable(
                name: "WorkflowArtifactPendingUploads");

            migrationBuilder.DropTable(
                name: "WorkflowArtifacts");

            migrationBuilder.DropTable(
                name: "WorkflowDispatchSnapshots");

            migrationBuilder.DropTable(
                name: "WorkflowRunEvents");

            migrationBuilder.DropTable(
                name: "WorkflowRunProfiles");

            migrationBuilder.DropTable(
                name: "WorkflowRunTaskMap");

            migrationBuilder.DropTable(
                name: "WorkflowStageLocks");

            migrationBuilder.DropTable(
                name: "WorkflowVariables");

            migrationBuilder.DropTable(
                name: "WorkspaceEvents");

            migrationBuilder.DropTable(
                name: "Workspaces");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropTable(
                name: "SlackOAuthAttempts");

            migrationBuilder.DropTable(
                name: "WorkflowRuns");

            migrationBuilder.DropTable(
                name: "ManagedSlackAgentApps");

            migrationBuilder.DropTable(
                name: "WorkflowProfileRecords");

            migrationBuilder.DropTable(
                name: "AgentConnections");

            migrationBuilder.DropTable(
                name: "SlackWorkspaceEnrollments");

            migrationBuilder.Sql("DELETE FROM OrleansQuery WHERE QueryKey IN ('WriteToStorageKey', 'ReadFromStorageKey', 'ClearStorageKey', 'DeleteReminderRowKey', 'DeleteReminderRowsKey', 'ReadRangeRows1Key', 'ReadRangeRows2Key', 'ReadReminderRowKey', 'ReadReminderRowsKey', 'UpsertReminderRowKey');");

            migrationBuilder.DropTable(
                name: "OrleansStorage");

            migrationBuilder.DropTable(
                name: "OrleansRemindersTable");

            migrationBuilder.DropTable(
                name: "OrleansQuery");
        }
    }
}
