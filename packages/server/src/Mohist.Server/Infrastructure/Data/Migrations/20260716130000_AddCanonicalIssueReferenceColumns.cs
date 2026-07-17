using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260716130000_AddCanonicalIssueReferenceColumns")]
public partial class AddCanonicalIssueReferenceColumns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE \"IssueEvents\" ADD COLUMN \"TimelineSource\" TEXT NOT NULL DEFAULT '';" );
        migrationBuilder.Sql("ALTER TABLE \"EpicEvents\" ADD COLUMN \"TimelineSource\" TEXT NOT NULL DEFAULT '';" );
        migrationBuilder.AddColumn<string>(
            name: "ProjectId",
            table: "IssueWorkflowProfiles",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "IssueNumber",
            table: "IssueWorkflowProfiles",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "IssueNumber",
            table: "WorkflowArtifacts",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "OwnerIssueNumber",
            table: "Attachments",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.Sql("""
            CREATE TABLE "__WorkflowRuns" (
                "WorkflowRunId" TEXT NOT NULL CONSTRAINT "PK_WorkflowRuns" PRIMARY KEY,
                "State" TEXT NOT NULL,
                "EpicId" TEXT NULL,
                "ETag" INTEGER NOT NULL,
                "MetadataProjectId" TEXT GENERATED ALWAYS AS (
                    COALESCE(
                        json_extract("State", '$.metadata.annotations.projectId'),
                        json_extract("State", '$.Metadata.Annotations.projectId'),
                        json_extract("State", '$.Metadata.Annotations.ProjectId'))
                ) STORED,
                "IssueNumber" INTEGER GENERATED ALWAYS AS (
                    CAST(COALESCE(
                        json_extract("State", '$.metadata.annotations.issueNumber'),
                        json_extract("State", '$.Metadata.Annotations.issueNumber'),
                        json_extract("State", '$.Metadata.Annotations.IssueNumber')) AS INTEGER)
                ) STORED,
                "CreatedAt" TEXT GENERATED ALWAYS AS (json_extract("State", '$.metadata.createdAt')),
                "AssignedWorkerId" TEXT GENERATED ALWAYS AS (
                    COALESCE(
                        json_extract("State", '$.assignment.workerId'),
                        json_extract("State", '$.assignment.runnerId'),
                        json_extract("State", '$.claim.runnerId'))
                ),
                "ReadySince" TEXT GENERATED ALWAYS AS (
                    COALESCE(json_extract("State", '$.readySince'), json_extract("State", '$.ReadySince'))
                ),
                "Status" TEXT GENERATED ALWAYS AS (
                    LOWER(COALESCE(json_extract("State", '$.status'), json_extract("State", '$.Status')))
                ) STORED
            );

            INSERT INTO "__WorkflowRuns" ("WorkflowRunId", "State", "EpicId", "ETag")
            SELECT "WorkflowRunId", "State", "EpicId", "ETag"
            FROM "WorkflowRuns";

            DROP TABLE "WorkflowRuns";
            ALTER TABLE "__WorkflowRuns" RENAME TO "WorkflowRuns";

            CREATE INDEX "IX_WorkflowRuns_AssignedWorkerId"
                ON "WorkflowRuns" ("AssignedWorkerId");
            CREATE INDEX "IX_WorkflowRuns_MetadataProjectId"
                ON "WorkflowRuns" ("MetadataProjectId");
            CREATE INDEX "IX_WorkflowRuns_Status"
                ON "WorkflowRuns" ("Status", "AssignedWorkerId");
            CREATE INDEX "IX_WorkflowRuns_MetadataProjectId_AssignedWorkerId_CreatedAt"
                ON "WorkflowRuns" ("MetadataProjectId", "AssignedWorkerId", "CreatedAt");
            CREATE INDEX "IX_WorkflowRuns_Status_ReadySince"
                ON "WorkflowRuns" ("Status", "AssignedWorkerId", "ReadySince");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "IssueNumber", table: "WorkflowRuns");
        migrationBuilder.DropColumn(name: "OwnerIssueNumber", table: "Attachments");
        migrationBuilder.DropColumn(name: "IssueNumber", table: "WorkflowArtifacts");
        migrationBuilder.DropColumn(name: "IssueNumber", table: "IssueWorkflowProfiles");
        migrationBuilder.DropColumn(name: "ProjectId", table: "IssueWorkflowProfiles");
    }
}
