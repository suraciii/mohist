using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260716140000_BackfillCanonicalIssueReferences")]
public partial class BackfillCanonicalIssueReferences : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) =>
        MohistDbContextModelSnapshot.BuildModelCore(modelBuilder);

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(CanonicalIssueReferenceReconciliation.Sql);

        migrationBuilder.AlterColumn<string>(
            name: "ProjectId",
            table: "IssueWorkflowProfiles",
            type: "TEXT",
            maxLength: 256,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldMaxLength: 256,
            oldNullable: true);

        migrationBuilder.AlterColumn<int>(
            name: "IssueNumber",
            table: "IssueWorkflowProfiles",
            type: "INTEGER",
            nullable: false,
            oldClrType: typeof(int),
            oldType: "INTEGER",
            oldNullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_IssueWorkflowProfiles_ProjectId_IssueNumber",
            table: "IssueWorkflowProfiles",
            columns: new[] { "ProjectId", "IssueNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_WorkflowArtifacts_ProjectId_IssueNumber_RecordedAt",
            table: "WorkflowArtifacts",
            columns: new[] { "ProjectId", "IssueNumber", "RecordedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_Attachments_ProjectId_OwnerIssueNumber",
            table: "Attachments",
            columns: new[] { "ProjectId", "OwnerKind", "OwnerIssueNumber" });

        migrationBuilder.CreateIndex(
            name: "IX_WorkflowRuns_ProjectId_IssueNumber",
            table: "WorkflowRuns",
            columns: new[] { "MetadataProjectId", "IssueNumber" });

        migrationBuilder.CreateIndex(
            name: "IX_AgentSessions_LabelProjectId_LabelIssueNumber_CreatedAt",
            table: "AgentSessions",
            columns: new[] { "LabelProjectId", "LabelIssueNumber", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_AgentSessions_LabelProjectId_LabelAgentLaunchIssueNumber_CreatedAt",
            table: "AgentSessions",
            columns: new[] { "LabelProjectId", "LabelAgentLaunchIssueNumber", "CreatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_AgentSessions_LabelProjectId_LabelAgentLaunchIssueNumber_CreatedAt",
            table: "AgentSessions");
        migrationBuilder.DropIndex(
            name: "IX_AgentSessions_LabelProjectId_LabelIssueNumber_CreatedAt",
            table: "AgentSessions");
        migrationBuilder.DropIndex(name: "IX_WorkflowRuns_ProjectId_IssueNumber", table: "WorkflowRuns");
        migrationBuilder.DropIndex(name: "IX_Attachments_ProjectId_OwnerIssueNumber", table: "Attachments");
        migrationBuilder.DropIndex(name: "IX_WorkflowArtifacts_ProjectId_IssueNumber_RecordedAt", table: "WorkflowArtifacts");
        migrationBuilder.DropIndex(name: "IX_IssueWorkflowProfiles_ProjectId_IssueNumber", table: "IssueWorkflowProfiles");

        migrationBuilder.AlterColumn<string>(
            name: "ProjectId",
            table: "IssueWorkflowProfiles",
            type: "TEXT",
            maxLength: 256,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldMaxLength: 256);

        migrationBuilder.AlterColumn<int>(
            name: "IssueNumber",
            table: "IssueWorkflowProfiles",
            type: "INTEGER",
            nullable: true,
            oldClrType: typeof(int),
            oldType: "INTEGER");
    }
}
