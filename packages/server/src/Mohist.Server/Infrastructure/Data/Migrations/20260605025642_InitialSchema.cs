using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BacklogStates",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BacklogStates", x => x.ProjectId);
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
                name: "EpicIssues",
                columns: table => new
                {
                    EpicId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IssueId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EpicIssues", x => new { x.EpicId, x.IssueId });
                });

            migrationBuilder.CreateTable(
                name: "Epics",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Priority = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Epics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Data = table.Column<string>(type: "JSON", nullable: false),
                    Time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SpecVersion = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => new { x.Source, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "IssueComments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IssueId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
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
                name: "Issues",
                columns: table => new
                {
                    IssueId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(State, '$.ProjectId')", stored: true),
                    Number = table.Column<int>(type: "INTEGER", nullable: true, computedColumnSql: "json_extract(State, '$.Number')", stored: true),
                    WorkflowRunId = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(State, '$.WorkflowRunId')", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Issues", x => x.IssueId);
                });

            migrationBuilder.CreateTable(
                name: "IssueWorkflowProfiles",
                columns: table => new
                {
                    IssueId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SourceTemplateId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Template = table.Column<string>(type: "TEXT", nullable: true),
                    Variables = table.Column<string>(type: "TEXT", nullable: false),
                    Prompts = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "{}"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueWorkflowProfiles", x => x.IssueId);
                });

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
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    BaseBranch = table.Column<string>(type: "TEXT", nullable: false),
                    RepositoriesJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProjectWorkflowProfiles",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DefaultTemplateId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Variables = table.Column<string>(type: "TEXT", nullable: false),
                    Prompts = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "{}"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectWorkflowProfiles", x => x.ProjectId);
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
                name: "AgentSessionRuntimeEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    WorkflowRunId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SessionName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AgentSessionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    WorkId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    WorkType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSessionRuntimeEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentSessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    WorkflowRunId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SessionName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    WorkId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    WorkType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    RunnerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AgentSessionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastDataAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowLeases",
                columns: table => new
                {
                    WorkflowRunId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowLeases", x => x.WorkflowRunId);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowRuns",
                columns: table => new
                {
                    WorkflowRunId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    MetadataProjectId = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(State, '$.Metadata.Annotations.projectId')", stored: true),
                    ETag = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowRuns", x => x.WorkflowRunId);
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

            migrationBuilder.CreateIndex(
                name: "IX_EpicIssues_ProjectId_IssueId",
                table: "EpicIssues",
                columns: new[] { "ProjectId", "IssueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EpicIssues_ProjectId_IssueNumber",
                table: "EpicIssues",
                columns: new[] { "ProjectId", "IssueNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_Epics_ProjectId_Number",
                table: "Epics",
                columns: new[] { "ProjectId", "Number" });

            migrationBuilder.CreateIndex(
                name: "IX_Epics_ProjectId_Status_CreatedAt",
                table: "Epics",
                columns: new[] { "ProjectId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_Type_Source_Id",
                table: "Events",
                columns: new[] { "Type", "Source", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_IssueComments_ProjectId_IssueNumber_CreatedAt",
                table: "IssueComments",
                columns: new[] { "ProjectId", "IssueNumber", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Issues_ProjectId_Number",
                table: "Issues",
                columns: new[] { "ProjectId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Issues_WorkflowRunId",
                table: "Issues",
                column: "WorkflowRunId");


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
                name: "IX_ProjectWorkflowTemplates_ProjectId",
                table: "ProjectWorkflowTemplates",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionRuntimeEvents_ProjectId_IssueNumber_Id",
                table: "AgentSessionRuntimeEvents",
                columns: new[] { "ProjectId", "IssueNumber", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionRuntimeEvents_SessionId_Sequence",
                table: "AgentSessionRuntimeEvents",
                columns: new[] { "SessionId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionRuntimeEvents_WorkflowRunId_SessionName_Sequence",
                table: "AgentSessionRuntimeEvents",
                columns: new[] { "WorkflowRunId", "SessionName", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_AgentSessionId",
                table: "AgentSessions",
                column: "AgentSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_ProjectId_IssueNumber_CreatedAt",
                table: "AgentSessions",
                columns: new[] { "ProjectId", "IssueNumber", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_ProjectId_Status_CreatedAt",
                table: "AgentSessions",
                columns: new[] { "ProjectId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_WorkflowRunId_SessionName",
                table: "AgentSessions",
                columns: new[] { "WorkflowRunId", "SessionName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_WorkflowRunId_WorkId",
                table: "AgentSessions",
                columns: new[] { "WorkflowRunId", "WorkId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_MetadataProjectId",
                table: "WorkflowRuns",
                column: "MetadataProjectId");

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BacklogStates");

            migrationBuilder.DropTable(
                name: "EpicCounters");

            migrationBuilder.DropTable(
                name: "EpicIssues");

            migrationBuilder.DropTable(
                name: "Epics");

            migrationBuilder.DropTable(
                name: "Events");

            migrationBuilder.DropTable(
                name: "IssueComments");

            migrationBuilder.DropTable(
                name: "IssueCounters");

            migrationBuilder.DropTable(
                name: "IssuePrerequisites");

            migrationBuilder.DropTable(
                name: "Issues");

            migrationBuilder.DropTable(
                name: "IssueWorkflowProfiles");

            migrationBuilder.DropTable(
                name: "OrleansQuery");

            migrationBuilder.DropTable(
                name: "OrleansRemindersTable");

            migrationBuilder.DropTable(
                name: "ProjectPromptTemplates");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropTable(
                name: "ProjectWorkflowProfiles");

            migrationBuilder.DropTable(
                name: "ProjectWorkflowTemplates");

            migrationBuilder.DropTable(
                name: "AgentSessionRuntimeEvents");

            migrationBuilder.DropTable(
                name: "AgentSessions");

            migrationBuilder.DropTable(
                name: "WorkflowLeases");

            migrationBuilder.DropTable(
                name: "WorkflowRuns");

            migrationBuilder.DropTable(
                name: "WorkflowStageLocks");

            migrationBuilder.DropTable(
                name: "WorkflowVariables");
        }
    }
}
