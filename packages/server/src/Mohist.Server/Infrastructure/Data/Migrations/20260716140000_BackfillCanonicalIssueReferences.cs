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

        migrationBuilder.Sql("""
            CREATE TABLE "__IssueWorkflowProfiles" (
                "IssueId" TEXT NOT NULL CONSTRAINT "PK_IssueWorkflowProfiles" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "IssueNumber" INTEGER NOT NULL,
                "SourceTemplateId" TEXT NULL,
                "Template" TEXT NULL,
                "Variables" TEXT NOT NULL,
                "Prompts" TEXT NOT NULL DEFAULT '{}',
                "UpdatedAt" TEXT NOT NULL
            );

            INSERT INTO "__IssueWorkflowProfiles" (
                "IssueId", "ProjectId", "IssueNumber", "SourceTemplateId", "Template",
                "Variables", "Prompts", "UpdatedAt")
            SELECT
                "IssueId", "ProjectId", "IssueNumber", "SourceTemplateId", "Template",
                "Variables", "Prompts", "UpdatedAt"
            FROM "IssueWorkflowProfiles";

            DROP TABLE "IssueWorkflowProfiles";
            ALTER TABLE "__IssueWorkflowProfiles" RENAME TO "IssueWorkflowProfiles";
            """);

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

        throw new NotSupportedException("Canonical Issue references cannot be reverted to nullable transition columns.");
    }
}
