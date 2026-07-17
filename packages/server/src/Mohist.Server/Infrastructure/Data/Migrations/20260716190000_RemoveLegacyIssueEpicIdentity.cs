using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class RemoveLegacyIssueEpicIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE "IssueEvents" AS event
            SET "TimelineSource" = '/mohist/projects/' || issue."ProjectId" || '/issues/' || issue."Number"
            FROM "Issues" AS issue
            WHERE event."Source" = '/mohist/issues/' || issue."IssueId";
            """);
        migrationBuilder.Sql("""
            UPDATE "IssueEvents" AS event
            SET "ExtensionsJson" = json_set(
                event."ExtensionsJson",
                '$.projectid', issue."ProjectId",
                '$.issue', CAST(issue."Number" AS TEXT))
            FROM "Issues" AS issue
            WHERE event."DispatchedAt" IS NULL
              AND event."Type" = 'com.mohist.issue.work-started'
              AND event."Source" = '/mohist/issues/' || issue."IssueId";
            """);
        migrationBuilder.Sql("""
            UPDATE "WorkflowRunEvents" AS event
            SET "ExtensionsJson" = json_set(
                event."ExtensionsJson",
                '$.projectid', run."MetadataProjectId",
                '$.issue', CAST(run."IssueNumber" AS TEXT),
                '$.workflowrunid', run."WorkflowRunId")
            FROM "WorkflowRuns" AS run
            WHERE event."DispatchedAt" IS NULL
              AND event."Type" = 'com.mohist.workflow.run.completed'
              AND run."MetadataProjectId" IS NOT NULL
              AND run."IssueNumber" IS NOT NULL
              AND event."Source" = '/mohist/workflows/' || run."WorkflowRunId";
            """);
        migrationBuilder.Sql("ALTER TABLE \"WorkflowRuns\" DROP COLUMN \"EpicId\";");
        migrationBuilder.Sql("""
            UPDATE "WorkflowRuns"
            SET "State" = json_remove(
                "State",
                '$.issueId',
                '$.IssueId',
                '$.epicId',
                '$.EpicId',
                '$.metadata.annotations.issueId',
                '$.metadata.annotations.IssueId',
                '$.metadata.annotations.issueid',
                '$.metadata.annotations.epicId',
                '$.metadata.annotations.EpicId',
                '$.metadata.annotations.epicid',
                '$.Metadata.Annotations.issueId',
                '$.Metadata.Annotations.IssueId',
                '$.Metadata.Annotations.issueid',
                '$.Metadata.Annotations.epicId',
                '$.Metadata.Annotations.EpicId',
                '$.Metadata.Annotations.epicid')
            WHERE json_type("State", '$') = 'object';
            """);
        migrationBuilder.Sql("DROP INDEX \"IX_WorkflowArtifacts_IssueId_RecordedAt\";");
        migrationBuilder.Sql("ALTER TABLE \"WorkflowArtifacts\" DROP COLUMN \"IssueId\";");
        migrationBuilder.Sql("ALTER TABLE \"IssueComments\" DROP COLUMN \"IssueId\";");
        migrationBuilder.Sql("ALTER TABLE \"InboxItems\" DROP COLUMN \"IssueId\";");

        migrationBuilder.Sql("""
            CREATE TABLE "__IssueWorkflowProfiles" (
                "ProjectId" TEXT NOT NULL,
                "IssueNumber" INTEGER NOT NULL,
                "Prompts" TEXT NOT NULL DEFAULT '{}',
                "SourceTemplateId" TEXT NULL,
                "Template" TEXT NULL,
                "UpdatedAt" TEXT NOT NULL,
                "Variables" TEXT NOT NULL,
                CONSTRAINT "PK_IssueWorkflowProfiles" PRIMARY KEY ("ProjectId", "IssueNumber")
            );

            INSERT INTO "__IssueWorkflowProfiles" (
                "ProjectId", "IssueNumber", "Prompts", "SourceTemplateId",
                "Template", "UpdatedAt", "Variables")
            SELECT
                "ProjectId", "IssueNumber", "Prompts", "SourceTemplateId",
                "Template", "UpdatedAt", "Variables"
            FROM "IssueWorkflowProfiles";

            DROP TABLE "IssueWorkflowProfiles";
            ALTER TABLE "__IssueWorkflowProfiles" RENAME TO "IssueWorkflowProfiles";
            CREATE UNIQUE INDEX "IX_IssueWorkflowProfiles_ProjectId_IssueNumber"
                ON "IssueWorkflowProfiles" ("ProjectId", "IssueNumber");
            """);

        migrationBuilder.Sql("""
            CREATE TABLE "__Issues" (
                "ProjectId" TEXT NOT NULL,
                "Number" INTEGER NOT NULL,
                "EpicNumber" INTEGER NULL,
                "IsArchived" INTEGER GENERATED ALWAYS AS (json_extract("State", '$.archivedAt') IS NOT NULL),
                "IsDraft" INTEGER GENERATED ALWAYS AS (COALESCE(json_extract("State", '$.isDraft'), json_extract("State", '$.IsDraft'))),
                "PrerequisiteNumbersJson" TEXT GENERATED ALWAYS AS (COALESCE(json_extract("State", '$.prerequisiteNumbers'), json_extract("State", '$.PrerequisiteNumbers'))),
                "Priority" TEXT GENERATED ALWAYS AS (COALESCE(json_extract("State", '$.priority'), json_extract("State", '$.Priority'))),
                "Risk" TEXT NULL,
                "State" TEXT NOT NULL,
                "Status" TEXT GENERATED ALWAYS AS (COALESCE(json_extract("State", '$.status'), json_extract("State", '$.Status'))),
                "Title" TEXT GENERATED ALWAYS AS (COALESCE(json_extract("State", '$.title'), json_extract("State", '$.Title'))),
                "WorkflowRunId" TEXT GENERATED ALWAYS AS (COALESCE(json_extract("State", '$.workflowRunId'), json_extract("State", '$.WorkflowRunId'))) STORED,
                CONSTRAINT "PK_Issues" PRIMARY KEY ("ProjectId", "Number")
            );

            INSERT INTO "__Issues" ("ProjectId", "Number", "EpicNumber", "Risk", "State")
            SELECT "ProjectId", "Number", "EpicNumber", "Risk", "State"
            FROM "Issues";

            DROP TABLE "Issues";
            ALTER TABLE "__Issues" RENAME TO "Issues";
            CREATE UNIQUE INDEX "IX_Issues_ProjectId_Number"
                ON "Issues" ("ProjectId", "Number");
            CREATE INDEX "IX_Issues_ProjectId_EpicNumber_Number"
                ON "Issues" ("ProjectId", "EpicNumber", "Number");
            CREATE INDEX "IX_Issues_WorkflowRunId" ON "Issues" ("WorkflowRunId");
            CREATE INDEX "IX_Issues_Status" ON "Issues" ("Status");
            """);

        migrationBuilder.Sql("""
            UPDATE "Issues"
            SET "State" = json_remove(
                CASE
                    WHEN COALESCE(
                        "EpicNumber",
                        json_extract("State", '$.epicNumber'),
                        json_extract("State", '$.EpicNumber')) IS NULL
                    THEN "State"
                    ELSE json_set(
                        "State",
                        '$.epicNumber',
                        COALESCE(
                            "EpicNumber",
                            json_extract("State", '$.epicNumber'),
                            json_extract("State", '$.EpicNumber')))
                END,
                '$.id',
                '$.Id',
                '$.issueId',
                '$.IssueId',
                '$.issueid',
                '$.epicId',
                '$.EpicId',
                '$.epicid',
                '$.EpicNumber')
            WHERE json_type("State", '$') = 'object';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException("Legacy Issue and Epic identifiers cannot be restored.");
    }
}
