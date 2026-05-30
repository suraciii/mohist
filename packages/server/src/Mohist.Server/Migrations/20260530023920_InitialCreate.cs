using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BacklogStates",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    StateJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BacklogStates", x => x.ProjectId);
                });

            migrationBuilder.CreateTable(
                name: "Configs",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Configs", x => x.Key);
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
                name: "IssueProfiles",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    StateJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueProfiles", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "IssueStates",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    StateJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueStates", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
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
                name: "workflow_runs",
                columns: table => new
                {
                    WorkflowRunId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    MetadataProjectId = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(State, '$.Metadata.Annotations.projectId')", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_runs", x => x.WorkflowRunId);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowAgentSessionEvents",
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
                    table.PrimaryKey("PK_WorkflowAgentSessionEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowAgentSessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    WorkflowRunId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SessionName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    WorkId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    WorkType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    RunnerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AgentSessionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    WorkDir = table.Column<string>(type: "TEXT", nullable: true),
                    ChangeDir = table.Column<string>(type: "TEXT", nullable: true),
                    ProcessPid = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastDataAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastHeartbeatAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FailureReason = table.Column<string>(type: "TEXT", nullable: true),
                    ExitCode = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowAgentSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IssueId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    IssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    WorkflowRunId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Category = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TaskId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CheckName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    RunnerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowLeases",
                columns: table => new
                {
                    WorkflowRunId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    StateJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowLeases", x => x.WorkflowRunId);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowRunProfiles",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    StateJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowRunProfiles", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowVariables",
                columns: table => new
                {
                    WorkflowRunId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    StateJson = table.Column<string>(type: "TEXT", nullable: false)
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
                name: "IX_Epics_ProjectId_Status_CreatedAt",
                table: "Epics",
                columns: new[] { "ProjectId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IssueComments_ProjectId_IssueNumber_CreatedAt",
                table: "IssueComments",
                columns: new[] { "ProjectId", "IssueNumber", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_Name",
                table: "Projects",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_MetadataProjectId",
                table: "workflow_runs",
                column: "MetadataProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowAgentSessionEvents_ProjectId_IssueNumber_Id",
                table: "WorkflowAgentSessionEvents",
                columns: new[] { "ProjectId", "IssueNumber", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowAgentSessionEvents_SessionId_Sequence",
                table: "WorkflowAgentSessionEvents",
                columns: new[] { "SessionId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowAgentSessionEvents_WorkflowRunId_SessionName_Sequence",
                table: "WorkflowAgentSessionEvents",
                columns: new[] { "WorkflowRunId", "SessionName", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowAgentSessions_AgentSessionId",
                table: "WorkflowAgentSessions",
                column: "AgentSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowAgentSessions_ProjectId_IssueNumber_CreatedAt",
                table: "WorkflowAgentSessions",
                columns: new[] { "ProjectId", "IssueNumber", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowAgentSessions_ProjectId_Status_CreatedAt",
                table: "WorkflowAgentSessions",
                columns: new[] { "ProjectId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowAgentSessions_WorkflowRunId_SessionName",
                table: "WorkflowAgentSessions",
                columns: new[] { "WorkflowRunId", "SessionName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowAgentSessions_WorkflowRunId_WorkId",
                table: "WorkflowAgentSessions",
                columns: new[] { "WorkflowRunId", "WorkId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowEvents_ProjectId_Id",
                table: "WorkflowEvents",
                columns: new[] { "ProjectId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowEvents_ProjectId_IssueNumber_Id",
                table: "WorkflowEvents",
                columns: new[] { "ProjectId", "IssueNumber", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowEvents_Type_CreatedAt",
                table: "WorkflowEvents",
                columns: new[] { "Type", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowEvents_WorkflowRunId_Id",
                table: "WorkflowEvents",
                columns: new[] { "WorkflowRunId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BacklogStates");

            migrationBuilder.DropTable(
                name: "Configs");

            migrationBuilder.DropTable(
                name: "EpicIssues");

            migrationBuilder.DropTable(
                name: "Epics");

            migrationBuilder.DropTable(
                name: "IssueComments");

            migrationBuilder.DropTable(
                name: "IssueCounters");

            migrationBuilder.DropTable(
                name: "IssuePrerequisites");

            migrationBuilder.DropTable(
                name: "IssueProfiles");

            migrationBuilder.DropTable(
                name: "IssueStates");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropTable(
                name: "workflow_runs");

            migrationBuilder.DropTable(
                name: "WorkflowAgentSessionEvents");

            migrationBuilder.DropTable(
                name: "WorkflowAgentSessions");

            migrationBuilder.DropTable(
                name: "WorkflowEvents");

            migrationBuilder.DropTable(
                name: "WorkflowLeases");

            migrationBuilder.DropTable(
                name: "WorkflowRunProfiles");

            migrationBuilder.DropTable(
                name: "WorkflowVariables");
        }
    }
}
