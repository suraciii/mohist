using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260716160000_BackfillCanonicalEpicReferences")]
public partial class BackfillCanonicalEpicReferences : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) =>
        MohistDbContextModelSnapshot.BuildModelCore(modelBuilder);

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(CanonicalEpicReferenceReconciliation.Sql);

        migrationBuilder.Sql("""
            CREATE TABLE "__EpicIssues" (
                "EpicId" TEXT NOT NULL,
                "IssueId" TEXT NOT NULL,
                "ProjectId" TEXT NOT NULL,
                "IssueNumber" INTEGER NOT NULL,
                "EpicNumber" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                CONSTRAINT "PK_EpicIssues" PRIMARY KEY ("EpicId", "IssueId")
            );

            INSERT INTO "__EpicIssues" (
                "EpicId", "IssueId", "ProjectId", "IssueNumber", "EpicNumber", "CreatedAt")
            SELECT "EpicId", "IssueId", "ProjectId", "IssueNumber", "EpicNumber", "CreatedAt"
            FROM "EpicIssues";

            DROP TABLE "EpicIssues";
            ALTER TABLE "__EpicIssues" RENAME TO "EpicIssues";
            CREATE INDEX "IX_EpicIssues_ProjectId_IssueId"
                ON "EpicIssues" ("ProjectId", "IssueId");
            CREATE INDEX "IX_EpicIssues_ProjectId_IssueNumber"
                ON "EpicIssues" ("ProjectId", "IssueNumber");
            """);

        migrationBuilder.Sql("""
            CREATE TABLE "__EpicActiveIssues" (
                "ProjectId" TEXT NOT NULL,
                "IssueId" TEXT NOT NULL,
                "EpicId" TEXT NOT NULL,
                "IssueNumber" INTEGER NOT NULL,
                "EpicNumber" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                CONSTRAINT "PK_EpicActiveIssues" PRIMARY KEY ("ProjectId", "IssueId")
            );

            INSERT INTO "__EpicActiveIssues" (
                "ProjectId", "IssueId", "EpicId", "IssueNumber", "EpicNumber", "CreatedAt")
            SELECT "ProjectId", "IssueId", "EpicId", "IssueNumber", "EpicNumber", "CreatedAt"
            FROM "EpicActiveIssues";

            DROP TABLE "EpicActiveIssues";
            ALTER TABLE "__EpicActiveIssues" RENAME TO "EpicActiveIssues";
            CREATE INDEX "IX_EpicActiveIssues_ProjectId_EpicId"
                ON "EpicActiveIssues" ("ProjectId", "EpicId");
            """);

        migrationBuilder.Sql("""
            CREATE TABLE "__Epics" (
                "ProjectId" TEXT NOT NULL,
                "Number" INTEGER NOT NULL,
                "Title" TEXT NOT NULL,
                "Description" TEXT NOT NULL,
                "Priority" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "PauseReason" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                CONSTRAINT "PK_Epics" PRIMARY KEY ("ProjectId", "Number")
            );

            INSERT INTO "__Epics" (
                "ProjectId", "Number", "Title", "Description", "Priority",
                "Status", "PauseReason", "CreatedAt", "UpdatedAt")
            SELECT
                "ProjectId", "Number", "Title", "Description", "Priority",
                "Status", "PauseReason", "CreatedAt", "UpdatedAt"
            FROM "Epics";

            DROP TABLE "Epics";
            ALTER TABLE "__Epics" RENAME TO "Epics";

            CREATE UNIQUE INDEX "IX_Epics_ProjectId_Number"
                ON "Epics" ("ProjectId", "Number");
            CREATE INDEX "IX_Epics_ProjectId_Status_CreatedAt"
                ON "Epics" ("ProjectId", "Status", "CreatedAt");
            """);

        migrationBuilder.CreateIndex(
            name: "IX_EpicIssues_ProjectId_EpicNumber_IssueNumber",
            table: "EpicIssues",
            columns: new[] { "ProjectId", "EpicNumber", "IssueNumber" });

        migrationBuilder.CreateIndex(
            name: "IX_EpicActiveIssues_ProjectId_EpicNumber_IssueNumber",
            table: "EpicActiveIssues",
            columns: new[] { "ProjectId", "EpicNumber", "IssueNumber" });

        migrationBuilder.CreateIndex(
            name: "IX_Issues_ProjectId_EpicNumber_Number",
            table: "Issues",
            columns: new[] { "ProjectId", "EpicNumber", "Number" });

        migrationBuilder.CreateIndex(
            name: "IX_WorkflowRuns_ProjectId_EpicNumber",
            table: "WorkflowRuns",
            columns: new[] { "MetadataProjectId", "EpicNumber" });

        migrationBuilder.CreateIndex(
            name: "IX_AgentSessions_LabelProjectId_LabelAgentLaunchEpicNumber_CreatedAt",
            table: "AgentSessions",
            columns: new[] { "LabelProjectId", "LabelAgentLaunchEpicNumber", "CreatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException("Canonical epic references cannot be reverted to technical identifiers.");
    }
}
